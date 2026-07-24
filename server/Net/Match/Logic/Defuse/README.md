# Defuse timer logic (shared)

Cross-mode **timer math** for bomb fuse / phase deadlines — not a game-mode FSM.

## API

`DefuseTimer` (`DefuseTimer.cs`):

| Helper | Role |
|--------|------|
| `FuseEndsUtc` / `TickFuse` | Explode only at `plantUtc + BombFuse` (Escalation gold) |
| `FuseWireDeadlineSec` | Room `Time` = now + fuse (Escalation / plant bags) |
| `ArmDeadline` | `PhaseEndsUtc = now + duration` |
| `BuyWireDeadlineSec` | Optional `clientClockPadSec` (Allies bfqt lag) |
| `BuyClientZeroSec` / `RemainToClientZeroSec` | Host wait until UI zero |
| `TryArmOrPassPostZeroGrace` | Allies buy→Live 200ms grace (checkpoint) |

Default fuse length: `MatchHostSettings.BombFuse` (~40s).

## Taken from Escalation

- Fuse countdown anchored on real `BombPlantedUtc` (never invent / re-anchor from `PhaseEndsUtc` alone)
- Wire fuse `Time` = `nowSec + fuseSec`
- Phase arming = wall `now + duration`

## Still mode-local (on purpose)

| Mode | Kept in mode files |
|------|--------------------|
| **Escalation** | C2=22 / 40 / 31 bags, auto-plant Rpc, PostPlantAnnounce gap, no buy pad/grace, no fixed Live clock |
| **Allies** | C2=22 buy bag, bomberId, Live C2=101, `BuyClientClockPad` / `BuyEndGrace` constants, half-time, round timeout |
| **Ranked (generic)** | Prep→Live C2 sequence, wipe/plant observe paths |

Mode duration constants stay on `EscalationFlowParams` / `AlliesFlowParams` / `MatchFlowTestParams` — only the arithmetic moved here.

## Rollback

Pre-refactor Allies buy grace: tag `allies-buy-grace-checkpoint-20260724` (commit `403f7cb`).
