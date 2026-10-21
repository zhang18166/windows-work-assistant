"""整合宿主的本地管道服务：只复用转换核心，不创建第二个界面或托盘。

输入/输出均为 UTF-8 JSON Lines。stdout 专用于协议，诊断仍写原日志文件。
父端关闭 stdin 时会停止监控；宿主另用 Windows Job 覆盖异常退出清理。
"""
from __future__ import annotations
import json
import logging
import multiprocessing
import os
from pathlib import Path
import sys
import threading
import time
import msvcrt
import win32pipe
import pywintypes

from app_logging import base_logger, recent_log_records
import config_store
from phase2_folder_monitor import FolderMonitor

VERSION = "1.3.4"
_output_lock = threading.Lock()


def emit(event: str, **payload):
    with _output_lock:
        try:
            sys.stdout.write(json.dumps(dict(event=event, **payload), ensure_ascii=False) + "\n")
            sys.stdout.flush()
        except (OSError, BrokenPipeError):
            pass


class PipeLogHandler(logging.Handler):
    def emit(self, record):
        emit("log", line=self.format(record))


def input_lines():
    """轮询匿名管道，不把同步标准输入句柄长期锁在阻塞 ReadFile 中。

    Windows spawn 子进程继承标准句柄，父进程正阻塞读同一管道时，子进程的
    标准输入初始化可能被卡住。只在 PeekNamedPipe 确认有数据后读取，避开此竞争。
    """
    if sys.stdin.isatty():
        yield from sys.stdin
        return
    handle = msvcrt.get_osfhandle(sys.stdin.fileno())
    pending = b""
    while True:
        try:
            _, available, _ = win32pipe.PeekNamedPipe(handle, 0)
        except pywintypes.error as exc:
            if exc.winerror in (109, 232):
                if pending: yield pending.decode("utf-8")
                return
            raise
        if not available:
            time.sleep(.03)
            continue
        pending += os.read(sys.stdin.fileno(), min(available, 65536))
        while b"\n" in pending:
            line, pending = pending.split(b"\n", 1)
            if len(line) > 65536: raise ValueError("请求过长")
            yield line.decode("utf-8")
        if len(pending) > 65536:
            raise ValueError("请求过长")


class Service:
    def __init__(self):
        self.config_path = Path(os.environ.get("APPDATA", Path.home()/"AppData/Roaming"))/"Excel自动转PDF/config.json"
        self.log_path = Path(os.environ.get("LOCALAPPDATA", Path.home()/"AppData/Local"))/"Excel自动转PDF/excel_auto_pdf.log"
        self.logger, formatter = base_logger(self.log_path)
        handler = PipeLogHandler(); handler.setFormatter(formatter); self.logger.addHandler(handler)
        self.monitor = None
        self.config = config_store.load(self.config_path, self.logger.warning)

    def stop(self):
        if self.monitor:
            emit("status", status="正在停止", detail="等待后台任务清理")
            while not self.monitor.stop(): time.sleep(.1)
            self.monitor = None
        emit("status", status="已停止", detail="")

    def command(self, request):
        command = request.get("command")
        if command == "start":
            if self.monitor: return
            folder = str(self.config.get("monitor_folder", "")).strip()
            if not folder or not Path(folder).is_dir(): raise ValueError("请先在监控设置里选择有效文件夹")
            config_store.save(self.config_path, dict(self.config, version=VERSION))
            timeout = self.config.get("task_timeout", 180)
            monitor = FolderMonitor(Path(folder), logger=self.logger,
                recursive=self.config.get("recursive", False), task_timeout=timeout,
                conversion_timeout=min(timeout, 120),
                status_callback=lambda status, path:emit("status", status=status, detail=path.name if path else ""),
                progress_callback=lambda remaining, average, eta:emit("progress", remaining=remaining, average=average, eta=eta))
            monitor.start(); self.monitor = monitor
        elif command == "stop": self.stop()
        elif command == "set_config":
            if self.monitor: raise ValueError("请先停止监控，再修改监控设置")
            data = request.get("config", {})
            if not isinstance(data, dict): raise ValueError("设置必须是对象")
            # 使用与独立版相同的严格校验和原子保存，保留未知配置字段。
            config_store.save(self.config_path, dict(data, version=VERSION))
            self.config = config_store.load(self.config_path)
            emit("config", config=self.config)
        elif command == "get_config": emit("config", config=self.config)
        elif command == "quit": self.stop(); return False
        else: raise ValueError("不支持的命令：" + str(command))
        return True

    def run(self):
        for line in recent_log_records(self.log_path): emit("log", line=line)
        emit("ready", version=VERSION, config=self.config, log_path=str(self.log_path))
        self.logger.info("整合版转换服务已就绪 | 版本=%s", VERSION)
        try:
            for raw in input_lines():
                request = {}
                try:
                    if len(raw) > 65536: raise ValueError("请求过长")
                    request = json.loads(raw)
                    if not isinstance(request, dict): raise ValueError("请求必须是对象")
                    keep_running = self.command(request)
                    emit("reply", id=request.get("id"), ok=True)
                    if keep_running is False: break
                except Exception as exc:
                    self.logger.error("整合请求失败 | 原因=%s", exc)
                    emit("reply", id=request.get("id") if isinstance(request, dict) else None, ok=False, error=str(exc))
        finally:
            self.stop()
            self.logger.info("整合版转换服务已退出")
            logging.shutdown()


def main():
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
    Service().run()


if __name__ == "__main__":
    multiprocessing.freeze_support()
    main()
