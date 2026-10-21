"""第二阶段：监控指定文件夹，并自动把 Excel 工作簿转换为 PDF。"""

from __future__ import annotations

import argparse
import logging
import shutil
import sys
import tempfile
import multiprocessing
import uuid
import threading
import time
from collections import deque, OrderedDict
from collections.abc import Callable
from pathlib import Path

from watchdog.events import FileSystemEvent, FileSystemEventHandler
from watchdog.observers import Observer

from phase1_excel_to_pdf import SUPPORTED_EXTENSIONS
from excel_job import ExcelJob
from pdf_output import OutputConflictError
from worker_tasks import run_worker, snapshot_task, conversion_task, cleanup_task
from app_logging import base_logger


StatusCallback = Callable[[str, Path | None], None]
ProgressCallback = Callable[[int, float | None, float | None], None]


def is_supported_excel(path: Path) -> bool:
    """判断是否为需要处理的 Excel 文件，并忽略 ~$ 临时文件。"""
    return (
        path.suffix.lower() in SUPPORTED_EXTENSIONS
        and not path.name.startswith("~$")
    )


def file_signature(path: Path) -> tuple[int, int]:
    """用文件大小和纳秒级修改时间标识一次具体保存。"""
    stat = path.stat()
    return stat.st_size, stat.st_mtime_ns






class ExcelEventHandler(FileSystemEventHandler):
    """把 watchdog 事件交给转换调度器，不在事件线程里执行耗时转换。"""

    def __init__(self, submit: Callable[[Path], None]) -> None:
        self._submit = submit

    def on_created(self, event: FileSystemEvent) -> None:
        self._handle_path(event.src_path, event.is_directory)

    def on_modified(self, event: FileSystemEvent) -> None:
        self._handle_path(event.src_path, event.is_directory)

    def on_moved(self, event: FileSystemEvent) -> None:
        destination = getattr(event, "dest_path", "")
        self._handle_path(destination, event.is_directory)

    def _handle_path(self, raw_path: str, is_directory: bool) -> None:
        if not is_directory and raw_path:
            self._submit(Path(raw_path))


class FolderMonitor:
    """串行处理文件事件，负责防重复、等待稳定、重试和状态通知。"""

    def __init__(
        self,
        folder: Path,
        *,
        logger: logging.Logger,
        status_callback: StatusCallback | None = None,
        progress_callback: ProgressCallback | None = None,
        recursive: bool = False,
        debounce_seconds: float = 2.0,
        stable_interval: float = 1.0,
        stable_checks: int = 3,
        ready_timeout: float = 300.0,
        task_timeout: float = 180.0,
        conversion_timeout: float = 120.0,
        max_retries: int = 3,
        retry_delay: float = 3.0,
    ) -> None:
        self.folder = folder.expanduser().resolve()
        self.logger = logger
        self.status_callback = status_callback
        self.progress_callback = progress_callback
        self.recursive = recursive
        self.debounce_seconds = debounce_seconds
        self.stable_interval = stable_interval
        self.stable_checks = stable_checks
        self.ready_timeout = ready_timeout
        self.task_timeout = task_timeout
        self.conversion_timeout = conversion_timeout
        self.max_retries = max_retries
        self.retry_delay = retry_delay

        self._observer: Observer | None = None
        self._worker: threading.Thread | None = None
        self._condition = threading.Condition()
        self._stop_event = threading.Event()
        self._pending: dict[str, tuple[Path, float]] = {}
        self._last_converted: OrderedDict[str, str] = OrderedDict()
        self._processing = False
        self._recent_durations: deque[float] = deque(maxlen=20)
        self.current_status = "已停止"

    def start(self) -> None:
        if not self.folder.is_dir():
            raise NotADirectoryError(f"监控目录不存在：{self.folder}")
        if self._observer is not None:
            return

        if self._worker is not None and self._worker.is_alive():
            raise RuntimeError("上一轮后台任务仍在清理，请稍后重试")
        self._stop_event.clear()
        self._worker = threading.Thread(
            target=self._worker_loop,
            name="excel-pdf-converter",
            daemon=True,
        )
        self._worker.start()

        try:
            handler = ExcelEventHandler(self.submit)
            self._observer = Observer()
            self._observer.schedule(
                handler,
                str(self.folder),
                recursive=self.recursive,
            )
            self._observer.start()
        except Exception:
            self._observer = None
            self._stop_event.set()
            with self._condition:
                self._condition.notify_all()
            self._worker.join(timeout=5)
            self._worker = None
            raise
        self._set_status("正在监控")
        self._notify_progress()
        self.logger.info(
            "监控已启动 | 目录=%s | 子文件夹=%s | 单文件超时=%g秒 | 转换超时=%g秒",
            self.folder,
            self.recursive,
            self.task_timeout,
            self.conversion_timeout,
        )

    def stop(self) -> bool:
        self._stop_event.set()
        with self._condition:
            self._condition.notify_all()

        observer = self._observer
        if observer is not None:
            observer.stop()
            observer.join(timeout=1)
            if observer.is_alive():
                return False
            self._observer = None

        if self._worker is not None:
            self._worker.join(timeout=1)
            if self._worker.is_alive():
                return False
            self._worker = None

        self._set_status("已停止")
        self._processing = False
        self._notify_progress()
        self.logger.info("监控已停止 | 目录=%s", self.folder)
        return True

    def submit(self, path: Path) -> None:
        """合并短时间内针对同一路径的连续文件事件。"""
        try:
            normalized = path.expanduser().resolve()
        except OSError:
            return
        if not is_supported_excel(normalized):
            return
        if self.recursive:
            try:
                normalized.relative_to(self.folder)
            except ValueError:
                return
        elif normalized.parent != self.folder:
            return

        key = str(normalized).casefold()
        with self._condition:
            self._pending[key] = (normalized, time.monotonic())
            self._condition.notify()
        self._notify_progress()

    def _worker_loop(self) -> None:
        while not self._stop_event.is_set():
            item = self._next_ready_item()
            if item is None:
                continue
            key, path = item
            started = time.monotonic()
            converted = self._convert_with_retries(key, path)
            with self._condition:
                self._processing = False
                if converted:
                    self._recent_durations.append(time.monotonic() - started)
            self._notify_progress()

    def _next_ready_item(self) -> tuple[str, Path] | None:
        with self._condition:
            while not self._stop_event.is_set():
                if not self._pending:
                    self._condition.wait(timeout=0.5)
                    continue

                key, (path, event_time) = min(
                    self._pending.items(), key=lambda entry: entry[1][1]
                )
                remaining = self.debounce_seconds - (time.monotonic() - event_time)
                if remaining > 0:
                    self._condition.wait(timeout=min(remaining, 0.5))
                    continue

                self._pending.pop(key, None)
                self._processing = True
                self._notify_progress()
                return key, path
        return None

    def _create_stable_snapshot(self, path: Path, timeout: float) -> tuple[tuple[int, int, str, bool], Path, Path]:
        self._set_status("等待文件写入完成", path)
        deadline = time.monotonic() + max(timeout, 0.1)
        directory = Path(tempfile.mkdtemp(prefix="ExcelAutoPDF_"))
        try:
            signature, snapshot = run_worker(snapshot_task,
                (str(path), str(directory), deadline, self.stable_interval, self.stable_checks),
                deadline, self._stop_event)
            return tuple(signature), Path(snapshot), directory
        except BaseException:
            shutil.rmtree(directory, ignore_errors=True)
            raise

    def _export_with_timeout(self, snapshot_path: Path, output_path: Path, timeout: float,
                             source_path: Path | None = None) -> Path:
        deadline = time.monotonic() + max(timeout, 0.1)
        staged = output_path.parent / (".ExcelAutoPDF-" + uuid.uuid4().hex + ".pdf")
        job = ExcelJob()
        success = False
        try:
            result = run_worker(conversion_task,
                (str(snapshot_path), str(source_path or snapshot_path), str(staged),
                 str(output_path), job.name, deadline), deadline, self._stop_event)
            success = True
            return Path(result)
        finally:
            try:
                job.close()
            finally:
                if not success:
                    try:
                        run_worker(cleanup_task, (str(staged),), time.monotonic() + 3)
                    except Exception as exc:
                        self.logger.warning("临时 PDF 清理未完成 | 路径=%s | 原因=%s", staged, exc)

    def _convert_with_retries(self, key: str, path: Path) -> bool:
        last_error: Exception | None = None
        task_deadline = time.monotonic() + self.task_timeout

        for attempt in range(1, self.max_retries + 1):
            if self._stop_event.is_set():
                return False
            remaining = task_deadline - time.monotonic()
            if remaining <= 0:
                last_error = TimeoutError(
                    f"单文件总处理时间超过 {self.task_timeout:g} 秒"
                )
                break

            snapshot_dir: Path | None = None
            try:
                signature, snapshot_path, snapshot_dir = self._create_stable_snapshot(
                    path,
                    min(self.ready_timeout, remaining),
                )
                if self._last_converted.get(key) == signature[2] and signature[3]:
                    self._last_converted.move_to_end(key)
                    self.logger.info("源文件=%s | 结果=内容相同，跳过转换", path.name)
                    self._set_status("正在监控")
                    return False

                self._set_status("正在转换", path)
                self.logger.info(
                    "源文件=%s | 结果=开始转换 | 尝试=%d/%d | 模式=临时快照",
                    path.name,
                    attempt,
                    self.max_retries,
                )
                remaining = task_deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(
                        f"单文件总处理时间超过 {self.task_timeout:g} 秒"
                    )
                pdf_path = self._export_with_timeout(
                    snapshot_path,
                    path.with_suffix(".pdf"),
                    min(self.conversion_timeout, remaining),
                    path,
                )
                self._last_converted[key] = signature[2]
                self._last_converted.move_to_end(key)
                if len(self._last_converted) > 10000:
                    self._last_converted.popitem(last=False)
                self.logger.info(
                    "源文件=%s | 结果=转换成功 | PDF=%s",
                    path.name,
                    pdf_path.name,
                )
                self._set_status("转换成功", path)
                # 成功状态短暂保留，随后恢复为正在监控。
                if not self._stop_event.wait(1.0):
                    self._set_status("正在监控")
                return True
            except OutputConflictError as exc:
                self.logger.error("源文件=%s | 结果=来源冲突，未覆盖 PDF | 原因=%s", path.name, exc)
                self._set_status("转换失败", path)
                return False
            except InterruptedError:
                return False
            except Exception as exc:
                last_error = exc
                self.logger.error(
                    "源文件=%s | 结果=转换失败 | 尝试=%d/%d | 原因=%s: %s",
                    path.name,
                    attempt,
                    self.max_retries,
                    type(exc).__name__,
                    exc,
                )
                self._set_status("转换失败，等待重试", path)
                if attempt < self.max_retries:
                    remaining = task_deadline - time.monotonic()
                    if remaining <= 0:
                        break
                    self._stop_event.wait(min(self.retry_delay, remaining))
            finally:
                if snapshot_dir is not None:
                    shutil.rmtree(snapshot_dir, ignore_errors=True)

        timed_out = isinstance(last_error, TimeoutError)
        result = "超时跳过" if timed_out else "最终失败"
        self._set_status("转换超时，已跳过" if timed_out else "转换失败", path)
        self.logger.error(
            "源文件=%s | 结果=%s | 最多重试=%d | 单文件超时=%g秒 | 原因=%s",
            path.name,
            result,
            self.max_retries,
            self.task_timeout,
            last_error,
        )
        if not self._stop_event.wait(2.0):
            self._set_status("正在监控")
        return False

    def _set_status(self, status: str, path: Path | None = None) -> None:
        self.current_status = status
        if self.status_callback is not None:
            try:
                self.status_callback(status, path)
            except Exception:
                self.logger.exception("状态回调执行失败")

    def _notify_progress(self) -> None:
        """报告当前任务总数、平均耗时和粗略预计剩余时间。"""
        if self.progress_callback is None:
            return
        with self._condition:
            remaining = len(self._pending) + (1 if self._processing else 0)
            average = (
                sum(self._recent_durations) / len(self._recent_durations)
                if self._recent_durations
                else None
            )
        eta = average * remaining if average is not None else None
        try:
            self.progress_callback(remaining, average, eta)
        except Exception:
            self.logger.exception("进度回调执行失败")


def configure_logger(log_path: Path) -> logging.Logger:
    """同时输出控制台日志和 UTF-8 文件日志。"""
    logger, formatter = base_logger(log_path)
    console_handler = logging.StreamHandler()
    console_handler.setFormatter(formatter)
    logger.addHandler(console_handler)
    return logger


def print_status(status: str, path: Path | None) -> None:
    filename = f" | 文件={path.name}" if path is not None else ""
    print(f"[当前状态] {status}{filename}", flush=True)


def application_dir() -> Path:
    """源码运行时返回脚本目录，打包后返回 EXE 所在目录。"""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="监控文件夹并自动将 Excel 转为 PDF")
    parser.add_argument(
        "folder",
        type=Path,
        nargs="?",
        default=application_dir(),
        help="需要监控的文件夹；省略时监控程序所在文件夹",
    )
    parser.add_argument(
        "--log-file",
        type=Path,
        default=application_dir() / "excel_auto_pdf.log",
        help="日志文件路径",
    )
    parser.add_argument(
        "--duration",
        type=float,
        default=0,
        help="测试用：运行指定秒数后自动停止；0 表示持续运行",
    )
    parser.add_argument(
        "--recursive",
        action="store_true",
        help="递归监控所有子文件夹",
    )
    parser.add_argument(
        "--task-timeout",
        type=float,
        default=180,
        help="单个文件从等待到转换结束的总超时秒数",
    )
    parser.add_argument(
        "--conversion-timeout",
        type=float,
        default=120,
        help="单次 Excel COM 导出的超时秒数",
    )
    return parser.parse_args()


def main() -> int:
    multiprocessing.freeze_support()
    args = parse_args()
    logger = configure_logger(args.log_file.expanduser().resolve())
    monitor = FolderMonitor(
        args.folder,
        logger=logger,
        status_callback=print_status,
        recursive=args.recursive,
        task_timeout=max(10, args.task_timeout),
        conversion_timeout=max(10, args.conversion_timeout),
    )

    try:
        monitor.start()
        if args.duration > 0:
            monitor._stop_event.wait(args.duration)
        else:
            while True:
                time.sleep(0.5)
    except KeyboardInterrupt:
        print("\n收到退出指令。", flush=True)
    except Exception as exc:
        logger.exception("程序启动失败：%s", exc)
        return 1
    finally:
        while not monitor.stop():
            time.sleep(0.1)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
