# 本机服务协议

`headless_service.py`/`ExcelPdfEngine.exe` 通过重定向的 stdin/stdout 传输 UTF-8 JSON Lines，无网络监听。
`stdout` 只写协议，文件日志仍写原路径；stderr 供诊断。父进程关闭 stdin（EOF）时正常停止监控并退出。
每条请求是一个 JSON 对象，可带 `id`；服务以 `event=reply` 回显 `id`、`ok` 及可选 `error`。

```json
{"id":"1","command":"set_config","config":{"monitor_folder":"C:\\Example\\ExcelInbox","recursive":true,"task_timeout":180}}
{"id":"2","command":"start"}
{"id":"3","command":"stop"}
{"id":"4","command":"quit"}
```

命令：`get_config`、`set_config`、`start`、`stop`、`quit`。监控中拒绝改配置；校验失败保留有效配置。
事件：`ready`（版本、配置、日志路径）、`config`、`status`、`progress`、`log`、`reply`。
`progress` 中 `remaining` 为任务数，`average` 和 `eta` 是秒，尚不能估计时为 null。
停止/退出的成功回复在清理完成后发出，不提前声称已停止。

启动只报告 ready，不自行开始监控。宿主应负责单实例、同独立版互斥及 Windows Job 清理。
请求限制为 64 KiB；不会把不支持的命令解释为 shell、脚本或程序路径。
