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

Host TX from `DuelConfig` dump (`DuelLoadouts.cs`):

- Non-modifier C2=22: random preset from 23 `_loadouts` → `current_loadout` + `current_round_modifier_id=null`
- Modifier C2=22 (~p=1/3): uniform among **enabled** mods → `current_round_modifier_id` + `used_round_modifier_ids`; **omit** `current_loadout` (client applies definition loadout)
- C2=10 WaitingPlayers: random preset + null modifier
- **OnlyGrenades** fully defined (HE loadout + infinite-HE flag) but **gated off** RNG until grenades work
- Knife: client default — host does not put Knife* in primary

See `MATCH_DUEL_PROBE.md` + `decompiled/DUEL_MODIFIERS_LOADOUTS.md`.

## Bots

Phone gold spawned bots; dedicated is human-only for now (no invented bot protocol).

## Allowed edit paths

- `server/Net/Match/Modes/Duel/**`
- Shared host branch / bootstrap / registry when wiring this mode

## Forbidden

- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / loadout bytes without gold/decompile
- Copying Allies ~10s prep or C2=40 plant into Duel
