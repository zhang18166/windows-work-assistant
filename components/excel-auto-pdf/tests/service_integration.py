"""隔离协议与真实 Excel 验证；只处理 tests/runs 中生成的样本。"""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import sys
import threading
import time

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT))
from integration_excel import make_workbooks
from pypdf import PdfReader


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--engine',type=Path)
    args=parser.parse_args()
    run=ROOT/'tests/runs'/('service-'+time.strftime('%Y%m%d-%H%M%S'))
    watched=run/'中文监控目录';watched.mkdir(parents=True)
    fixtures=run/'fixtures';fixtures.mkdir()
    make_workbooks(fixtures)
    # 启动前已有文件不应被主动扫描转换。
    shutil.copy2(fixtures/'modern.xlsx',watched/'已存在.xlsx')
    configdir=run/'Roaming/Excel自动转PDF';configdir.mkdir(parents=True)
    config={'monitor_folder':str(watched),'recursive':True,'task_timeout':45}
    (configdir/'config.json').write_text(json.dumps(config,ensure_ascii=False),encoding='utf-8')
    command=[str(args.engine.resolve())] if args.engine else [sys.executable,'-u',str(ROOT/'headless_service.py')]
    stderr=(run/'stderr.log').open('w',encoding='utf-8')
    process=subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=stderr,
        text=True,encoding='utf-8',env=dict(os.environ,APPDATA=str(run/'Roaming'),LOCALAPPDATA=str(run/'Local'),PYTHONIOENCODING='utf-8'))
    messages=queue.Queue();all_messages=[]
    def read():
        for line in process.stdout:
            try: messages.put(json.loads(line))
            except Exception: messages.put({'event':'bad_protocol','line':line})
    threading.Thread(target=read,daemon=True).start()
    def wait_for(predicate,seconds=60):
        deadline=time.monotonic()+seconds
        while time.monotonic()<deadline:
            if any(predicate(m) for m in all_messages):return
            try: all_messages.append(messages.get(timeout=.2))
            except queue.Empty:
                if process.poll() is not None:raise RuntimeError('服务退出，请查看 '+str(run/'stderr.log'))
        raise TimeoutError(str(all_messages[-5:]))
    first_request=True
    def send(command,**values):
        nonlocal first_request
        if first_request:
            process.stdin.write('\ufeff')
            first_request=False
        process.stdin.write(json.dumps(dict(command=command,**values),ensure_ascii=False)+'\n');process.stdin.flush()
    try:
        wait_for(lambda m:m['event']=='ready')
        send('start',id='start')
        wait_for(lambda m:m['event']=='reply' and m.get('id')=='start' and m.get('ok'))
        assert not (watched/'已存在.pdf').exists()
        send('set_config',id='blocked',config=config)
        wait_for(lambda m:m['event']=='reply' and m.get('id')=='blocked' and not m.get('ok'))
        for name in ('modern.xlsx','legacy.xls','macro.xlsm'):shutil.copy2(fixtures/name,watched/name)
        wait_for(lambda m:m['event']=='log' and 'PDF=macro.pdf' in m.get('line',''),90)
        for name in ('modern','legacy','macro'):assert len(PdfReader(watched/(name+'.pdf')).pages)==2
        send('stop',id='stop');wait_for(lambda m:m['event']=='reply' and m.get('id')=='stop')
        send('set_config',id='invalid',config=dict(config,task_timeout=1))
        wait_for(lambda m:m['event']=='reply' and m.get('id')=='invalid' and not m.get('ok'))
        assert json.loads((configdir/'config.json').read_text(encoding='utf-8'))['task_timeout']==45
        send('set_config',id='save',config=dict(config,task_timeout=60))
        wait_for(lambda m:m['event']=='reply' and m.get('id')=='save' and m.get('ok'))
        send('start',id='restart');wait_for(lambda m:m['event']=='reply' and m.get('id')=='restart')
        # EOF 也是正常退出入口，验证父进程离开不会继续监控。
        process.stdin.close();assert process.wait(timeout=15)==0
        assert not (watched/'已存在.pdf').exists()
        assert not any(m['event']=='bad_protocol' for m in all_messages)
        (run/'result.json').write_text(json.dumps({'passed':True,'formats':['xls','xlsx','xlsm'],
            'two_pages':True,'no_initial_scan':True,'reject_config_while_running':True,
            'invalid_config_preserves_previous':True,'unicode_pipe':True,'restart':True,'eof_cleanup':True},indent=2),encoding='utf-8')
        print('SERVICE_INTEGRATION_PASS',run,flush=True)
    finally:
        if process.poll() is None:
            try:send('quit')
            except OSError:pass
            try:process.wait(timeout=15)
            except subprocess.TimeoutExpired:process.kill()


if __name__=='__main__':main()
