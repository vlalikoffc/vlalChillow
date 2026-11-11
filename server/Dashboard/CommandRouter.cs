using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Plugins;

namespace StandChillow.LanServer.Dashboard;

/// <summary>Dispatches console-only commands and delegates shared aliases to the phone-chat command path.</summary>
public sealed class CommandRouter
{
    private readonly GameNetHost _game;
    private readonly PluginManager _plugins;
    private readonly LanDiscoveryHost _discovery;
    private readonly Func<LanDiscoveryInfo> _getDiscoveryInfo;
    private readonly Action<LanDiscoveryInfo> _setDiscoveryInfo;
    private readonly Action<string> _setLobbyTitle;
    private readonly Action _syncDiscovery;
    private readonly Action<string> _feedback;

    public CommandRouter(
        GameNetHost game,
        PluginManager plugins,
        LanDiscoveryHost discovery,
        Func<LanDiscoveryInfo> getDiscoveryInfo,
        Action<LanDiscoveryInfo> setDiscoveryInfo,
        Action<string> setLobbyTitle,
        Action syncDiscovery,
        Action<string> feedback)
    {
        _game = game;
        _plugins = plugins;
        _discovery = discovery;
        _getDiscoveryInfo = getDiscoveryInfo;
        _setDiscoveryInfo = setDiscoveryInfo;
        _setLobbyTitle = setLobbyTitle;
        _syncDiscovery = syncDiscovery;
        _feedback = feedback;
    }

    public bool Handle(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return true;

        // Slash form: /set start, /play, … — same as phone chat (silent command, no chat bubble).
        if (trimmed.StartsWith('/'))
        {
            var body = trimmed[1..].Trim();
            if (body.Length == 0) return true;
            return Handle(body);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Length == 0) return true;
        var cmd0 = parts[0].ToLowerInvariant();

        // Shared host commands (mode/map/set/play/start/end/help) — same as in-game /.
        if (cmd0 is "mode" or "map" or "maps" or "level" or "levels" or "set"
            or "play" or "start" or "startmatch" or "старт" or "игра"
            or "end" or "stopmatch" or "стопматч" or "help" or "?" or "команды"
            or "режим" or "карта")
        {
            if (!_game.TryDispatchHostCommand(trimmed, _feedback, who: "console"))
                _feedback($"unknown command '{trimmed}' — help");
            return true;
        }

        switch (cmd0)
        {
            case "quit":
            case "exit":
                return false;
            case "stat":
                // Typo for status — never chat/teardown.
                goto case "status";
            case "title" when parts.Length == 2:
                _setLobbyTitle(parts[1]);
                _feedback($"title → {parts[1]}");
                break;
            case "members" when parts.Length == 2 && byte.TryParse(parts[1], out var n):
                var next = _getDiscoveryInfo() with { MemberCount = n };
                _setDiscoveryInfo(next);
                _discovery.UpdateInfo(next);
                break;
            case "binds":
                foreach (var e in _discovery.BoundEndpoints)
                    _feedback($"{e.InterfaceName} {e.Address} br={e.Broadcast}");
                break;
            case "roster":
                foreach (var (name, status) in _game.Session.SnapshotRoster())
                    _feedback($"{name} ({LobbySession.FormatStatus(status)})");
                break;
            case "config":
                if (parts.Length == 2 && parts[1].Equals("reload", StringComparison.OrdinalIgnoreCase))
                    _game.ReloadConfig(_feedback);
                else
                    _game.ShowConfig(_feedback);
                break;
            case "status":
                _syncDiscovery();
                _feedback(
                    $"members={_getDiscoveryInfo().MemberCount} mode={_game.Session.GameModeId} " +
                    $"match={_game.MatchStarted} peers={_game.PeerCount} joined={_game.JoinedCount} " +
                    $"matchPeers={_game.Match.PeerCount}");
                _feedback(MatchHostSettings.FormatStatusLine());
                break;
            case "plugins":
            case "plugin":
            {
                var sub = parts.Length > 1 ? parts[1].Trim() : "";
                if (sub.Equals("reload", StringComparison.OrdinalIgnoreCase))
                {
                    var n = _plugins.Reload();
                    _feedback($"[plugins] reloaded → {n} ({string.Join(", ", _plugins.LoadedNames)})");
                }
                else
                {
                    var names = _plugins.LoadedNames;
                    _feedback(
                        names.Count == 0
                            ? $"[plugins] none loaded (drop DLLs in {_plugins.PluginsRoot})"
                            : $"[plugins] loaded: {string.Join(", ", names)}");
                    _feedback("usage: plugins | plugins reload");
                }
                break;
            }
            default:
            {
                // Plugin console commands first; else free text → lobby chat as Server.
                var cmd = parts[0];
                var argsRest = parts.Length > 1 ? parts[1] : "";
                if (_plugins.TryHandleConsoleCommand(cmd, argsRest, _feedback))
                    break;
                _game.BroadcastConsoleChat(trimmed);
                break;
            }
        }
        return true;
    }

}
