# 第二层 · 转译层

[文档索引](README.md) · [第一层：接口](layer1_bridge.md) · [第三层：Harness 调度](layer3_workflow.md)

第二层由 [`mcp_server.py`](../mcp_server.py) 实现。它接收 MCP 工具调用，把参数转成第一层的 HTTP 请求，再把 Bridge 回执转换为 MCP 工具结果。

```text
MCP 客户端 → JSON-RPC / stdio → mcp_server.py → 本机 HTTP → 游戏桥接
游戏桥接   → 事件/状态读取 → Harness 调度层 → 人机交互层显示
```

Agent 负责提出工单及观察条件、接收提醒。第一层独立采样并产生事件，上层工单运行时负责关联证据、推进状态和到期询问玩家。已登记的规则由游戏进程继续执行，期间无需 Agent 调用工具或推理。当前第二层是开发和集成用的直连工具面，包含原始字段读取、游戏写入和 `sp_raw`。正式 Agent 可用能力由上层控制。

## 启动和部署

从游戏根目录运行，MCP 客户端也可以用同一脚本的绝对路径配置 stdio 服务。

```powershell
python .\tools\spmcp_v2\mcp_server.py
```

脚本使用 Python 标准库。MCP 消息是逐行 UTF-8 JSON-RPC，当前协议版本为 `2024-11-05`。本机 HTTP 请求绕过系统代理。`build.ps1 -Deploy` 将脚本和桥接 DLL 一起复制到 `Sea Power_Data\StreamingAssets\dsh-spmcp`。

| 环境变量 | 默认 | 用途 |
|---|---:|---|
| `SPMCP_PORT` | `19080` | 客户端连接的 Bridge 端口 |
| `SPMCP_TIMEOUT` | `25` 秒 | HTTP 基础预算，路由可以提高下限 |
| `SPMCP_SHOT_TIMEOUT` | `15` 秒 | 截图等待预算 |

## 工具与回执

`TOOLS` 是工具 schema 和 handler 的注册表。`tools/list` 返回工具定义，`tools/call` 按名称分派。工具定义以运行中的 `tools/list` 为准。单位命令、编组命令和绘图都有各自的路由映射。截图成功时返回 MCP 图片块和元数据。

调用方应保留 Bridge 的 `error.code`、`error.retryable` 和转译层补充的 `http_status`、`uncertain`、`resend`。写请求失去回执、遇到不安全的 5xx 或编组部分执行时，先核实实际状态，再决定是否提交新命令。连接被拒绝会标记 `sent:false`。普通路由预算至少 25 秒，编组至少 30 秒，慢速绘图至少 50 秒。

## 回包压缩与航迹简报

第二层支持两条只读路径。用 `sp_tracks_brief` 做常规态势更新，发现值得追查的变化时，再用 `sp_tracks` 或其他只读工具核对证据。攻击前应核对当前航迹、对象 ID 和目标解析结果。

| 路径 | 行为 |
|---|---|
| `sp_tracks`、`sp_session` | `CompactReplies=true` 时，经 [compact_replies.py](../compact_replies.py) 投影并用紧凑 JSON 返回。航迹仍是完整的当前列表，保留航迹 ID、对象 ID、阵营、识别和位置。会话快照省略历史和标记列表。 |
| `sp_deck` | 保留甲板容量、起降状态、在舰机型、数量和挂载。省略支持机型目录及重复的在舰数量字段。发射参数从 `airgroupOnBoard[].fileName` 和挂载 `name` 取值。 |
| `sp_session_fleet` | 保留世代、单位 ID、类别、位置、运动和命令状态。省略固定说明及所有行重复的己方归属标记。 |
| `sp_ammo` | 弹药表已是 ID 到数量的映射。保留全部条目和数量，使用紧凑 JSON。 |
| `sp_tracks_brief` | 独立工具。每个 MCP 进程第一次读取当前战局时建立基准，后续仅输出变化。读航迹前后核对世代，切换战局时重建基准。规则保护已知对象 ID 的重映射，送入 `mustReport`。其余候选由 [Tiny MLP](../track_briefs.py) 排序，普通正文预算为 800 UTF-8 字节，并返回省略条数。 |

配置位于与 MCP 脚本同目录的 `setting.ini`。部署槽为 `Sea Power_Data/StreamingAssets/dsh-spmcp`。运行中的进程会在查询时重读配置。

```ini
[MCP]
CompactReplies=true

[TrackMotion]
SurfaceFormationNm=2
AirFormationNm=4
SurfaceHighSpeedKts=30
```

`CompactReplies=false` 或配置缺失时，原有工具返回完整格式。该开关控制字段投影，`sp_tracks_brief` 仍可独立调用。命令及错误回执走原有路径。部署的 Python 端使用标准库，MLP 权重随模块一起复制。

[track_motion.py](../track_motion.py) 从至少三个有效的玩家侧实测位置估算运动。Unknown 水面接触满足疑似编队或持续单独高速时，标为 `suspicious=true`。Unknown 空中接触按疑似编队标记。`false` 也包括证据不足。宽泛候选名单与排列不参与判定。可疑标记是复核线索，阵营与身份仍按游戏观测记录。

### 字段投影

字段投影在 Python 层拿到 Bridge 回包后执行，按工具固定字段规则处理单份响应。它减少 MCP 返回给模型的字段和 JSON 空白，Bridge 到 Python 的 HTTP 回包大小仍取决于原路由。投影是无状态的，既有字段按原值保留，缺失字段不补默认值，显式 `null` 保留。失败回包、`data.redacted=true` 和无法识别的结构直接透传。要恢复完整回包，可在同目录的 `setting.ini` 设 `CompactReplies=false`，再调用对应读取工具。

`sp_tracks` 的投影保留每条航迹及其 `trackId`、`objectId`、阵营和分类、可用性和新鲜度、坐标及观测来源。候选条目仅保留 `classRef` 和 `objectType`，并保留航迹上的候选总数及截断标志。候选列表缩减和 JSON 紧凑序列化是两项独立的体积优化。字段规则的实际定义见 [compact_replies.py](../compact_replies.py)。

### 航迹简报与 Tiny MLP

`sp_tracks_brief` 的路径为「确认任务世代 → 获取玩家航迹 → 再次确认世代 → 运动标注 → 航迹投影与差分 → 排序与预算裁剪」。它在内部使用航迹投影，即使关闭普通回包的 `CompactReplies` 也仍按这条路径执行。每个 MCP 进程按 `trackId` 维护已见集合和差分基准。首次或世代变化时返回 `baseline`，后续返回 `delta`。常规时间戳和检测年龄的变化不会单独触发更新，位置移动达到 0.1 海里或坐标可用性发生变化时进入差分。航迹离开快照仅标为 `removed_from_snapshot`。候选多于一条或候选列表被截断时，简报将识别视为未解决。

[部署权重](../track_mlp_weights.json) 对应返回的模型标识 `bdnetwork_11_4_mlp_run_01_motion`。推理由 Python 标准库完成，结构为 17 个布尔输入、8 个 `tanh` 隐藏单元和一个线性排序分数。权重首次用到时加载，并校验特征顺序及层维度。

| 输入组 | 内容 |
|---|---|
| 阵营与类别 | Enemy、Unknown、航空、水面、陆地、潜艇。 |
| 变化 | 新增或重现、离开快照、身份相关字段变更、`live` 变更、`fresh` 变更、位置变更。 |
| 观测与识别 | 已识别、识别未解决、实测位置来源、`suspicious`。另保留 `own_sensor` 特征位，当前简报固定为 false。 |

MLP 给普通候选排序。已知 `objectId` 变化由规则直接送入 `mustReport`，独立于排序和普通正文的 800 UTF-8 字节预算。普通候选按分数从高到低尝试装入 `brief`，超过剩余预算的条目跳过，`omitted` 返回本轮省略数。差分基准随成功构建更新，预算省略的条目不会留到下一轮补发。需要完整快照、简报无法识别输入，或权重文件缺失或不兼容时，调用 `sp_tracks`。当前简报没有备用排序器。

训练在 `tools/bdnetwork/train_mlp_11_4.py` 离线完成，按战局拆分，使用人工标注与 Codex 弱标签构造候选间的成对偏好，人工偏好赋予更高权重。该轮留出集使用 Codex 弱标签。模型分数用于选择回包内容，接触身份、阵营和交战依据来自游戏观测与后续核对。

### 回放测量

使用部署版 MCP 回包路径和 `o200k_base` 计数。合计结果覆盖两份实录的所有路由，按采集器各路由都独立交给模型计算。

| 实录 | 原始正文 token | 字段投影 | 字段投影与航迹简报 | 总降幅 |
|---|---:|---:|---:|---:|
| 500 批航迹 | 10,805,138 | 2,234,334 | 1,093,640 | 89.9% |
| 164 批航迹 | 10,438,009 | 2,375,170 | 1,553,759 | 85.1% |
| 合计 | 21,243,147 | 4,609,504 | 2,647,399 | 87.5% |

500 批长回放仅看航迹时，简报正文比原始回包减少 98.8%。两份回放的本地简报处理 P95 为 0.45 和 0.56 ms。实时 `sp_tracks_brief` 的两次会话核对只增加 Bridge 往返，回执不进入模型正文。上表未测在线额度、缓存收益或实时延迟，也未隔离 MLP 排序本身的战术收益。


## Watch 登记与事件读回

| MCP 工具 | 数据路径 |
|---|---|
| `sp_watch_create` | `POST /watch`，返回字符串 `watch_id`、`world_generation` 和 `event_cursor` |
| `sp_watch_list` | `GET /watch` |
| `sp_watch_get` | `GET /watch/{watch_id}` |
| `sp_watch_delete` | `DELETE /watch/{watch_id}` |
| `sp_watch_events` | 只读扫描本机部署目录的 `comms/outbox.jsonl` |

创建时可指定 `mode=state` 或 `mode=edge`。状态模式支持 `eq`、`ne`、`gt`、`lt`、`exists`，边沿模式按变化触发。查询与撤销使用 Bridge 返回的字符串 ID，例如 `w1`。

1. 登记 Watch，保存 `watch_id`、`world_generation`、`event_cursor`。游标取在发出登记请求之前，涵盖登记时已经成立的即时命中。
2. 调用 `sp_watch_events`，传入游标及可选的 `watch_id`、`world_generation`。后续沿用返回的 `cursor`。不同过滤条件各自维护游标。省略游标会从当前文件末尾开始。
3. `more=true` 时继续读取，按 `event_key` 跨轮去重。只将 `evidence=true` 的事件用作命中证据。收到 `outbox_reset` 时重新核对活跃规则并初始化游标。

这一层提供主动轮询。工单状态推进和问题通知由[第三层 Harness 调度层](layer3_workflow.md)负责；通用 Watch 事件驱动的自动结单仍未接通。Watch 规则由游戏进程独立采样。采集器的常规 Watch 轮询开关与此路径独立。


## 其他接口差异

当前 Bridge 要求发射舰载机时提供 `loadout`，但 `sp_launch_aircraft` 的 schema 仍称可省略并使用默认挂载。调用方应从甲板信息选择并传入挂载。旧工具描述里的情报遮蔽说明也需要与当前 Bridge 策略同步。
