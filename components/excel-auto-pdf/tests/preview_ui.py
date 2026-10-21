"""交互预览；F6 小窗口、F7 常规窗口，不启动监控或托盘。"""
import os
import sys
import tempfile
import logging
from pathlib import Path
from unittest.mock import patch
import tkinter as tk

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
data=tempfile.TemporaryDirectory(prefix="ExcelPDF-ui-preview-")
os.environ['APPDATA']=os.environ['LOCALAPPDATA']=data.name
import excel_auto_pdf_gui as gui
gui.enable_dpi_awareness()
root=tk.Tk()
with patch.object(gui.ExcelAutoPdfApp,'_start_tray'),patch.object(gui,'is_startup_enabled',return_value=True):
    app=gui.ExcelAutoPdfApp(root)
root.title('Excel自动转PDF v'+gui.APP_VERSION+' · 界面验收')
app.folder_var.set(str(Path.home()/'Documents'/'ExcelInbox'))
app._set_controls(running=True)
app.status_var.set('正在转换')
app.status_label.configure(fg=app.STATUS_COLORS['正在转换'])
app.status_detail_var.set('示例：专用加工面板—支座与长条—工艺尺寸及技术要求.xlsx')
app.progress_var.set('剩余任务：12  |  平均：8 秒  |  预计剩余：1 分 36 秒')
app._poll_messages()
app._append_log('2026-10-21 14:02:00 | INFO | 界面验收示例，未执行实际转换。')
app._append_log('2026-10-21 14:02:08 | INFO | 源文件=示例工作簿.xlsx | 结果=转换成功 | PDF=示例工作簿.pdf')
root.geometry('640x540')
root.bind('<F6>',lambda e:root.geometry('640x540'))
root.bind('<F7>',lambda e:root.geometry('860x760'))
root.protocol('WM_DELETE_WINDOW',root.destroy)
root.after(300000,root.destroy)
try:root.mainloop()
finally:
    logging.shutdown()
    data.cleanup()
