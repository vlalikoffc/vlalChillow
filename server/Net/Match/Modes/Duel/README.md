# Mode: Duel

**Owner agent:** mode-duel (see `decompiled/AGENT_SWARM.md`)

**Status:** IMPLEMENTED — host flow from gold `MATCH_DUEL_PROBE.md` (`20260724_041*`)

**SelectedLevels (probe run-20260722_213859):** Block, Cableway, Pipeline, Bridge, Pool, Temple, Yard

**Family:** duel — first-to-8 eliminate rounds; **no** BombManager / C2=40.

## FSM (dedicated)

| Phase | C2 | Notes |
|-------|---:|-------|
| WaitingPlayers | 10 | until `/set start` + both teams |
| PreWarmup FFA | 11 | ~20s then kill/score wipe |
| WarmUp | 21 | ~3s (R1 only) |
| Freeze | 22 | ReCreate×2 (ids 4,5); Time=RST=now; ~5s wall |
| Combat | 31 | stay until wipe / host timeout |
| RoundEnd | 101 | GameRpcHelper field=1 → WinTeam + score |
| FinalHud | 201 | first-to-8; FinalWinTeam + FinalPlayers |

## Weapons / modifiers

Escalation-style: host does **not** TX `current_loadout` or `OnlyKnifes`/`OnlyHeadshots`/`OnlyGrenades`
schedules — no known codec builder (gold phone host did send them). Clients pick/view weapons.
TODO when loadout/modifier builders are reversed.

## Bots

Phone gold spawned bots; dedicated is human-only for now (no invented bot protocol).

## Allowed edit paths

- `server/Net/Match/Modes/Duel/**`
- Shared host branch / bootstrap / registry when wiring this mode

## Forbidden

- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / loadout bytes without gold/decompile
- Copying Allies ~10s prep or C2=40 plant into Duel
