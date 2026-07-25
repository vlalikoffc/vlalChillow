# Contributing to влалChillow

Thanks for looking at this project. It is an **evidence-driven reverse-engineering
project**, so the contribution rules are stricter than in a typical C# repo.
Read this file before opening a pull request.

## Non-negotiable rules

These rules keep the server compatible with a client we do not control. A pull
request that breaks one of them will be rejected regardless of how well it is
written.

1. **Do not invent wire behaviour.** Opcodes, field layouts, auth strings,
   discovery packets, game mode ids and timings come from ConnectAsClient
   captures or from the decompile. Never from intuition.
2. **Unknown means unimplemented.** If a field is not understood, log it or
   leave it out. Do not fill it with a plausible value.
3. **Never replay capture blobs.** Captures are evidence for decoding. They are
   never memcpy'd into a dedicated host reply.
4. **Modes do not call modes.** Shared behaviour belongs in
   `server/Net/Match/Kernel/`. Cross-imports between
   `server/Net/Match/Modes/<ModeId>/` folders are forbidden.
5. **Layers stay separate.** Discovery lives in `server/Lan/`, lobby in
   `server/Net/Lobby/`, match in `server/Net/Match/`. Match logic never leaks
   into discovery or lobby code.
6. **Plugins are sidecars.** Plugins may read state and display it. They must
   not produce wire traffic.
7. **Nothing local gets committed.** `decompiled/`, `original/`, game binaries,
   `captures/` and `logs/` stay on disk. CI enforces this.

## Where new code goes

| Change | Destination |
|---|---|
| Mode win rule or phase | `server/Net/Match/Modes/<ModeId>/` |
| Shared timer or deadline ownership | `server/Net/Match/Kernel/` |
| New match opcode handler | `server/Net/Match/Host/` partial |
| New lobby op | `server/Net/Lobby/` |
| Discovery change | `server/Lan/` |
| Console rendering or command parsing | `server/Dashboard/` |

See `ARCHITECTURE.md` for the full module map.

## File size budget

The project deliberately avoids god-files. Target a few hundred lines per file.
If a file passes ~1000 lines, split it into partials before adding more. Known
offenders are tracked as issues — do not add to them.

## Build and check locally

```bash
dotnet build server/StandChillow.LanServer.csproj -c Release
dotnet build server/plugins-src/StandChillow.Plugin.Telegram/StandChillow.Plugin.Telegram.csproj -c Release
dotnet format server/StandChillow.LanServer.csproj --severity warn
```

CI runs the same build on Linux, macOS and Windows.

## Regression invariants

These behaviours are known-good against a real client. Changing them requires
new capture evidence in the pull request description:

- Ranked2v2 RoundEnd sends `C2=101` **with** WinTeam
- WeaponDrop honours `exceptSender`
- Allies-family combat timeout is **109s**, matching the client's 1:49 UI
- Buy phase is `C2=22` for ~10s, then `C2=31`
- Win targets: Ranked2v2 / Ranked2v2Alt / RankedDefuse first-to-8, Defuse
  first-to-6, Duel first-to-8

## Commit and PR style

- Imperative subject line under ~72 characters.
- Body explains **why**, and cites the probe run or capture id for wire changes.
- One logical change per pull request.
- Update the README if a change alters what is actually playable. Honest status
  reporting is a feature of this project.

## Testing against a client

Use only networks and devices you control. `--client` mode is for learning the
protocol from a phone host. It is not a production hosting path, and captures it
produces must not be committed.

## Licence of contributions

By contributing you agree that your work is licensed under GPL-3.0, the same
licence as the project.
