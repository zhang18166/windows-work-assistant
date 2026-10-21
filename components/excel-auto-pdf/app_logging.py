"""图形界面与命令行共用的 UTF-8 滚动文件日志。"""
from pathlib import Path
import logging
from logging.handlers import RotatingFileHandler
import re

LOG_MAX_BYTES = 5 * 1024 * 1024
LOG_BACKUP_COUNT = 5


def recent_log_records(path: Path, limit: int = 200) -> list[str]:
    """有界读取当前日志尾部；按记录分组，避免把异常堆栈倒序拆散。"""
    try:
        with path.open("rb") as stream:
            stream.seek(0, 2)
            start = max(0, stream.tell() - 256 * 1024)
            stream.seek(start)
            data = stream.read()
        if start:
            data = data.partition(b"\n")[2]
        records = []
        for line in data.decode("utf-8", errors="replace").splitlines():
            if re.match(r"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} \|", line):
                records.append(line)
            elif records:
                records[-1] += "\n" + line
        return records[-limit:]
    except OSError:
        return []


def base_logger(path: Path) -> tuple[logging.Logger, logging.Formatter]:
    logger = logging.getLogger("excel_auto_pdf")
    logger.setLevel(logging.INFO)
    logger.propagate = False
    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        handler.close()
    path.parent.mkdir(parents=True, exist_ok=True)
    formatter = logging.Formatter("%(asctime)s | %(levelname)s | %(message)s", datefmt="%Y-%m-%d %H:%M:%S")
    file_handler = RotatingFileHandler(path, maxBytes=LOG_MAX_BYTES, backupCount=LOG_BACKUP_COUNT, encoding="utf-8")
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)
    return logger, formatter
