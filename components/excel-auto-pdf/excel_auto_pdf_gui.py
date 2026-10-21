"""Excel自动转PDF：Windows 中文图形界面。"""

from __future__ import annotations

import argparse
import ctypes
import json
import logging
import multiprocessing
import os
import queue
import sys
import threading
import tkinter as tk
import winreg
from pathlib import Path
from tkinter import filedialog, messagebox, scrolledtext, ttk

import pystray
import win32api
import win32con
import win32event
import winerror
from PIL import Image, ImageDraw, ImageTk

from phase2_folder_monitor import FolderMonitor, application_dir
from app_logging import base_logger, recent_log_records
import config_store
from ui_layout import ResponsiveLayout


APP_NAME = "Excel自动转PDF"
APP_VERSION = "1.3.5"
CONFIG_FILENAME = "config.json"
LOG_FILENAME = "excel_auto_pdf.log"
MAX_LOG_LINES = 2_000
STARTUP_VALUE_NAME = "ExcelAutoPDF"
STARTUP_REGISTRY_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
SINGLE_INSTANCE_MUTEX = r"Local\ExcelAutoPDF_6F71F13C"
SHOW_WINDOW_EVENT = r"Local\ExcelAutoPDF_ShowWindow_6F71F13C"
_INSTANCE_MUTEX_HANDLE = None
_SHOW_WINDOW_EVENT_HANDLE = None


def user_data_dir(environment_name: str) -> Path:
    """取得适合保存用户配置或日志的目录，失败时回退到程序目录。"""
    base = os.environ.get(environment_name)
    if base:
        target = Path(base) / APP_NAME
    else:
        appdata_kind = "Roaming" if environment_name == "APPDATA" else "Local"
        target = Path.home() / "AppData" / appdata_kind / APP_NAME
    try:
        target.mkdir(parents=True, exist_ok=True)
        return target
    except OSError:
        return application_dir()


CONFIG_PATH = user_data_dir("APPDATA") / CONFIG_FILENAME
LOG_PATH = user_data_dir("LOCALAPPDATA") / LOG_FILENAME
CONFIG_WARNINGS: list[str] = []


class GuiLogHandler(logging.Handler):
    """把后台线程产生的日志安全地投递给 tkinter 主线程。"""

    def __init__(self, message_queue: queue.Queue[tuple]) -> None:
        super().__init__()
        self.message_queue = message_queue

    def emit(self, record: logging.LogRecord) -> None:
        try:
            self.message_queue.put(("log", self.format(record)))
        except Exception:
            self.handleError(record)


def configure_gui_logger(
    message_queue: queue.Queue[tuple],
) -> logging.Logger:
    """创建 UTF-8 文件日志和界面日志处理器。"""
    logger, formatter = base_logger(LOG_PATH)
    gui_handler = GuiLogHandler(message_queue)
    gui_handler.setFormatter(formatter)
    logger.addHandler(gui_handler)
    return logger


def load_config() -> dict:
    """读取 JSON 配置；旧版本配置缺少新字段时自动使用默认值。"""
    CONFIG_WARNINGS.clear()
    return config_store.load(CONFIG_PATH, CONFIG_WARNINGS.append)


def save_config(folder: str, recursive: bool, task_timeout: int) -> None:
    config_store.save(CONFIG_PATH, {
        "monitor_folder": folder, "recursive": recursive,
        "task_timeout": task_timeout, "version": APP_VERSION,
    })


def startup_command() -> str:
    """生成开机启动命令；打包后使用 EXE，源码模式使用 pythonw。"""
    if getattr(sys, "frozen", False):
        return f'"{Path(sys.executable).resolve()}" --silent'

    pythonw = Path(sys.executable).with_name("pythonw.exe")
    return f'"{pythonw}" "{Path(__file__).resolve()}" --silent'


def current_startup_value() -> str:
    """读取当前用户的开机启动项，不存在时返回空字符串。"""
    try:
        with winreg.OpenKey(
            winreg.HKEY_CURRENT_USER,
            STARTUP_REGISTRY_KEY,
            0,
            winreg.KEY_QUERY_VALUE,
        ) as key:
            value, _ = winreg.QueryValueEx(key, STARTUP_VALUE_NAME)
            return str(value)
    except FileNotFoundError:
        return ""


def is_startup_enabled() -> bool:
    """仅当启动项指向当前程序位置时显示为已启用。"""
    return current_startup_value().casefold() == startup_command().casefold()


def set_startup_enabled(enabled: bool) -> None:
    """为当前 Windows 用户添加或移除开机启动项。"""
    with winreg.CreateKeyEx(
        winreg.HKEY_CURRENT_USER,
        STARTUP_REGISTRY_KEY,
        0,
        winreg.KEY_SET_VALUE,
    ) as key:
        if enabled:
            winreg.SetValueEx(
                key,
                STARTUP_VALUE_NAME,
                0,
                winreg.REG_SZ,
                startup_command(),
            )
        else:
            try:
                winreg.DeleteValue(key, STARTUP_VALUE_NAME)
            except FileNotFoundError:
                pass


def bundled_resource(filename: str) -> Path:
    """取得源码目录或 PyInstaller 单文件临时目录中的资源。"""
    base = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
    return base / filename


def create_tray_image() -> Image.Image:
    """优先加载正式应用图标，资源缺失时使用内置备用图形。"""
    icon_path = bundled_resource("app_icon.png")
    try:
        with Image.open(icon_path) as source:
            return source.convert("RGBA").resize((64, 64), Image.Resampling.LANCZOS)
    except (OSError, ValueError):
        pass

    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((4, 4, 60, 60), radius=13, fill="#147D64")
    draw.rounded_rectangle((15, 11, 47, 53), radius=3, fill="white")
    draw.polygon([(37, 11), (47, 21), (37, 21)], fill="#B9E6D8")
    draw.line((21, 30, 41, 30), fill="#147D64", width=4)
    draw.line((21, 38, 41, 38), fill="#147D64", width=4)
    draw.polygon([(27, 46), (37, 46), (32, 52)], fill="#E5484D")
    return image


def acquire_single_instance() -> bool:
    """通过命名互斥量防止同一用户重复启动多个实例。"""
    global _INSTANCE_MUTEX_HANDLE, _SHOW_WINDOW_EVENT_HANDLE
    handle = win32event.CreateMutex(None, False, SINGLE_INSTANCE_MUTEX)
    if win32api.GetLastError() == winerror.ERROR_ALREADY_EXISTS:
        handle.Close()
        try:
            show_event = win32event.OpenEvent(
                win32con.EVENT_MODIFY_STATE,
                False,
                SHOW_WINDOW_EVENT,
            )
            win32event.SetEvent(show_event)
            show_event.Close()
        except win32api.error:
            pass
        return False
    _INSTANCE_MUTEX_HANDLE = handle
    _SHOW_WINDOW_EVENT_HANDLE = win32event.CreateEvent(
        None,
        False,
        False,
        SHOW_WINDOW_EVENT,
    )
    return True


class ExcelAutoPdfApp(ResponsiveLayout):
    """主窗口及其与后台目录监控器之间的协调逻辑。"""

    APP_NAME = APP_NAME
    APP_VERSION = APP_VERSION
    LOG_PATH = LOG_PATH
    _icon_factory = staticmethod(create_tray_image)

    STATUS_COLORS = {
        "已停止": "#6B7280",
        "正在监控": "#147D64",
        "等待文件写入完成": "#9A6700",
        "正在转换": "#0756A5",
        "转换成功": "#147D64",
        "转换失败，等待重试": "#B45309",
        "转换超时，已跳过": "#B45309",
        "转换失败": "#B42318",
        "正在停止": "#6B7280",
        "正在退出": "#6B7280",
    }

    def __init__(self, root: tk.Tk, *, start_hidden: bool = False) -> None:
        self.root = root
        self.start_hidden = start_hidden
        self.message_queue: queue.Queue[tuple] = queue.Queue()
        self.logger = configure_gui_logger(self.message_queue)
        self.monitor: FolderMonitor | None = None
        self.stopping = False
        self.closing = False
        self.tray_hint_shown = start_hidden
        self.startup_enabled = is_startup_enabled()
        self.tray_icon: pystray.Icon | None = None
        self.tray_thread: threading.Thread | None = None

        saved_config = load_config()
        saved_folder = saved_config.get("monitor_folder", "")
        if not isinstance(saved_folder, str):
            saved_folder = ""
        self.notice_var = tk.StringVar(value="；".join(CONFIG_WARNINGS))
        self.folder_var = tk.StringVar(value=saved_folder)
        self.recursive_var = tk.BooleanVar(
            value=bool(saved_config.get("recursive", False))
        )
        saved_timeout = saved_config.get("task_timeout", 180)
        if not isinstance(saved_timeout, int) or not 30 <= saved_timeout <= 3600:
            saved_timeout = 180
        self.task_timeout_var = tk.IntVar(value=saved_timeout)
        self.status_var = tk.StringVar(value="已停止")
        self.status_detail_var = tk.StringVar(value="请选择文件夹并开始监控")
        self.progress_var = tk.StringVar(value="剩余任务：0")
        self.startup_var = tk.BooleanVar(value=self.startup_enabled)

        self._configure_window()
        self._build_widgets()
        for line in recent_log_records(LOG_PATH):
            self._append_log(line)
        self._set_controls(running=False)
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        self.root.bind("<Unmap>", self._on_unmap)
        self._start_tray()
        self.root.after(100, self._poll_messages)
        self.root.after(300, self._poll_external_show_request)
        self.logger.info("程序已启动 | 版本=%s | 日志=%s", APP_VERSION, LOG_PATH)
        for warning in CONFIG_WARNINGS:
            self.logger.warning(warning)
        if CONFIG_WARNINGS and self.start_hidden:
            self._notify("；".join(CONFIG_WARNINGS), "配置恢复提示")
        if self.start_hidden:
            self.root.after(300, self._start_silently)



    def _start_tray(self) -> None:
        """在独立线程运行系统托盘消息循环。"""
        menu = pystray.Menu(
            pystray.MenuItem(
                "显示主窗口",
                lambda _icon, _item: self._queue_tray_command("show"),
                default=True,
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(
                "开始监控",
                lambda _icon, _item: self._queue_tray_command("start"),
                enabled=lambda _item: self.monitor is None and not self.stopping,
            ),
            pystray.MenuItem(
                "停止监控",
                lambda _icon, _item: self._queue_tray_command("stop"),
                enabled=lambda _item: self.monitor is not None and not self.stopping,
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(
                "开机自动启动",
                lambda _icon, _item: self._queue_tray_command("toggle_startup"),
                checked=lambda _item: self.startup_enabled,
            ),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem(
                "退出程序",
                lambda _icon, _item: self._queue_tray_command("exit"),
            ),
        )
        self.tray_icon = pystray.Icon(
            "ExcelAutoPDF",
            create_tray_image(),
            APP_NAME,
            menu,
        )
        self.tray_thread = threading.Thread(
            target=self.tray_icon.run,
            name="system-tray",
            daemon=True,
        )
        self.tray_thread.start()

    def _queue_tray_command(self, command: str) -> None:
        self.message_queue.put(("tray_command", command))

    def _poll_external_show_request(self) -> None:
        """收到重复启动信号时，从托盘恢复已有窗口。"""
        if _SHOW_WINDOW_EVENT_HANDLE is not None:
            result = win32event.WaitForSingleObject(_SHOW_WINDOW_EVENT_HANDLE, 0)
            if result == win32con.WAIT_OBJECT_0:
                self.show_window()
        if not self.closing and self.root.winfo_exists():
            self.root.after(300, self._poll_external_show_request)

    def _handle_tray_command(self, command: str) -> None:
        if command == "show":
            self.show_window()
        elif command == "start":
            self.start_monitoring()
        elif command == "stop":
            self.stop_monitoring()
        elif command == "toggle_startup":
            self.startup_var.set(not self.startup_enabled)
            self.apply_startup_setting()
        elif command == "exit":
            self.request_exit(confirm=False)

    def show_window(self) -> None:
        """从托盘恢复并激活主窗口。"""
        self.start_hidden = False
        self.startup_enabled = is_startup_enabled()
        self.startup_var.set(self.startup_enabled)
        self.root.deiconify()
        self.root.state("normal")
        self.root.lift()
        self.root.after(50, self.root.focus_force)
        self.logger.info("主界面已打开 | 后台监控状态=%s", self.status_var.get())
        self.root.after_idle(self._show_latest_log)

    def hide_to_tray(self) -> None:
        """隐藏主窗口，后台监控和 tkinter 消息循环继续运行。"""
        self.root.withdraw()
        if not self.tray_hint_shown:
            self.tray_hint_shown = True
            self._notify(
                "程序仍在后台运行，可双击托盘图标恢复窗口。",
                "已最小化到系统托盘",
            )

    def _on_unmap(self, _event: tk.Event) -> None:
        """点击最小化按钮时自动隐藏到托盘。"""
        self.root.after(100, self._hide_if_minimized)

    def _hide_if_minimized(self) -> None:
        if not self.closing and self.root.state() == "iconic":
            self.hide_to_tray()

    def _start_silently(self) -> None:
        """开机启动模式：保持窗口隐藏并自动开始监控上次目录。"""
        folder_text = self.folder_var.get().strip()
        folder = Path(folder_text).expanduser()
        if folder_text and folder.is_dir():
            self.start_monitoring(silent=True)
        else:
            self.logger.error("静默启动失败 | 原因=上次监控目录不存在")
            self._notify(
                "上次监控目录不存在，请从托盘打开程序并重新选择。",
                "自动监控未启动",
            )

    def _notify(self, message: str, title: str) -> None:
        """显示 Windows 右下角托盘通知；通知失败不影响主流程。"""
        icon = self.tray_icon
        if icon is None:
            return
        try:
            icon.notify(message, title)
        except Exception as exc:
            self.logger.warning("系统通知发送失败 | 原因=%s", exc)

    def _refresh_tray_menu(self) -> None:
        icon = self.tray_icon
        if icon is None:
            return
        try:
            icon.update_menu()
        except Exception as exc:
            self.logger.warning("托盘菜单刷新失败 | 原因=%s", exc)

    def apply_startup_setting(self) -> None:
        """应用界面或托盘中的开机启动选项。"""
        requested = bool(self.startup_var.get())
        if requested:
            folder = Path(self.folder_var.get().strip()).expanduser()
            if not self.folder_var.get().strip() or not folder.is_dir():
                self.startup_var.set(False)
                messagebox.showwarning(
                    "无法启用",
                    "请先选择有效的监控文件夹，再启用开机自动启动。",
                    parent=self.root,
                )
                return

        try:
            if requested:
                # 先持久化手动输入的目录；失败时不启用自动启动。
                save_config(str(folder.resolve()), bool(self.recursive_var.get()),
                            self._validated_task_timeout())
            set_startup_enabled(requested)
        except OSError as exc:
            self.startup_var.set(self.startup_enabled)
            self.logger.error("修改开机启动项失败 | 原因=%s", exc)
            messagebox.showerror(
                "设置失败",
                f"无法修改开机启动项：\n{exc}",
                parent=self.root,
            )
            return

        self.startup_enabled = requested
        self.logger.info(
            "开机自动启动=%s | 模式=静默启动到托盘",
            "已启用" if requested else "已关闭",
        )
        self._refresh_tray_menu()

    def save_current_config(self) -> bool:
        """保存当前目录、子文件夹选项和超时，失败时仅记录日志。"""
        try:
            task_timeout = self._validated_task_timeout()
            save_config(
                self.folder_var.get().strip(),
                bool(self.recursive_var.get()),
                task_timeout,
            )
        except (OSError, tk.TclError, ValueError) as exc:
            self.logger.error("保存配置失败 | 原因=%s", exc)
            self._set_notice("配置保存失败，之前的有效配置已保留：" + str(exc))
            return False
        return True

    def _validated_task_timeout(self) -> int:
        """把界面输入限制在 30 秒至 1 小时。"""
        try:
            value = int(self.task_timeout_var.get())
        except (tk.TclError, ValueError):
            value = 180
        value = max(30, min(3600, value))
        self.task_timeout_var.set(value)
        return value

    def choose_folder(self) -> None:
        initial = self.folder_var.get().strip()
        if not Path(initial).is_dir():
            initial = str(Path.home())
        selected = filedialog.askdirectory(
            parent=self.root,
            title="选择需要监控的文件夹",
            initialdir=initial,
            mustexist=True,
        )
        if selected:
            self.folder_var.set(str(Path(selected).resolve()))
            self.save_current_config()

    def open_folder(self) -> None:
        folder = Path(self.folder_var.get().strip()).expanduser()
        if not folder.is_dir():
            messagebox.showwarning("提示", "请先选择有效的监控文件夹。", parent=self.root)
            return
        try:
            os.startfile(str(folder.resolve()))
        except OSError as exc:
            messagebox.showerror(
                "打开失败", f"无法打开文件夹：\n{exc}", parent=self.root
            )

    def open_log_folder(self) -> None:
        """在资源管理器中打开日志所在目录，并尽量选中日志文件。"""
        try:
            LOG_PATH.parent.mkdir(parents=True, exist_ok=True)
            if LOG_PATH.exists():
                os.startfile("explorer.exe", arguments=f'/select,"{LOG_PATH}"')
            else:
                os.startfile(str(LOG_PATH.parent))
        except OSError as exc:
            messagebox.showerror(
                "打开失败",
                f"无法打开日志目录：\n{exc}",
                parent=self.root,
            )

    def start_monitoring(self, *, silent: bool = False) -> None:
        if self.monitor is not None or self.stopping:
            return

        folder_text = self.folder_var.get().strip()
        folder = Path(folder_text).expanduser()
        if not folder_text or not folder.is_dir():
            if silent:
                self.logger.error("启动监控失败 | 原因=监控目录无效")
                self._notify("请重新选择有效的监控文件夹。", "自动监控未启动")
            else:
                messagebox.showwarning(
                    "目录无效",
                    "请选择一个存在的文件夹后再开始监控。",
                    parent=self.root,
                )
            return

        folder = folder.resolve()
        self.folder_var.set(str(folder))
        if not self.save_current_config():
            self._notify("配置未能保存，请查看窗口提示和日志。", "监控未启动")
            return

        monitor = FolderMonitor(
            folder,
            logger=self.logger,
            status_callback=self._queue_status,
            progress_callback=self._queue_progress,
            recursive=bool(self.recursive_var.get()),
            task_timeout=self._validated_task_timeout(),
            conversion_timeout=min(120, self._validated_task_timeout()),
        )
        try:
            monitor.start()
        except Exception as exc:
            self.logger.exception("启动监控失败 | 原因=%s", exc)
            if silent:
                self._notify(str(exc), "自动监控启动失败")
            else:
                messagebox.showerror(
                    "启动失败",
                    f"无法启动文件夹监控：\n{exc}",
                    parent=self.root,
                )
            return

        self.monitor = monitor
        self._set_controls(running=True)
        self._refresh_tray_menu()

    def stop_monitoring(self) -> None:
        if self.monitor is None or self.stopping:
            return
        self.stopping = True
        self._show_status("正在退出" if self.closing else "正在停止", None)
        self._set_controls(running=True, stopping=True)
        threading.Thread(
            target=self._stop_monitor_in_background,
            name="monitor-stopper",
            daemon=True,
        ).start()

    def _stop_monitor_in_background(self) -> None:
        monitor = self.monitor
        if monitor is not None:
            while not monitor.stop():
                # 只有观察线程、复制/转换进程及工作线程退出后，才通知界面。
                threading.Event().wait(0.1)
        self.message_queue.put(("stopped",))

    def _queue_status(self, status: str, path: Path | None) -> None:
        self.message_queue.put(("status", status, path))

    def _queue_progress(
        self,
        remaining: int,
        average: float | None,
        eta: float | None,
    ) -> None:
        self.message_queue.put(("progress", remaining, average, eta))

    def _poll_messages(self) -> None:
        try:
            for _ in range(200):
                try:
                    message = self.message_queue.get_nowait()
                except queue.Empty:
                    break
                kind = message[0]
                if kind == "log":
                    self._append_log(message[1])
                elif kind == "status":
                    self._show_status(message[1], message[2])
                elif kind == "progress":
                    self._show_progress(message[1], message[2], message[3])
                elif kind == "stopped":
                    self.monitor = None
                    self.stopping = False
                    self._set_controls(running=False)
                    self._show_status("已停止", None)
                    self._show_progress(0, None, None)
                    if self.closing:
                        self._destroy()
                        return
                elif kind == "tray_command":
                    self._handle_tray_command(message[1])
        except Exception as exc:
            self.logger.error("界面消息处理失败：%s", exc)
        finally:
            if not self.closing or self.monitor is not None:
                if self.root.winfo_exists():
                    self.root.after(100, self._poll_messages)

    def _append_log(self, line: str) -> None:
        follow_latest = self.log_text.yview()[0] < 0.01
        self.log_text.mark_set("previous_top", "@0,0")
        self.log_text.mark_gravity("previous_top", tk.RIGHT)
        self.log_text.configure(state=tk.NORMAL)
        self.log_text.insert("1.0", line + "\n\n")
        if len(line) >= 19 and line[4:5] == "-" and line[10:11] == " ":
            self.log_text.tag_add("timestamp", "1.0", "1.19")
            self.last_log_var.set("最近记录：" + line[:19])
        line_count = int(self.log_text.index("end-1c").split(".")[0])
        if line_count > MAX_LOG_LINES:
            self.log_text.delete(f"{MAX_LOG_LINES}.0", tk.END)
        if follow_latest:
            self.log_text.yview_moveto(0)
        else:
            self.log_text.yview("previous_top")
        self.log_text.configure(state=tk.DISABLED)

    @staticmethod
    def _format_duration(seconds: float) -> str:
        seconds = max(0, round(seconds))
        if seconds < 60:
            return f"{seconds} 秒"
        minutes, seconds = divmod(seconds, 60)
        if minutes < 60:
            return f"{minutes} 分 {seconds} 秒"
        hours, minutes = divmod(minutes, 60)
        return f"{hours} 小时 {minutes} 分"

    def _show_progress(
        self,
        remaining: int,
        average: float | None,
        eta: float | None,
    ) -> None:
        text = f"剩余任务：{remaining}"
        if remaining > 0 and average is not None and eta is not None:
            text += (
                f"  |  平均：{self._format_duration(average)}"
                f"  |  预计剩余：{self._format_duration(eta)}"
            )
        elif remaining > 0:
            text += "  |  正在计算预计时间"
        self.progress_var.set(text)

    def _show_status(self, status: str, path: Path | None) -> None:
        if self.stopping and status != "已停止":
            status = "正在退出" if self.closing else "正在停止"
            path = None
        self.status_var.set(status)
        detail = path.name if path is not None else ""
        if status == "正在监控":
            detail = "等待 Excel 文件事件"
        elif status == "已停止":
            detail = "请选择文件夹并开始监控"
        self.status_detail_var.set(detail)
        self.status_label.configure(
            foreground=self.STATUS_COLORS.get(status, "#172B4D")
        )
        if self.tray_icon is not None:
            self.tray_icon.title = f"{APP_NAME} - {status}"
            self._refresh_tray_menu()
        if status == "转换成功" and path is not None:
            self._notify(f"{path.name}\nPDF 已生成或覆盖。", "Excel 转 PDF 成功")
        elif status == "转换失败" and path is not None:
            self._notify(
                f"{path.name}\n请查看转换日志了解失败原因和处理次数。",
                "Excel 转 PDF 失败",
            )
        elif status == "转换超时，已跳过" and path is not None:
            self._notify(
                f"{path.name}\n处理超过设定时间，已跳过并继续后续任务。",
                "Excel 转 PDF 已超时",
            )

    def _set_controls(self, running: bool, stopping: bool = False) -> None:
        normal_or_disabled = tk.DISABLED if running else tk.NORMAL
        self.folder_entry.configure(state=normal_or_disabled)
        self.choose_button.configure(state=normal_or_disabled)
        self.recursive_check.configure(state=normal_or_disabled)
        self.timeout_spinbox.configure(state=normal_or_disabled)
        self.start_button.configure(state=normal_or_disabled)
        self.stop_button.configure(
            state=tk.DISABLED if not running or stopping else tk.NORMAL
        )
        self.open_button.configure(state=tk.NORMAL)
        self.exit_button.configure(state=tk.NORMAL)

    def on_close(self) -> None:
        """关闭按钮改为最小化到托盘，真正退出由按钮或托盘菜单完成。"""
        if not self.closing:
            self.hide_to_tray()

    def request_exit(self, *, confirm: bool) -> None:
        if self.closing:
            return
        if confirm:
            prompt = (
                "程序正在监控文件夹。\n退出后将停止自动转换，确定退出吗？"
                if self.monitor is not None
                else "确定退出 Excel自动转PDF 吗？"
            )
            if not messagebox.askyesno("退出程序", prompt, parent=self.root):
                return

        self.closing = True
        if self.monitor is not None:
            self._show_status("正在退出", None)
            self.stop_monitoring()
        else:
            self._destroy()

    def _destroy(self) -> None:
        self.logger.info("程序已退出")
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except Exception:
                pass
            self.tray_icon = None
        logging.shutdown()
        self.root.destroy()


def enable_dpi_awareness() -> None:
    """让 tkinter 在 Windows 11 高 DPI 屏幕上保持清晰。"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except (AttributeError, OSError):
        pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=APP_NAME)
    parser.add_argument(
        "--silent",
        action="store_true",
        help="隐藏主窗口，并自动监控上次选择的目录",
    )
    return parser.parse_args()


def main() -> int:
    multiprocessing.freeze_support()
    args = parse_args()
    if not acquire_single_instance():
        return 0

    enable_dpi_awareness()
    root = tk.Tk()
    if args.silent:
        root.withdraw()
    ExcelAutoPdfApp(root, start_hidden=args.silent)
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
