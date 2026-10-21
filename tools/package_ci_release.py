"""仅使用本次 Git 提交和已构建 EXE 生成 Release 包；不收集本机用户数据。"""
from pathlib import Path
import argparse
import hashlib
import json
import shutil
import subprocess
import zipfile

ROOT=Path(__file__).resolve().parents[1]


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--binary',type=Path,required=True)
    parser.add_argument('--version',required=True)
    parser.add_argument('--name',required=True)
    args=parser.parse_args()
    output=ROOT/'release-assets';output.mkdir(exist_ok=True)
    stage=output/'package';stage.mkdir(exist_ok=True)
    shutil.copy2(args.binary,stage/args.binary.name)
    for name in ('README.md','LICENSE','CHANGELOG.md','THIRD_PARTY_NOTICES.md'):
        if (ROOT/name).exists():shutil.copy2(ROOT/name,stage/name)
    if (ROOT/'THIRD_PARTY_LICENSES').exists():
        shutil.copytree(ROOT/'THIRD_PARTY_LICENSES',stage/'THIRD_PARTY_LICENSES',dirs_exist_ok=True)
    commit=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip()
    (stage/'BUILD_INFO.json').write_text(json.dumps({'version':args.version,'commit':commit,
        'binary_sha256':hashlib.sha256(args.binary.read_bytes()).hexdigest()},indent=2),encoding='utf-8')
    binary_zip=output/(args.name+'-v'+args.version+'-win-x64.zip')
    with zipfile.ZipFile(binary_zip,'w',zipfile.ZIP_DEFLATED) as archive:
        for path in stage.rglob('*'):
            if path.is_file():archive.write(path,path.relative_to(stage).as_posix())
    source_zip=output/(args.name+'-v'+args.version+'-source.zip')
    subprocess.run(['git','archive','--format=zip','--prefix='+args.name+'-'+args.version+'/',
        '--output='+str(source_zip),'HEAD'],cwd=ROOT,check=True)
    (output/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(path.read_bytes()).hexdigest()+'  '+path.name+'\n'
        for path in (binary_zip,source_zip)),encoding='utf-8')
    print('RELEASE_PACKAGE_READY',output)


if __name__=='__main__':main()
