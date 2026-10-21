# Excel 自动转 PDF / Excel Auto PDF

Watch a local folder and export changed Excel workbooks to PDF using desktop Microsoft Excel.

只需要保存 Excel 后自动生成 PDF，使用这个独立版即可。当前版本 **1.3.4**。

[下载独立版](https://github.com/zhang18166/excel-auto-pdf/releases) · [自动输入法助手](https://github.com/zhang18166/auto-ime-switcher) · [两者整合版](https://github.com/zhang18166/windows-work-assistant)

## 功能与行为

- 监控指定文件夹，支持所有层级子文件夹及 `.xls`、`.xlsx`、`.xlsm`，忽略 `~$` 临时文件。
- 文件稳定后复制只读临时快照，用独立 Excel 实例导出同名 PDF；**成功后覆盖同名 PDF**，失败保留旧结果。
- 不执行工作簿宏，不更新外部链接，不保存源工作簿。不同扩展名但同名 Excel 来源冲突时跳过，不互相覆盖。
- SHA-256 内容去重、事件合并、重试、单文件超时、剩余任务及粗略预计时间。
- 使用 Windows Job 管理自己创建的 Excel；停止时等待清理，不按 `EXCEL.EXE` 名称结束用户实例。
- 配置原子保存并保留有效备份；UTF-8 日志每份约 5 MiB，保留 5 份历史。
- 主窗口日志固定显示、最新在上、保留时间；详细设置单独打开。支持系统托盘与静默开机监控。
- **启动时不扫描并批量转换已有文件**。开始监控后保存/修改文件才会触发转换，内容去重缓存在本次监控中有效。

## 使用

需要 Windows 10/11 x64 和本机安装的 **Microsoft Excel 桌面版**。只装 WPS、浏览器版 Office 或 PDF 阅读器不能替代 Excel COM。

下载 Releases ZIP 解压运行 `Excel自动转PDFv1.3.4.exe`，点击“设置…”选择文件夹，配置子文件夹及超时，再开始监控。
关窗口/最小化会收进托盘，退出使用“退出程序”。勾选开机启动后用 `--silent` 读取上次目录并监控。

配置：`%APPDATA%\Excel自动转PDF\config.json`；备份：`.bak`；损坏副本：`.bad-*`。
日志：`%LOCALAPPDATA%\Excel自动转PDF\excel_auto_pdf.log`。启动项：当前用户 Run 下的 `ExcelAutoPDF`。
现有其他版本的配置可直接读取，升级不会主动改换监控目录。

## 从源码构建

Python 3.12 x64，建议独立虚拟环境：

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
.venv\Scripts\python.exe tools/build_release.py
```

输出：`dist/v1.3.4/Excel自动转PDFv1.3.4.exe`。

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -p "test_*.py"
.venv\Scripts\python.exe tests/test_ui.py
# 以下需要本机 Excel，仅生成/转换 tests/runs 内的样本
.venv\Scripts\python.exe tests/integration_excel.py
.venv\Scripts\python.exe tests/service_integration.py
```

CI 跑不依赖 Office 的回归和 UI 布局检查；实机 Office 验证在本地进行。

## 源码结构

| 文件 | 责任 |
|---|---|
| `excel_auto_pdf_gui.py` / `ui_layout.py` | 独立 GUI、托盘、状态与日志 |
| `phase2_folder_monitor.py` | 事件合并、稳定检测、串行调度与重试 |
| `worker_tasks.py` / `excel_job.py` | 受截止时间控制的工作进程及专用 Excel 清理 |
| `phase1_excel_to_pdf.py` / `pdf_output.py` | Excel COM 导出、验证与原子发布 |
| `config_store.py` / `app_logging.py` | 原子配置、有界日志 |
| `headless_service.py` | 整合版 JSON Lines 本机管道接口，不创建第二套 GUI |

生成协议引擎：`python tools/build_engine.py`。详见 [协议说明](docs/PROTOCOL.md)。
代码采用 [MIT License](LICENSE)，打包依赖的许可证随 Release 一起提供。Microsoft Excel 需自行安装和授权。
