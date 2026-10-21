"""PDF 发布规则：校验后原子替换，保留旧结果，拒绝同名来源冲突。"""
from __future__ import annotations

import os
import tempfile
from pathlib import Path

from pypdf import PdfReader

SUPPORTED_EXTENSIONS = {".xlsx", ".xls", ".xlsm"}


class OutputConflictError(ValueError):
    pass


def check_output_conflict(source: Path) -> None:
    """Windows 文件名不区分大小写；不同扩展名的相同主名不能共用 PDF。"""
    for sibling in source.parent.iterdir():
        if (sibling.suffix.lower() in SUPPORTED_EXTENSIONS
                and sibling.stem.casefold() == source.stem.casefold()
                and sibling.name.casefold() != source.name.casefold()
                and sibling.is_file()):
            raise OutputConflictError(
                f"同名来源冲突：{source.name} 与 {sibling.name} 会输出同一个 PDF；请重命名其中一个文件"
            )


def staged_pdf_path(target: Path) -> Path:
    fd, name = tempfile.mkstemp(prefix=".ExcelAutoPDF-", suffix=".pdf", dir=target.parent)
    os.close(fd)
    return Path(name)


def validate_pdf(path: Path) -> None:
    with path.open("rb") as stream:
        reader = PdfReader(stream, strict=True)
        if reader.is_encrypted or len(reader.pages) == 0:
            raise ValueError("导出的 PDF 已加密或没有可读取的页面")
        # 访问所有页面，检查页面树可读取；验证发生在受超时控制的转换子进程中。
        for page in reader.pages:
            _ = page.mediabox


def publish_pdf(staged: Path, target: Path, source: Path) -> None:
    check_output_conflict(source)
    # 同目录暂存保证同一文件系统；目标被占用时 replace 失败，旧文件保留。
    os.replace(staged, target)
