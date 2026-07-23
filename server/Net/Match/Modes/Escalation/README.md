# Mode: Escalation

**Owner agent:** mode-escalation (see `decompiled/AGENT_SWARM.md`)

**Status:** LIVE — phone-host gold `decompiled/MATCH_ESCALATION_PROBE.md`

**SelectedLevels:** Prison…Province (+ Sandstone)

**Family:** bomb — FSM is **not** Ranked Prep→Live.

## Gold loop

`C2=22 (~8s) → auto-plant field=3 + C2=40 (~3s) → C2=31 combat → C2=101 bag → C2=22`

PreStart TX: `ReCreateSceneManager` ids 4/5/6/8 + `BombSite` (no `bomberId`).

## Allowed edit paths

- `server/Net/Match/Modes/Escalation/**`
- Evidence notes under `decompiled/` that are mode-specific (do not invent wire)

## Forbidden

- Editing other `Modes/<Other>/` trees for Escalation-only behavior (shared tick hooks only when branching on `IsEscalationRoom`)
- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / timer fields without decompile or gold log evidence
