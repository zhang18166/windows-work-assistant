"""仅在 tests/runs 内创建测试工作簿；不读取真实配置、不注册启动项。"""
from __future__ import annotations
import hashlib
import json
import logging
import multiprocessing
import shutil
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import pythoncom
import win32api
import win32con
import win32com.client
import win32process
from excel_job import ExcelJob, assign_excel
from phase2_folder_monitor import FolderMonitor
from pypdf import PdfReader
from excel_auto_pdf_gui import APP_VERSION


def make_workbooks(folder):
    pythoncom.CoInitialize()
    job = ExcelJob(); excel = None; book = None; process = None
    try:
        excel = win32com.client.DispatchEx("Excel.Application")
        pid = win32process.GetWindowThreadProcessId(excel.Hwnd)[1]
        process = win32api.OpenProcess(win32con.PROCESS_SET_QUOTA | win32con.PROCESS_TERMINATE,
                                      False, pid)
        assign_excel(job.name, process)
        excel.Visible = False; excel.DisplayAlerts = False; excel.AutomationSecurity = 3
        book = excel.Workbooks.Add()
        while book.Worksheets.Count > 2: book.Worksheets(book.Worksheets.Count).Delete()
        while book.Worksheets.Count < 2: book.Worksheets.Add(After=book.Worksheets(book.Worksheets.Count))
        for i in (1, 2):
            sheet = book.Worksheets(i); sheet.Name = "Test" + str(i)
            sheet.Range("A1:D1").Merge(); sheet.Range("A1").Value = "Excel PDF v" + APP_VERSION + " TEST " + str(i)
            sheet.Range("A1:D1").Font.Bold = True
            sheet.Range("A3").Value = "零件名称 / PART"
            sheet.Range("B3").Value = "测试支座"
            sheet.Range("A4").Value = "Quantity"
            sheet.Range("B4").Value = 12 * i
            sheet.Range("A5").Value = "Calculated"
            sheet.Range("B5").Formula = "=B4*2"
            sheet.Range("A1:D8").Font.Name = "Microsoft YaHei"
            sheet.Range("A1:D8").Font.Size = 14
            sheet.Columns("A:D").ColumnWidth = 22
            sheet.Rows("1:8").RowHeight = 28
            sheet.PageSetup.PrintArea = "$A$1:$D$8"
            sheet.PageSetup.Zoom = False
            sheet.PageSetup.FitToPagesWide = 1; sheet.PageSetup.FitToPagesTall = 1
        for name, fmt in (("modern.xlsx",51),("legacy.xls",56),("macro.xlsm",52)):
            book.SaveAs(str(folder / name), FileFormat=fmt)
        book.Worksheets(1).Range("B4").Value = 99
        book.SaveAs(str(folder / "modern-update.xlsx"), FileFormat=51)
    finally:
        if book is not None: book.Close(SaveChanges=False)
        if excel is not None: excel.Quit()
        book = excel = None
        if process is not None: process.Close()
        job.close(); pythoncom.CoUninitialize()


def wait_for(test, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if test(): return
        time.sleep(.1)
    raise AssertionError("等待测试条件超时")


def main():
    run = ROOT / "tests" / "runs" / time.strftime("%Y%m%d-%H%M%S")
    fixtures = run / "fixtures"; watched = run / "watched"
    fixtures.mkdir(parents=True); watched.mkdir(); (watched / "sub").mkdir()
    make_workbooks(fixtures)
    hashes = {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in fixtures.iterdir()}
    print("FIXTURES_READY", run, flush=True)
    logger = logging.getLogger("integration"); logger.setLevel(logging.INFO)
    logger.addHandler(logging.FileHandler(run / "integration.log", encoding="utf-8"))
    events = []
    def status(state, path):
        events.append((state, str(path)))
        print(state, path.name if path else "", flush=True)
    monitor = FolderMonitor(watched, logger=logger, status_callback=status, recursive=True,
                            debounce_seconds=.05, stable_interval=.05, stable_checks=2,
                            task_timeout=45, conversion_timeout=35, retry_delay=.1)
    # 已存在文件继续遵守原来的规则：不在启动时批量转换。
    existing = watched / "existing.xlsx"; shutil.copy2(fixtures / "modern.xlsx", existing)
    monitor.start()
    try:
        time.sleep(.5)
        assert not existing.with_suffix(".pdf").exists()
        for name in ("modern.xlsx","legacy.xls","macro.xlsm"):
            shutil.copy2(fixtures / name, watched / name)
        shutil.copy2(fixtures / "modern.xlsx", watched / "sub" / "nested.xlsx")
        outputs = [watched / "modern.pdf",watched / "legacy.pdf",watched / "macro.pdf",watched / "sub" / "nested.pdf"]
        wait_for(lambda: all(p.exists() for p in outputs))
        wait_for(lambda: not monitor._processing and not monitor._pending)
        for pdf in outputs:
            reader = PdfReader(pdf, strict=True)
            assert len(reader.pages) == 2
            assert ("v" + APP_VERSION + " TEST 1") in reader.pages[0].extract_text()
            assert ("v" + APP_VERSION + " TEST 2") in reader.pages[1].extract_text()
        before = outputs[0].stat().st_mtime_ns
        target_source = watched / "modern.xlsx"
        time.sleep(.1); target_source.touch()
        wait_for(lambda: not monitor._processing and not monitor._pending)
        assert outputs[0].stat().st_mtime_ns == before, "仅修改时间不应重新转换"
        shutil.copy2(fixtures / "modern-update.xlsx", target_source)
        wait_for(lambda: outputs[0].stat().st_mtime_ns != before)
        wait_for(lambda: not monitor._processing and not monitor._pending)
        assert len(PdfReader(outputs[0]).pages) == 2
        assert "99" in PdfReader(outputs[0]).pages[0].extract_text()
        # 文件名冲突时保留原来的 PDF，并记录失败。
        prior = outputs[0].read_bytes()
        shutil.copy2(fixtures / "legacy.xls", watched / "modern.xls")
        wait_for(lambda: any(s == "转换失败" and p.endswith("modern.xls") for s,p in events))
        assert outputs[0].read_bytes() == prior
    finally:
        while not monitor.stop(): time.sleep(.1)
    for name, expected in hashes.items():
        assert hashlib.sha256((fixtures / name).read_bytes()).hexdigest() == expected
    assert not list(watched.rglob(".ExcelAutoPDF-*.pdf"))
    report = {"passed":True,"formats":["xls","xlsx","xlsm"],"pages_per_workbook":2,
              "recursive_monitor":True,"existing_files_not_auto_converted":True,
              "overwrite_update":True,"content_dedup":True,"conflict_preserves_old_pdf":True,"fixture_hashes_unchanged":True}
    (run / "result.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
    print("INTEGRATION_PASS", run, flush=True)


if __name__ == "__main__":
    multiprocessing.freeze_support()
    main()
