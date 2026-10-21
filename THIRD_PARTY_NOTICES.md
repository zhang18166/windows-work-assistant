# 第三方组件

本项目自身采用 MIT。发布包包含的 Python 及 Python 包仍遵守各自许可证；原文收录在 `THIRD_PARTY_LICENSES/`。
主要组件：CPython（PSF）、pywin32、watchdog、pypdf、Pillow、pystray，以及构建工具 PyInstaller（含其分发例外）。实际版本见 requirements.txt。

使用者可以从源码构建并替换第三方库：创建 Python 3.12 虚拟环境，安装/修改对应包，再运行 tools/build_release.py 或 tools/build_engine.py。
整合版重建引擎后运行 tools/build.ps1，嵌入修改后的引擎。项目没有限制用户替换这些组件或调试自行修改后的版本。

Microsoft Excel、Windows 和 .NET Framework 由用户/操作系统提供；不在源码仓库内分发。
