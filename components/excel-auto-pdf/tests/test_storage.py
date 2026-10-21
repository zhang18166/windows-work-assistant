from __future__ import annotations

import hashlib
import json
import logging
import multiprocessing
import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import config_store
import app_logging
from phase2_folder_monitor import FolderMonitor
from worker_tasks import run_worker, snapshot_task


def change_during_copy(sender, *args):
    import worker_tasks
    original_copy = worker_tasks.shutil.copy2
    changed = False
    def copy(source, target):
        nonlocal changed
        result = original_copy(source, target)
        if not changed:
            changed = True
            before = source.stat()
            source.write_bytes(b"BBBB")
            os.utime(source, ns=(before.st_atime_ns, before.st_mtime_ns))
        return result
    worker_tasks.shutil.copy2 = copy
    return snapshot_task(sender, *args)


class StorageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="ExcelPDF-storage-")
        self.folder = Path(self.temp.name)
        self.config = self.folder / "config.json"
        self.backup = self.folder / "config.json.bak"
        self.original = {"monitor_folder": "D:/Original", "recursive": True, "task_timeout": 180, "version": "1.3.1"}
        self.updated = {**self.original, "monitor_folder": "D:/Updated", "version": "1.3.2"}

    def tearDown(self):
        for handler in list(logging.getLogger("excel_auto_pdf").handlers):
            logging.getLogger("excel_auto_pdf").removeHandler(handler); handler.close()
        self.temp.cleanup()

    def test_first_save_creates_valid_primary_and_backup(self):
        config_store.save(self.config, self.original)
        self.assertEqual(config_store.load(self.config), self.original)
        self.assertEqual(json.loads(self.backup.read_text(encoding="utf-8")), self.original)

    def test_atomic_replace_failure_preserves_old_primary(self):
        config_store.save(self.config, self.original)
        before = self.config.read_bytes()
        replace = os.replace
        def fail_primary(source, target):
            if Path(target) == self.config: raise OSError("simulated disk error")
            return replace(source, target)
        with patch.object(config_store.os, "replace", side_effect=fail_primary):
            with self.assertRaises(OSError): config_store.save(self.config, self.updated)
        self.assertEqual(self.config.read_bytes(), before)
        self.assertEqual(json.loads(self.backup.read_text(encoding="utf-8")), self.original)
        self.assertFalse(list(self.folder.glob("*.tmp")))

    def test_backup_failure_does_not_write_primary(self):
        config_store.save(self.config, self.original)
        before = self.config.read_bytes()
        with patch.object(config_store.os, "replace", side_effect=PermissionError("backup locked")):
            with self.assertRaises(PermissionError): config_store.save(self.config, self.updated)
        self.assertEqual(self.config.read_bytes(), before)

    def test_corrupt_primary_restores_backup_and_preserves_bad_bytes(self):
        config_store.save(self.config, self.original)
        self.config.write_bytes(b'{"broken":')
        warnings = []
        self.assertEqual(config_store.load(self.config, warnings.append), self.original)
        self.assertTrue(any("恢复" in text for text in warnings))
        bad = list(self.folder.glob("config.json.bad-*"))
        self.assertEqual(len(bad), 1); self.assertEqual(bad[0].read_bytes(), b'{"broken":')
        config_store.save(self.config, self.updated)
        self.assertEqual(json.loads(self.backup.read_text(encoding="utf-8")), self.original)

    def test_missing_primary_uses_backup(self):
        config_store.save(self.config, self.original); self.config.unlink()
        self.assertEqual(config_store.load(self.config), self.original)

    def test_no_valid_config_returns_empty_with_notice(self):
        self.config.write_text("[]", encoding="utf-8"); self.backup.write_bytes(b"invalid")
        warnings = []
        self.assertEqual(config_store.load(self.config, warnings.append), {})
        self.assertTrue(warnings)

    def test_invalid_field_types_do_not_silently_enable_options(self):
        config_store.save(self.config, self.original)
        for invalid in ({"recursive":"false"},{"task_timeout":True},{"task_timeout":99999},{"monitor_folder":123}):
            self.config.write_text(json.dumps(invalid), encoding="utf-8")
            self.assertEqual(config_store.load(self.config), self.original)

    def test_legacy_and_unknown_fields_are_preserved(self):
        self.config.write_text(json.dumps({"monitor_folder":"D:/Legacy", "future_option":42}),encoding="utf-8")
        config_store.save(self.config, self.updated)
        self.assertEqual(config_store.load(self.config)["future_option"],42)

    def test_failed_read_does_not_replace_unknown_primary(self):
        config_store.save(self.config,self.original)
        with patch.object(config_store,"_read",side_effect=PermissionError("access denied")):
            with self.assertRaises(PermissionError): config_store.save(self.config,self.updated)
        self.assertEqual(config_store.load(self.config),self.original)

    def monitor(self):
        logger = logging.getLogger("dedup-isolated"); logger.addHandler(logging.NullHandler()); logger.propagate=False
        return FolderMonitor(self.folder,logger=logger,stable_checks=1,stable_interval=.01,max_retries=1)

    def run_conversion(self, monitor, source):
        def export(snapshot, target, *args):
            target.write_bytes(b"test-pdf-"+hashlib.sha256(snapshot.read_bytes()).digest())
            return target
        with patch.object(monitor,"_export_with_timeout",side_effect=export) as call, \
             patch.object(monitor._stop_event,"wait",return_value=False):
            result=monitor._convert_with_retries(str(source).casefold(),source)
        return result,call.call_count

    def test_same_content_new_timestamp_does_not_reconvert(self):
        source=self.folder/"part.xlsx";source.write_bytes(b"AAAA")
        m=self.monitor();self.assertEqual(self.run_conversion(m,source),(True,1))
        stat=source.stat();os.utime(source,ns=(stat.st_atime_ns,stat.st_mtime_ns+1000000))
        self.assertEqual(self.run_conversion(m,source),(False,0))

    def test_different_content_identical_size_and_timestamp_is_converted(self):
        source=self.folder/"part.xlsx";source.write_bytes(b"AAAA")
        m=self.monitor();self.assertEqual(self.run_conversion(m,source),(True,1))
        stat=source.stat();source.write_bytes(b"BBBB");os.utime(source,ns=(stat.st_atime_ns,stat.st_mtime_ns))
        self.assertEqual(self.run_conversion(m,source),(True,1))
        self.assertIn(hashlib.sha256(b"BBBB").digest(),source.with_suffix(".pdf").read_bytes())

    def test_missing_pdf_is_regenerated_for_unchanged_source(self):
        source=self.folder/"part.xlsx";source.write_bytes(b"AAAA")
        m=self.monitor();self.run_conversion(m,source);source.with_suffix(".pdf").unlink()
        self.assertEqual(self.run_conversion(m,source),(True,1))

    def test_failed_export_does_not_enter_content_cache(self):
        source=self.folder/"part.xlsx";source.write_bytes(b"AAAA")
        m=self.monitor()
        with patch.object(m,"_export_with_timeout",side_effect=RuntimeError("export failed")), \
             patch.object(m._stop_event,"wait",return_value=False):
            self.assertFalse(m._convert_with_retries(str(source).casefold(),source))
        self.assertEqual(len(m._last_converted),0)

    def test_source_changes_during_copy_retries_snapshot(self):
        import time
        source=self.folder/"part.xlsx";source.write_bytes(b"AAAA")
        destination=self.folder/"snap";destination.mkdir()
        deadline=time.monotonic()+6
        signature,snapshot=run_worker(change_during_copy,
            (str(source),str(destination),deadline,.01,1),deadline)
        self.assertEqual(Path(snapshot).read_bytes(),b"BBBB")
        self.assertEqual(signature[2],hashlib.sha256(b"BBBB").hexdigest())

    def test_utf8_log_rotation_and_retention(self):
        logfile=self.folder/"run.log"
        with patch.object(app_logging,"LOG_MAX_BYTES",400),patch.object(app_logging,"LOG_BACKUP_COUNT",2):
            logger,_=app_logging.base_logger(logfile)
            for n in range(35):logger.info("中文转换日志 %s %s",n,"样本"*25)
            logger.info("FINAL-MARKER")
            for h in logger.handlers:h.flush()
        paths=list(self.folder.glob("run.log*"))
        self.assertLessEqual(len(paths),3);self.assertGreater(len(paths),1)
        for p in paths:p.read_text(encoding="utf-8")
        self.assertIn("FINAL-MARKER",logfile.read_text(encoding="utf-8"))

    def test_existing_log_appends_and_reconfigure_closes_old_handle(self):
        logfile=self.folder/"run.log";logfile.write_text("PREVIOUS LOG\n",encoding="utf-8")
        first,_=app_logging.base_logger(logfile);first.info("FIRST")
        old=first.handlers[0]
        second,_=app_logging.base_logger(logfile);second.info("SECOND")
        self.assertIsNone(old.stream);self.assertEqual(len(second.handlers),1)
        self.assertIn("PREVIOUS LOG",logfile.read_text(encoding="utf-8"))


if __name__=="__main__":
    multiprocessing.freeze_support()
    unittest.main(verbosity=2)
