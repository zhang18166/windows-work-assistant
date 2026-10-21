"""第一阶段：使用 Microsoft Excel COM 将单个工作簿导出为同名 PDF。"""

from __future__ import annotations

import argparse
import gc
import sys
from collections.abc import Callable
from pathlib import Path

import pythoncom
import win32api
import win32con
import win32com.client
import win32event
import win32process

from excel_job import assign_excel
from pdf_output import SUPPORTED_EXTENSIONS, check_output_conflict, publish_pdf, staged_pdf_path, validate_pdf


XL_TYPE_PDF = 0
MSO_AUTOMATION_SECURITY_FORCE_DISABLE = 3
EXCEL_EXIT_TIMEOUT_MS = 5_000


def export_excel_to_pdf(
    source_path: Path,
    output_path: Path | None = None,
    excel_pid_callback: Callable[[int], None] | None = None,
    job_name: str | None = None,
) -> Path:
    """独立 Excel 完成导出、退出和校验后，才原子替换正式 PDF。"""
    source_path = source_path.expanduser().resolve()
    target = output_path.expanduser().resolve() if output_path is not None else source_path.with_suffix(".pdf")
    check_output_conflict(source_path)
    staged = staged_pdf_path(target)
    try:
        _export_excel_to_path(source_path, staged, excel_pid_callback, job_name)
        validate_pdf(staged)
        publish_pdf(staged, target, source_path)
        return target
    finally:
        staged.unlink(missing_ok=True)


def _export_excel_to_path(
    source_path: Path,
    output_path: Path,
    excel_pid_callback: Callable[[int], None] | None = None,
    job_name: str | None = None,
) -> Path:
    """通过后台独立 Excel 实例，把整个工作簿导出为同名 PDF。"""
    source_path = source_path.expanduser().resolve()

    if not source_path.is_file():
        raise FileNotFoundError(f"Excel 文件不存在：{source_path}")
    if source_path.suffix.lower() not in SUPPORTED_EXTENSIONS:
        raise ValueError("仅支持 .xlsx、.xls 和 .xlsm 文件")
    if source_path.name.startswith("~$"):
        raise ValueError("不能转换 Excel 临时文件")

    pdf_path = (
        output_path.expanduser().resolve()
        if output_path is not None
        else source_path.with_suffix(".pdf")
    )
    excel = None
    workbook = None
    process_handle = None

    pythoncom.CoInitialize()
    try:
        # DispatchEx 创建独立 Excel 进程，避免影响用户已打开的 Excel。
        excel = win32com.client.DispatchEx("Excel.Application")
        _, excel_pid = win32process.GetWindowThreadProcessId(excel.Hwnd)
        # 预先取得进程句柄，既能等待正常退出，也避免误伤同名 Excel 进程。
        process_handle = win32api.OpenProcess(
            win32con.SYNCHRONIZE | win32con.PROCESS_TERMINATE | win32con.PROCESS_SET_QUOTA,
            False,
            excel_pid,
        )
        if job_name is not None:
            assign_excel(job_name, process_handle)
        if excel_pid_callback is not None:
            excel_pid_callback(excel_pid)
        excel.Visible = False
        excel.DisplayAlerts = False
        excel.AskToUpdateLinks = False
        excel.EnableEvents = False
        excel.AutomationSecurity = MSO_AUTOMATION_SECURITY_FORCE_DISABLE

        # 只读打开，不更新外部链接，也不把文件加入 Excel 最近列表。
        workbook = excel.Workbooks.Open(
            str(source_path),
            UpdateLinks=0,
            ReadOnly=True,
            IgnoreReadOnlyRecommended=True,
            Notify=False,
            AddToMru=False,
        )
        # 对工作簿调用导出，多个工作表会合并到同一个 PDF。
        workbook.ExportAsFixedFormat(
            Type=XL_TYPE_PDF,
            Filename=str(pdf_path),
            Quality=0,
            IncludeDocProperties=True,
            IgnorePrintAreas=False,
            OpenAfterPublish=False,
        )
        return pdf_path
    finally:
        if workbook is not None:
            try:
                workbook.Close(SaveChanges=False)
            except Exception:
                pass
        if excel is not None:
            try:
                excel.Quit()
            except Exception:
                pass
        # 显式释放 COM 包装对象，给 Excel 正常结束进程的机会。
        del workbook
        del excel
        gc.collect()
        pythoncom.CoUninitialize()

        if process_handle is not None:
            try:
                wait_result = win32event.WaitForSingleObject(
                    process_handle, EXCEL_EXIT_TIMEOUT_MS
                )
                if wait_result == win32con.WAIT_TIMEOUT:
                    # 仅终止本次 DispatchEx 创建的独立实例，不影响其他 Excel。
                    win32api.TerminateProcess(process_handle, 0)
                    win32event.WaitForSingleObject(process_handle, 1_000)
            finally:
                process_handle.Close()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="通过本机 Microsoft Excel 将一个工作簿导出为同名 PDF。"
    )
    parser.add_argument("excel_file", type=Path, help="要转换的 Excel 文件路径")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        pdf_path = export_excel_to_pdf(args.excel_file)
    except Exception as exc:
        print(f"转换失败：{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1

    print(f"转换成功：{pdf_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
