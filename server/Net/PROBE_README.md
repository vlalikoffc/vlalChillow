# ConnectAsClient probe

Learning-mode client (`dotnet run -c Release -- --client …`) that joins a **real** phone host (or dedicated LAN host) over Bonjour V2 + LiteNetLib, captures match traffic, and can optionally spawn as a **visible fighting pawn**.

**Last probe update targets game version: StandChillow `2.06 OBT F1`.**

Captures under `bin/.../captures/` are for analysis only — never replay blobs into dedicated-host replies.

---

## Run

```bash
cd server
dotnet run -c Release -- --client --team spectator   # observe only (default)
dotnet run -c Release -- --client --team tr          # spawn Tr_Tr at catalog map spawn
dotnet run -c Release -- --client --team ct          # spawn Ct_Ct at catalog map spawn
```

Useful flags: `--name <roster>`, `--pick`, `--ip` / `--port`, `--match-ip` (see `Program.cs` / `RunConnectAsClientAsync`).

---

## Fighter spawn (real visible player)

After JoinRoom Found + bootstrap (`C2=10` / scene managers):

1. Read room `C1` (map).
2. SetProperty identity + `team` (Tr/Ct).
3. `CreateWorldObject` `Tr_Tr` / `Ct_Ct` at **catalog** pose for that map+team (`MatchSpawnCatalog` ← `decompiled/MAP_SPAWN_POINTS.json`).
4. Post-create `WorldObjectRpc` + standing `WorldObjectState`.

If the current map has no catalog entry, the probe waits for a **peer same-team** pawn CWO and copies that pose (still evidence-only).

Docs: [`decompiled/PLAYER_SPAWN.md`](../../decompiled/PLAYER_SPAWN.md), [`decompiled/MAP_SPAWN_POINTS.json`](../../decompiled/MAP_SPAWN_POINTS.json).

---

## Code

| File | Role |
|------|------|
| `GameMatchClient.cs` | Match channel + probe spawn / State loop |
| `GameNetClient.cs` | Lobby channel before op9 |
| `Match/MatchSpawnCatalog.cs` | Map×team poses |
| `Match/MatchCodec.cs` | CWO / Rpc / State builders |
| `tools/decode_allies_probe.py` | Decode SetProperties C2 timelines |

Mode-specific gold FSMs stay under `Net/Match/Modes/*/MATCH_*_PROBE.md`.
