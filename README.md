# spmcp_v2 - game bridge and MCP adapter

Migration of the C# game bridge out of `tools/spmcp` (now legacy, untouched).
Same wire protocol, same AnchorChain entry point, plus a native trigger surface.

## What layer 1 is

- `Bridge.cs` - AnchorChain mod entry, single main-thread hop, MiniHttp server on 19080
- `Api.cs` and the capability modules - the request/response surface (migrated as-is)
- `GameReflect.cs` - reflection index and IL stub detection
- `Watch.cs` - **new**: temporary triggers (`/watch`), sampled every 0.5 s while rules are active
- `CommsBridge.cs` - outbound file channel to the harness (unchanged; triggers append to it)

## Docs

- [doc/README.md](doc/README.md) - layer 1: files, threads, envelope, budgets, gaps
- [doc/layer1_bridge.md](doc/layer1_bridge.md) - the `/watch` contract in full
- [doc/layer2_translation.md](doc/layer2_translation.md) - MCP adapter and current watch compatibility

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

    POST   /watch   {"unit_id":"12","field":"IsDestroyed","mode":"state","op":"eq","value":true,"once":true,"ttl_s":600}
    GET    /watch                 list with hit counts and last value
    GET    /watch/w3               one rule
    DELETE /watch/w3               remove

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

Output: `build\SeaPowerAgentBridge.dll`. `-Deploy` updates the existing
`Sea Power_Data\StreamingAssets\dsh-spmcp` slot. Restart the game after deployment and
check `GET /ping` for `build.stamp`, `build.onDiskStamp` and `build.staleDll`.
