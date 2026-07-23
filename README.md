# влалChillow

LAN multiplayer **dedicated server** for [**StandChillow**](https://play.google.com/store) — a Unity mobile FPS with built-in LAN play, but no official dedicated host. This project implements a C# LAN server phones can discover and join on your local network.

Authorized reverse-engineering work for **self-hosted LAN** with the game owner's permission. Not a public cheat tool, not an official product, and not affiliated with the game publisher.

## What works

- **LAN discovery** — Bonjour V2 probe/reply on UDP **5056**; phones find the host like a phone-hosted lobby
- **Lobby** — LiteNetLib on UDP **7778** (join, roster, chat, mode/map selection)
- **Match host** — LiteNetLib on UDP **7777** after `/play` (Handshake + JoinRoom)
- **Console / chat commands** — `/mode`, `/map`, `/set`, `/play`, `/start`, status, plugins
- **Escalation** on **Prison** (and other bomb maps) — live mode loop from reverse + gold captures
- **Ranked 2v2** — default lobby mode (MR-8, first to 5)
- **CLI dashboard** — full-screen terminal UI when stdout is a TTY
- **Plugins** — optional sidecars (Telegram status pusher included in source)

Other game modes are registered but may be partial or unimplemented — check each mode's README under `server/Net/Match/Modes/`.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- StandChillow client on the same LAN (Wi‑Fi/Ethernet; VPN interfaces are skipped for bind/advertise)
- Linux, macOS, or Windows

## Quick start

```bash
cd server
dotnet build -c Release
dotnet run -c Release
```

Phones: open StandChillow → LAN → your lobby should appear → join → chat.

### Ports

| Port | Role |
|------|------|
| **5056/udp** | Discovery only (Bonjour V2 / `sosalbolt482` probe) |
| **7778/udp** | Lobby (LiteNetLib join after discovery) |
| **7777/udp** | Match (after host starts the game) |

### Starting a match

Two steps matter:

1. **`play`** (or phone **`/play`**) — creates the match channel and moves clients to UDP 7777. Use again for rematch (tears down the previous match).
2. **`set start`** / **`start`** / **`/set start`** — arms WarmUp once both teams have at least one player. Without this, the match stays in waiting (C2=10).

Typical flow: everyone joins lobby → host runs `play` → both teams pick CT/T → host runs `set start` → WarmUp → live rounds.

### Useful console commands

```
mode escalation          # or ranked2v2, defuse, …
map Prison               # map id from GameModeCatalog
set start                # arm WarmUp when both teams ready
play                     # launch / rematch
status                   # lobby + match summary
set round 8              # MR-N rounds (default 8 → first to 5)
plugins                  # list loaded plugins
quit
```

Phone chat accepts the same slash commands (`/mode`, `/map`, `/set start`, `/play`, …).

Optional lobby title as first argument:

```bash
dotnet run -c Release -- "my LAN room"
```

### ConnectAsClient (learning only)

Sniff a **real phone host** to learn protocol — do not use for production hosting:

```bash
dotnet run -c Release -- --client
```

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
server/Plugins/   # Plugin API + loader
server/plugins-src/   # Plugin source (Telegram)
ARCHITECTURE.md   # Internal module map for contributors
```

Reverse-engineering notes and Ghidra dumps stay **local** (`decompiled/`) and are not part of the published tree.

## Protocol fidelity

Everything the server sends is derived from client reverse-engineering and observed LAN traffic. Unknown fields are logged or left unimplemented — not guessed. Discovery auth strings, LiteNetLib keys, opcodes, and game mode IDs match what the mobile client expects.

## License / scope

Private research and self-host tooling for StandChillow LAN play with authorization from the game side. Use only on networks and instances you control.
