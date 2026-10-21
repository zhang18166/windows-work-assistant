"""为整合版构建控制台协议引擎；主机重定向其管道，不创建用户控制台。"""
from pathlib import Path
import PyInstaller.__main__

ROOT=Path(__file__).resolve().parents[1]
if __name__ == "__main__":
    PyInstaller.__main__.run(["--noconfirm","--clean","--onefile","--console",
        "--name","ExcelPdfEngine","--paths",str(ROOT),
        "--distpath",str(ROOT/"dist/engine"),"--workpath",str(ROOT/"build/engine"),
        "--specpath",str(ROOT),str(ROOT/"headless_service.py")])
