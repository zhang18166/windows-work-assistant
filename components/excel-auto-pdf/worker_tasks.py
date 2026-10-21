"""受父进程截止时间控制的文件快照、Excel 转换与临时文件清理。"""
from __future__ import annotations

import multiprocessing
import hashlib
import shutil
import time
from pathlib import Path

from phase1_excel_to_pdf import _export_excel_to_path
from pdf_output import OutputConflictError, check_output_conflict, publish_pdf, validate_pdf


def _entry(target, sender, args) -> None:
    try:
        result = target(sender, *args)
        sender.send(("success", result))
    except BaseException as exc:
        try:
            sender.send(("error", type(exc).__name__, str(exc)[:1500]))
        except (OSError, EOFError):
            pass
    finally:
        sender.close()


def run_worker(target, args: tuple, deadline: float, stop_event=None):
    """小消息单向管道；不共享 Queue，不在主线程执行可能阻塞的文件复制。"""
    if stop_event is not None and stop_event.is_set():
        raise InterruptedError("监控已停止")
    if time.monotonic() >= deadline:
        raise TimeoutError("任务已达到截止时间")
    context = multiprocessing.get_context("spawn")
    receiver, sender = context.Pipe(duplex=False)
    process = context.Process(target=_entry, args=(target, sender, args), daemon=True)
    started = False
    try:
        process.start()
        started = True
        sender.close()
        while True:
            if stop_event is not None and stop_event.is_set():
                raise InterruptedError("监控已停止")
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError("文件处理超过设定时间")
            if receiver.poll(min(0.05, remaining)):
                try:
                    message = receiver.recv()
                except EOFError as exc:
                    raise RuntimeError(f"工作进程异常退出，退出码={process.exitcode}") from exc
                if message[0] == "excel_pid":
                    # 仅供诊断；Excel 清理由 Job 对象负责，不凭 PID 结束进程。
                    continue
                if message[0] == "error":
                    if message[1] == "OutputConflictError":
                        raise OutputConflictError(message[2])
                    if message[1] == "TimeoutError":
                        raise TimeoutError(message[2])
                    raise RuntimeError(f"{message[1]}: {message[2]}")
                if message[0] == "success":
                    if time.monotonic() > deadline:
                        raise TimeoutError("工作进程完成时已超过截止时间")
                    return message[1]
                raise RuntimeError("工作进程返回未知消息")
            if not process.is_alive() and not receiver.poll():
                raise RuntimeError(f"工作进程异常退出，退出码={process.exitcode}")
    finally:
        try:
            if started:
                process.join(timeout=0.1)
                if process.is_alive():
                    process.terminate()
                    process.join(timeout=2)
                if process.is_alive():
                    process.kill()
                    process.join(timeout=2)
                if process.is_alive():
                    # 不允许调用者将未结束的任务误报为已停止。
                    while process.is_alive():
                        process.kill()
                        process.join(timeout=1)
        finally:
            receiver.close()
            sender.close()
            process.close()


def _content_hash(path: Path, deadline: float) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while True:
            if time.monotonic() >= deadline:
                raise TimeoutError("内容校验超过截止时间")
            chunk = stream.read(1024 * 1024)
            if not chunk:
                return digest.hexdigest()
            digest.update(chunk)


def snapshot_task(sender, source: str, directory: str, deadline: float,
                  stable_interval: float, stable_checks: int):
    path = Path(source)
    snapshot = Path(directory) / path.name
    previous = None
    unchanged = 0
    last_error = ""
    while time.monotonic() < deadline:
        try:
            stat = path.stat()
            current = stat.st_size, stat.st_mtime_ns
            unchanged = unchanged + 1 if current == previous and current[0] > 0 else 1
            previous = current
            if current[0] > 0 and unchanged >= stable_checks:
                shutil.copy2(path, snapshot)
                after = path.stat()
                if (after.st_size, after.st_mtime_ns) == current:
                    digest = _content_hash(snapshot, deadline)
                    # 防止同步软件保留大小和时间时，在复制期间更换了内容。
                    if digest != _content_hash(path, deadline):
                        unchanged = 0
                        previous = None
                        continue
                    after = path.stat()
                    if (after.st_size, after.st_mtime_ns) != current:
                        unchanged = 0
                        previous = None
                        continue
                    output = path.with_suffix(".pdf")
                    output_ready = output.is_file() and output.stat().st_size > 0
                    if time.monotonic() >= deadline:
                        raise TimeoutError("复制完成时已超过截止时间")
                    return (*current, digest, output_ready), str(snapshot)
                unchanged = 0
                previous = None
        except OSError as exc:
            last_error = str(exc)
            previous = None
            unchanged = 0
        time.sleep(min(stable_interval, max(0, deadline - time.monotonic())))
    raise TimeoutError("等待文件稳定或复制快照超时：" + last_error)


def conversion_task(sender, snapshot: str, source: str, staged: str,
                    target: str, job_name: str, deadline: float):
    original = Path(source)
    staged_path = Path(staged)
    try:
        check_output_conflict(original)
        _export_excel_to_path(Path(snapshot), staged_path,
                              lambda pid: sender.send(("excel_pid", pid)), job_name)
        validate_pdf(staged_path)
        if time.monotonic() >= deadline:
            raise TimeoutError("转换完成时已超过截止时间，未替换正式 PDF")
        # 再检查一次来源冲突，覆盖导出期间出现同名其他扩展名文件的情况。
        check_output_conflict(original)
        if time.monotonic() >= deadline:
            raise TimeoutError("发布前已超过截止时间，未替换正式 PDF")
        publish_pdf(staged_path, Path(target), original)
        return target
    finally:
        staged_path.unlink(missing_ok=True)


def cleanup_task(sender, filename: str):
    Path(filename).unlink(missing_ok=True)
