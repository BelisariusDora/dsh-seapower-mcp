# 第一层 · 接口层

[文档索引](README.md) · [第二层：转译](layer2_translation.md) · [部署](DEPLOY.md)

本页汇总游戏内 HTTP Bridge、线程边界、真实回执与原生 Watch。原 doc/README.md 的桥接技术内容迁入此页，既有 Watch 契约保留。

## 1. 这一层负责什么

第一层负责 Unity 游戏对象的读取和命令提交。第四层 Noesis 界面同样运行在现有 DLL 内，但交互职责单独归类，不属于接口层的业务职责。第一层的主要职责：

1. **宿主**：作为 AnchorChain mod 被游戏加载，提供一个本地 HTTP 服务（默认 `127.0.0.1:19080`）。
2. **线程闸门**：所有游戏对象访问都必须在主线程执行；HTTP 线程只能投递任务。
3. **事实来源**：把游戏世界读成 JSON、把命令写回游戏，**不做策略判断**（这是与 legacy 的唯一设计差别，见 §6）。

它不负责 MCP 工具面、Agent 会话、工单派发与账本、界面交互。HOLD、平台能力及游戏命令校验仍在本层执行；这与第三层的任务授权范围检查共同生效。

## 2. 文件地图

| 文件 | 职责 | 迁移状态 |
|---|---|---|
| `src/Bridge.cs` | `[ACPlugin]` 入口、`Core` 主线程队列、`MiniHttp` 启动、每帧泵 | 原样迁入 + 一行 `Watch.Tick()` |
| `src/MiniHttp.cs` | 手写 socket HTTP/1.1，无第三方依赖 | 原样 |
| `src/Api.cs` | 路由总表、信封、`DeadlineMs`、单位命令七层门 | 原样 + `Dispatch` 前面加了 `/watch` 分流 + 两个只读辅助 |
| `src/GameReflect.cs` | 程序集索引、成员反射、IL 判 stub、异常回传变体 | 原样 |
| `src/SessionRegistry.cs` | 对局/世界重建分类、`RequireStable` 写闸 | 原样 |
| `src/CommsBridge.cs`、`src/TicketBridge.cs` | 通联及工单 HTTP / 文件协议边界；工单业务状态由第三层维护 | 现有通信保留 |
| `src/ContactView.cs` `TrackScope.cs` `IntelScope.cs` `IntelPolicy.cs` | 接触投影与情报策略 | 保留接口，情报可见性策略已放开（见 §6） |
| `src/Weapons.cs` `Control.cs` `Sonobuoy.cs` `LandUnits.cs` `LandRead.cs` `UnitTypeGate.cs` | 平台能力与类型闸 | 原样 |
| `src/MapView.cs` `MapUnits.cs` `MapPlot.cs` `MapDraw.cs` | 地图与图元 | 原样 |
| `src/Shots.cs` | 截图（与武器无关，名字是历史包袱） | 原样 |
| `src/ConsoleHost.cs`、`CommsWindow.cs`、`SettingsWindow.cs` 等 | 同 DLL 的第四层交互实现，详见 [人机交互层](layer4_interaction.md) | 按职责单独归类 |
| **`src/Watch.cs`** | **原生触发器注册表 + 主线程求值** | **新增**，见本文下方原生触发器契约 |

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

## 5. 主线程之外的独立通道

1. `GET /ping`、`GET /diag` 走 inline（`IsThreadFreeProbe`），不经过主线程 hop——泵死时它们是唯一还能回答的东西，因此**不能访问游戏对象**。
2. `/watch` 自己 hop 一次（8000 ms），再在主线程求值。
3. `GET /shot/png` 返回 PNG 字节，不套信封。
4. `/tickets` 的工单文件请求/等待由 HTTP 工作线程承接，不能占住 Unity 主线程等待调度器，否则调度器反向读取游戏状态会形成循环等待。

## 6. 当前策略与部署

第一层按项目决定放开情报隔离。平台能力、命令参数校验和写入闸门仍由桥接层处理。当前部署更新已有的 `dsh-spmcp` 槽位，插件 ID 为 `dsh.spmcp.bridge`，监听端口为 `19080`。

## 7. 构建与部署

```powershell
cd tools\spmcp_v2
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1            # 只编译
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Deploy    # 部署到 dsh-spmcp（必须先关游戏）
```

编译器是 .NET Framework 的 `csc.exe`（C# 5 语法，必须带 `/noconfig`），引用取自 `Sea Power_Data\Managed` 与 AnchorChain 的 `AnchorChain.dll`。改代码时注意：本仓 `src/*.cs` 行尾**不统一**（`Api.cs` CRLF，`Control/Weapons/Sonobuoy/MapDraw` LF），批量替换脚本必须两种行尾都试。

## 8. 部署后核验

1. 重启游戏后请求 `GET /ping`，检查 `build.stamp` 与 `build.onDiskStamp` 一致，且 `build.staleDll` 为 false。
2. 请求 `GET /diag`、`GET /state` 和 `GET /session`，核验主线程泵、场景和世界代际。
3. 按 [Watch 契约](#原生触发器契约) 验证登记、命中、落盘与读档失效。

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


## 原生触发器契约

`src/Watch.cs` 在 Unity 主线程登记和求值。Agent 可以提交规则，规则随后由游戏进程独立执行。当前最多 64 条活规则，重启后需重新登记。

| 方法 | 路由 | 作用 |
|---|---|---|
| POST | `/watch` | 登记并返回字符串 `watch_id`（例如 `w1`）、世界代际及初始观察 |
| GET | `/watch` | 列出规则和采样设置 |
| GET | `/watch/{watch_id}` | 查询规则 |
| DELETE | `/watch/{watch_id}` | 撤销规则 |

```json
{"unit_id":"12","field":"IsDestroyed","mode":"state","op":"eq","value":true,"once":true,"ttl_s":120}
```

- `state` 为默认模式。登记时检查谓词并返回 `satisfied_now`，条件当时成立可立即生成命中。后续在条件转为成立时报告。操作符包括 `eq`、`ne`、`gt`、`lt`、`exists`。
- `edge` 以登记时的读数为基线，操作由 Bridge 设为 `changed`，登记回执包含 `baseline` 和 `baseline_known`。
- 登记时绑定对象和字段读取器。每 0.5 秒检查订阅，不按规则反复扫描单位表。相同单位与字段在单个采样周期共享读数，见下方审查说明。
- 命中或状态变化写入部署目录的 `comms/outbox.jsonl`，事件带 `event_id`、`watch_id`、`world_generation`、`kind` 和 `evidence`。一次性命中会冻结事件，追加失败时重试同一条，写入后退休。其他事件当前按尽力写入处理。
- 第二层已支持游标轮询。第三层已能通过受控工具登记、查询 Watch 并展示记录，但尚无通用 Watch 事件驱动的自动结单消费者（见 [Harness 调度层](layer3_workflow.md)）。部署后可用一个已知可读的单位字段验证登记，再触发变化并检查 JSONL。使用 MCP 前先检查 [第二层 Watch 登记与事件读回](layer2_translation.md)。

### Watch 当前审查发现

- 字段读取失败记为非证据。相同单位与字段在一个采样周期共享一次读数。
- `ObjectStillBound` 只比对绑定对象上的 ID。对象退出游戏列表后如果该引用仍可读，检查可能继续通过。对象失效信号仍需用实机验证。

### Hardening from the 2026-11-05 live test

Three things the live run taught, all fixed in the bridge itself:

- **`GET /ping` now says which build is loaded.** `build.stamp` is the stamp compiled into
  the assembly; `build.onDiskStamp` is read from `SeaPowerAgentBridge.build` next to the DLL,
  and `build.staleDll` is true when the file is a later build than the running process. A
  deploy while the game is open changes the file but not the loaded assembly, and every
  symptom of that looks like a bug in the new code. `build.ps1` writes the stamp file next to
  the DLL on every compile and copies it on `-Deploy`.
- **`POST /units/{id}/sonobuoy {"strategy":"order"}` no longer refuses a snapped waypoint.**
  The game's path code may move the task's waypoint, so the read-back compares it with a
  2e-3 deg tolerance and reports `waypointOffsetDeg` (or `waypointReadback:"unreadable"`)
  instead of calling a 200 m snap a mismatch. `strategy:"direct"` still requires the exact
  point.
- **`POST /time` refuses fields it does not take.** `{"speed":10}` used to answer `ok:true`
  with the clock untouched; it is now `400 unknown_field` naming the accepted fields
  (`compression`, `paused`).

`GET /units/{id}/weapon_status` still has to be POSTed with `{"status": ...}`; that asymmetry
is the game's own, not a bug in the bridge. `fire` at a point still drops the whole rack for
free-fall ammunition; targeting a unit with `attack` is the path that tracks.
