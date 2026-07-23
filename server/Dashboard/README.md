# CLI Dashboard

A full-screen terminal dashboard for the dedicated LAN host — a Standoff-2 style TAB
scoreboard plus lobby view, chat and kill feed. **Display-only**: it renders state the host
already tracks and events pushed through `DashboardHub`. It never touches the wire protocol.

## Run

```bash
cd server
dotnet run -c Release            # dedicated host + dashboard (default)
dotnet run -c Release -- "моё лобби"   # optional lobby title
```

The dashboard activates automatically whenever stdout is a real terminal. If output is piped
/ redirected, it falls back to plain logging.

- **Logs** go to `server/latest.log` (truncated on start) and `bin/.../logs/run-*.log`.
  In dashboard mode the console shows **only** the dashboard — normal `Console.WriteLine`
  logging is routed to the files. Tail the log in another shell:

  ```bash
  tail -f server/latest.log
  ```

## Views

- **Lobby** (before Play): header (mode / map / players / match state), a players card
  (all real members — the host still tracks everyone under the hide-peers illusion) and a
  chat card.
- **Match** (after Play / match in progress): centered mode·map + phase/timer, big
  team-colored score tiles, a two-column TAB scoreboard (**DEFENSE (CT)** cyan left,
  **ATTACK (T)** orange right) with `# · avatar · Name · Money · K A D Score · Ping`
  (`HOST` for the synthetic host actor), a round/MVP banner, and chat + kill feed below.
  Kill feed is `killer  Kill ☠ victim` with team colors and no red highlight.

## Commands (typed into the input line)

`start`/`play`, `mode <m>`, `map <l>`, `title <t>`, `roster`, `status`, `binds`, `quit`.
Phone chat slash-commands (`/mode`, `/map`, `/play`) keep working as before.

## Kill-feed attribution caveat

The wire only carries **victim** deaths (`death` prop / DeathMatch kill). The killer is a
**best-effort** guess: the dashboard credits the enemy-team actor whose `kills`/`fair_kills`
counter bumped within ~2.5s of the death. When no matching bump is seen the killer shows as
`?`. No packets are invented to attribute kills.

## Layout preview (dev aid)

```bash
STANDCHILLOW_DASH_DEMO=match dotnet run -c Release   # seeds fake actors/chat/feed
STANDCHILLOW_DASH_DEMO=lobby dotnet run -c Release
```
