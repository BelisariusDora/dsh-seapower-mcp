# 架构图片来源

对应 Alpha 1.1 的逻辑架构，不表示每个模块都是独立进程。首页引用仓库内 PNG，不依赖在线渲染。

- `layer2-mcp.mmd` / `.png`：第二层 MCP 转译、字段投影、航迹简报与 Watch 事件读取。
- `layer3-harness.mmd` / `.png`：第三层会话、调度器、两种执行方式、调用证据与异常通知。
- `player-interaction.mmd` / `.png`：玩家输入到执行、结果、异常、STOP 与销毁的时序。

更新 `.mmd` 后在本目录运行 `python render.py`。脚本需要 Python 和 Pillow（`python -m pip install Pillow`），会将图表源码发送到公共 Kroki 服务生成 PNG，并铺白色背景保证 GitHub 深色主题可读；只适用于可公开的架构内容。渲染结果受服务端 Mermaid 版本影响，提交前检查中文、连线与裁切。

图中接口回执不等于战果确认；通用 Watch 自动结单尚未接通。可独立运行的 Worker 由 Adapter / 宿主持有。STOP 不等于销毁已派出的 Worker 工单，已提交的游戏命令不会因此撤回。
