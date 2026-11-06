# 第四层 · 人机交互层

[文档索引](README.md) · [第三层：Harness 调度](layer3_workflow.md) · [设置详情](settings_page.md)

第四层把玩家输入、任务状态和 Agent 回复放到可操作的游戏界面：负责控制台、通联、工单详情、停止/销毁入口、Settings、菜单键位、本地化与表达。它不派 Worker，不绕过 HOLD，不把接受命令解释为战果完成。

## 模块与部署

| 模块或资源 | 职责 |
|---|---|
| src/ConsoleHost.cs | 单壳控制台、Inspector / Comms / Settings 三页、窗口布局与菜单键 |
| src/CommsWindow.cs | 目标选择、头像、输入/发送、通话记录、任务卡片、详情分页、STOP / DESTROY |
| src/InspectorWindow.cs、src/InspectorBridge.cs | 诊断/决策记录展示及对应界面入口 |
| src/SettingsWindow.cs、src/PlayerSettings.cs | 设置草稿、录制键位、显式保存/重新读取和保留未知配置 |
| src/Strings.cs、language_cn / language_en | 已接入控件的中英静态文本、语言选择与回退 |
| portrait/、persona.md | 头像资源及可编辑海军通联表达约束；persona 由第三层隐藏注入 |

游戏 UI 依赖 Unity/Noesis，因此当前与第一层同处 SeaPowerAgentBridge.dll。分层是职责划分，不移动 DLL、不新增前端服务。通联和工单协议由既有 Bridge 提供，第三层仍是状态权威。

## 玩家通联

默认 N 打开/关闭控制台，实际菜单键可在 Settings 录制并保存。输入消息时，聊天输入优先于菜单键。Target 选择发送和显示的 DSH/Codex 通道；Auto 头像分别使用 deepseek/gpt。queued 表示进入本地出站队列，不代表 Harness 已接收，更不代表任务已完成。

游戏只显示玩家原文和可见回复；onceopen、persona、异常通知注入及私有凭证不作为玩家发言展示。回复风格保留 Harness persona，面向玩家简短、自然，避免无故使用工单、Worker、回执等内部词汇。技术调查时可解释必要细节。编辑 <MOD_ROOT>/persona.md 后，下一次输入由第三层重新读取，无需为修改风格重启游戏。

## 工单卡片、详情与控制

卡片显示标题、状态、摘要、已记录命令/读取数量和 Watch 边界。显示详情展开任务目标、执行者完整回报、实际命令参数、未确认写入和已有判据证据；采用分页避免长回复被单行摘要替代。详见 [工单结果与详情](ticket_results_ui.md)。

- STOP：停止所选通道当前主轮次。仅主 Agent 模式会据执行证据收束其活动任务；默认 Worker 模式停止主回复不等于撤销已派出的任务。
- DESTROY：向第三层请求隐藏并取消指定任务，停止其对应 Worker 或主执行轮次。取消之前已经提交的游戏操作不能撤回。
- executed 是调用已记录，completed 必须有支持的真值证据；Watch 创建成功或执行者说“完成”都不能替代宿主判定。

显示和展开详情不会额外发送作战命令，也不会为了画卡片注册或轮询新的 Watch。

## Settings

| 选项 | 配置 | 生效范围 |
|---|---|---|
| 菜单打开/关闭键 | [AgentInspector] ToggleKey | 保存后立即更新；默认 N，支持字母、数字、F1–F12；Esc 取消录制 |
| 界面语言 | [AgentInspector] Language | 跟随游戏 / en / zh；保存后更新已接入静态标签 |
| 精简 MCP 回复 | [MCP] CompactReplies | 第二层后续支持投影的查询 |
| 截图保存原始分辨率 | [AgentInspector] ShotSaveFull | 后续截图保存；传给模型的图像仍按现有缩小策略 |
| 仅使用主 Agent 操作 | [Tickets] MainAgentOnly | 默认 false；只影响保存后新建任务的执行者 |

点击选项只改草稿；保存才写入，放弃修改/重新读取则丢弃草稿。小窗口按页显示。设置是跨对局玩家偏好，区别于每局独立聊天。主模式可能改善执行效果，同时可能增加思考耗时。完整保存语义及检查见 [Settings 页面](settings_page.md)。

## 本地化与验证边界

已有静态标签走 Strings 中英表和既有资源路径；模型回复、单位名、错误代码及原始证据不自动翻译。全面本地化仍未全部完成，不能把设置中能切换语言写成所有控件均已翻译。

2026-11-06 已实测主模式开关开启/保存、关闭/保存和重新读取；两端主任务均显示实际编成回复，STOP 按钮中断生成后 idle，账本停止增长。本轮销毁通过既有 /tickets 协议验证，没有把它写成重新完成 DESTROY 按钮点击验收。设置最终恢复为本轮开始前原样。键位录制、小窗口分页和详情展开的历史实测见相应专题，不随文档整理虚增验证范围。
