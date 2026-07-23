# Mode: Ranked2v2

**Owner agent:** mode-ranked2v2 (see `decompiled/AGENT_SWARM.md`)

**Status:** IMPLEMENTED — Allies loop with half-time swap + first-to-8 in GameMatchHost.Ranked2v2*.cs

**Probe doc:** [MATCH_ALLIES_PROBE.md](./MATCH_ALLIES_PROBE.md) (`allies-probe` Sandstone 2x2 run-20260723_215727)

**SelectedLevels (probe run-20260723_215727):** Sandstone 2x2 … Prison 2x2

**Family:** bomb — bomb-family modes may share phase *interfaces* later; implementations stay in this folder only. **Do not import sibling Modes/\***.

## Allowed edit paths

- `server/Net/Match/Modes/Ranked2v2/**`
- Evidence notes under `decompiled/` that are mode-specific (do not invent wire)

## Forbidden

- Editing other `Modes/<Other>/` trees
- Pasting capture bytes into host replies
- Inventing C2 / WinTeam / timer fields without decompile or gold log evidence
