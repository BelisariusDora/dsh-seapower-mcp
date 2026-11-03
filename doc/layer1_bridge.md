# 原生触发器（`/watch`）契约

实现：`src/Watch.cs`。这一层只做「登记条件 + 求值 + 把结果交出去」，**不做投递重试、不做授权、不做遮蔽**。

## 1. 为什么是"临时"触发器

上层要的不是长期订阅，而是「在这个对局里，当 X 单位的 Y 变成 Z 时告诉我一声」。所以规则天然带三条限制：

- `ttl_s`：到点自动注销并推送一条 `expired`；
- 最多 **64** 条活规则（`watch_limit`）；
- `once:true`：首次命中后自动注销。

规则**不落盘**：进程退出即消失。这是有意的——一条跨重启仍然存在的触发器，会在世界重建后指向空对象。

## 2. 路由

| 方法 | 路径 | 作用 |
|---|---|---|
| `POST` | `/watch` | 创建，返回 `watch_id` 与创建瞬间的读数 `probe` |
| `GET` | `/watch` | 列出所有活规则（含 `hits`、`last_value`） |
| `GET` | `/watch/{id}` | 读一条 |
| `DELETE` | `/watch/{id}` | 注销一条（幂等：再删返回 `watch_not_found`） |

其他方法 → `method_not_allowed`。所有 `/watch` 请求在主线程执行，预算 **8000 ms**；超时返回 `main_thread_timeout`，`retryable` 取 `MainThreadTimeoutException.Safe`——**`retryable:false` 表示主线程已经开始执行、登记可能已经发生，不要盲目重发**。

## 3. 创建请求

```json
{
  "unit_id": "12",
  "field":   "destroyed",
  "op":      "eq",
  "value":   true,
  "once":    true,
  "ttl_s":   600
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `unit_id` | 是 | 单位 id，字符串或数字均可（内部按整数比较） |
| `field` | 是 | 单位上的**成员名**，按 `GameReflect.Member` 解析；找不到时依次尝试 `field` 小写、`is<Field>`、`_<field>`、`<field>Flag` |
| `op` | 否 | `eq`\|`ne`\|`gt`\|`lt`\|`exists`\|`changes`\|`crosses`，缺省 `eq` |
| `value` | 视 op | `eq`/`ne`/`gt`/`lt`/`crosses` 需要；接受 JSON 字面量，字符串可带引号也可不带 |
| `once` | 否 | `true` = 首次命中后自动注销 |
| `ttl_s` | 否 | 秒；缺省或 `0` = 不过期 |

### 创建时就会拒绝的情况（不是等到永远不触发）

- `unit_not_found`：该 id 没有对象（层一不区分"不存在"与"你不该知道"，因为层一不做策略）。
- `field_unreadable`：字段解析不到（`op:"exists"` 除外——它的语义正是"有没有"）。
- `unknown_op` / `missing_unit_id` / `missing_field` / `watch_limit`。

创建成功的 `data` 里带 `probe` 与 `probeKnown`：**登记那一刻的读数**。这是有意设计的——上层可以立刻知道"条件当前是否已经成立"，而不用等一个周期。

## 4. 求值语义

求值点：主线程每帧（`Bridge.Update` 里 `Watch.Tick()`，紧跟截图采样之后）。当前**没有节流**；64 条上限下可控，但每条每帧一次反射，下一步应加约 0.2 s 的节流（记为 TODO，未实现）。

比较规则：

| op | 成立的判定 |
|---|---|
| `eq` | 字符串按 **OrdinalIgnoreCase** 相等；都能解析成数字时按 `|a-b| < 1e-9` |
| `ne` | `eq` 取反 |
| `gt` / `lt` | 仅数字；任一侧非数字 → **不命中，也不报错**（不猜） |
| `crosses` | 数字从 `< value` 穿到 `>= value` 或反向；需要上一帧读数 |
| `exists` | 成员能被读到（值可为 null） |
| `changes` | 与上一帧读数不同（含 `null→值`、`值→null`） |

### 三态铁律

读值失败（对象消失、反射抛异常、字段读不到）**不是 false**，而是 `unknown`：

- 推一条 `kind:"unknown"`，并附 `why`；
- `unknown` 只在上一次读数不是 unknown 时推送一次（避免每帧刷屏）；
- `once:true` 的规则遇 `missing`/`unknown` 也会注销（它已经不可能按原意回答了）；
- 永不把 unknown 折算成 hit。

## 5. 命中记录怎么交出去

每一命中/状态变化/过期/未知，追加一行到 **`<ModDir>\comms\outbox.jsonl`**：

```json
{"k":"event","from":"trigger","watchId":1,"kind":"hit","unitId":"12","field":"destroyed",
 "op":"eq","want":"true","now":"true","hits":1,"at":"2026-11-03T04:12:31.998Z"}
```

`kind` ∈ `hit` | `changed` | `missing` | `unknown` | `expired`。

选择复用 outbox 的理由：那条路已经有**锁文件、字节游标、`sent/unknown/failed` 三态记录**（legacy 修了三轮），触发器不该再造一条更安静的通道。

**但要说清楚当前的真实状态**：`comms/outbox.jsonl` 的既有消费者是 legacy 的 `harness_link.js`，它按"玩家发言"的格式逐行解析。`k:"event"` 行**目前没有上层消费者**——v2 的 harness 层还没写。所以触发器的信息现在只会落在文件里，需要读文件或等 v2 第三层补上消费者。这是已知缺口，不是"已经通了"。

## 6. 世界重建

每条规则创建时记录 `world_generation`（取 `/session` 的 `worldGeneration`）。读档或重开后，同一 id 指向别的对象，因此：

- **契约**：上层在消费 `kind:"hit"` 前必须先比对 `GET /watch` 里的 `world_generation` 与当前 `worldGeneration`，不一致就整批作废。
- **现状**：这一层**不自动作废**（没有订阅 `SessionRegistry` 的重建信号）。自动作废是下一步，写进 TODO 而不是假装已有。

## 7. 与 HOLD / 授权的关系

- **不受 HOLD 管**：HOLD 拦的是"下令"，观察不是下令。
- **不需要授权**：触发器是只读登记，不产生命令，因此不进命令账本。

## 8. 已知局限（按重要性）

1. **每帧求值、无节流**：规则多时是纯粹的反射税。上限 64 + `ttl_s` 只能缓解。
2. **无上层消费者**：见 §5。
3. **字段名靠反射**：`destroyed` 这类名字是否真存在于目标类型上，只有实机才知道；`probeKnown:false` 是判据。
4. **`crosses` 依赖上一帧**：若两帧之间跨越又返回，会被漏掉（采样定理，不是 bug，但要写下来）。
5. **`changes` 对浮点噪声敏感**：没有死区（deadband），连续微变会连续推送。
6. **寻单位是线性扫描**：`FindUnit` 反射全表比较 `UniqueId` 等四种拼写；单位上千时每帧每规则一次全表扫，实测前应改成 id 字典缓存。

## 9. 最小验证脚本（游戏运行时）

```powershell
$b = 'http://127.0.0.1:19080'
Invoke-RestMethod "$b/ping" | ConvertTo-Json -Depth 4
$r = Invoke-RestMethod "$b/watch" -Method Post -ContentType 'application/json' -Body (@{
      unit_id='1'; field='destroyed'; op='eq'; value=$true; once=$true; ttl_s=120 } | ConvertTo-Json)
$r | ConvertTo-Json -Depth 5          # 期望 ok=true 且 probe='false'
Invoke-RestMethod "$b/watch" | ConvertTo-Json -Depth 5
Get-Content 'Sea Power_Data\StreamingAssets\dsh-spmcp-v2\comms\outbox.jsonl' -Tail 3
```

## 审查修复记录（对第一版 `Watch.cs` 的 6 条意见）

| # | 意见 | 处理 |
|---|---|---|
| P1 | 查单位漏掉游戏实际用的 `UniqueID` | 候选名单加入 `UniqueID`/`uniqueID`（大写 ID） |
| P1 | 读档后复用 id 的旧规则会打到新单位 | 每 tick 比对 `world_generation`，不一致即注销并推 `expired`（原因写明代号变化） |
| P1 | outbox 写失败仍消耗一次性规则 | `Emit` 返回是否真写入；`once` 只在**投递成功**后注销，失败保留重试 |
| P2 | `null` 与读不到被合并，破坏 `exists`/`changes` | 新增 `MemberExists`（沿基类查 field/property）：成员存在但值为 null → 值 `"null"`，只有成员缺失才算不可读 |
| P2 | `changes` 事件不计数、不注销 `once` | `changed` 与 `hit` 同样计入 `hits` 并触发 `once` 注销 |
| P2 | 只改插件 id 仍无法与 legacy 同时跑 | 端口从 19080 改为 **19081**（v2 默认），并在文档标注 |

**顺带修掉的运行成本问题（文档偏差的根因）**：原实现每帧求值、且条件持续为真时**每帧追加一行 outbox**。现在：
- 求值节流 **0.2 s**（`EvalIntervalSeconds`）；
- 命中改为**边沿触发**（条件由假变真才报一次，持续为真不重复）；
- 文档里"每 0.5 秒求值"一并改为"0.2 秒节流 + 边沿触发"。

**仍然存在的局限**（未修，别当已解决）：上层消费者还没写（`k:"event"` 行目前只有文件没有读者）；`crosses` 依赖采样、两帧间穿越再返回会漏；`changes` 无死区；`FindUnit` 仍是线性扫描。
