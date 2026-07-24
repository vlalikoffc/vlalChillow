using System.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

public sealed partial class GameNetHost
{
    private bool TryHandleSlashCommand(ConnectedPlayer player, string message)
    {
        void Reply(string text) => ReplyPrivate(player, text);
        return TryDispatchHostCommand(message, Reply, who: $"chat:{player.Name}", player);
    }

    /// <summary>
    /// Shared host command dispatch for lobby chat <c>/</c>, mid-match ChatManager <c>/</c>,
    /// and console/dashboard (with or without leading <c>/</c>).
    /// </summary>
    /// <returns>True if the line was recognized as a host command (handled or bad args).</returns>
    public bool TryDispatchHostCommand(
        string raw,
        Action<string> reply,
        string who = "console",
        ConnectedPlayer? player = null)
    {
        var message = raw.Trim();
        if (message.Length == 0)
            return false;
        if (message.StartsWith('/'))
            message = message[1..].Trim();
        if (message.Length == 0)
            return false;

        var space = message.IndexOf(' ');
        var cmd = space < 0 ? message : message[..space];
        var args = space < 0 ? "" : message[(space + 1)..].Trim();

        if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("?", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("команды", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in FormatCommandHelp().Split('\n'))
                reply(line.TrimEnd());
            return true;
        }

        if (cmd.Equals("mode", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("режим", StringComparison.OrdinalIgnoreCase))
        {
            HandleModeCommand(reply, args, who);
            return true;
        }

        if (cmd.Equals("map", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("maps", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("level", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("levels", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("карта", StringComparison.OrdinalIgnoreCase))
        {
            HandleMapCommand(reply, args, who);
            return true;
        }

        if (cmd.Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            HandleSetCommand(reply, args, who, player);
            return true;
        }

        if (IsEndMatchCommand(cmd, args))
        {
            TryEndMatchNow(who, reply);
            return true;
        }

        if (IsPlayCommand("/" + cmd)
            || cmd.Equals("play", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("игра", StringComparison.OrdinalIgnoreCase))
        {
            TryStartMatch(who);
            reply("play — запуск/рематч");
            return true;
        }

        if (IsArmWarmupCommand("/" + cmd)
            || cmd.Equals("start", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("startmatch", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("старт", StringComparison.OrdinalIgnoreCase))
        {
            // Bare start only — "set start" is under /set.
            if (args.Length == 0)
            {
                TryArmWarmupOrFirstPlay(who);
                return true;
            }
        }

        try
        {
            if (PluginChatCommandHandler?.Invoke(who, cmd, args) == true)
                return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] plugin chat cmd '{cmd}': {ex.Message}");
            return true;
        }

        return false;
    }

    private static bool IsEndMatchCommand(string cmd, string args) =>
        cmd.Equals("end", StringComparison.OrdinalIgnoreCase)
        || cmd.Equals("stopmatch", StringComparison.OrdinalIgnoreCase)
        || cmd.Equals("стопматч", StringComparison.OrdinalIgnoreCase);

    public static string FormatCommandHelp() =>
        """
        команды: /mode /map /set /play /start /end /help
          /mode [alias]  список или сменить режим
          /map [alias]   карты текущего режима (короткие alias)
          /set …         параметры (start, end, round, wins, …)
          /play          запуск / рематч
          /start         WarmUp (когда матч уже поднят)
          /end           завершить матч сразу (без 5с)
        """.Trim();

    private static string FormatUnknownCommandHelp(string message) =>
        $"неизвестная команда '{message}' — /help";

    private void ReplyPrivate(ConnectedPlayer player, string text) =>
        SendServerChatTo(player.Peer, text);

    private void NotifyMatchPlayerJoined(string name)
    {
        var player = _session.JoinedPlayers()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
        if (player is not null)
            _session.TrySetPlayerStatus(player, PlayerLobbyStatus.InMatch);
        BroadcastServerChat($"{name} Подключился!");
        BroadcastServerChat(_session.BuildPlayerListMessage());
        try { _onRosterChanged?.Invoke(); } catch { /* ignore */ }
    }

    private void NotifyMatchPlayerLeft(string name)
    {
        var player = _session.JoinedPlayers()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
        if (player is not null)
            _session.TrySetPlayerStatus(player, PlayerLobbyStatus.Lobby);
        BroadcastServerChat($"{name} Отключился!");
        BroadcastServerChat(_session.BuildPlayerListMessage());
        try { _onRosterChanged?.Invoke(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Immediate match teardown — same cleanup as series end, no MatchResults 5s wait.
    /// </summary>
    public bool TryEndMatchNow(string who, Action<string>? reply = null)
    {
        bool started;
        lock (_gate)
            started = _matchStarted;
        if (!started)
        {
            reply?.Invoke("нет активного матча");
            return false;
        }

        Console.WriteLine($"[lobby] {who}: /end — immediate return to lobby (no 5s wait)");
        BroadcastServerChat("матч завершён досрочно");
        ReturnPlayersToLobbyIdle($"end:{who}");
        reply?.Invoke("матч закрыт (7777 stopped)");
        return true;
    }

    private void HandleSetCommand(
        Action<string> reply,
        string args,
        string who,
        ConnectedPlayer? player)
    {
        if (string.IsNullOrWhiteSpace(args)
            || args.Equals("help", StringComparison.OrdinalIgnoreCase)
            || args.Equals("?", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in MatchHostSettings.FormatSetHelp().Split('\n'))
                reply(line.TrimEnd());
            reply(MatchHostSettings.FormatStatusLine());
            return;
        }

        var parts = args.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var key = parts[0];
        var val = parts.Length > 1 ? parts[1] : "";

        if (key.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            BroadcastServerChat(MatchHostSettings.FormatStatusLine());
            return;
        }

        if (key.Equals("end", StringComparison.OrdinalIgnoreCase)
            || key.Equals("stopmatch", StringComparison.OrdinalIgnoreCase))
        {
            TryEndMatchNow(who, reply);
            return;
        }

        if (key.Equals("start", StringComparison.OrdinalIgnoreCase)
            || key.Equals("startmatch", StringComparison.OrdinalIgnoreCase))
        {
            ArmMatchStart(who);
            return;
        }

        if (key.Equals("round", StringComparison.OrdinalIgnoreCase)
            || key.Equals("rounds", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(val, out var n) || n < 1)
            {
                reply("usage: /set round <N>  (e.g. /set round 16 → first to 9)");
                return;
            }

            MatchHostSettings.TotalRounds = n;
            var msg =
                $"раунды → MR-{MatchHostSettings.TotalRounds} " +
                $"(Escalation: победа до {MatchHostSettings.TotalRounds / 2 + 1}, " +
                $"или ничья {MatchHostSettings.TotalRounds / 2}:{MatchHostSettings.TotalRounds / 2})";
            Console.WriteLine($"[lobby] {who}: {msg}");
            BroadcastServerChat(msg);
            return;
        }

        if (key.Equals("wins", StringComparison.OrdinalIgnoreCase)
            || key.Equals("win", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(val, out var n) || n < 1)
            {
                reply("usage: /set wins <N>  (Allies first-to-N, default 8)");
                return;
            }

            MatchHostSettings.WinsNeeded = n;
            var msg = $"победа → first-to-{MatchHostSettings.WinsNeeded} (Allies / Ranked2v2)";
            Console.WriteLine($"[lobby] {who}: {msg}");
            BroadcastServerChat(msg);
            return;
        }

        if (key.Equals("team", StringComparison.OrdinalIgnoreCase))
        {
            if (player is null)
            {
                reply("/set team только из чата в матче (не из консоли)");
                return;
            }

            if (!TryParseTeamArg(val, out var team))
            {
                reply("usage: /set team ct|tr|t|spectator");
                return;
            }

            var ip = player.Peer.Address;
            if (ip is null)
            {
                reply("нет IP у lobby-peer");
                return;
            }

            if (!_match.TryForceTeamByIp(ip, team, out var teamMsg))
            {
                reply(teamMsg);
                return;
            }

            Console.WriteLine($"[lobby] {player.Name}: {teamMsg}");
            BroadcastServerChat($"{player.Name}: {teamMsg}");
            return;
        }

        if (key.Equals("money", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(val, out var money))
            {
                reply("usage: /set money <0-16000>");
                return;
            }

            MatchHostSettings.RoundStartMoney = money;
            var msg = $"money → {MatchHostSettings.RoundStartMoney}";
            Console.WriteLine($"[lobby] {who}: {msg}");
            BroadcastServerChat(msg);
            return;
        }

        if (TrySetTimedParam(key, val, out var timedMsg, out var timedErr))
        {
            if (timedErr is not null)
            {
                reply(timedErr);
                return;
            }

            Console.WriteLine($"[lobby] {who}: {timedMsg}");
            BroadcastServerChat(timedMsg!);
            return;
        }

        reply($"unknown /set '{key}' — /set help");
    }

    private static bool TryParseTeamArg(string raw, out MatchTeam team)
    {
        team = MatchTeam.None;
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "ct":
            case "cts":
            case "def":
            case "defense":
                team = MatchTeam.Ct;
                return true;
            case "tr":
            case "t":
            case "terror":
            case "atk":
            case "attack":
                team = MatchTeam.Tr;
                return true;
            case "spec":
            case "spectator":
            case "spectate":
            case "none":
                team = MatchTeam.Spectator;
                return true;
            default:
                return false;
        }
    }

    private static bool TrySetTimedParam(string key, string val, out string? okMsg, out string? err)
    {
        okMsg = null;
        err = null;
        if (!int.TryParse(val, out var sec))
        {
            if (key is "fuse" or "prep" or "prestart" or "warmup" or "roundtime" or "pause")
            {
                err = $"usage: /set {key} <seconds>";
                return true;
            }

            return false;
        }

        switch (key.ToLowerInvariant())
        {
            case "fuse":
                MatchHostSettings.BombFuseSeconds = sec;
                okMsg = $"fuse → {MatchHostSettings.BombFuseSeconds}s";
                return true;
            case "prep":
                MatchHostSettings.PrepSeconds = sec;
                okMsg = $"prep → {MatchHostSettings.PrepSeconds}s";
                return true;
            case "prestart":
                MatchHostSettings.PreStartSeconds = sec;
                okMsg = $"prestart → {MatchHostSettings.PreStartSeconds}s";
                return true;
            case "warmup":
                MatchHostSettings.WarmupSeconds = sec;
                okMsg = $"warmup → {MatchHostSettings.WarmupSeconds}s";
                return true;
            case "roundtime":
                MatchHostSettings.RoundSeconds = sec;
                okMsg = $"roundtime → {MatchHostSettings.RoundSeconds}s";
                return true;
            case "pause":
                MatchHostSettings.RoundEndPauseSeconds = sec;
                okMsg = $"pause → {MatchHostSettings.RoundEndPauseSeconds}s";
                return true;
            default:
                return false;
        }
    }

    private void HandleModeCommand(Action<string> reply, string args, string who)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            foreach (var line in GameModeCatalog.FormatModeHelp().Split('\n'))
                reply(line.TrimEnd());
            return;
        }

        if (!GameModeCatalog.TryResolveMode(args, out var mode))
        {
            foreach (var line in GameModeCatalog.FormatModeHelp().Split('\n'))
                reply(line.TrimEnd());
            reply($"неизвестный режим '{args}'");
            return;
        }

        var levels = _session.SelectedLevels
            .Where(L => mode.Levels.Any(c => c.Equals(L, StringComparison.Ordinal)))
            .ToList();
        if (levels.Count == 0)
            levels.Add(GameModeCatalog.DefaultLevelFor(mode.GameModeId));

        _session.GameModeId = mode.GameModeId;
        _session.SelectedLevels = levels;
        BroadcastModeLevelProps();
        _onRosterChanged?.Invoke();

        var msg =
            $"режим → {mode.GameModeId} ({mode.DisplayName}); карты: {string.Join(", ", levels)}";
        Console.WriteLine($"[lobby] {who}: {msg}");
        BroadcastServerChat(msg);
        reply(msg);
    }

    private void HandleMapCommand(Action<string> reply, string args, string who)
    {
        var modeId = _session.GameModeId;
        if (!GameModeCatalog.TryGetMode(modeId, out var mode))
        {
            foreach (var line in GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels).Split('\n'))
                reply(line.TrimEnd());
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            foreach (var line in GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels).Split('\n'))
                reply(line.TrimEnd());
            return;
        }

        var tokens = args.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            foreach (var line in GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels).Split('\n'))
                reply(line.TrimEnd());
            return;
        }

        var resolved = new List<string>();
        foreach (var token in tokens)
        {
            if (!GameModeCatalog.TryResolveLevel(mode, token, out var level, out var err))
            {
                reply(err ?? $"bad map '{token}'");
                foreach (var line in GameModeCatalog.FormatMapHelp(modeId, _session.SelectedLevels).Split('\n'))
                    reply(line.TrimEnd());
                return;
            }

            if (!resolved.Contains(level, StringComparer.Ordinal))
                resolved.Add(level);
        }

        _session.SelectedLevels = resolved;
        BroadcastModeLevelProps();
        _onRosterChanged?.Invoke();

        var msg = $"карты ({mode.GameModeId}) → {string.Join(", ", resolved)}";
        Console.WriteLine($"[lobby] {who}: {msg}");
        BroadcastServerChat(msg);
        reply(msg);
    }
}
