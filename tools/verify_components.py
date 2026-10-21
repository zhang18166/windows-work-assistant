"""离线校验组件源码快照；文本统一 LF，兼容 Git 在 Windows 检出 CRLF。"""
from pathlib import Path
import hashlib
import json

ROOT=Path(__file__).resolve().parents[1]


def main():
    lock=json.loads((ROOT/'components.lock.json').read_text(encoding='utf-8'))
    for name,component in lock['components'].items():
        rows=[]
        directory=(ROOT/'components'/name).resolve()
        for relative,expected in sorted(component['files'].items()):
            path=(directory/relative).resolve()
            if directory not in path.parents:raise ValueError('组件路径越界')
            data=path.read_bytes()
            if b'\0' not in data:data=data.replace(b'\r\n',b'\n')
            actual=hashlib.sha256(data).hexdigest()
            if actual!=expected:raise ValueError('组件快照与锁定版本不符：'+name+'/'+relative)
            rows.append(relative+'\0'+actual+'\n')
        tree=hashlib.sha256(''.join(rows).encode('utf-8')).hexdigest()
        if tree!=component['tree_sha256']:raise ValueError('组件树校验失败：'+name)
        print('COMPONENT_VERIFIED',name,component['commit'])


if __name__=='__main__':main()
