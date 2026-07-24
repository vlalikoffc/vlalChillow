# Mode: RankedDefuse

**Status:** Implemented — Allies gold bomb-round flow (Competitive / MM).

**SelectedLevels (probe run-20260722_213859):** Prison, Hanami, Rust, Dune, Breeze, Province, Sandstone

**Family:** bomb — C2=22→31→40?→101, plant field=3, host 109s combat, first-to-8.

## Behaviour

- Timers: `DefuseFlowParams` (aliases Allies gold)
- Win target: **8**
- Host branch: `IsAlliesRoom` → `TickAlliesFlowRoom` (C0=`RankedDefuse`)
- No Live C2=101 without WinTeam; no invented Live `Time=now+N` on wire

## Allowed edit paths

- `server/Net/Match/Modes/RankedDefuse/**`
- Shared host partials only when adapting Allies-family wiring (do not invent wire)
