# 工单结果与展开详情（第一步）

归属：第三层证据视图与第四层详情展示。参见 [四层文档索引](README.md)。

2026-11-06。本文记录第一步的工单结果与显示。第二步异常回报已接通，见 [异常通知闭环](ticket_notifications.md)；设置页与仅主 Agent 模式已接入，见 [第四层人机交互](layer4_interaction.md) 和 [主模式](main_agent_mode.md)；全量本地化尚未完成。

## 两个维度

调用状态由宿主账本与原有真值判据确定；任务结果由执行者（Worker 或仅主 Agent）通过结构化字段报告，不能代替宿主真值验证。

`sp_api_finish` 新增 `outcome=fulfilled|partial|blocked|needs_input`。blocked/needs_input 必须提供具体 `reason`，可以在未调用业务接口时结束；fulfilled/partial 仍需有调用证据。只读取态势不能当成攻击/机动任务已落实。未知写入和失败回执优先产生 uncertain/failed，不能被 Worker 的成功声明覆盖。

正常执行的通用任务维持 executed、verification=execution_only、resultVerified=false，同时记录 taskOutcome。blocked/needs_input 为新的终态，不占用待执行队列，但原生 Worker 未退出前仍占执行槽。速度工单继续由两次新鲜真值样本判定 completed。

旧调用方未传 outcome 时保持兼容，记录 unreported；界面显示“调用已记录 · 结果未报告”，不从自由文本猜 fulfilled 或 blocked，也不改写旧账本。旧 SH-2 工单显示五次读取、未记录游戏写入，以及“未下达攻击”的原始回报。

## 界面与证据

卡片显示标题、结果、优先原因摘要、已记录命令/读取数量、Watch 边界摘要。点击“显示详情”在右栏展开；滚轮和 PREV/NEXT 翻阅详情页，“收起详情”返回原列表页。完整任务说明和 Worker 回报可以读完，不再把固定技术前缀作为唯一可见摘要。

详情含任务目标、期望、调用状态、独立验证标志、已提交命令及参数、未确认写入、读取列表、Watch/速度判据证据、工单和对局标识。已提交命令仅来自成功的宿主写入回执；校验调用及明确 executed=false 的返回不列为已提交命令。接受命令仍不等于起飞、发射或摧毁已确认。

Watch 展示本任务已有的 create/get/events 回执，包括过期、字段不可读、对象消失、世界变化等原始边界信息；创建成功不等于命中。当前阶段不会为了显示详情新增 Watch 轮询或游戏操作。速度判据显示最近观测和连续样本数。详情中的摘要是 Worker 报告，实际命令列表是宿主账本，两者明确区分。

`ticket_presentation.py` 是只读视图生成模块，部署在 modules/tickets；按工单修订及账本文件变化缓存。账本不可读时显示证据未知，不把读失败描述为没有下令。私有 token/authorization/password 等参数不输出到视图。

## 部署与验收

使用现有 build.ps1 -Deploy；新增模块在 deployment/modules.json 中。DSH 的 helper schema 在插件启动时加载，需同步插件并重启空闲 Harness。`/health` 的 taskResultProtocol=1 表示新插件启动；旧运行中的插件仍可按旧 schema 结束，但界面会明确显示结果未报告。

验证包含结构化受阻/澄清、旧记录兼容、宿主证据不能被摘要伪造、未知写入优先、只读任务正常结束、Watch 边界、跨局过滤与缓存刷新。真实 Codex 隔离验证：一次只读编成查询后以 needs_input 结束，Worker 退出，没有游戏写入。游戏内详情布局须另做实际验收，不以编译代替。


### 本机验收记录（2026-11-06）

81 项 Python 回归通过（工单 63、Codex 18），DSH 工具/插件回归通过，18 个模块部署清单和兼容入口检查通过。实际运行的 DSH 插件 revision 为 v7-results，taskResultProtocol=1。Bridge 构建标记 20261106T033343Z，staleDll=false。

真实 Codex 隔离及 mcptest 场景验证 needs_input：一次只读编成调用后明确要求目标航迹号，没有游戏写入，原生 Worker 退出。另一验收任务因测试参数不兼容被正确标为 blocked 并显示原因；修正为 mode=state、op=gt 后，成功注册 ttl_s=0.1 的 Velocity Watch，随后只读一次事件，得到 expired、reason=deadline reached、evidence=false、hits=0，没有等待或轮询。

游戏内实际点击显示详情、收起、PREV/NEXT，并滚轮翻页；验证了三页和六页长详情、宿主命令参数、无写入说明和 Watch 过期/非命中证据。测试仅执行编成读取和短期 Watch 注册，没有下达单位、武器、起飞、绘图或时间命令。首次验收误触隐藏了一张只读测试工单，补建后重复点击验收通过；没有改写历史工单或重放战术任务。

第二步现已实现由调度器通知原主会话转述异常，通知轮次由宿主限制为仅文字回复，详见 [异常通知闭环](ticket_notifications.md)。上方第一步验收记录保留为对应阶段的事实。


### 工单语言资源

按钮、状态、统计、Watch 和详情固定标题已接入中英资源；调度器读取 Bridge 已解析的 uiLanguage，并在语言或资源文件变化时更新呈现缓存。原始摘要、原因、命令与回执不翻译。格式、部署保留规则和实测见 [语言文件](localization.md)。
