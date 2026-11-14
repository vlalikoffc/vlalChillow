using System.Net;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

public sealed partial class GameNetHost
{
    private bool TryHandleSlashCommand(ConnectedPlayer player, string message)
    {
        void Reply(string text) => ReplyPrivate(player, text);

        // Права: часть команд доступна только админам из config.json.
        // Консоль сервера этим гейтом не проходит — оператор и так хозяин.
        if (!IsCommandAllowedFor(player, message, out var denyReason))
        {
            Reply(denyReason);
            return true; // отказ — это обработанная команда, «неизвестная» не показываем
        }

        return TryDispatchHostCommand(message, Reply, who: $"chat:{player.Name}", player);
    }

    /// <summary>
    /// Пускать ли игрока к команде. Решает <c>config.json</c>:
    /// <c>admin.nicknames</c>, <c>admin.adminOnlyCommands</c>, <c>admin.anyoneCanStart</c>,
    /// а админство — ещё и код этого запуска (<see cref="IsAdminAuthorized"/>).
    /// </summary>
    private bool IsCommandAllowedFor(ConnectedPlayer player, string message, out string denyReason)
    {
        // Само решение — в CommandAccessPolicy (чистая функция, покрыта тестами).
        // Права проверяем по нику + адресу: ник присылает клиент, и адрес, который код
        // запуска не предъявлял, админом не становится — ровно то, что показывает /whoami.
        var allowed = CommandAccessPolicy.IsAllowed(
            _config,
            player.Name,
            message,
            out denyReason,
            nick => IsAdminAuthorized(nick, player.Peer.Address));
        if (allowed)
            return true;

        // Отказ виден и в консоли: иначе игрок получает только приватный ответ, а
        // оператор не понимает, почему команда «не работает». Причина — та же строка,
        // что ушла игроку (ник без прав и ник без кода — разные случаи).
        var parsed = CommandAccessPolicy.Parse(message);
        Console.WriteLine(
            $"[chat] /{parsed.Command} ОТКЛОНЕНО: {player.Name} ({player.Peer.Address}) — " +
            denyReason);
        return false;
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
            // Игроку — короткая памятка, оператору в консоли — полная.
            // Консоль зовёт диспетчер с who = "console".
            var help = who == "console" ? FormatConsoleHelp() : FormatPlayerHelp();
            foreach (var line in help.Split('\n'))
                reply(line.TrimEnd());
            return true;
        }

        if (cmd.Equals("mode", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("режим", StringComparison.OrdinalIgnoreCase))
        {
            HandleModeCommand(reply, args, who);
            return true;
        }

        if (cmd.Equals("join", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("войти", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("комната", StringComparison.OrdinalIgnoreCase))
        {
            HandleJoinCommand(reply, args, who, player);
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

        if (cmd.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("код", StringComparison.OrdinalIgnoreCase))
        {
            HandleAuthCommand(reply, args, player);
            return true;
        }

        if (cmd.Equals("whoami", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("ктоя", StringComparison.OrdinalIgnoreCase))
        {
            HandleWhoAmICommand(reply, player);
            return true;
        }

        if (cmd.Equals("ban", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("бан", StringComparison.OrdinalIgnoreCase))
        {
            HandleBanCommand(reply, args, player);
            return true;
        }

        // Старое имя оставлено тихим алиасом: делает то же, что /ban.
        if (cmd.Equals("banid", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("банид", StringComparison.OrdinalIgnoreCase))
        {
            HandleBanCommand(reply, args, player);
            return true;
        }

        if (cmd.Equals("unban", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("разбан", StringComparison.OrdinalIgnoreCase))
        {
            HandleUnbanCommand(reply, args);
            return true;
        }

        if (cmd.Equals("unbanid", StringComparison.OrdinalIgnoreCase))
        {
            HandleUnbanCommand(reply, args);
            return true;
        }

        if (cmd.Equals("ids", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("айди", StringComparison.OrdinalIgnoreCase))
        {
            HandleIdsCommand(reply);
            return true;
        }

        if (CommandAccessPolicy.IsChatTestCommand(cmd))
        {
            HandleChatTestCommand(reply, player);
            return true;
        }

        if (cmd.Equals("bans", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("баны", StringComparison.OrdinalIgnoreCase))
        {
            HandleBansCommand(reply);
            return true;
        }

        if (cmd.Equals("ready", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("r", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("готов", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("г", StringComparison.OrdinalIgnoreCase))
        {
            HandleReadyCommand(reply, player, ready: true);
            return true;
        }

        if (cmd.Equals("unready", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("nr", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("неготов", StringComparison.OrdinalIgnoreCase)
            || cmd.Equals("нг", StringComparison.OrdinalIgnoreCase))
        {
            HandleReadyCommand(reply, player, ready: false);
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
                var startedNow = TryArmWarmupOrFirstPlay(who);
                // Персональный ответ обязателен: BroadcastServerChat идёт в лобби-канал,
                // а игрок в катке его не видит — команда выглядела «проигнорированной».
                reply(startedNow
                    ? "start — матч запущен (первый запуск)"
                    : "start — armed: WarmUp пойдёт, когда в обеих командах будет по игроку");
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

    /// <summary>
    /// Краткая памятка для игрока: только то, что ему действительно нужно.
    /// </summary>
    /// <remarks>
    /// Комнаты создаются на сайте, поэтому <c>/mode</c>, <c>/map</c>, <c>/start</c>,
    /// <c>/play</c> и <c>/set</c> в этой памятке не упоминаются: игроку они больше не
    /// нужны, а длинный список при входе только закрывал экран чата. Команды не
    /// удалены — они остались для консоли сервера и легаси-пути (см.
    /// <see cref="FormatConsoleHelp"/>), но игрок о них не узнаёт.
    /// </remarks>
    public static string FormatPlayerHelp() =>
        """
        /join <режим> #<номер>  войти в комнату с сайта
        /ready, /unready        готовность к старту
        /whoami                 твой ник, ID и IP
        /help                   эта памятка
        Комнату создают на сайте — там же кнопка «создать» и номер комнаты.
        """.Trim();

    /// <summary>
    /// Полная памятка: консоль сервера и дашборд. Здесь оператору нужны все команды,
    /// включая те, что игрокам больше не показываются.
    /// </summary>
    public static string FormatConsoleHelp() =>
        """
        команды: /mode /map /join /set /play /start /end /ready /ban /help
          /mode [alias]  список или сменить режим
          /map [alias]   карты текущего режима (короткие alias)
          /join <режим> #<номер>  войти в комнату с сайта (только из лобби)
          /set …         параметры (start, end, round, wins, …)
          /play          запуск / рематч
          /start         WarmUp (когда матч уже поднят)
          /end           завершить матч сразу (без 5с)
          /ready         отметить готовность (режим старта ready)
          /unready       отменить готовность
          /ban id срок [причина]    бан на 10m…14d по серверному id (только админ)
          /unban id                 снять все баны этого id (только админ)
          /bans                     активные баны и остаток (только админ)
          /ids                      кто в лобби и катке: id, ник, IP (только админ)
          /whoami                   что сервер знает про тебя: ник, id, IP, права
          /chattest                 диагностика чата катки: 4 варианта кадра (только админ)
          /auth                     код админа (только из консоли сервера)
        Те же команды работают в чате катки: ответ придёт туда же и только тебе.
        «Только админ» — это ник в admin.nicknames плюс код этого запуска (/whoami).
        """.Trim();

    /// <summary>
    /// Совместимость: прежнее имя полной памятки.
    /// </summary>
    public static string FormatCommandHelp() => FormatConsoleHelp();

    private static string FormatUnknownCommandHelp(string message) =>
        $"неизвестная команда '{message}' — /help";

    /// <summary>
    /// Личный ответ игроку: в лобби-сокет и в матч-чат, потому что канал зависит от того,
    /// где сейчас клиент. Если катка ещё не готова — ответ в очереди и досылается сам.
    /// </summary>
    private void ReplyPrivate(ConnectedPlayer player, string text)
    {
        DashboardHub.PostChat(LobbySession.ServerPlayerName, $"[{player.Name}] {text}", fromServer: true);

        // Лобби-сокет: видно, пока клиент на экране лобби.
        Send(player.Peer, LobbyCodec.BuildChatEvent(LobbySession.ServerMemberId, text));

        // Матч-чат: адресно. Клиент, ушедший в катку, лобби-кадров больше не рисует —
        // именно из-за этого «команда выполнилась, а ответа нет».
        if (_match.SendServerChat(
                text, nick: player.Name, alsoLastChanceIp: player.Peer.Address) > 0)
        {
            Console.WriteLine($"[chat] Server → {player.Name} (match): {text}");
            return;
        }

        // Катка идёт? Тогда ответ обязан появиться и в матч-чате — но пир подключается к
        // 7777 через несколько секунд после op9 (bootstrap ждёт identity до 15 с).
        if (!_match.IsListening)
            return;

        var where = player.Status == PlayerLobbyStatus.InMatch ? "в катке" : "в лобби";
        Console.WriteLine(
            $"[chat] Server → {player.Name} ({where}): адресата на 7777 пока нет " +
            $"({_match.DescribePeers()}) — ответ в очереди");
        RememberMatchReply(player.Name, text, player.Peer.Address);
    }

    private void NotifyMatchPlayerJoined(string name)
    {
        var player = _session.JoinedPlayers()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
        if (player is not null)
            _session.TrySetPlayerStatus(player, PlayerLobbyStatus.InMatch);
        BroadcastServerChat($"{name} Подключился!");
        BroadcastServerChat(_session.BuildPlayerListMessage());

        // Команда, набранная до подключения к катке, ждала именно этого момента.
        FlushMatchReplies(name);
        try { _onRosterChanged?.Invoke(); } catch { /* ignore */ }
    }

    private void NotifyMatchPlayerLeft(string name)
    {
        var player = _session.JoinedPlayers()
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
        if (player is not null)
            _session.TrySetPlayerStatus(player, PlayerLobbyStatus.Lobby);

        // Вышел из катки — его голос больше не считается, иначе он вечно
        // блокировал бы старт. Пересчитываем и показываем новый счёт.
        if (_startGate.Remove(name))
        {
            Console.WriteLine($"[start-gate] {name}: голос снят (вышел из катки)");
            AnnounceReadyProgress();
        }

        BroadcastServerChat($"{name} Отключился!");
        BroadcastServerChat(_session.BuildPlayerListMessage());
        try { _onRosterChanged?.Invoke(); } catch { /* ignore */ }

        // Вернувшемуся в лобби нельзя оставлять «игра идёт»: клиент рисует кнопку
        // «вернуться в катку» и считает, что матч ещё жив. Снимаем признак hosting
        // персонально у него — остальные в комнате продолжают играть.
        ClearHostingForPlayer(player, name);
    }

    /// <summary>
    /// Сказать одному игроку, что матча для него больше нет (<c>hasHosting=0</c>).
    /// </summary>
    /// <remarks>
    /// Именно персонально, а не <c>Broadcast</c>: катка продолжается, и остальным
    /// снимать hosting нельзя — они в ней остаются. Идемпотентно: если игрок уже ушёл
    /// из лобби, отправлять некому, и это нормально.
    /// </remarks>
    private void ClearHostingForPlayer(ConnectedPlayer? player, string name)
    {
        if (player is null)
        {
            Console.WriteLine(
                $"[lobby] '{name}' вышел из катки, но его уже нет в лобби — hosting снимать некому");
            return;
        }

        try
        {
            Send(player.Peer, LobbyCodec.BuildGameHostingState(null, 0));

            // «Ищу игру» тоже больше не про него: иначе ответ на /join искал бы
            // матч, которого для этого игрока нет.
            Send(player.Peer, LobbyCodec.BuildCustomProperty(
                LobbyPropKeys.SearchingStarted, LobbyVariant.FromBool(false)));

            Console.WriteLine(
                $"[lobby] '{player.Name}' вернулся в лобби — hosting снят " +
                "(остальные в катке не затронуты)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[lobby] снять hosting у '{name}': {ex.Message}");
        }
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
            var status = MatchHostSettings.FormatStatusLine();
            BroadcastServerChat(status);
            reply(status);
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
            // Раньше эта ветка молчала: ArmMatchStart шлёт только общий чат,
            // который из катки может быть не виден — и команда выглядела как
            // «ушла в никуда». Теперь есть и персональный ответ.
            ArmMatchStart(who);
            reply("старт армлен — WarmUp, как только в обеих командах будет минимум по игроку");
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

    /// <summary>
    /// <c>/ready</c> и <c>/unready</c>. Голосовать можно только находясь в катке —
    /// из лобби команда не принимается, иначе голос повиснет и заблокирует старт.
    /// </summary>
    private void HandleReadyCommand(Action<string> reply, ConnectedPlayer? player, bool ready)
    {
        if (player is null)
        {
            reply(ready ? "/ready только из чата игрока" : "/unready только из чата игрока");
            return;
        }

        if (_startGate.Mode != MatchStartMode.Ready)
        {
            reply("режим старта по готовности выключен — config.json → matchStart.mode = \"ready\"");
            return;
        }

        if (player.Status != PlayerLobbyStatus.InMatch)
        {
            reply("ты не в катке — /ready работает только из матча (сначала /play)");
            return;
        }

        var nick = player.Name;

        if (ready)
        {
            if (!_startGate.MarkReady(nick))
            {
                reply("ты уже в списке готовых");
                return;
            }

            Console.WriteLine($"[start-gate] {nick}: /ready");
        }
        else
        {
            if (!_startGate.AllowUnready)
            {
                reply("/unready отключён в конфиге (ready.allowUnready = false)");
                return;
            }

            if (!_startGate.MarkUnready(nick))
            {
                reply("ты и так не был в списке готовых");
                return;
            }

            Console.WriteLine($"[start-gate] {nick}: /unready");
        }

        var inMatch = InMatchPlayerNames();
        var everyoneReady = _startGate.ShouldStart(inMatch, 0, 0, out var reason);
        Console.WriteLine($"[start-gate] {reason}");

        var action = ready ? $"{nick} готов" : $"{nick} передумал";
        if (_startGate.AnnounceInChat)
            BroadcastServerChat($"{action} — {reason}");
        else
            reply(reason);

        // Все готовы — стартуем сразу, не дожидаясь следующего тика.
        if (everyoneReady)
            ArmMatchStart($"ready:{nick}");
    }

    /// <summary>Пишет в чат прогресс готовности — только в режиме <c>ready</c>.</summary>
    private void AnnounceReadyProgress()
    {
        if (_startGate.Mode != MatchStartMode.Ready)
            return;
        if (!_startGate.AnnounceInChat)
            return;

        var inMatch = InMatchPlayerNames();
        _startGate.ShouldStart(inMatch, 0, 0, out var reason);
        BroadcastServerChat(reason);
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

    /// <summary>
    /// Диагностика матч-чата (<c>/chattest</c>, только админ): один и тот же текст уходит
    /// четырьмя кадрами ChatManager — автор (актор хоста «Server» / живой игрок) ×
    /// <c>field</c> (1/2). Задача — одним прогоном на живом клиенте узнать, какой из
    /// вариантов он вообще рисует.
    /// </summary>
    /// <remarks>
    /// Почему это не выдумка, а эксперимент: latest.log (2026-11-14, 3 пира) показал, что
    /// серверный кадр доходит до всех (<c>Server → match: матч armed … (peers=3)</c>), но на
    /// экране ничего нет, и в обратную сторону клиенты не прислали ни одного Rpc на id=7
    /// (80 Rpc — только пешки, RadarManager id=6, BombManager id=8). Значит чат клиента к
    /// нашему ChatManager не привязан. Обе ниточки одинаково подтверждены капчурами:
    /// <c>field</c> у живого чата бывает 1 и 2 (gold …040303/…040308 против …040310 «сучка»),
    /// а байт автора — это номер актора (в том же логе id=257→rpc=2, id=385→rpc=3, id=513→rpc=4,
    /// то есть rpc==owner пешки). Выбрать между ними по капчурам нельзя — нужно наблюдение.
    /// </remarks>
    private void HandleChatTestCommand(Action<string> reply, ConnectedPlayer? player)
    {
        if (!_match.IsListening || !_match.HasMatchPeers)
        {
            reply("chattest: катка не идёт — сначала /start, нужны живые клиенты на 7777");
            return;
        }

        // Аудитория важнее самих проб: кадры, ушедшие в комнату без заказчика, не отвечают
        // ни на один вопрос (latest-3.log: /chattest из лобби при отключённом 7777 — буквы
        // увидели двое других, а ответы заказчику истекли в очереди).
        var audience = _match.DescribeChatAudience(
            player?.Name, player?.Peer.Address, out var requesterPresent);
        Console.WriteLine(
            $"[chat] chattest: аудитория — {audience}; заказчик " +
            (player is null ? "неизвестен (консоль)" : player.Name) +
            (requesterPresent ? " в катке" : " НЕ в катке"));
        reply($"chattest: аудитория — {audience}");
        if (!requesterPresent)
            reply("chattest: тебя среди них нет — буквы увидят только они (зайди в катку и повтори)");

        // SnapshotPublicIds уже без актора сервера, поэтому первый — живой игрок.
        var human = _match.SnapshotPublicIds().FirstOrDefault();
        var hasHuman = !string.IsNullOrWhiteSpace(human.Nick);
        var variants = new List<(string Label, byte Author, string Who, short Field)>
        {
            ("A", MatchHostActor.ActorNr, "Server", 1),
            ("B", MatchHostActor.ActorNr, "Server", 2),
        };
        if (hasHuman)
        {
            variants.Add(("C", human.ActorNr, human.Nick, 1));
            variants.Add(("D", human.ActorNr, human.Nick, 2));
        }

        foreach (var (label, author, who, field) in variants)
        {
            var text = $"chattest {label} (автор {author}={who}, field={field})";
            var peers = _match.SendServerChatProbe(text, author, field);
            Console.WriteLine(
                $"[chat] chattest {label}: author={author} field={field} → peers={peers} " +
                $"(аудитория: {audience})");
            reply($"chattest {label} → peers={peers}: автор {author} ({who}), field={field}");
        }

        // Прогон 2026-11-14 (3 пира) дал ответ: видна только C — живой автор и field=1.
        // A/B (актор сервера) и D (field=2) клиент не рисует, поэтому серверный чат теперь
        // подписывается живым актором (MatchChatAuthor). Проба оставлена как регрессия.
        reply(hasHuman
            ? "chattest: известно с 2026-11-14 — видна только C (живой автор, field=1); " +
              "A/B от актора сервера и D с field=2 клиент не рисует. Серверный чат теперь " +
              $"подписывается живым актором как C (здесь это {human.ActorNr}={human.Nick})."
            : "chattest: живых игроков в ростере нет — отправлены только варианты от актора сервера.");
    }
}
