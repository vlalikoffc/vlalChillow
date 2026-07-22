# Match architecture (server)

Companion to root [`ARCHITECTURE.md`](../ARCHITECTURE.md). Behavioral wire evidence stays in [`MATCH_WORLD.md`](MATCH_WORLD.md).

## Layers

```
GameMatchHost (orchestrator partial)
  ├─ Host/          handshake, bootstrap, props, world relay, broadcast
  ├─ Kernel/        MatchClock (deadline ownership)
  ├─ MatchFlow.cs   phase enum + wipe/death rules (shared)
  ├─ MatchWorld.cs  prop keys, C2 ids, evidence timings
  ├─ MatchCodec.cs  builders/parsers only
  └─ Modes/<Id>/    per-GameModeId isolation
```

## Ranked2v2 wiring (today)

1. Room created with gap defaults `C0=Ranked2v2`, `C1=Sandstone 2x2`.  
2. Host poll → `TickMatchFlow()` in `Modes/Ranked2v2/GameMatchHost.Ranked2v2Phases.cs`.  
3. Round end gold bag in `…Ranked2v2RoundEnd.cs` (`C2=101`, flat TrScore/CtScore, WinTeam).  
4. WeaponDrop / WorldObjectRpc `exceptSender` in `Host/GameMatchHost.WorldObjects.cs`.

## Timers

- Evidence-labeled durations: `MatchFlowTestParams` in `MatchWorld.cs` (Allies test loop from docs/gold).  
- `MatchClock` owns deadline scheduling for future mode adapters — **do not invent** new wire durations into live TX without evidence.  
- Room `Time` / `RoundStartTime` = seconds (`TickCount/1000`); FetchServerTime header = ms.

## Adding a mode

1. Implement `IMatchMode` in `Modes/<Id>/` (replace stub).  
2. Register already present in `MatchModeRegistry`.  
3. Only then hook host tick to that mode — never from another mode folder.  
4. Document evidence in mode README + `GAME_MODES.md`.
