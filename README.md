# spmcp_v2 - layer 1 (game bridge)

Migration of the C# game bridge out of `tools/spmcp` (now legacy, untouched).
Same wire protocol, same AnchorChain entry point, plus a native trigger surface.

## What layer 1 is

- `Bridge.cs` - AnchorChain mod entry, single main-thread hop, MiniHttp server on 19080
- `Api.cs` and the capability modules - the request/response surface (migrated as-is)
- `GameReflect.cs` - reflection index and IL stub detection
- `Watch.cs` - **new**: temporary triggers (`/watch`), evaluated on the 0.5 s tick
- `CommsBridge.cs` - outbound file channel to the harness (unchanged; triggers append to it)

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
