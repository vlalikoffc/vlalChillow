# Mode: Ranked2v2Alt

**Status:** Implemented — same Allies gold FSM as Ranked2v2 (client Alt map half).

**SelectedLevels (probe run-20260722_213859):** * 2x2 Alt

**Family:** bomb — shares Allies C2 sequence / plant field=3 / host 109s combat / first-to-8.

## Behaviour

- Wire + host timers: copy of Allies (`MATCH_ALLIES_PROBE.md` / `DefuseFlowParams`)
- Win target: **8** (`MatchHostSettings.TryDefaultWinsForMode`)
- Host branch: `IsAlliesRoom` → `TickAlliesFlowRoom` (C0=`Ranked2v2Alt`)

## Allowed edit paths

- `server/Net/Match/Modes/Ranked2v2Alt/**`
- Shared host partials only when adapting Allies-family wiring (do not invent wire)
