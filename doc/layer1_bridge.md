# 第一层 · 原生触发器

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
- 登记时绑定对象和字段读取器。每 0.5 秒检查订阅，不按规则反复扫描单位表。一次采样内部仍有重复读取，见下方。
- 命中或状态变化写入部署目录的 `comms/outbox.jsonl`，事件带 `event_id`、`watch_id`、`world_generation`、`kind` 和 `evidence`。一次性命中会冻结事件，追加失败时重试同一条，写入后退休。其他事件当前按尽力写入处理。
- 第二层已支持游标轮询。[第三层工单消费者](layer3_workflow.md)尚待接入。部署后可用一个已知可读的单位字段验证登记，再触发变化并检查 JSONL。使用 MCP 前先检查 [第二层 Watch 登记与事件读回](layer2_translation.md)。

## 当前审查发现

- 字段读取失败记为非证据。相同单位与字段在一个采样周期共享一次读数。
- `ObjectStillBound` 只比对绑定对象上的 ID。对象退出游戏列表后如果该引用仍可读，检查可能继续通过。对象失效信号仍需用实机验证。

## Hardening from the 2026-11-05 live test

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
