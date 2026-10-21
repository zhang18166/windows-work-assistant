from __future__ import annotations

import hashlib
import logging
import multiprocessing
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from pypdf import PdfWriter, PdfReader
import win32api
import win32con
import win32event
import win32file
from excel_job import ExcelJob, assign_excel
from pdf_output import check_output_conflict, OutputConflictError, publish_pdf, validate_pdf
from worker_tasks import run_worker, snapshot_task, conversion_task
import phase2_folder_monitor as monitor_module
from phase2_folder_monitor import FolderMonitor, ExcelEventHandler

# GUI 模块导入时只创建隔离目录，绝不读取或写入真实配置、注册表。
IMPORT_DATA = tempfile.TemporaryDirectory(prefix="ExcelPDF-test-config-")
with patch.dict(os.environ, {"APPDATA": IMPORT_DATA.name, "LOCALAPPDATA": IMPORT_DATA.name}):
    import excel_auto_pdf_gui as gui


def sleep_task(sender):
    time.sleep(30)


def blocked_copy_task(sender, *args):
    import worker_tasks
    worker_tasks.shutil.copy2 = lambda *_: time.sleep(30)
    return snapshot_task(sender, *args)


def invalid_export_task(sender, *args):
    import worker_tasks
    worker_tasks._export_excel_to_path = lambda source, dest, *a: dest.write_bytes(b"invalid pdf")
    return conversion_task(sender, *args)


def slow_export_task(sender, *args):
    import worker_tasks
    def export(source, dest, *other):
        dest.write_bytes(b"%PDF-incomplete")
        time.sleep(30)
    worker_tasks._export_excel_to_path = export
    return conversion_task(sender, *args)


def crash_with_job_member(sender, job_name, pid_file):
    proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"],
                            creationflags=subprocess.CREATE_NO_WINDOW)
    handle = win32api.OpenProcess(win32con.PROCESS_SET_QUOTA | win32con.PROCESS_TERMINATE,
                                 False, proc.pid)
    try:
        assign_excel(job_name, handle)
    finally:
        handle.Close()
    Path(pid_file).write_text(str(proc.pid))
    sender.send(("excel_pid", proc.pid))
    os._exit(7)


def write_pdf(path, pages=1):
    writer = PdfWriter()
    for _ in range(pages):
        writer.add_blank_page(width=200, height=300)
    with Path(path).open("wb") as stream:
        writer.write(stream)


class Var:
    def __init__(self, value): self.value = value
    def get(self): return self.value
    def set(self, value): self.value = value


class Regression(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ExcelPDF-regression-")
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.logger = logging.getLogger("isolated-regression")
        self.logger.addHandler(logging.NullHandler())
        self.logger.propagate = False

    def monitor(self, **kwargs):
        return FolderMonitor(self.folder, logger=self.logger, stable_checks=1,
                             stable_interval=0.01, **kwargs)

    def test_atomic_replacement_valid_pdf(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"source")
        target = source.with_suffix(".pdf"); write_pdf(target)
        staged = self.folder / ".stage.pdf"; write_pdf(staged, 2)
        validate_pdf(staged)
        publish_pdf(staged, target, source)
        self.assertEqual(len(PdfReader(target).pages), 2)
        self.assertEqual(source.read_bytes(), b"source")
        self.assertFalse(staged.exists())

    def test_same_stem_collision_keeps_existing_pdf(self):
        source = self.folder / "Part.xlsx"; source.write_bytes(b"a")
        (self.folder / "part.xls").write_bytes(b"b")
        target = source.with_suffix(".pdf"); write_pdf(target)
        original = target.read_bytes()
        staged = self.folder / ".stage.pdf"; write_pdf(staged, 2)
        with self.assertRaises(OutputConflictError): publish_pdf(staged, target, source)
        self.assertEqual(target.read_bytes(), original)

    def test_different_folder_same_stem_is_allowed(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        sub = self.folder / "sub"; sub.mkdir(); (sub / "book.xls").write_bytes(b"b")
        check_output_conflict(source)

    def test_late_collision_before_publish(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        check_output_conflict(source)
        (self.folder / "book.xlsm").write_bytes(b"b")
        with self.assertRaises(OutputConflictError): check_output_conflict(source)

    def test_directory_with_excel_suffix_does_not_collide(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        (self.folder / "book.xls").mkdir()
        check_output_conflict(source)

    def test_locked_pdf_does_not_destroy_old_result(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        target = source.with_suffix(".pdf"); write_pdf(target)
        previous = target.read_bytes()
        staged = self.folder / ".stage.pdf"; write_pdf(staged, 2)
        handle = win32file.CreateFile(str(target), win32con.GENERIC_READ,
                                     win32con.FILE_SHARE_READ, None, win32con.OPEN_EXISTING, 0, None)
        try:
            with self.assertRaises(OSError): publish_pdf(staged, target, source)
            self.assertEqual(target.read_bytes(), previous)
        finally:
            handle.Close()

    def test_invalid_export_keeps_old_pdf_and_removes_staging(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        target = source.with_suffix(".pdf"); write_pdf(target); previous = target.read_bytes()
        with patch.object(monitor_module, "conversion_task", invalid_export_task):
            with self.assertRaises(RuntimeError): self.monitor()._export_with_timeout(source, target, 5, source)
        self.assertEqual(target.read_bytes(), previous)
        self.assertFalse(list(self.folder.glob(".ExcelAutoPDF-*.pdf")))

    def test_timed_out_export_keeps_old_pdf(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        target = source.with_suffix(".pdf"); write_pdf(target); previous = target.read_bytes()
        started = time.monotonic()
        with patch.object(monitor_module, "conversion_task", slow_export_task):
            with self.assertRaises(TimeoutError): self.monitor()._export_with_timeout(source, target, 0.6, source)
        self.assertLess(time.monotonic() - started, 7)
        self.assertEqual(target.read_bytes(), previous)
        self.assertFalse(list(self.folder.glob(".ExcelAutoPDF-*.pdf")))

    def test_copy_timeout_is_enforced_in_parent(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"a")
        snapshot_dir = self.folder / "snapshot"; snapshot_dir.mkdir()
        deadline = time.monotonic() + 0.6
        with self.assertRaises(TimeoutError):
            run_worker(blocked_copy_task, (str(source), str(snapshot_dir), deadline, .01, 1), deadline)
        self.assertLess(time.monotonic() - deadline, 4)

    def test_copy_snapshot_preserves_source(self):
        source = self.folder / "book.xlsx"; source.write_bytes(b"original")
        stat = source.stat()
        signature, snapshot, directory = self.monitor()._create_stable_snapshot(source, 5)
        try:
            self.assertEqual(snapshot.read_bytes(), b"original")
            self.assertEqual(signature[:2], (stat.st_size, stat.st_mtime_ns))
            self.assertEqual(signature[2], hashlib.sha256(b"original").hexdigest())
            self.assertEqual(source.stat().st_mtime_ns, stat.st_mtime_ns)
        finally:
            import shutil; shutil.rmtree(directory)

    def test_cancel_worker_without_waiting_for_timeout(self):
        event = threading.Event(); timer = threading.Timer(.5, event.set); timer.start()
        started = time.monotonic()
        try:
            with self.assertRaises(InterruptedError): run_worker(sleep_task, (), started + 30, event)
        finally: timer.join()
        self.assertLess(time.monotonic() - started, 5)

    def test_abnormal_worker_exit_job_cleans_only_owned_process(self):
        unrelated = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(30)"],
                                     creationflags=subprocess.CREATE_NO_WINDOW)
        self.addCleanup(lambda: unrelated.poll() is None and unrelated.terminate())
        job = ExcelJob(); pidfile = self.folder / "pid.txt"
        try:
            with self.assertRaises(RuntimeError):
                run_worker(crash_with_job_member, (job.name, str(pidfile)), time.monotonic() + 8)
            handle = win32api.OpenProcess(win32con.SYNCHRONIZE, False, int(pidfile.read_text()))
            try:
                job.close()
                self.assertEqual(win32event.WaitForSingleObject(handle, 3000), win32con.WAIT_OBJECT_0)
            finally: handle.Close()
            self.assertIsNone(unrelated.poll())
        finally:
            job.close()
            unrelated.terminate(); unrelated.wait(timeout=5)

    def test_stop_retains_live_worker_and_honest_state(self):
        m = self.monitor(); worker = Mock(); worker.is_alive.return_value = True
        m._worker = worker; m.current_status = "正在转换"
        self.assertFalse(m.stop())
        self.assertIs(m._worker, worker)
        self.assertNotEqual(m.current_status, "已停止")
        worker.is_alive.return_value = False
        self.assertTrue(m.stop()); self.assertIsNone(m._worker)

    def test_no_restart_with_live_old_worker(self):
        m = self.monitor(); m._worker = Mock(); m._worker.is_alive.return_value = True
        with self.assertRaises(RuntimeError): m.start()

    def test_blank_silent_folder_does_not_start(self):
        obj = SimpleNamespace(folder_var=Var(" "), logger=self.logger,
                              start_monitoring=Mock(), _notify=Mock())
        gui.ExcelAutoPdfApp._start_silently(obj)
        obj.start_monitoring.assert_not_called(); obj._notify.assert_called_once()

    def test_blank_manual_folder_does_not_start(self):
        obj = SimpleNamespace(monitor=None, stopping=False, folder_var=Var(""), logger=self.logger,
                              _notify=Mock())
        gui.ExcelAutoPdfApp.start_monitoring(obj, silent=True)
        obj._notify.assert_called_once()

    def test_startup_saves_manual_folder_before_registry_change(self):
        order=[]
        obj=SimpleNamespace(startup_var=Var(True),folder_var=Var(str(self.folder)),recursive_var=Var(True),
                            _validated_task_timeout=lambda:180,startup_enabled=False,logger=self.logger,
                            root=None,_refresh_tray_menu=Mock())
        with patch.object(gui,"save_config",side_effect=lambda *a:order.append(("save",a))), \
             patch.object(gui,"set_startup_enabled",side_effect=lambda *a:order.append(("startup",a))):
            gui.ExcelAutoPdfApp.apply_startup_setting(obj)
        self.assertEqual([x[0] for x in order],["save","startup"])
        self.assertEqual(order[0][1],(str(self.folder.resolve()),True,180))

    def test_failed_config_save_does_not_enable_startup(self):
        obj=SimpleNamespace(startup_var=Var(True),folder_var=Var(str(self.folder)),recursive_var=Var(False),
                            _validated_task_timeout=lambda:180,startup_enabled=False,logger=self.logger,
                            root=None,_refresh_tray_menu=Mock())
        with patch.object(gui,"save_config",side_effect=OSError("disk error")), \
             patch.object(gui,"set_startup_enabled") as startup, patch.object(gui.messagebox,"showerror"):
            gui.ExcelAutoPdfApp.apply_startup_setting(obj)
        startup.assert_not_called(); self.assertFalse(obj.startup_var.get())

    def test_events_still_filter_temporary_files_and_subfolders(self):
        m=self.monitor();m.submit(self.folder/"~$book.xlsx");m.submit(self.folder/"x.txt")
        m.submit(self.folder/"sub"/"book.xls")
        self.assertEqual(len(m._pending),0)
        m.recursive=True;m.submit(self.folder/"sub"/"book.xls")
        m.submit(self.folder/"sub"/"book.xls")
        self.assertEqual(len(m._pending),1)


if __name__ == "__main__":
    multiprocessing.freeze_support()
    unittest.main(verbosity=2)
