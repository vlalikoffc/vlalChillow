# Mode: Defuse

**Status:** Implemented — same flow as RankedDefuse; casual first-to-**6**.

**SelectedLevels (probe run-20260722_213859):** Prison…Sandstone

**Family:** bomb — Allies gold C2 bags / plant field=3 / host 109s combat.

## Behaviour

- Timers: `DefuseFlowParams` (aliases Allies gold)
- Win target: **6** (not 8) — half-time after round 5 (`WinsNeeded - 1`)
- Host branch: `IsAlliesRoom` → `TickAlliesFlowRoom` (C0=`Defuse`)

## Allowed edit paths

- `server/Net/Match/Modes/Defuse/**`
- Shared host partials only when adapting Allies-family wiring (do not invent wire)
