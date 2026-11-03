# spmcp_v2 - game bridge and MCP adapter

Migration of the C# game bridge out of `tools/spmcp` (now legacy, untouched).
Same wire protocol, same AnchorChain entry point, plus a native trigger surface.

## What layer 1 is

- `Bridge.cs` - AnchorChain mod entry, single main-thread hop, MiniHttp server on 19080
- `Api.cs` and the capability modules - the request/response surface (migrated as-is)
- `GameReflect.cs` - reflection index and IL stub detection
- `Watch.cs` - **new**: temporary triggers (`/watch`), evaluated on the 0.2 s 节流 + 边沿触发
- `CommsBridge.cs` - outbound file channel to the harness (unchanged; triggers append to it)

## Docs

- [doc/README.md](doc/README.md) - layer 1: files, threads, envelope, budgets, gaps
- [doc/layer1_bridge.md](doc/layer1_bridge.md) - the `/watch` contract in full

## Layer 2

Run `python tools/spmcp_v2/mcp_server.py` from the game directory to start the
MCP stdio adapter. `SPMCP_PORT` selects the bridge port (default `19080`).
`build.ps1 -Deploy` copies the adapter alongside the bridge DLL.

The adapter preserves the legacy tool mappings and adds `sp_watch_create`,
`sp_watch_list`, `sp_watch_get` and `sp_watch_delete`. Trigger events go to the
bridge outbox for a separate consumer.

Use this direct adapter for development and integration. It exposes `sp_raw`,
bridge writes and raw watch fields. A restricted Agent tool surface needs
upstream authorization, intel policy and command bookkeeping.

## Triggers

    POST   /watch   {"unit_id":"12","field":"destroyed","op":"eq","value":true,"once":true,"ttl_s":600}
    GET    /watch                 list with hit counts and last value
    GET    /watch/3               one rule
    DELETE /watch/3               remove

Contracts that are not negotiable:

1. A condition that cannot be read answers `unknown`, never `true`/`false`.
2. A hit or a state transition is written to `comms/outbox.jsonl` with `k:"event"` and
   `from:"trigger"`, so it rides the delivery path that already has a lock, a byte cursor
   and a tri-state record (`sent` / `unknown` / `failed`).
3. Rules are temporary by construction: `ttl_s`, at most 64 live rules, `once:true` retires
   a rule after its first hit.
4. Every rule records the `world_generation` it was created in. Reload/restart means old ids
   name different objects, so a rule from another generation must not fire.

## Not in this layer (by instruction)

- No intel policy: layer 1 answers in truth. `IntelScope`/`IntelPolicy` are still compiled in
  from the migration; removing their call sites is the next step, so that the policy lives
  exactly one layer up and cannot be half-applied.
- No HOLD check on triggers: watching is not commanding.

## Build

    powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

Output: `build\SeaPowerAgentBridge.dll`. `-Deploy` writes to
`Sea Power_Data\StreamingAssets\dsh-spmcp-v2` so it cannot overwrite the legacy mod.
The plugin id in `Bridge.cs` must get the same suffix before both are ever installed at once.

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
