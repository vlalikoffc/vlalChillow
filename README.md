# влалChillow

LAN multiplayer **dedicated server** for [**StandChillow**](https://t.me/chillow) — a Unity mobile FPS with built-in LAN play, but no official dedicated host. This project implements a C# LAN server phones can discover and join on your local network.

Unofficial LAN dedicated server for **self-hosted multiplayer** — reverse-engineering authorized by the game owner. Not an official product and not affiliated with the game publisher.

**Supported client version:** StandChillow **2.07 OBT F1 only**. Version 2.06 is intentionally rejected; other builds are untested.

## What works

- **Lobby** — LiteNetLib on UDP **7778** (join, full roster, chat, mode/map selection, `/end`)
- **Auto-discovery** — 2.07 Bonjour V3 (`SC LAN Bonjour V3`) probe/reply on UDP **5056**; when phones search LAN lobbies, this host appears automatically (same as a phone-hosted lobby)
- **Match channel** — after `play`, clients move to UDP **7777**
- **CLI dashboard** — TTY console + phone chat slash commands (`/mode`, `/map`, `/set start`, `/play`, `/end`, …)
- **Graceful shutdown** — CTRL+C tears down cleanly; rematch via `play` again
- **ConnectAsClient probe** — optional learning client (`--client`) can observe a real phone host or spawn a visible CT/Tr fighter from the map spawn catalog (see `server/Net/PROBE_README.md`)

### Playable modes (from phone-host gold probes)

| Mode | How to start | Notes |
|------|----------------|-------|
| **Escalation** | `mode escalation` + `map Prison` | Bomb rounds; auto-plant → combat → round end. **Prison only** for now. |
| **Ranked2v2 (Allies)** | `mode ranked2v2` + `* 2x2` map | First-to-8, half-time after round 7, ~10s buy (C2=22), plant/defuse, **109s** host combat timeout |
| **Ranked2v2Alt** | `mode ranked2v2alt` | Same Allies FSM; Alt map half |
| **RankedDefuse** | `mode rankeddefuse` | Same Allies bomb-round flow; first-to-8 |
| **Defuse** | `mode defuse` | Same Allies flow; casual first-to-**6** |
| **Duel** | `mode duel` + map e.g. `Block` | First-to-8 eliminate rounds; host rolls loadouts / round modifiers |

Mode FSMs are driven from ConnectAsClient captures and decompile — not invented. Per-mode notes live under `server/Net/Match/Modes/<Mode>/`.

### Honest limits

- **Escalation** — only **Prison** is a working map right now (other Escalation maps untested / not ready)
- **Duel OnlyGrenades** — modifier fully defined in `DuelLoadouts`, but **RNG-gated off** until host grenade throw works
- **ArmsRace / FreeForAll / DeathMatch** — catalog stubs or not listed as playable; refuse or incomplete wire drive
- Maps: use each mode’s `SelectedLevels` list where the mode is playable

## Load and stability

The dedicated host is built to stay smooth well beyond a 4-phone lobby. Two things used to
make it melt at ~10 players, and both are fixed:

- **Per-packet disk I/O in the network thread.** Every packet used to be written synchronously
  as a capture file *and* logged through a lock into two auto-flushed log files. At 10 players
  that is thousands of file operations per second on the LiteNetLib poll thread: the socket
  receive buffer overflows, reliable packets retransmit (the “ping 200” everyone sees), and
  eventually the event queue backs up until the host stops sending. Captures are now **off by
  default** and, when enabled, go through a single background writer with a bounded queue and
  a file/byte budget. Log lines are queued and batched by one writer thread too.
- **Unbounded fan-out to slow phones.** Broadcasts now check each peer’s reliable queue.
  A peer that cannot keep up gets game ticks dropped (it will rubber-band, but it can never
  stall the room); if the whole send fails the packet is skipped instead of throwing.

Other changes that matter under load:

| Area | Change |
|------|--------|
| Tick loop | 128 Hz busy-poll → 33 ms loop, socket drained first; phase/wipe/fuse checks stay per-tick, `WorldObjectState` ticks every 50 ms |
| Reliable queue | `PacketPoolSize` 1000 → 4096, disconnect timeout 5 s → 10 s (a hiccup no longer kills the session) |
| Logging | `Console.WriteLine` in hot paths is gated behind `--verbose`; silent by default |
| Health line | `[health] peers=… reliableQueue=… worstPeer=… slowPeers=… droppedSends=… rttMax=… captures=…` every 10 s |

**The wire is unchanged.** No packet format, opcode, field or client-facing timing was
touched — only when/how the host logs and how it schedules its own work. Anything the clients
used to receive in a round, they still receive.

### Debugging a protocol issue

```bash
dotnet run -c Release -- --verbose          # or VLAL_VERBOSE=1
```

`--verbose` restores the old behaviour: per-packet RX/TX lines and capture `.bin` files under
`captures/` (written asynchronously, capped at 4000 files / 64 MB per run). Expect heavy disk
I/O and worse performance — use it only when capturing evidence.

## Screenshots

Escalation on Prison over LAN — from lobby ready-up through round outcomes on the dedicated host.

| Waiting for players | After bomb defuse |
|:-:|:-:|
| ![Escalation on Prison — waiting for players](docs/screenshots/waiting-for-players.png) | ![Round outcome after bomb defuse](docs/screenshots/bomb-defused-mvp.png) |
| CT/T roster ready in lobby | MVP awarded on successful defuse |

## Requirements

- StandChillow **2.07 OBT F1** on the same LAN (Wi‑Fi/Ethernet; VPN interfaces are skipped for bind/advertise)
- Linux x64, macOS x64, or Windows x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) **only when building from source** — downloadable server binaries are self-contained

## Quick start

### Ready-to-run server (no .NET installation)

Every successful build on `main` publishes three artifacts under **Actions → CI → latest successful run**:

- `standchillow-server-win-x64` → run `StandChillow.LanServer.exe`
- `standchillow-server-linux-x64` → run `./StandChillow.LanServer`
- `standchillow-server-osx-x64` → run `./StandChillow.LanServer`

On Linux/macOS, first run `chmod +x StandChillow.LanServer`. Tags matching `v*` also create a GitHub Release with ZIP files for all three platforms.

### Build from source

```bash
cd server
dotnet build -c Release
dotnet run -c Release
```

Phones: open StandChillow → LAN → your lobby should appear → join → chat. The interactive dashboard requires an ANSI-capable TTY; use `--console` for plain logs or `--dashboard` to force the full-screen UI.

### Ports

| Port | Role |
|------|------|
| **5056/udp** | Discovery only (2.07 Bonjour V3 LAN interoperability probe) |
| **7778/udp** | Lobby (LiteNetLib join after discovery) |
| **7777/udp** | Match (after host starts the game) |

### Starting a match

Two steps matter:

1. **`play`** (or phone **`/play`**) — creates the match channel and moves clients to UDP 7777. Use again for rematch (tears down the previous match).
2. **`set start`** / **`start`** / **`/set start`** — arms WarmUp / waiting exit once both teams have at least one player. Without this, the match stays in waiting (C2=10).

Typical flow: everyone joins lobby → host picks `mode` + `map` → host runs `play` → both teams pick CT/T → host runs `set start` → WarmUp / buy → live rounds.

### Config file (`config.json`)

On first launch the server writes a **commented `config.json`** next to the executable and
prints where. Edit it, then restart the host (or run console `config reload`).
Comments use `//` and are short on purpose — one line per knob.

```jsonc
{
  // ----- ЗАПУСК: только для выделенного сервера -------------------------
  "launch": {
    "args": [],              // доп. аргументы, как в командной строке: "--verbose"
    "dashboard": null,       // true / false / null — авто
    "title": null,           // название лобби (null — дефолтное)
    "verbose": false
  },
  "server":  { "welcomeMessage": "…", "maxMembers": 16, "lobbyPort": 7778 },
  "network": { "bindAddress": "0.0.0.0", "ignoreVpn": true, "advertiseIp": null },
  "admin":   { "nicknames": [ "влал" ], "adminOnlyCommands": [ … ], "anyoneCanStart": false },
  "matchStart": { "mode": "manual", "auto": { … }, "ready": { … } },
  "timings": { "warmup": 3, "preStart": 3, "prep": 10, "roundTime": 109,
               "roundEndPause": 6, "bombFuse": 40, "totalRounds": 8,
               "winsNeeded": 0, "roundStartMoney": 800 },
  "tuning":  { "tickMs": 33, "stateIntervalMs": 50, "reliableQueueSoftLimit": 256,
               "packetPoolSize": 4096, "disconnectTimeoutMs": 10000,
               "netUpdateTimeMs": 15, "pingIntervalMs": 1000, "healthIntervalSec": 10 },
  "debug":   { "verbose": false, "captures": false, "capturesDir": null,
               "capturesMaxFiles": 4000, "capturesMaxBytesMb": 64,
               "matchChat": false, "health": true,
               "logPackets": false, "logDiscovery": false }
}
```

**`launch`** — параметры, которые обычно пишут в командной строке. `launch.args` дописываются
к реальным аргументам (консоль главнее). `--client` там **запрещён**: конфиг не может
переключить сервер в режим пробы.

**`debug`** — только для выделенного сервера. Режим пробы (`--client`) `config.json` не читает
вообще: ни `debug`, ни `tuning`, ни `timings` не влияют на пробу.

**`network`** — где открывать сервер. `bindAddress: "0.0.0.0"` слушает **все** интерфейсы
сразу; задай конкретный адрес, чтобы слушать только его. `ignoreVpn: true` (по умолчанию)
держит VPN/Hamachi/Radmin/ZeroTier в стороне; `false` — сервер слушает и на них.
`advertiseIp` — что писать телефонам в op9 как адрес матча (`null` — авто по LAN);
задай вручную, если играете через Hamachi/Radmin и авто берёт не тот адрес.

**`timings`** — фазы матча в секундах, применяются и через `/set`. `winsNeeded: 0` — взять
дефолт режима (Allies 8, Defuse 6); `winsNeeded: N` — жёстко N и режим его не перебивает.

**`tuning`** — темп хоста. `tickMs` — период цикла, `stateIntervalMs` — рассылка поз,
`reliableQueueSoftLimit` — порог, после которого дропается только косметика (позы/RPC),
`healthIntervalSec` — как часто печатать `[health]` (0 — выключить).

**`debug.captures`** — писать capture-файлы (по умолчанию выключено: на 10+ игроках это
тысячи файловых операций в секунду). Отдельные флаги `logPackets`/`logDiscovery` включают
шумный per-packet лог без общего `verbose`.

**Who is an admin.** Matched by the lobby nickname, case-insensitive. Anyone not in
`admin.nicknames` gets a private «только для админов» reply and the command does not run.
`/help`, `/ready` and `/unready` are always open to everyone.

**Three ways to start a match** — pick one with `matchStart.mode`:

| Mode | How the match starts |
|------|----------------------|
| **`manual`** (default) | Only by `/start` or `/set start`. Who may use it is decided by `admin` above. |
| **`auto`** | The server arms the start **by itself** as soon as both teams have at least `auto.minPlayersPerTeam` players. Does exactly what `/set start` does. |
| **`ready`** | Everyone currently **in the match** types `/ready`. The server posts «ник готов (1/3)» in chat and starts once all of them are ready. |

`/ready` is rejected from the lobby — you must be in the match. If a ready player leaves the
match, the vote is dropped and the counter is recalculated, so a leaver can never block the
start. `/unready` cancels a vote (disable with `ready.allowUnready`).

Console helpers: `config` prints the active settings (admins, start mode, timings, tuning,
debug), `config reload` re-reads the file and applies it without a restart.

### Useful console commands

```
mode escalation          # or ranked2v2, defuse, duel, …
map Prison               # Escalation: Prison only for now; other modes: SelectedLevels
set start                # arm when both teams ready
play                     # launch / rematch
end                      # end match / return path
status                   # lobby + match summary
plugins                  # list loaded plugins
config                   # show active config (admins, timings, debug)
config reload            # re-read config.json without restarting
quit
```

Phone chat accepts the same slash commands (`/mode`, `/map`, `/set start`, `/play`, `/end`, …).

Optional lobby title as first argument:

```bash
dotnet run -c Release -- "my LAN room"
```

### ConnectAsClient (learning only)

Sniff a **real phone host** to learn protocol — do not use for production hosting:

```bash
dotnet run -c Release -- --client
dotnet run -c Release -- --client --team ct    # visible fighter at catalog spawn
dotnet run -c Release -- --client --team tr
```

Captures are for analysis only — never replayed as host replies. See `server/Net/PROBE_README.md`.

## Plugins

Build the optional Telegram status plugin:

```bash
dotnet build -c Release plugins-src/StandChillow.Plugin.Telegram/StandChillow.Plugin.Telegram.csproj
```

Copy the DLL into `bin/Release/net8.0/plugins/` (the build target does this automatically when the main project was built first). See `server/Plugins/README.md`.

Plugins are admin/display sidecars only — they must not invent wire protocol or replay capture blobs.

## Project layout

```
server/           # Dedicated host (build & run here)
server/Net/Match/Modes/   # Per-mode FSM + probe docs
server/Plugins/   # Plugin API + loader
server/plugins-src/   # Plugin source (Telegram)
ARCHITECTURE.md   # Internal module map for contributors
```

Reverse-engineering notes and Ghidra dumps stay **local** (`decompiled/`) and are not part of the published tree.

## Protocol fidelity

Everything the server sends is derived from client reverse-engineering and observed LAN traffic. Unknown fields are logged or left unimplemented — not guessed. Discovery auth strings, LiteNetLib keys, opcodes, and game mode IDs match what the mobile client expects.

## Reverse-engineering basis and boundaries

The game owner has authorized the reverse engineering used for this project. Its sole interoperability target is self-hosted **LAN play** with StandChillow 2.07 OBT F1; the project does not connect to or provide access to official online services and contains no anti-cheat bypass.

No game assets, game binaries, or decompiled source code are distributed in this repository. Local reverse-engineering dumps, original binaries, captures, and logs are excluded by `.gitignore`, and the CI hygiene guard rejects them if they are accidentally tracked.

`--client` mode is a protocol-learning tool only. Run it exclusively on networks and game instances you control; it is not a production hosting path or a tool for accessing third-party services.

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE) (GPL-3.0).

You are free to use, modify, and distribute this code. If you build on it or ship a derivative work, that work must also be open source under GPL-3.0 (copyleft).

Copyright (C) 2026 влал (vlalikoffc).

## Scope

Unofficial self-host tooling for StandChillow LAN play, with reverse-engineering authorized by the game owner. Use only on networks and instances you control.
