"""原子配置写入、上一份有效备份和损坏恢复。"""
from __future__ import annotations

import json
import os
import tempfile
import threading
import time
import uuid
from pathlib import Path
from collections.abc import Callable

_LOCK = threading.RLock()
MAX_CONFIG_BYTES = 1024 * 1024


def _validate(data: object) -> dict:
    if not isinstance(data, dict):
        raise ValueError("配置必须是 JSON 对象")
    if "monitor_folder" in data and not isinstance(data["monitor_folder"], str):
        raise ValueError("监控目录格式错误")
    if "recursive" in data and type(data["recursive"]) is not bool:
        raise ValueError("子文件夹开关格式错误")
    if "task_timeout" in data:
        value = data["task_timeout"]
        if type(value) is not int or not 30 <= value <= 3600:
            raise ValueError("单文件超时必须在 30～3600 秒之间")
    if "version" in data and not isinstance(data["version"], str):
        raise ValueError("配置版本格式错误")
    return data


def _read(path: Path) -> dict:
    with path.open("rb") as stream:
        raw = stream.read(MAX_CONFIG_BYTES + 1)
    if len(raw) > MAX_CONFIG_BYTES:
        raise ValueError("配置文件过大")
    return _validate(json.loads(raw.decode("utf-8-sig")))


def _atomic_write(path: Path, data: dict) -> None:
    raw = json.dumps(_validate(data), ensure_ascii=False, indent=2).encode("utf-8")
    if len(raw) > MAX_CONFIG_BYTES:
        raise ValueError("配置文件过大")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix="." + path.name + "-", suffix=".tmp", dir=path.parent)
    temporary = Path(name)
    try:
        with os.fdopen(fd, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _preserve_bad(path: Path, warn: Callable[[str], None]) -> None:
    try:
        # 不移动原件；恢复后的下一次保存会原子替换原配置。
        bad = path.with_name(path.name + ".bad-" + time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6])
        with path.open("rb") as stream:
            raw = stream.read(MAX_CONFIG_BYTES + 1)
        if len(raw) <= MAX_CONFIG_BYTES:
            bad.write_bytes(raw)
    except OSError as exc:
        warn("损坏配置副本未能保存：" + str(exc))


def load(path: Path, warn: Callable[[str], None] = lambda message: None) -> dict:
    backup = path.with_name(path.name + ".bak")
    with _LOCK:
        try:
            return _read(path)
        except FileNotFoundError:
            primary_missing = True
        except (ValueError, UnicodeError) as exc:
            primary_missing = False
            warn("主配置损坏：" + str(exc))
            _preserve_bad(path, warn)
        except OSError as exc:
            primary_missing = False
            warn("主配置无法读取：" + str(exc))
        try:
            data = _read(backup)
        except FileNotFoundError:
            if not primary_missing:
                warn("没有可恢复的配置备份，请重新选择监控目录")
        except (ValueError, UnicodeError, OSError) as exc:
            warn("配置备份无法读取，请重新选择监控目录：" + str(exc))
        else:
            warn("已从上一份有效备份恢复配置，请核对监控目录")
            return data
        return {}


def save(path: Path, values: dict) -> None:
    _validate(values)
    backup = path.with_name(path.name + ".bak")
    with _LOCK:
        previous = None
        for candidate in (path, backup):
            try:
                previous = _read(candidate)
                break
            except FileNotFoundError:
                continue
            except (ValueError, UnicodeError):
                continue
        merged = dict(previous or {})
        merged.update(values)
        _validate(merged)
        # 只把验证有效的旧配置备份，不用损坏的主文件覆盖备份。
        if previous is not None:
            _atomic_write(backup, previous)
        else:
            _atomic_write(backup, merged)
        _atomic_write(path, merged)
