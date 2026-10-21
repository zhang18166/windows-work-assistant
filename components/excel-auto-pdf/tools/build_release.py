"""从源码中的版本号生成带版本资源的单文件 EXE。"""
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import excel_auto_pdf_gui as gui
import PyInstaller.__main__


def main():
    version = gui.APP_VERSION
    numbers = tuple(int(x) for x in version.split(".")) + (0,)
    name = f"Excel自动转PDFv{version}"
    image = gui.create_tray_image()
    image.save(ROOT / "app_icon.png")
    image.save(ROOT / "app_icon.ico", sizes=[(16,16),(32,32),(48,48),(64,64)])
    resource = f'''VSVersionInfo(
 ffi=FixedFileInfo(filevers={numbers!r}, prodvers={numbers!r}, mask=0x3f, flags=0, OS=0x40004, fileType=1, subtype=0, date=(0,0)),
 kids=[StringFileInfo([StringTable('080404B0',[
 StringStruct('FileDescription','Excel自动转PDF'), StringStruct('FileVersion','{version}.0'),
 StringStruct('ProductName','Excel自动转PDF'), StringStruct('ProductVersion','{version}'),
 StringStruct('OriginalFilename','{name}.exe')])]), VarFileInfo([VarStruct('Translation',[2052,1200])])])'''
    (ROOT / "version_info.txt").write_text(resource, encoding="utf-8")
    PyInstaller.__main__.run(["--noconfirm","--clean","--onefile","--windowed","--name",name,
        "--icon",str(ROOT / "app_icon.ico"),"--version-file",str(ROOT / "version_info.txt"),
        "--add-data",str(ROOT / "app_icon.png") + ";.","--paths",str(ROOT),
        "--distpath",str(ROOT / "dist" / ("v" + version)),
        "--workpath",str(ROOT / "build" / ("v" + version)),"--specpath",str(ROOT),
        str(ROOT / "excel_auto_pdf_gui.py")])


if __name__ == "__main__": main()
