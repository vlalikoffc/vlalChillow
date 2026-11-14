using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>Что спросили у реестра на входе: ник и адрес сокета.</summary>
/// <param name="Nick">Ник из пакета входа; на коннекте в матч его ещё нет.</param>
/// <param name="Ip">Любой адрес: LAN, Hamachi, VPN, публичный. Фильтра по типу нет.</param>
public readonly record struct BanProbe(string? Nick, string? Ip);

/// <summary>
/// Ответ реестра. <see cref="Banned"/> — не пускать; <see cref="AdminExempt"/> — это админ,
/// на него бан не распространяется (пишем в лог, а не молчим).
/// </summary>
public sealed class BanDecision
{
    public static readonly BanDecision Allowed = new();

    public bool Banned { get; init; }
    public bool AdminExempt { get; init; }
    public string MatchedBy { get; init; } = "";
    public bool LearnedAlias { get; init; }
    public BanRecord? Record { get; init; }
}

/// <summary>
/// Реестр банов: ник ↔ IP, с автоматическим запоминанием алиасов.
/// </summary>
/// <remarks>
/// <para>
/// Только две связки, как в ТЗ: ник и IP. Стабильный токен установки намеренно не
/// используется — чтобы не задеть нормального игрока, которому достался чужой токен
/// или который просто переустановил игру.
/// </para>
/// <para>
/// Два индекса, потому что нарушитель меняет то одну сторону, то другую: новый IP при
/// старом нике дописывается в запись, новый ник на старом IP — тоже. Обе связки живут
/// до конца срока.
/// </para>
/// <para>
/// Всё под одним <c>lock</c>: проверка на входе — один словарный lookup, без скана
/// списка и без диска. Вызывается на подключении, не на каждом пакете.
/// </para>
/// </remarks>
public sealed class BanRegistry
{
    private readonly object _gate = new();
    private readonly List<BanRecord> _records = new();
    private readonly Dictionary<string, BanRecord> _byNick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BanRecord> _byIp = new(StringComparer.Ordinal);

    /// <summary>Вызывается после изменения набора — сюда навешивается запись на диск.</summary>
    public Action? Changed { get; set; }

    /// <summary>Учить новые ники/IP на попытке входа (защита от смены ника или адреса).</summary>
    public bool LearnAliases { get; set; } = true;

    public int Count
    {
        get { lock (_gate) return _records.Count; }
    }

    /// <summary>
    /// Проверка на входе. <see cref="BanDecision.Banned"/> = false значит «пускать».
    /// </summary>
    /// <param name="isAdmin">Предикат «ник админский»: на админов бан не распространяется.</param>
    public BanDecision Inspect(BanProbe probe, DateTime nowUtc, Func<string?, bool>? isAdmin = null)
    {
        lock (_gate)
        {
            var pruned = PruneLocked(nowUtc);
            if (pruned > 0)
                NotifyChanged();

            if (isAdmin is not null && isAdmin(probe.Nick))
                return new BanDecision { AdminExempt = true, MatchedBy = "админ" };

            var record = FindLocked(probe, out var matchedBy);
            if (record is null)
                return BanDecision.Allowed;

            var learned = LearnAliases && LearnLocked(record, probe);
            if (learned)
                NotifyChanged();

            return new BanDecision
            {
                Banned = true,
                Record = record,
                MatchedBy = matchedBy,
                LearnedAlias = learned,
            };
        }
    }

    /// <summary>Быстрая проверка только по IP — для гейта на коннекте, где ника ещё нет.</summary>
    public bool IsIpBanned(string? ip, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return false;

        lock (_gate)
            return _byIp.TryGetValue(ip, out var record) && record.IsActive(nowUtc);
    }

    /// <summary>Найти активную запись по нику.</summary>
    public BanRecord? FindByNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return null;

        lock (_gate)
            return _byNick.TryGetValue(normalized, out var record) ? record : null;
    }

    /// <summary>Выдать бан. Существующая запись для этого ника/IP продлевается, а не дублируется.</summary>
    /// <param name="matchId">
    /// Серверный id матча, если бан выдан по нему: тег нужен для <c>/unban</c>.
    /// </param>
    public BanRecord Add(
        BanProbe probe,
        TimeSpan duration,
        string reason,
        string bannedBy,
        DateTime nowUtc,
        int? matchId = null,
        IReadOnlyList<string>? knownIps = null)
    {
        var nick = ServerConfig.NormalizeNick(probe.Nick);
        if (nick.Length == 0 && string.IsNullOrWhiteSpace(probe.Ip))
            throw new ArgumentException("нужен ник или IP", nameof(probe));

        lock (_gate)
        {
            PruneLocked(nowUtc);

            BanRecord? record = null;
            if (nick.Length > 0 && _byNick.TryGetValue(nick, out var byNick))
                record = byNick;
            else if (!string.IsNullOrWhiteSpace(probe.Ip) && _byIp.TryGetValue(probe.Ip, out var byIp))
                record = byIp;

            if (record is null)
            {
                record = new BanRecord
                {
                    Nick = nick,
                    CreatedUtcMs = BanRecord.ToUnixMs(nowUtc),
                    UtcOffsetMinutes = BanRecord.CurrentUtcOffsetMinutes(),
                    CreatedLocalIso = BanRecord.LocalIsoNow(),
                };
                _records.Add(record);
            }

            if (nick.Length > 0)
            {
                if (string.IsNullOrEmpty(record.Nick))
                    record.Nick = nick;
                else if (!record.HasNick(nick))
                    record.Aliases.Add(nick);
                _byNick[nick] = record;
            }

            if (!string.IsNullOrWhiteSpace(probe.Ip))
            {
                if (!record.HasIp(probe.Ip))
                    record.Ips.Add(probe.Ip);
                _byIp[probe.Ip] = record;
            }

            // Бан по id для уже вышедшего игрока: подтягиваем все его известные адреса,
            // иначе «бан по id» не закрыл бы ему вход с того же IP.
            if (knownIps is not null)
            {
                foreach (var known in knownIps)
                {
                    if (string.IsNullOrWhiteSpace(known) || record.HasIp(known))
                        continue;
                    record.Ips.Add(known);
                    _byIp[known] = record;
                }
            }

            if (matchId is { } id && id != 0 && !record.MatchIds.Contains(id))
                record.MatchIds.Add(id);

            record.Reason = reason.Trim();
            record.BannedBy = string.IsNullOrWhiteSpace(bannedBy) ? "Server" : bannedBy.Trim();
            record.DurationSeconds = (int)Math.Clamp(duration.TotalSeconds, 0, int.MaxValue);
            record.ExpiresUtcMs = BanRecord.ToUnixMs(nowUtc.Add(duration));

            NotifyChanged();
            return record;
        }
    }

    /// <summary>Снять бан по нику или IP. Возвращает снятую запись.</summary>
    public bool Remove(string? nickOrIp, out BanRecord? removed)
    {
        removed = null;
        var token = (nickOrIp ?? string.Empty).Trim();
        if (token.Length == 0)
            return false;

        lock (_gate)
        {
            var nick = ServerConfig.NormalizeNick(token);
            var found = nick.Length > 0 && _byNick.TryGetValue(nick, out removed);
            if (!found && !_byIp.TryGetValue(token, out removed))
                return false;

            if (removed is null)
                return false;

            DropLocked(removed, notify: true);
            return true;
        }
    }

    /// <summary>Снять все баны, выданные под серверным id матча (для <c>/unban</c>).</summary>
    public List<BanRecord> RemoveByMatchId(int matchId)
    {
        lock (_gate)
        {
            var hits = _records.Where(r => r.HasMatchId(matchId)).ToList();
            foreach (var record in hits)
                DropLocked(record, notify: false);

            if (hits.Count > 0)
                NotifyChanged();
            return hits;
        }
    }

    /// <summary>Удалить истёкшие записи. Возвращает, сколько удалено.</summary>
    public int Prune(DateTime nowUtc)
    {
        lock (_gate)
        {
            var removed = PruneLocked(nowUtc);
            if (removed > 0)
                NotifyChanged();
            return removed;
        }
    }

    /// <summary>Активные баны: ближайший к концу — первым.</summary>
    public IReadOnlyList<BanRecord> Active(DateTime nowUtc)
    {
        lock (_gate)
        {
            var removed = PruneLocked(nowUtc);
            if (removed > 0)
                NotifyChanged();

            return _records
                .Where(r => r.IsActive(nowUtc))
                .OrderBy(r => r.ExpiresUtcMs)
                .ToList();
        }
    }

    /// <summary>Снимок для записи на диск (копия списка, сами записи общие).</summary>
    public IReadOnlyList<BanRecord> Export()
    {
        lock (_gate)
            return _records.ToList();
    }

    /// <summary>Заполнить реестр прочитанными с диска записями (истёкшие сразу отбрасываются).</summary>
    public void Load(IEnumerable<BanRecord> records)
    {
        lock (_gate)
        {
            _records.Clear();
            _byNick.Clear();
            _byIp.Clear();

            foreach (var record in records)
            {
                if (record is null)
                    continue;

                record.Ips ??= new List<string>();
                record.Aliases ??= new List<string>();
                record.MatchIds ??= new List<int>();
                record.Nick = ServerConfig.NormalizeNick(record.Nick);
                if (record.Nick.Length == 0)
                    record.Nick = record.Ips.FirstOrDefault() ?? "?";
                if (record.ExpiresUtcMs <= 0)
                    continue;

                _records.Add(record);
                _byNick[record.Nick] = record;

                foreach (var alias in record.Aliases)
                {
                    var normalized = ServerConfig.NormalizeNick(alias);
                    if (normalized.Length > 0)
                        _byNick[normalized] = record;
                }

                foreach (var ip in record.Ips)
                {
                    if (!string.IsNullOrWhiteSpace(ip))
                        _byIp[ip] = record;
                }
            }

            PruneLocked(DateTime.UtcNow);
        }
    }

    private BanRecord? FindLocked(BanProbe probe, out string matchedBy)
    {
        matchedBy = string.Empty;

        var nick = ServerConfig.NormalizeNick(probe.Nick);
        if (nick.Length > 0 && _byNick.TryGetValue(nick, out var byNick))
        {
            matchedBy = "ник";
            return byNick;
        }

        if (!string.IsNullOrWhiteSpace(probe.Ip) && _byIp.TryGetValue(probe.Ip, out var byIp))
        {
            matchedBy = "IP";
            return byIp;
        }

        return null;
    }

    private bool LearnLocked(BanRecord record, BanProbe probe)
    {
        var learned = false;

        var nick = ServerConfig.NormalizeNick(probe.Nick);
        if (nick.Length > 0 && !record.HasNick(nick))
        {
            record.Aliases.Add(nick);
            _byNick[nick] = record;
            learned = true;
        }

        if (!string.IsNullOrWhiteSpace(probe.Ip) && !record.HasIp(probe.Ip))
        {
            record.Ips.Add(probe.Ip);
            _byIp[probe.Ip] = record;
            learned = true;
        }

        return learned;
    }

    private int PruneLocked(DateTime nowUtc)
    {
        var nowMs = BanRecord.ToUnixMs(nowUtc);
        var expired = _records.Where(r => r.ExpiresUtcMs <= nowMs).ToList();
        foreach (var record in expired)
            DropLocked(record, notify: false);
        return expired.Count;
    }

    private void DropLocked(BanRecord record, bool notify)
    {
        _records.Remove(record);
        DropIndex(_byNick, record);
        DropIndex(_byIp, record);
        if (notify)
            NotifyChanged();
    }

    private static void DropIndex(Dictionary<string, BanRecord> index, BanRecord record)
    {
        foreach (var key in index.Where(kv => ReferenceEquals(kv.Value, record)).Select(kv => kv.Key).ToList())
            index.Remove(key);
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] сохранить изменения не удалось: {ex.Message}");
        }
    }
}
