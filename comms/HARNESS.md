# Sea Power Harness 接入包

> Alpha 1.0：默认 Worker 模式沿用下述建单后结束/独立执行流程。Settings 开启“仅使用主 Agent 操作”后，仍建单，由创建工单的主会话同轮调用 sp_main_api_call/finish，不派 Worker；回执 executionMode 决定执行方式。STOP 同时收束该主轮次的活动任务，未知写入不重发。详见源码 doc/main_agent_mode.md，安装包 harness/MAIN_AGENT_MODE.md。早期 TR 状态属于历史记录。

从此目录选择对应的接入说明。模组安装好后，在游戏通信面板底部选择 Target。

- [DSH](DSH/README.md) · 游戏消息转发到 DeepSeek Harness，并把可见回复回传游戏。
- [Codex](Codex/README.md) · 每局独立的临时 app-server 会话，游戏内双向收发；不绑定桌面聊天。

两边共用模组的 comms 目录，但使用不同的出站队列。可同时运行两个接入器，各消费自己的队列。分发本目录即可提供脚本和配置模板。运行时请保持它位于模组根目录的 harness 位置。
