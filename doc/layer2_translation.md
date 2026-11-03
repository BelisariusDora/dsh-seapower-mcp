# 第二层 · MCP 转译

第二层由 [`mcp_server.py`](../mcp_server.py) 实现。它接收 MCP 工具调用，把参数转成第一层的 HTTP 请求，再把 Bridge 回执转换为 MCP 工具结果。

```text
MCP 客户端 → JSON-RPC / stdio → mcp_server.py → 本机 HTTP → 游戏桥接
游戏桥接   → comms/outbox.jsonl → 工单运行时 → 提醒（待接入）
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

`TOOLS` 是工具 schema 和 handler 的注册表。`tools/list` 返回工具定义，`tools/call` 按名称分派。当前有 69 个迁移工具和 4 个 watch 工具。单位命令、编组命令和绘图都有各自的路由映射。截图成功时返回 MCP 图片块和元数据。

调用方应保留 Bridge 的 `error.code`、`error.retryable` 和转译层补充的 `http_status`、`uncertain`、`resend`。写请求失去回执、遇到不安全的 5xx 或编组部分执行时，先核实实际状态，再决定是否提交新命令。连接被拒绝会标记 `sent:false`。普通路由预算至少 25 秒，编组至少 30 秒，慢速绘图至少 50 秒。

## Watch 映射与当前兼容缺口

| MCP 工具 | Bridge 路由 |
|---|---|
| `sp_watch_create` | `POST /watch` |
| `sp_watch_list` | `GET /watch` |
| `sp_watch_get` | `GET /watch/{watch_id}` |
| `sp_watch_delete` | `DELETE /watch/{watch_id}` |

第一层更新了触发器契约，当前 MCP schema 和部分参数映射仍沿用旧版：

- 创建工具未声明或转发 `mode`，因此不能通过该工具选择 `edge`。Bridge 默认用 `state`，会在登记时检查条件是否已经成立。
- 创建工具仍声明 `changes` 和 `crosses`。当前 Bridge 接受 `eq`、`ne`、`gt`、`lt`、`exists` 和 `changed`，并将 `edge` 的操作设为 `changed`。
- 查询和删除的 schema 声明整数 `watch_id`，Bridge 返回的是形如 `w1` 的字符串。handler 会把实参转成字符串拼入路径，遵循 schema 的客户端仍会遇到类型冲突。

对接工单前，应同步创建参数和 ID 类型。工单运行时应使用登记返回的 `watch_id`、`world_generation` 关联工单版本。命中事件由第一层追加到 `comms/outbox.jsonl`，MCP 工具回执本身没有事件推送能力。上层消费者接通后，应使用事件身份去重并核对工单和世界代际，再将 `evidence:true` 的事件交给工单状态机。询问玩家的工单期限独立于规则的 `ttl_s`。

## 其他接口差异

当前 Bridge 要求发射舰载机时提供 `loadout`，但 `sp_launch_aircraft` 的 schema 仍称可省略并使用默认挂载。调用方应从甲板信息选择并传入挂载。旧工具描述里的情报遮蔽说明也需要与当前 Bridge 策略同步。
