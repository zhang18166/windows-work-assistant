"""打包 EXE 验收：隔离用户数据，仅处理生成的测试文件；失败恢复旧版。"""
from __future__ import annotations
import json
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
import win32api
import win32con
import win32event
import win32job
import win32process
from excel_job import ExcelJob
from pypdf import PdfReader
from excel_auto_pdf_gui import APP_VERSION


def wait_for(fn, seconds=90):
    deadline=time.monotonic()+seconds
    while time.monotonic()<deadline:
        if fn(): return
        time.sleep(.2)
    raise TimeoutError("打包 EXE 验收超时")


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--fixtures',type=Path,required=True)
    parser.add_argument('--rollback-exe',type=Path,required=True)
    parser.add_argument('--quick',action='store_true')
    args=parser.parse_args()
    exe=ROOT/f'dist/v{APP_VERSION}/Excel自动转PDFv{APP_VERSION}.exe' 
    run=ROOT/'tests/runs'/('package-'+time.strftime('%Y%m%d-%H%M%S'))
    watched=run/'watched'; watched.mkdir(parents=True); (watched/'sub').mkdir()
    roaming=run/'Roaming'; local=run/'Local'
    config_dir=roaming/'Excel自动转PDF'; config_dir.mkdir(parents=True)
    config={'monitor_folder':str(watched),'recursive':True,'task_timeout':45,'version':APP_VERSION}
    (config_dir/'config.json.bak').write_text(json.dumps(config,ensure_ascii=False),encoding='utf-8')
    (config_dir/'config.json').write_bytes(b'{broken-primary')
    env=dict(os.environ,APPDATA=str(roaming),LOCALAPPDATA=str(local))
    job=ExcelJob(); hp=ht=None
    try:
        hp,ht,pid,tid=win32process.CreateProcess(str(exe),f'"{exe}" --silent',None,None,False,
            win32con.CREATE_SUSPENDED | win32con.CREATE_UNICODE_ENVIRONMENT,env,str(exe.parent),win32process.STARTUPINFO())
        win32job.AssignProcessToJobObject(job.handle,hp)
        win32process.ResumeThread(ht)
        ht.Close();ht=None
        logfile=local/'Excel自动转PDF/excel_auto_pdf.log'
        wait_for(lambda: logfile.exists() and '监控已启动' in logfile.read_text(encoding='utf-8'),25)
        print('PACKAGED_MONITOR_STARTED',pid,flush=True)
        fixtures=args.fixtures
        if args.quick:
            shutil.copy2(fixtures/'modern.xlsx',watched/'final-check.xlsx')
            output=watched/'final-check.pdf'
            wait_for(lambda:output.exists())
            assert len(PdfReader(output,strict=True).pages)==2
            assert '已从上一份有效备份恢复配置' in logfile.read_text(encoding='utf-8')
            (run/'result.json').write_text(json.dumps({'passed':True,'quick':True,'binary':str(exe),
                'sha256':hashlib.sha256(exe.read_bytes()).hexdigest(),'final_ui_import':True,
                'config_recovery':True,'real_excel_export':True},indent=2),encoding='utf-8')
            print('FINAL_PACKAGE_CHECK_PASS',run,flush=True)
            return
        for name in ('modern.xlsx','legacy.xls','macro.xlsm'):
            shutil.copy2(fixtures/name,watched/name)
        shutil.copy2(fixtures/'modern.xlsx',watched/'sub/nested.xlsx')
        outputs=[watched/'modern.pdf',watched/'legacy.pdf',watched/'macro.pdf',watched/'sub/nested.pdf']
        wait_for(lambda:all(p.exists() for p in outputs))
        for path in outputs:
            reader=PdfReader(path,strict=True)
            assert len(reader.pages)==2
            assert ('v'+APP_VERSION+' TEST 2') in reader.pages[1].extract_text()
        previous=outputs[0].stat().st_mtime_ns
        offset=len(logfile.read_text(encoding='utf-8'))
        (watched/'modern.xlsx').touch()
        wait_for(lambda:'源文件=modern.xlsx | 结果=内容相同，跳过转换' in logfile.read_text(encoding='utf-8')[offset:])
        assert outputs[0].stat().st_mtime_ns==previous
        shutil.copy2(fixtures/'modern-update.xlsx',watched/'modern.xlsx')
        wait_for(lambda:outputs[0].stat().st_mtime_ns!=previous)
        assert '99' in PdfReader(outputs[0]).pages[0].extract_text()
        outputs[0].unlink()
        (watched/'modern.xlsx').touch()
        wait_for(lambda:outputs[0].exists())
        assert '99' in PdfReader(outputs[0]).pages[0].extract_text()
        assert '已从上一份有效备份恢复配置' in logfile.read_text(encoding='utf-8')
        assert list(config_dir.glob('config.json.bad-*'))
        assert json.loads((config_dir/'config.json').read_text(encoding='utf-8'))['monitor_folder']==str(watched)
        old=outputs[0].read_bytes()
        shutil.copy2(fixtures/'legacy.xls',watched/'modern.xls')
        wait_for(lambda:'来源冲突，未覆盖 PDF' in logfile.read_text(encoding='utf-8'))
        assert outputs[0].read_bytes()==old
        # 等待转换任务结束，测试实例无真实用户状态，无需保存；关闭专用 Job 清理实例树。
        time.sleep(2)
        assert win32event.WaitForSingleObject(hp,0)==win32con.WAIT_TIMEOUT
        (run/'result.json').write_text(json.dumps({'passed':True,'binary':str(exe),'sha256':hashlib.sha256(exe.read_bytes()).hexdigest(),
            'formats':['xls','xlsx','xlsm'],'pages':2,'recursive':True,'overwrite':True,'content_dedup':True,'missing_pdf_regenerated':True,'config_recovery':True,'conflict_preserves_pdf':True},indent=2),encoding='utf-8')
        print('PACKAGE_SMOKE_PASS',run,flush=True)
    except BaseException:
        job.close()
        subprocess.Popen([str(args.rollback_exe),'--silent'])
        raise
    finally:
        job.close()
        if ht is not None: ht.Close()
        if hp is not None: hp.Close()


if __name__=='__main__': main()
