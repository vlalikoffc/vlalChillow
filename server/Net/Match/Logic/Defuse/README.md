# Defuse timer logic (shared)

Cross-mode **timer math** for bomb fuse / phase deadlines — not a game-mode FSM.

## API

`DefuseTimer` (`DefuseTimer.cs`):

| Helper | Role |
|--------|------|
| `FuseEndsUtc` / `TickFuse` | Explode only at `plantUtc + BombFuse` (Escalation gold) |
| `FuseWireDeadlineSec` | Room `Time` = now + fuse (Escalation / Ranked plant bags) |
| `ArmDeadline` | `PhaseEndsUtc = now + duration` |
| `BuyWireDeadlineSec` / `BuyClientZeroSec` / grace helpers | Legacy buy-pad path — **Allies gold no longer uses these** (C2=22 Time=RST=now) |

Default fuse length: `MatchHostSettings.BombFuse` (~40s).

## Taken from Escalation

- Fuse countdown anchored on real `BombPlantedUtc` (never invent / re-anchor from `PhaseEndsUtc` alone)
- Wire fuse `Time` = `nowSec + fuseSec` (Escalation; Allies C2=40 is C2-only — no Time)
- Phase arming = wall `now + duration`

## Still mode-local (on purpose)

| Mode | Kept in mode files |
|------|--------------------|
| **Escalation** | C2=22 / 40 / 31 bags, auto-plant Rpc, PostPlantAnnounce gap, no fixed Live clock |
| **Allies** | C2=22 anchor → ~10s → C2=31 combat stay, field=3 plant → C2=40 C2-only, C2=101 WinTeam only, half-time, host round timeout |
| **Ranked (generic)** | Prep→Live C2 sequence, wipe/plant observe paths |

Mode duration constants stay on `EscalationFlowParams` / `AlliesFlowParams` / `MatchFlowTestParams` — only the arithmetic moved here.

## Rollback

Pre-gold Allies buy-pad / 1s C2=31 flash / Live 101: tag `allies-buy-grace-checkpoint-20260724` (commit `403f7cb`) — superseded by phone gold 2026-07-24.
