# Mode: Ranked2v2

**Owner agent:** mode-ranked2v2 (see `decompiled/AGENT_SWARM.md`)

**Status:** IMPLEMENTED — live Allies loop in GameMatchHost.Ranked2v2*.cs partials

**SelectedLevels (probe run-20260722_213859):** Prison 2x2 … Sandstone 2x2

**Family:** bomb — bomb-family modes may share phase *interfaces* later; implementations stay in this folder only. **Do not import sibling Modes/\***.

## Allowed edit paths

- `server/Net/Match/Modes/Ranked2v2/**`
- Evidence notes under `decompiled/` that are mode-specific (do not invent wire)

## Forbidden

- Editing other `Modes/<Other>/` trees
- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / timer fields without decompile or gold log evidence
