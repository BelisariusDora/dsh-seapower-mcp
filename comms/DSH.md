# DeepSeek Harness 接入

需要 Node.js 24+ 和已启动的本地 DSH，默认地址为 127.0.0.1:19387。发送器使用本机用户的 DSH 配置。必要时用 --port、--bridge-port 或 DSH_HOME 指定实际配置。

在模组的 harness/DSH 目录打开两个 PowerShell 窗口，分别运行：

    node .\harness_link.js --out ..\..\comms --cwd "<DSH_WORKSPACE>" --game-world
    node .\dsh_tailer.js --out ..\..\comms\run --game-world

将 <DSH_WORKSPACE> 替换为 DSH 工作区绝对路径。进入对局后选择 Target: DSH。每局新建聊天，切局会请求停止旧游戏聊天。首条玩家消息附带 prompts/onceopen.md，面板显示原始玩家消息。

用 --probe 做只读连接诊断。故障时查看 comms/run/link.log 和 link.status.json。同一出站队列和回传目录各运行一个进程。
