# влалChillow server plugins

Plugins are **display / admin sidecars**. They must **not** invent match opcodes or replay
captures into the host reply path. Hook host events and admin APIs only.

## Folder layout

| Path | Role |
|------|------|
| `{exe}/plugins/` | **Runtime drop folder** (auto-created on host start). Put plugin `.dll` files here. |
| `{exe}/plugins/{Name}/` | Per-plugin data dir (`config.json`, etc.). |
| `server/Plugins/StandChillow.Server.Plugins/` | Stable **API** class library (`IServerPlugin`, `IPluginContext`, …). |
| `server/Plugins/Host/` | Loader inside LanServer (`PluginManager`). |
| `server/plugins-src/` | Plugin **source** projects (build → copy DLL into `bin/.../plugins/`). |

Default root: **beside the running binary** (`AppContext.BaseDirectory/plugins`), e.g.

```text
server/bin/Release/net8.0/plugins/
```

## Create a plugin

1. Class library, `net8.0`, reference **only** `StandChillow.Server.Plugins` (not the host exe).
2. Implement `IServerPlugin`.
3. Build and copy the DLL into `plugins/`.
4. Restart the host, or run console `plugins reload` (best-effort unload).

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Plugins\StandChillow.Server.Plugins\StandChillow.Server.Plugins.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>
</Project>
```

```csharp
using StandChillow.Server.Plugins;

public sealed class HelloPlugin : IServerPlugin
{
    public string Name => "Hello";
    public void OnLoad(IPluginContext ctx)
    {
        ctx.Log.Info("hello");
        ctx.Events.PlayerJoinedLobby += e => ctx.Log.Info($"{e.Name} joined");
        ctx.RegisterConsoleCommand("hello", c => { c.Reply("hi"); return true; });
    }
    public void OnUnload() { }
}
```

Host log on success: `[plugins] loaded Hello ← YourPlugin.dll`

## Telegram / tgbot connector (`StandChillow.Plugin.Telegram`)

**Not a second Telegram bot.** The Python tgbot already listens:

- Listener: `/home/vlal/tgbot/plugins/standchillow.py`
- Contract: `/home/vlal/tgbot/deploy/STANDCHILLOW_TELEMETRY.md`
- Ingest: `POST http://127.0.0.1:5267/state` with JSON `standchillow.status.v1`

This server plugin is the **HTTP client** that POSTs a status snapshot every ~2s (and tolerates tgbot being down). Bot → server admin commands are **not** in the current tgbot contract (ingest only); `IPluginHost.TryStartMatch` exists if that is added later.

### Build & install

```bash
cd server
dotnet build -c Release
dotnet build -c Release plugins-src/StandChillow.Plugin.Telegram/StandChillow.Plugin.Telegram.csproj
# DLL is copied to bin/Release/net8.0/plugins/StandChillow.Plugin.Telegram.dll
dotnet run -c Release --no-build
```

Keep tgbot running so `:5267` accepts POSTs. You should see `[plugins] loaded Telegram …` and the «Игра» tab go live.

### Config (`plugins/Telegram/config.json`)

Seeded on first load. Env overrides file:

| Key / env | Default | Meaning |
|-----------|---------|---------|
| `url` / `STANDCHILLOW_TG_URL` | `http://127.0.0.1:5267/state` | tgbot ingest URL |
| `interval_ms` / `STANDCHILLOW_TG_INTERVAL_MS` | `2000` | POST period |
| `enabled` / `STANDCHILLOW_TG_ENABLED` | `true` | master switch |
| `timeout_ms` | `1500` | HTTP timeout |

No bot token on the server — tokens stay in tgbot `.env`.

### Console

- `plugins` — list loaded plugins
- `plugins reload` — unload/reload DLLs (restart if unload fails)
- `tgstatus` — connector URL / fail streak (registered by Telegram plugin)

## Failure isolation

Plugin `OnLoad` / event handlers / commands are try/caught. One bad plugin must not crash the dedicated host.
