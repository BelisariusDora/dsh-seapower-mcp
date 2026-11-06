# 仅主 Agent 模式

归属：第三层执行调度；设置入口属于第四层。参见 [四层文档索引](README.md)。

在游戏控制台 Settings / 设置中勾选“仅使用主 Agent 操作”并保存。
持久配置为 <MOD_ROOT>/setting.ini 的 [Tickets] MainAgentOnly=true，缺省 false。
DSH 与 Codex 均使用新建的本局主会话，不新增跨存档聊天记忆。

## 执行边界

- 调度器在建单时固定 executionMode=main/worker；切换设置只影响新工单，重复 requestId 不改变执行方式。
- worker 沿用原后台 Worker；main 直接进入 running，不创建子代理、不读取 Worker 执行上下文。
- 主 Agent 用 sp_api_find/schema 快速查签名，再用 sp_main_api_call 执行、sp_main_api_finish 收尾并回复玩家。宿主绑定创建会话、轮次和 worldKey；凭证不交给模型。
- HOLD、资源范围、调用预算、幂等调用 ID、结果不明禁止重发、真实回执账本均复用原实现。主模式也不能直接调用业务写接口、shell 或委派。
- 通用任务 executed 只证明 API 已执行；舰艇航速继续进入 monitoring，由调度器采集两次新鲜真值样本后才 completed。
- 主轮次正常结束但未收尾时标记 uncertain；STOP 时取消，若存在未确认调用则 uncertain；绝不自动转派或重试。DESTROY、截止或切局会阻止后续接口并请求中断对应主轮次。
- 主 Agent 已通过 finish 报告的问题不再额外投递一轮异常通知；异常结束而没有结构化报告的问题仍可走原通知闭环。seapower-notice 始终只能文字回复，主模式不放宽这一限制。

## 模块和部署

ticket_main.py 是小型主会话 Adapter；ticket_store.py 与 ticket_scheduler.py 决定执行方式，ticket_api.py 保留唯一业务调用管线。
comms/codex_chat.py、comms/dsh_ticket_plugin.mjs 负责当前轮次身份、工具边界和中断。DSH 限制仅作用于桥接绑定的游戏主会话，其他聊天和现有 Worker 不受影响。
src/PlayerSettings.cs 与 src/SettingsWindow.cs 管理保存和开关，中英标签在 src/Strings.cs。

退出游戏后，在 <SOURCE_ROOT> 执行 build.ps1 -Deploy。Python 模块按 deployment/modules.json 安装，主 Adapter 位于 <MOD_ROOT>/modules/tickets/ticket_main.py。
更新实际 DSH profile 中配置的插件入口及其同目录 dsh_ticket_notifications.mjs，再在空闲时重启对应 DSH Host；不要仅替换未配置的入口文件。
重启本项目拥有的 scheduler、Codex chat 和 DSH sender，保留原命令参数；不要停止其他项目进程。
部署保留 setting.ini 与 persona.md。新建配置或缺失键时保持 Worker 模式。

## 验收

test/test_main_agent.py 覆盖模式固定、幂等、跨轮次拒绝、预算、HOLD、销毁、停止、重启不重发，以及航速继续由真值监视。
test/settings_preferences.ps1 对真实 DLL 检查默认关闭、保存和旧配置消费者兼容。
DSH Adapter 需检查局部工具限制和 turn/end 清理；Codex 需检查主会话同轮调用助手且没有 Worker。
模型隔离验收和游戏内验收分别记录，不能把离线通过写成真实游戏完成。


## 实机验收（2026-11-06）

构建 20261106T064011Z，staleDll=false。Settings 实际点击开启并保存后，DSH 和 Codex 各在原主会话同轮创建 main 工单、查询一次己方编成并 finish；两次均 executed，readCount=1，workerHandle=null，没有游戏写入。
两端均通过游戏 STOP 按钮中断实际运行中的只读主任务，状态 cancelled、generation=idle，账本停止增长且没有迟到完成回复。两端通过原工单 HTTP 协议销毁活动任务，hidden=true、cancelled，主轮次停止；这项不等同于本轮重新验收 DESTROY 按钮点击。
关闭选项并保存后，DSH 正常回到 Worker 路径；一次查询后 needs_input，通过原通知闭环交回主 Agent 转述。最后设置重新读取为关闭，配置与本轮开始前逐字节一致，两端均 idle。
验收修正了一处本地进程启动参数：dsh_tailer.js 的 --out 必须是 <MOD_ROOT>/comms/run，指针和 live.jsonl 均位于此处；sender 的 --out 仍是 <MOD_ROOT>/comms。无需修改游戏 DLL。
本轮没有战术写入，未重新验证实际战果、切局或超时；这些边界的离线检查不能替代对应实机验证。首次 Codex 停止操作前任务已完成，不计为通过，随后重测成功。
