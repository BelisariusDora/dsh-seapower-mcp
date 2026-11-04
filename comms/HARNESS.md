# Sea Power Harness 接入包

从此目录选择对应的接入说明。模组安装好后，在游戏通信面板底部选择 Target。

- [DSH](DSH/README.md) · 游戏消息转发到 DeepSeek Harness，并把可见回复回传游戏。
- [Codex](Codex/README.md) · 游戏消息通过 Windows UI 发送到选定的 Codex 桌面聊天。

两边共用模组的 comms 目录，但使用不同的出站队列。可同时运行两个接入器，各消费自己的队列。分发本目录即可提供脚本和配置模板。运行时请保持它位于模组根目录的 harness 位置。
