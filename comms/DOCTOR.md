# Harness 部署检查

在模组根目录运行随包附带的检查器。需要 Python 3.11+，可使用已有的 `$Python` 和 `$ModRoot` 绝对路径变量。

```powershell
# 先检查文件、提示词和本机入口
& $Python (Join-Path $ModRoot 'harness/doctor.py') --backend dsh

# 组件启动并载入游戏场景后，增加本机运行状态检查
& $Python (Join-Path $ModRoot 'harness/doctor.py') --backend dsh --live
```

`--backend` 可选 `mcp`、`dsh`、`codex`、`both`，默认 `both`。仅装一种后端时应显式选择。`--root` 可指定其他模组目录，默认从已部署的 `harness/doctor.py` 位置定位模组根目录。源码运行时需要传 `--root`。

`--live` 使用只读 GET 请求检查 Bridge 构建身份及所选 DSH 插件的 `/health`，并读取近期 scheduler、sender 或 Codex 状态文件。自定义端口通过 `--bridge-port`、`--dsh-port` 指定。DSH 的凭据从本机 `comms/run/tickets/auth.token` 读取，报告不输出凭据。先启动共享调度器再运行在线检查。

检查器会启动短生命周期 Python 子进程读取 MCP 工具目录与 CLI 帮助，DSH 模式还会检查当前 PATH 的 Node.js 版本。Codex 检查当前 PATH 能否找到 CLI。接入器、调度器若使用不同 PATH，需要核对它们实际使用的可执行文件。

| 结果 | 含义与处理 |
|---|---|
| `PASS` | 该项已获得对应证据，按本行说明理解其范围 |
| `FAIL` | 该项未通过，先修复缺文件、依赖、加载版本、绑定或运行状态 |
| `NOT_CHECKED` | 该项尚无证据，继续完成在线检查或人工验收 |

加 `--json` 可获取机器可读报告。进程退出码 `1` 表示存在 `FAIL`，`2` 表示仍有未验证项。参数错误也可能由命令行解析器返回 `2`，应同时阅读输出。当前检查器始终保留两项人工验收，因此不会自动返回“完整安装成功”。

人工验收必须在实际目标会话完成。先确认所需 `sp_*` 工具和对应模式的权限已注入，再从游戏发送一句普通招呼，以及“只查询一次己方编成并报告数量”的只读任务，核对真实回复、任务与 API 账本。普通聊天成功、health 成功或近期状态文件均不能单独替代该闭环。安装 Agent 应报告每个未验证项，完成这些步骤后再宣布接入完成。

近期状态文件只作为近期运行证据。检查器不会调用模型、创建工单、重载 Host 或修改游戏状态，也不会回答已挂起的选择题卡片。被卡片阻塞的旧轮次仍需在 Harness 界面回答或取消。
