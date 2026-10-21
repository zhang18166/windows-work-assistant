# 自动输入法助手 / Auto IME Switcher

Windows foreground-aware English input switching, with verified state and a compact indicator.

只需要指定程序进入前台时自动切英文，使用这个独立版即可。当前版本 **1.0.13**。

[下载独立版](https://github.com/zhang18166/auto-ime-switcher/releases) · [Excel 转 PDF](https://github.com/zhang18166/excel-auto-pdf) · [两者整合版](https://github.com/zhang18166/windows-work-assistant)

## 功能

- 按进程名匹配目标程序，可选“窗口标题包含”条件；支持从当前窗口选择、浏览 EXE、编辑与删除规则。
- 支持输入法开关、Shift、CapsLock 和键盘布局四种方式；动作后读取状态，不能确认时显示“未验证”，不把请求发送当作成功。
- 用户手动切换、按住修饰键、焦点/前台变化时撤销过期任务。可选择离开目标程序后恢复原中文状态。
- 迷你浮窗显示当前程序图标、EN/中文/CAPS、执行状态和最近动作；位置重置到右下角、置顶及鼠标穿透。
- **仅背景**透明度 0～100%，文字和程序图标保持清晰；托盘与当前用户开机启动。
- 不修改 Windows 剪贴板历史或跨设备同步，也不控制微信输入法的跨设备复制粘贴。它不负责解决第三方剪贴板占用冲突。

## 使用

Windows 10/11 x64，.NET Framework 4.8。下载 Releases ZIP，解压后运行 `AutoImeSwitcher.exe`。
首次默认给出 SolidWorks、Siemens NX 示例规则，可自行修改；以后读取已有规则。

双击托盘图标打开设置；关窗口仅隐藏到托盘。真正退出使用设置或托盘里的“退出”。
`AutoImeSwitcher.exe --settings` 可恢复当前实例的设置窗口。

配置和日志：`%APPDATA%\AutoImeSwitcher\config.json`、`run.log`。写入配置使用原子替换；损坏配置另存 `.bad-*` 后提示回退。
日志超过约 2 MiB 留一份历史。开机启动写当前用户 Run 项 `AutoImeSwitcher`，不需要管理员权限。

部分第三方输入法不公开可靠状态，程序会保守跳过或显示未验证。管理员程序可能需要同等权限才能收到切换请求。

## 构建与测试

无需 NuGet 包，使用 Windows 自带 Framework 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File tools/build-and-test.ps1
```

输出：`src/bin/Release/AutoImeSwitcher.exe`。48 项回归检查使用模拟平台，不注入真实按键、不读取用户配置。

`src/` 为实际程序，`tests/RegressionTests.cs` 为行为回归，`tools/` 为构建与 UI 验证脚本。
核心约束：`OperationEpoch` 撤销过期动作，`WorkerQueue` 串行执行，`ImeController` 复核真实输入状态。

## 整合接口

1.0.13 增加可选 `AppHostOptions`、`AppContext.GetSettingsForm()` 和 `SetModuleEnabled()`。
独立版行为保持不变；宿主可隐藏组件托盘、嵌入设置窗口，并只暂停本次模块而不覆盖用户规则。
测试专用 `AUTO_IME_DATA_DIR` 可隔离配置/日志；日常运行不用设置。

代码采用 [MIT License](LICENSE)。使用系统 Windows API 与 .NET Framework，不随源码分发第三方输入法或 CAD 软件。
