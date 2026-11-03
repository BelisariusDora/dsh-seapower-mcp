# spmcp_v2 — 第一层（游戏桥接）文档

第二层的启动、工具映射和回执约定见 [MCP 转译](layer2_translation.md)。

状态：**已迁移、已编译、未部署、未实机验证**。文档基线对应提交 `5d8457a`。

老项目 `tools/spmcp` 从此是 legacy：只读、不再改动，作为行为参照与回退点。

---

## 1. 这一层负责什么

第一层是唯一能碰 Unity / Noesis 的代码层。它的职责只有三条：

1. **宿主**：作为 AnchorChain mod 被游戏加载，提供一个本地 HTTP 服务（默认 `127.0.0.1:19080`）。
2. **线程闸门**：所有游戏对象访问都必须在主线程执行；HTTP 线程只能投递任务。
3. **事实来源**：把游戏世界读成 JSON、把命令写回游戏，**不做策略判断**（这是与 legacy 的唯一设计差别，见 §6）。

它不负责：MCP 工具面、Agent 会话、命令授权/账本、玩家情报遮蔽、界面交互。那些在更上层。

## 2. 文件地图

| 文件 | 职责 | 迁移状态 |
|---|---|---|
| `src/Bridge.cs` | `[ACPlugin]` 入口、`Core` 主线程队列、`MiniHttp` 启动、每帧泵 | 原样迁入 + 一行 `Watch.Tick()` |
| `src/MiniHttp.cs` | 手写 socket HTTP/1.1，无第三方依赖 | 原样 |
| `src/Api.cs` | 路由总表、信封、`DeadlineMs`、单位命令七层门 | 原样 + `Dispatch` 前面加了 `/watch` 分流 + 两个只读辅助 |
| `src/GameReflect.cs` | 程序集索引、成员反射、IL 判 stub、异常回传变体 | 原样 |
| `src/SessionRegistry.cs` | 对局/世界重建分类、`RequireStable` 写闸 | 原样 |
| `src/CommsBridge.cs` | 游戏→外部（harness）的**出站**文件通道 | 原样 |
| `src/ContactView.cs` `TrackScope.cs` `IntelScope.cs` `IntelPolicy.cs` | 接触投影与情报策略 | 原样（**仍生效**，见 §6） |
| `src/Weapons.cs` `Control.cs` `Sonobuoy.cs` `LandUnits.cs` `LandRead.cs` `UnitTypeGate.cs` | 平台能力与类型闸 | 原样 |
| `src/MapView.cs` `MapUnits.cs` `MapPlot.cs` `MapDraw.cs` | 地图与图元 | 原样 |
| `src/Shots.cs` | 截图（与武器无关，名字是历史包袱） | 原样 |
| `src/ConsoleHost.cs` `InspectorWindow.cs` `InspectorBridge.cs` `CommsWindow.cs` `Strings.cs` | 游戏内 UI 单壳两页 | 原样 |
| **`src/Watch.cs`** | **原生触发器注册表 + 主线程求值** | **新增**，见 `layer1_bridge.md` |

## 3. 线程模型（改动这一层前必读）

- `Core.OnMainThread(fn, timeoutMs)`：已在主线程则**直接执行**（入队会死锁）；否则入队等待。
- 超时用 `CompareExchange(Pending→Cancelled)` 判定：
  - 仍是 Pending → 任务从未执行，抛 `MainThreadTimeoutException(safe:true)`；
  - 已开始 → 再等 50 ms，仍未完成抛 `safe:false`，含义是「**可能已经执行**」。
- 这个 `safe` 位是防重复开火的核心：上层看到 `safe:false` 时**不许重发**。
- 每帧 `Core.Pump(6)` 排 6 个任务；游戏卡住时队列会积压，这是预期行为。
- 唯一会长时间占住主线程的是 `Shots` 的等帧截图（内部 12 s 兜底）。

## 4. 请求路径与信封

```
HTTP → MiniHttp → Api.Dispatch → (Watch.Matches? Watch.Handle : DispatchCore)
```

- 统一信封：`{ "cmd": <requestId>, "ok": <bool>, "data": {...}, "refused": <bool>, "error": {code,message,retryable} }`。
- `ok` 为 `false` 时 HTTP 状态仍是 **200**（业务拒绝不是传输失败）；只有路由未捕获异常是 500，主线程超时是 504。
- 每路由等待预算由 `DeadlineMs(method, path)` 给出（表驱动，见 `Api.cs`）：`/console` 4000、`/state|/taskforces|/time` 8000、`/units/{id}` 读 8000、`/units/{id}/{weapons,deck,airborne,decktasks,vertical,sub,land}` 12000、`/map/*` 15000（`units|unit-screen` 20000）、`/plot/course` 30000、`/plot/{draw,drawings,erase}` 45000、`/group/*` 25000、默认 15000。
- 客户端预算必须**严格大于**桥侧预算（legacy 用 `TIMEOUT_FLOOR=25` 保证），否则超时会先断在客户端，看起来像"桥没回答"。
- 写入口只有两处：`POST /units/{id}/{op}` 与 `POST /plot/*`、`POST /map/*`、`POST /time`、`POST /ui/hold`。

## 5. 主线程之外的三个"例外"通道

1. `GET /ping`、`GET /diag` 走 inline（`IsThreadFreeProbe`），不经过主线程 hop——泵死时它们是唯一还能回答的东西，因此**不能访问游戏对象**。
2. `/watch` 自己 hop 一次（8000 ms），再在主线程求值。
3. `GET /shot/png` 返回 PNG 字节，不套信封。

## 6. 与 legacy 的差别（当前仅两条）

1. **多了一个 `/watch` 触发器面**（`Watch.cs`，纯新增，不改动任何老路由）。
2. `build.ps1` 的 `-Deploy` 目标改为 `Sea Power_Data/StreamingAssets/dsh-spmcp-v2`，**不会覆盖 legacy**。

**未完成、必须知道的三件事**：

- **情报闸仍在生效**：`Api.cs` 里 `IntelScope.PlayerKnows` / `IntelPolicy.AllowsDetail` 的调用点全部保留，所以老路由**依然按 legacy 的策略遮蔽**（未知单位 404、航速/弹药可被红标）。只有 `/watch` 是全真观测。按"层一不做策略"的目标，下一步应把这些调用点整体摘掉——**不要删文件**，先摘调用点、验证接口齐全，再删实现，否则会出现"半套策略"，比全有或全无更危险。
- **插件身份未改**：`Bridge.cs` 仍是 `[ACPlugin("dsh.spmcp.bridge", ...)]`，与 legacy 相同。两个 mod 同时安装会冲突，安装前必须改成 `dsh.spmcp.bridge.v2`（并同步 `_info.ini`）。
- **未验证**：只做过编译（`645120` 字节）。没有部署、没有开游戏、没有跑过任何一条真实请求。

## 7. 构建与部署

```powershell
cd tools\spmcp_v2
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1            # 只编译
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Deploy    # 部署到 dsh-spmcp-v2（必须先关游戏）
```

编译器是 .NET Framework 的 `csc.exe`（C# 5 语法，必须带 `/noconfig`），引用取自 `Sea Power_Data\Managed` 与 AnchorChain 的 `AnchorChain.dll`。改代码时注意：本仓 `src/*.cs` 行尾**不统一**（`Api.cs` CRLF，`Control/Weapons/Sonobuoy/MapDraw` LF），批量替换脚本必须两种行尾都试。

## 8. 实机验证清单（第一次开游戏时按顺序做）

1. `/ping` → `200`，`coreIdentity` 里有 mod 目录。
2. `/diag` → `mainThreadId` 非 -1，`pumpTicks` 在增长。
3. `/state` → 有场景名；`/session` → `worldGeneration` 有值。
4. `/watch` 注册一条 `{"unit_id":"<自己单位>","field":"destroyed","op":"eq","value":true,"ttl_s":120}` → 拿到 `watch_id` 与 `probe`（应为 `"false"`）。
5. `GET /watch` 确认规则在；等 0.5 s 看 `hits` 不变（说明求值在跑但不误报）。
6. 打沉该单位（或用调试手段置位）→ `comms/outbox.jsonl` 出现 `k:"event"` 行；`GET /watch/1` 的 `hits` 变 1；`once:true` 时规则自动消失。
7. 读一条不存在的字段注册 → `field_unreadable`；注册不存在的单位 → `unit_not_found`。
8. 读档后 `GET /watch` → 旧规则的 `world_generation` 与新 `worldGeneration` 不同，按 §`layer1_bridge.md` 的规则处理。

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

## 9. Height control (measured 2026-11-05)

**The unit of `GET /units/{id}.altitude` is feet.** The aircraft ini files say so
(`StreamingAssets/original/aircraft/usn_f-4j.ini:78  Ceiling=55000.0 // ceiling in feet`,
`:98  Altitudes=100,1000,3000,6000,10000,20000 // in feet`), and the same file annotates
`MaxClimbRate=62 // Meters per second` and `WingSpan=11.71 //meters`, so the unit comments
can be trusted. A freshly launched F-4J reads 64-68 ft while sitting on a flight deck ~20 m
above the waterline - that is the second confirmation.

**Climb with the preset band, not with an absolute altitude:**

```
POST /units/{id}/move  {"lat": 31.35483, "lon": 26.80100, "height_state": "High"}
```

`height_state` is `WaypointHeightState`; a request without it means `NoChange` and asks for
no band at all (`src/Api.cs:2251`). Measured on an F-4J (35 s settle at 10x, feet):

| height_state | measured | the ini layer it lands on |
|---|---|---|
| `Low` | 936 | 1000 |
| **`High`** | **5,940 - 5,972** | **6000** |
| `Medium` | 9,961 - 9,971 | 10000 |
| `Ceiling` | still climbing at 32,689 | 55000 |
| `VeryLow`, `Cruise`, `VeryHigh` | not settled cleanly (samples caught mid-climb) | - |

So "come in at 6,000 ft" is `height_state:"High"`, and it is exact.

**`op:"altitude"` is experimental and should not be used yet.** It writes
`_desiredAltitudeToSet` after converting feet to metres, but the read-back
(`heightInFeet`) comes back null on every call, so the unit of the field was never verified.
The supported channel is the preset band above; revisit the absolute-altitude op only with a
readable read-back. (Owner decision, 2026-11-05: use the presets for now.)

**Do not issue an approach while a recovery is running.** `recover` climbs the aircraft to
its `Ceiling` band first; on 2026-11-05 a pair of F-4J went to 51,776 ft on a queued recall
and only came down when a fresh `move` replaced the recall. Give the approach order after
the recovery is finished, or cancel the recall first.
