## What changed

<!-- One or two sentences. -->

## Evidence

<!--
Protocol changes need evidence, not guesses. Link the probe run, capture id or
decompile note that justifies every opcode, field, timing or auth string you
touched. Write "none - no wire change" if this PR does not touch the wire.
-->

- Probe / capture:
- Client build tested against:

## Checklist

- [ ] `dotnet build server/StandChillow.LanServer.csproj -c Release` passes
- [ ] No invented opcodes, fields, auth strings or discovery packets
- [ ] No capture blobs replayed into dedicated host replies
- [ ] Mode logic stays inside `server/Net/Match/Modes/<ModeId>/` and does not import another mode
- [ ] Ranked2v2 RoundEnd (`C2=101` + WinTeam) and WeaponDrop `exceptSender` do not regress
- [ ] No file grew past ~1000 lines
- [ ] README claims still honest (playable modes, working maps)
