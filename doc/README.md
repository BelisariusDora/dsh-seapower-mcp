# Sea Power MCP 项目文档

本项目按职责划分为四层。源码和部署目录沿用现有结构，不因文档分层新增进程、框架或通信协议。当前状态以各层说明和实测范围为准；TR 工单文档同时保留历史过程。

| 层 | 主要职责 | 文档 |
|---|---|---|
| 第一层：接口层 | 游戏内状态读取、命令提交、主线程访问、HOLD 与原生 Watch | [接口层](layer1_bridge.md) |
| 第二层：转译层 | MCP 工具/schema、HTTP 参数与回执转译、字段投影、航迹简报和 MLP、图像及 Watch 事件读取 | [转译层](layer2_translation.md) |
| 第三层：Harness 调度层 | 本局主会话、DSH/Codex Adapter、工单生命周期、执行者选择、调用账本、真值判定与异常通知 | [Harness 调度层](layer3_workflow.md) |
| 第四层：人机交互层 | 游戏控制台、通联、工单详情、STOP/DESTROY 入口、Settings、键位、本地化与 persona 表达 | [人机交互层](layer4_interaction.md) |

## 阅读顺序与依赖

部署先读 [部署指南](DEPLOY.md) 和 [模块目录](deployment_layout.md)；开发先读对应层，再读相邻层的输入输出边界。

```text
玩家 ↔ 第四层：控制台 / 通联 / 设置
              ↕ 玩家输入、结果显示、停止请求
       第三层：主 Agent / 工单调度 / 执行 Adapter
              ↕ MCP 工具、受控调用、结果证据
       第二层：MCP ↔ HTTP 转译 / 字段投影
              ↕ 本机 HTTP 请求与回执
       第一层：Bridge ↔ Sea Power 游戏对象
```

上图是主要业务路径，并非强制逐层网络转发：第三层调度器也直接读取第一层真值和工单协议；第四层通过现有通联/工单协议提交控制意图，并读取第三层快照。第一层和第四层的 C# 代码共用 SeaPowerAgentBridge.dll；第二层和第三层复用既有 Python handler，不复制游戏 API。

## 专题归属

| 专题 | 归属及说明 |
|---|---|
| [工单运行时与 TR 历史](ticket_runtime_plan.md) | 第三层；历史里程碑不等于当前全部能力 |
| [仅主 Agent 模式](main_agent_mode.md) | 第三层执行模式；第四层 Settings 提供默认关闭的开关 |
| [异常通知闭环](ticket_notifications.md) | 第三层决定投递、去重和权限；第四层显示主 Agent 的文字转述 |
| [工单结果与详情](ticket_results_ui.md) | 第三层生成账本视图；第四层卡片、展开和分页 |
| [Settings 与菜单键位](settings_page.md) | 第四层草稿、保存和语言；各配置消费者在对应层生效 |
| [模块化部署](deployment_layout.md) | 跨层物理目录与兼容入口 |

## 当前能力与验证边界

- DSH、Codex 均支持本局独立主会话；Worker 模式保留，开启仅主 Agent 后仍建单，由主会话同轮执行。
- 通用任务 executed 只表示接口执行有回执，不表示战果确认；航速专用判据由调度器采集两次新鲜真值后才 completed。
- Worker 问题结果可交回原主会话转述；通知轮次始终只能文字回复，不得追加操作。
- 2026-11-06 实机验收覆盖两端仅主 Agent 只读任务、游戏 STOP、经工单 HTTP 协议的销毁，以及关闭开关后的 DSH Worker/异常通知回归。本轮没有验证战术写入、切局或实际战果，详见 [主模式验收](main_agent_mode.md)。
- Watch 原生采样、事件读取和证据展示已存在；通用 Watch 自动结单未接通，不能把登记成功当成条件命中。

所有公开路径使用 <SOURCE_ROOT>、<MOD_ROOT>、<SEA_POWER_ROOT>、<PYTHON_EXE> 等占位符。tools/spmcp 为冻结的 legacy；当前维护 tools/spmcp_v2。不把 comms/run 下的凭证、会话或日志作为发行内容。
