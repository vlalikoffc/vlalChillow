# Mode: DeathMatch

**Owner agent:** mode-deathmatch (see `decompiled/AGENT_SWARM.md`)

**Status:** STUB — unimplemented; refuse wire drive

**SelectedLevels (probe run-20260722_213859):** Perimeter, Hanari, Favelas, Arena, Calypso, Sandyards, TrainingOutside, Village

**Family:** dm — bomb-family modes may share phase *interfaces* later; implementations stay in this folder only. **Do not import sibling Modes/\***.

## Allowed edit paths

- `server/Net/Match/Modes/DeathMatch/**`
- Evidence notes under `decompiled/` that are mode-specific (do not invent wire)

## Forbidden

- Editing other `Modes/<Other>/` trees
- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / timer fields without decompile or gold log evidence
