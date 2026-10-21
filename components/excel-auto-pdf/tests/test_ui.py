"""隔离 UI 验收：日志必须直接可见，真实勾选状态和时间记录可读。"""
from pathlib import Path
import os
import sys
import tempfile
import logging
import tkinter as tk
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
DATA = tempfile.TemporaryDirectory(prefix="ExcelPDF-layout-", ignore_cleanup_errors=True)
os.environ["APPDATA"] = DATA.name
os.environ["LOCALAPPDATA"] = DATA.name
import excel_auto_pdf_gui as gui
from app_logging import recent_log_records


def descendants(widget):
    for child in widget.winfo_children():
        if isinstance(child, tk.Toplevel): continue
        yield child
        yield from descendants(child)


def settle(root):
    for _ in range(5): root.update_idletasks(); root.update()


def check_bounds(window):
    left, top = window.winfo_rootx(), window.winfo_rooty()
    right, bottom = left+window.winfo_width(), top+window.winfo_height()
    for widget in descendants(window):
        if not widget.winfo_ismapped(): continue
        x,y = widget.winfo_rootx(),widget.winfo_rooty()
        assert x >= left-2 and x+widget.winfo_width() <= right+2, (widget, 'horizontal clipping')
        assert y >= top-2 and y+widget.winfo_height() <= bottom+2, (widget, 'vertical clipping')
        if isinstance(widget, tk.Label):
            assert widget.winfo_width()+2 >= widget.winfo_reqwidth(), (widget, 'label clipping')


def main():
    total = 0
    for scale in (1.0, 1.5, 2.0):
        root = tk.Tk(); root.withdraw(); root.tk.call('tk','scaling',scale*96/72)
        with patch.object(gui.ExcelAutoPdfApp,'_start_tray'), patch.object(gui,'is_startup_enabled',return_value=True):
            app = gui.ExcelAutoPdfApp(root)
        app._poll_messages()
        app.folder_var.set('D:/监控目录/机械加工/'+('很长的子目录/'*15))
        app.status_detail_var.set('非常长的工作簿名称-'+('加工尺寸和技术要求-'*14)+'.xlsx')
        app.progress_var.set('剩余任务：1234 | 平均：12 分 30 秒 | 预计剩余：123 小时 12 分')
        app._append_log('2026-10-21 14:01:02 | INFO | 结果=旧记录')
        app._append_log('2026-10-21 14:02:03 | INFO | 结果=最新转换成功 | '+('长文件名称'*60)+'.xlsx')
        for width,height in ((640,540),(860,700),(1100,900)):
            root.geometry(f'{width}x{height}'); root.deiconify(); settle(root)
            check_bounds(root)
            font_height = int(app.log_text.tk.call('font','metrics',app.log_text.cget('font'),'-linespace'))
            assert app.log_text.winfo_height() >= font_height*3+12, ('fewer than 3 visible log lines',scale,app.log_text.winfo_height())
            app._show_latest_log(); settle(root)
            assert app.log_text.dlineinfo('1.0') is not None, 'latest timestamp not visible'
            assert app.log_text.get('1.0','1.19') == '2026-10-21 14:02:03'
            assert app.last_log_var.get().endswith('14:02:03')
            print('UI_PASS',scale,width,height,'log_height',app.log_text.winfo_height(),flush=True)
            total += 1
        app._show_settings(); settle(root); check_bounds(app.settings_window)
        app.recursive_var.set(True)
        with patch.object(app,'save_current_config') as save:
            app.recursive_check.configure(command=app.save_current_config)
            app.recursive_check.invoke()
            assert not app.recursive_var.get(); save.assert_called_once()
        app._set_controls(running=True)
        before = app.recursive_var.get(); app.recursive_check.invoke()
        assert app.recursive_var.get() == before
        assert str(app.startup_check.cget('state')) != 'disabled'
        assert len(app._check_images) == 4
        for i in range(300): app._append_log(f'2026-10-21 15:00:00 | INFO | record {i}')
        app.log_text.yview_moveto(.5); settle(root)
        old = app.log_text.get('@0,0','@0,0 lineend')
        app._append_log('2026-10-21 15:01:00 | INFO | new while reading history')
        settle(root)
        assert old == app.log_text.get('@0,0','@0,0 lineend'), 'history jumped on new log'
        app._show_latest_log(); settle(root)
        assert app.log_text.get('1.0','1.end').endswith('new while reading history')
        for after_id in root.tk.call('after','info'): root.after_cancel(after_id)
        root.destroy()
    logfile = Path(DATA.name)/'history.log'
    logfile.write_text('2026-10-21 13:01:00 | ERROR | old\nTraceback preserved\n  frame 1\n2026-10-21 14:01:00 | INFO | new\n',encoding='utf-8')
    assert recent_log_records(logfile) == ['2026-10-21 13:01:00 | ERROR | old\nTraceback preserved\n  frame 1','2026-10-21 14:01:00 | INFO | new']
    assert len(recent_log_records(logfile,1)) == 1
    assert recent_log_records(logfile.with_suffix('.missing')) == []
    print('UI_LAYOUT_TOTAL',total,'CHECK_STATE_AND_LOG_TESTS_PASS',flush=True)
    logging.shutdown(); DATA.cleanup()


if __name__ == '__main__': main()
