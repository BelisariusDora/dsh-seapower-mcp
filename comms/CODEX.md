# Game chat forwarding

The communication footer has separate Target and Portrait selectors. Target starts at DSH.
Portrait starts at Auto, which uses deepseek.png for DSH and gpt.png for Codex. Manual
portrait selection is independent of routing. Images are read from the deployed mod's
portrait directory.

DSH keeps its existing comms/outbox.jsonl and harness_link.js. Codex uses
comms/codex.outbox.jsonl. The target is applied when the player clicks Send.

From the Sea Power installation root, run the Codex helper with Node.js and PowerShell 7
available on PATH.

    node tools/spmcp_v2/comms/codex_link.js --check
    node tools/spmcp_v2/comms/codex_link.js

The installed comms/codex.target.json holds the default thread ID and exact chat title.
For a different chat, use --thread and --chat-title. The queue cursor is bound to its
thread on first use. With Codex in the foreground, --check lists the visible chat markers,
editors and send buttons without editing or sending. Use --composer-name and --send-name
if the accessible names differ from the defaults.

The helper waits for the intended Codex chat and an empty composer. It does not switch
windows. A submission with an ambiguous result stops the helper and leaves a pending
cursor entry. Check the desktop chat before deciding whether to advance or retry that
entry. Codex replies are read in the desktop chat. The game transcript still follows DSH.
