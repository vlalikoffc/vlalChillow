using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Баны на стороне лобби: команды, теневой вход, кик, разбан по сроку.
/// </summary>
/// <remarks>
/// Проверка выполняется только на входе (первый пакет лобби), не на каждом пакете:
/// это один словарный lookup, без диска и без блокировок в hot path. Запись на диск
/// случается лишь когда админ банит/разбанит или когда реестр выучил новый алиас.
/// </remarks>
public sealed partial class GameNetHost
{
    private readonly object _shadowGate = new();
    private readonly Dictionary<NetPeer, string> _shadowPeers = new();

    private BanRegistry? _bans;
    private readonly PlayerDirectory _directory = new();
    private BanStore? _banStore;
    private DateTime _lastBanSweepUtc = DateTime.MinValue;
    private DateTime _lastShadowRefreshUtc = DateTime.MinValue;

    /// <summary>Реестр банов (null — проба/тесты без банов).</summary>
    public BanRegistry? Bans => _bans;

    private void InitBans()
    {
        var settings = _config.Admin.Ban;
        var path = ResolveBanPath(settings.File);

        _banStore = new BanStore(path);
        _bans = new BanRegistry { LearnAliases = settings.BindAliases };

        var stored = _banStore.Load();
        _bans.Load(stored.Bans);
        _directory.Load(stored.Players);

        _bans.Changed = PersistBans;
        _directory.Changed = PersistBans;

        // Матч проверяет те же записи: IP на коннекте, ник до выдачи актора.
        _match.Bans = _bans;
        _match.IsAdminNick = IsBanExemptNick;
        _match.ExposeServerUid = settings.ExposeIds;

        Console.WriteLine(
            $"[ban] реестр: {_bans.Count} банов, {_directory.Count} личностей в справочнике, файл {path}, срок " +
            $"{settings.MinMinutes}м..{settings.MaxDays}д, проверка каждые {settings.SweepSec}с, " +
            "админов банить нельзя");
    }

    private static string ResolveBanPath(string? configured)
    {
        var name = string.IsNullOrWhiteSpace(configured) ? "banlist.json" : configured.Trim();
        return System.IO.Path.IsPathRooted(name)
            ? name
            : System.IO.Path.Combine(AppContext.BaseDirectory, name);
    }

    private void PersistBans()
    {
        if (_banStore is null || _bans is null)
            return;
        _banStore.Save(_bans.Export(), _directory.Export());
    }

    /// <summary>Проверка бана на входе в лобби (ник + IP).</summary>
    private BanDecision InspectBan(string? nick, IPAddress? address)
    {
        if (_bans is null || !_config.Admin.Ban.Enabled)
            return BanDecision.Allowed;

        return _bans.Inspect(
            new BanProbe(nick, address?.ToString()),
            DateTime.UtcNow,
            IsBanExemptNick);
    }

    /// <summary>
    /// Кого бан не берёт: админа из конфига и сам сервер. Предикат читает <c>_config</c> на
    /// каждом входе, поэтому перечитывание конфига не оставляет старый список админов.
    /// </summary>
    private bool IsBanExemptNick(string? nick) =>
        _config.IsAdmin(nick) || ServerIdentity.IsServerNick(nick);

    private bool IsShadowPeer(NetPeer peer)
    {
        lock (_shadowGate)
            return _shadowPeers.ContainsKey(peer);
    }

    /// <summary>
    /// Забаненный заходит в лобби: успешный JoinResponse с росписью из одного Server,
    /// затем четыре строки от Server — только ему.
    /// </summary>
    private void HandleShadowJoin(NetPeer peer, JoinLobbyRequest req, BanDecision decision)
    {
        var record = decision.Record;
        if (record is null)
        {
            Send(peer, LobbyCodec.BuildJoinFail(JoinFailReason.NotJoinable));
            return;
        }

        var result = _session.HandleShadowJoinRequest(peer, req);
        if (result.ResponseBytes is not null)
            Send(peer, result.ResponseBytes);

        if (!result.Success)
        {
            Console.WriteLine($"[ban] теневой вход отклонён: {result.FailReason} ({result.Detail})");
            return;
        }

        var nick = ServerConfig.NormalizeNick(req.Profile.Name);
        _directory.Remember(nick, peer.Address?.ToString(), DateTime.UtcNow);
        lock (_shadowGate)
            _shadowPeers[peer] = nick;

        Console.WriteLine(
            $"[ban] теневой вход '{req.Profile.Name}' {peer.Address} — совпало по {decision.MatchedBy}, " +
            $"осталось {BanDuration.FormatRemaining(record.Remaining(DateTime.UtcNow))}" +
            (decision.LearnedAlias ? ", алиас записан" : ""));

        DashboardHub.PostChat(
            LobbySession.ServerPlayerName,
            $"[бан] {req.Profile.Name} ({peer.Address}) — вход закрыт по {decision.MatchedBy}",
            fromServer: true);

        // Четыре строки по порядку: ReliableOrdered доставит их так же, как приветствие.
        foreach (var line in BanMessages.NoticeLines(record, DateTime.UtcNow))
            SendServerChatTo(peer, line);
    }

    /// <summary>
    /// Раз в <c>admin.ban.sweepSec</c> (по умолчанию 5 с): кто получил разбан по сроку —
    /// того сразу отпускает и теневой сокет. Это же чистит banlist.json.
    /// </summary>
    private void SweepBans()
    {
        if (_bans is null)
            return;

        var now = DateTime.UtcNow;
        var every = TimeSpan.FromSeconds(Math.Max(1, _config.Admin.Ban.SweepSec));
        if (now - _lastBanSweepUtc < every)
            return;
        _lastBanSweepUtc = now;

        var removed = _bans.Prune(now);
        if (removed > 0)
            Console.WriteLine($"[ban] срок вышел у {removed} — снято с banlist");

        // Срок мог выйти (или баны выключили) — отпускаем теневые сокеты сразу, не ждём минуты.
        foreach (var (peer, nick) in SnapshotShadowPeers())
        {
            if (InspectBan(nick, peer.Address).Banned)
                continue;

            SendServerChatTo(peer, "бан снят — заходи заново");
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            lock (_shadowGate)
                _shadowPeers.Remove(peer);
            Console.WriteLine($"[ban] '{nick}' больше не в бане — теневой сокет закрыт");
        }
    }

    /// <summary>Обновление строки «Разбан через: …» в теневом лобби (раз в минуту).</summary>
    private void RefreshShadowCountdowns()
    {
        if (_bans is null)
            return;

        var now = DateTime.UtcNow;
        var every = TimeSpan.FromSeconds(Math.Max(10, _config.Admin.Ban.ShadowRefreshSec));
        if (now - _lastShadowRefreshUtc < every)
            return;
        _lastShadowRefreshUtc = now;

        foreach (var (peer, nick) in SnapshotShadowPeers())
        {
            var decision = InspectBan(nick, peer.Address);
            if (decision.Banned && decision.Record is not null)
                SendServerChatTo(peer, BanMessages.CountdownLine(decision.Record, now));
        }
    }

    /// <summary>
    /// <c>/ban &lt;id&gt; &lt;срок&gt; [причина]</c>. Id — только селектор: баним человека,
    /// то есть его ник + IP, а не само число (оно достанется другому).
    /// </summary>
    private void HandleBanCommand(Action<string> reply, string args, ConnectedPlayer? player)
    {
        if (!BanReady(reply))
            return;

        if (!BanCommandLine.TryParse(args, out var id, out var durationToken, out var reason, out var error))
        {
            reply(error);
            return;
        }

        // 1) кто сейчас в катке. Коллизия id (два ника → один id) — отказ, а не
        // «баню первого попавшегося»: забанить не того хуже, чем попросить уточнить.
        var inMatch = _match.SnapshotPublicIds().Where(e => e.Id == id).ToList();
        if (inMatch.Count > 1)
        {
            reply($"id {id} делят {inMatch.Count} игрока: " +
                  string.Join(", ", inMatch.Select(e => $"'{e.Nick}' (actor {e.ActorNr})")) +
                  " — совпадение ников, бан по id невозможен");
            return;
        }
        if (inMatch.Count == 1)
        {
            var entry = inMatch[0];
            TryBanTarget(reply, entry.Nick, entry.Address, durationToken, reason, player, id);
            return;
        }

        // 2) кто в лобби (id вычисляется из ника, поэтому его видно и до катки)
        var inLobby = _session.JoinedPlayers()
            .Where(p => MatchPublicId.ForNick(p.Name) == id)
            .ToList();
        if (inLobby.Count > 1)
        {
            reply($"id {id} делят {inLobby.Count} игрока в лобби: " +
                  string.Join(", ", inLobby.Select(p => $"'{p.Name}'")) +
                  " — совпадение ников, бан по id невозможен");
            return;
        }
        if (inLobby.Count == 1)
        {
            TryBanTarget(
                reply, ServerConfig.NormalizeNick(inLobby[0].Name), inLobby[0].Peer.Address,
                durationToken, reason, player, id);
            return;
        }

        // 3) уже вышел: справочник помнит ник и все его IP — бан сработает и без него.
        var seen = _directory.Resolve(id);
        if (seen is null)
        {
            reply($"никого с id {id} сервер не помнит — список: /ids");
            return;
        }

        var sharing = _directory.CountById(id);
        if (sharing > 1)
        {
            reply($"id {id} делят {sharing} ника (коллизия) — чтобы не задеть не того, " +
                  "бан по id невозможен");
            return;
        }

        reply($"id {id} = '{seen.Nick}', известные IP: " +
              $"{(seen.Ips.Count == 0 ? "—" : string.Join(", ", seen.Ips))}");
        TryBanTarget(
            reply, seen.Nick, address: null, durationToken, reason, player,
            id, knownIps: seen.Ips);
    }

    private bool BanReady(Action<string> reply)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return false;
        }

        if (!_config.Admin.Ban.Enabled)
        {
            reply("баны выключены (admin.ban.enabled = false)");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Общее тело <c>/ban</c>: проверки, запись, кик из матча и лобби.
    /// </summary>
    private void TryBanTarget(
        Action<string> reply,
        string nick,
        IPAddress? address,
        string durationToken,
        string reason,
        ConnectedPlayer? player,
        int? matchId,
        IReadOnlyList<string>? knownIps = null)
    {
        if (_bans is null)
            return;

        var settings = _config.Admin.Ban;
        if (nick.Length == 0)
        {
            reply("пустой ник");
            return;
        }

        // Сам сервер не банится ни по нику, ни по id: это не игрок, а хост.
        if (ServerIdentity.IsServerNick(nick))
        {
            reply($"'{ServerIdentity.Nick}' — это сам сервер, его забанить нельзя");
            return;
        }

        // Правило №2: админов не баним.
        if (_config.IsAdmin(nick))
        {
            reply($"'{nick}' — админ, админов банить нельзя");
            Console.WriteLine($"[ban] отказ: '{nick}' в admin.nicknames — админа банить нельзя");
            return;
        }

        if (player is not null
            && string.Equals(ServerConfig.NormalizeNick(player.Name), nick, StringComparison.OrdinalIgnoreCase))
        {
            reply("себя банить нельзя");
            return;
        }

        if (!BanDuration.TryParse(
                durationToken,
                TimeSpan.FromMinutes(settings.MinMinutes),
                TimeSpan.FromDays(settings.MaxDays),
                out var duration,
                out var durationError))
        {
            reply(durationError);
            return;
        }

        // Цели в лобби: ник нормализованно, устройств с одним ником может быть несколько.
        var targets = _session.JoinedPlayers()
            .Where(p => string.Equals(
                ServerConfig.NormalizeNick(p.Name), nick, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ip = address ?? targets.Select(t => t.Peer.Address).FirstOrDefault(a => a is not null);

        // Id детерминирован от ника: запись всегда тегируется id, чтобы /unban потом
        // нашёл её без отдельной истории.
        var idForRecord = matchId is { } requested && requested != 0
            ? requested
            : MatchPublicId.ForNick(nick);

        var record = _bans.Add(
            new BanProbe(nick, ip?.ToString()),
            duration,
            reason,
            player?.Name ?? LobbySession.ServerPlayerName,
            DateTime.UtcNow,
            idForRecord,
            knownIps);

        // Кик из матча (7777) и из лобби (7778) — разные сокеты, нужны оба.
        var matchKicked = _match.DisconnectBanned(nick, ip);
        var lobbyKicked = 0;
        foreach (var target in targets)
        {
            try
            {
                target.Peer.Disconnect();
                lobbyKicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ban] не смог отключить '{target.Name}': {ex.Message}");
            }
        }

        // Теневые сокеты того же нарушителя.
        foreach (var (peer, shadowNick) in SnapshotShadowPeers())
        {
            if (!string.Equals(shadowNick, nick, StringComparison.OrdinalIgnoreCase))
                continue;
            try { peer.Disconnect(); }
            catch { /* уже мёртв */ }
            lock (_shadowGate)
                _shadowPeers.Remove(peer);
        }

        var ips = record.Ips.Count == 0
            ? "IP неизвестен (банить будем по нику)"
            : string.Join(", ", record.Ips);
        reply($"забанен {record.DisplayNick} на {BanDuration.FormatShort(duration)}: {DescribeReason(record)}");
        reply($"IP: {ips} · до {record.ExpiresUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}" +
              (record.MatchIds.Count == 0 ? "" : $" · id {string.Join(", ", record.MatchIds)}"));

        Console.WriteLine(
            $"[ban] {record.DisplayNick} на {BanDuration.FormatShort(duration)} причиной " +
            $"'{DescribeReason(record)}' (кто: {record.BannedBy}); IP=[{string.Join(", ", record.Ips)}] " +
            $"ids=[{string.Join(", ", record.MatchIds)}]; кик матч={matchKicked}, лобби={lobbyKicked}; " +
            $"выдан {record.CreatedLocalIso} (UTC{BanRecord.CurrentUtcOffsetMinutes() / 60.0:+0.#;-0.#})");

        if (settings.NotifyEveryone)
            BroadcastServerChat(BanMessages.PublicLine(record));
    }

    private void HandleUnbanCommand(Action<string> reply, string args)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return;
        }

        var token = (args ?? string.Empty).Trim();
        if (token.Length == 0)
        {
            reply("формат: /unban <id> — id смотри в /ids и в /bans");
            return;
        }

        // Id — основной адрес разбана: снимаем все записи, помеченные этим человеком.
        var digits = token.StartsWith('#') ? token[1..] : token;
        if (int.TryParse(digits, out var id) && id is >= BanCommandLine.MinId and <= BanCommandLine.MaxId)
        {
            RemoveBansById(reply, id);
            return;
        }

        // Разбан по нику или IP оставлен для записей, сделанных до перехода на id.
        var removedOk = _bans.Remove(token, out var removed);
        if (!removedOk || removed is null)
        {
            reply($"в бане нет '{token}'");
            return;
        }

        reply($"разбанен {removed.DisplayNick} (IP: " +
              $"{(removed.Ips.Count == 0 ? "—" : string.Join(", ", removed.Ips))})");
        Console.WriteLine($"[ban] разбан {removed.DisplayNick} по '{token}'");
        BroadcastServerChat($"разбанен {removed.DisplayNick}");
    }

    /// <summary>Снять все баны, выданные под этим серверным id.</summary>
    private void RemoveBansById(Action<string> reply, int id)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return;
        }

        var removed = _bans.RemoveByMatchId(id);
        if (removed.Count == 0)
        {
            reply($"под id {id} активных банов нет");
            return;
        }

        foreach (var record in removed)
        {
            reply($"разбанен {record.DisplayNick} (id {id}, IP: " +
                  $"{(record.Ips.Count == 0 ? "—" : string.Join(", ", record.Ips))})");
            BroadcastServerChat($"разбанен {record.DisplayNick}");
        }

        Console.WriteLine($"[ban] /unban {id}: снято {removed.Count} записей");
    }

    private void HandleBansCommand(Action<string> reply)
    {
        if (_bans is null)
        {
            reply("баны не инициализированы");
            return;
        }

        var now = DateTime.UtcNow;
        var active = _bans.Active(now);
        if (active.Count == 0)
        {
            reply("активных банов нет");
            return;
        }

        reply($"активных банов: {active.Count}");
        foreach (var record in active)
        {
            var ips = record.Ips.Count == 0 ? "—" : string.Join("/", record.Ips);
            var ids = record.MatchIds.Count == 0 ? "" : $" · id {string.Join(",", record.MatchIds)}";
            reply(
                $"{record.DisplayNick} · {BanDuration.FormatRemaining(record.Remaining(now))} · " +
                $"IP {ips}{ids} · {DescribeReason(record)} · забанил {record.BannedBy}");
        }
    }

    /// <summary>
    /// <c>/ids</c> — кто есть на сервере: серверный id, ник, адрес. Список общий для лобби и
    /// катки, потому что баны теперь только по id: админ должен видеть id того, кто ещё не
    /// входил в матч.
    /// </summary>
    private void HandleIdsCommand(Action<string> reply)
    {
        var entries = _match.SnapshotPublicIds();
        foreach (var entry in entries)
        {
            // У актора сервера id — строка «Server», а не число: он вне нумерации.
            var idText = entry.ActorNr == MatchHostActor.ActorNr
                ? ServerIdentity.Uid
                : $"#{entry.Id}";
            reply($"{idText} · {entry.Nick} · {entry.Address?.ToString() ?? "—"} · actor {entry.ActorNr}");
        }

        // Катка идёт, но кто-то сидит в лобби — его id тоже нужен.
        var shown = entries
            .Select(e => ServerConfig.NormalizeNick(e.Nick))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lobby = _session.JoinedPlayers()
            .Where(p => !shown.Contains(ServerConfig.NormalizeNick(p.Name)))
            .ToList();

        foreach (var player in lobby)
        {
            reply($"#{MatchPublicId.ForNick(player.Name)} · {player.Name} · " +
                  $"{player.Peer.Address?.ToString() ?? "—"} · лобби");
        }

        if (entries.Count == 0 && lobby.Count == 0)
            reply("в катке и в лобби никого нет");
    }

    private List<(NetPeer Peer, string Nick)> SnapshotShadowPeers()
    {
        lock (_shadowGate)
            return _shadowPeers.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string DescribeReason(BanRecord record) =>
        string.IsNullOrWhiteSpace(record.Reason) ? "не указана" : record.Reason.Trim();
}
