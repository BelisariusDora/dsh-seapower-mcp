# DeepSeek Harness 接入

需要 Node.js 24+、已启动的本地 DeepSeek Harness 和当前 Windows 用户的 DSH 配置。默认连接 127.0.0.1:19387。不同端口用 --port 指定；DSH 配置目录不同则设置 DSH_HOME。发送器使用本机 DSH 凭据。

在本 README 所在的 DSH 目录打开 PowerShell。先只读检查连接：

    node .\harness_link.js --out ..\..\comms --probe

首次安装时，开一个终端运行发送器。把工作区替换成你的 DSH 工作区：

    node .\harness_link.js --out ..\..\comms --new --cwd "D:\MyWorkspace"

游戏里把 Target 选为 DSH。第一条游戏消息会创建 DSH 会话并写入 comms/run/session.pointer。之后重启发送器时省略 --new，复用这个会话。已有由发送器管理的指针时可以直接用复用模式。

第一条消息写出指针后，在另一个终端运行回复读取器：

    node .\dsh_tailer.js --out ..\..\comms\run --strict-pin

发送器读取 comms/outbox.jsonl。回复读取器按 session.pointer 跟随 DSH 会话，将可见回复写入游戏读取的 comms/run/live.jsonl。发送故障先看 comms/run/link.log 和 link.status.json。同一个 outbox 只运行一个发送器，同一个 run 目录只运行一个回复读取器。
