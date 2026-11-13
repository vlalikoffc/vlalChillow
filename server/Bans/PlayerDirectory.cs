using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>Кого сервер когда-либо видел: id (из ника), ник, адреса, когда заходил.</summary>
/// <remarks>
/// Это память, а не бан: нужна, чтобы <c>/banid</c> находил человека, который уже вышел
/// из катки или вообще отключился. Хранится в <c>banlist.json</c> рядом с банами.
/// </remarks>
public sealed class SeenPlayer
{
    public int Id { get; set; }
    public string Nick { get; set; } = "";
    public List<string> Ips { get; set; } = new();
    public long LastSeenUtcMs { get; set; }
}

/// <summary>
/// Постоянный справочник «ник ↔ id ↔ IP», переживающий рестарт сервера.
/// </summary>
/// <remarks>
/// <para>
/// Id детерминирован (<see cref="MatchPublicId.ForNick"/>), поэтому один и тот же ник
/// всегда попадает в одну и ту же запись. Раздельные личности не склеиваются: разные
/// ники — разные записи, даже если у них совпал id (теоретическая коллизия хеша).
/// </para>
/// <para>
/// Ограничение размера: последние <see cref="MaxEntries"/> личностей. Старые вытесняются,
/// чтобы файл не рос бесконечно на публичном сервере.
/// </para>
/// </remarks>
public sealed class PlayerDirectory
{
    public const int MaxEntries = 5000;

    private readonly object _gate = new();
    private readonly List<SeenPlayer> _players = new();
    private readonly Dictionary<string, SeenPlayer> _byNick = new(StringComparer.Ordinal);

    /// <summary>Вызывается после изменения — сюда навешивается запись на диск.</summary>
    public Action? Changed { get; set; }

    public int Count
    {
        get { lock (_gate) return _players.Count; }
    }

    /// <summary>Запомнить вход: ник, его id и адрес. Возвращает запись или null для пустого ника.</summary>
    public SeenPlayer? Remember(string? nick, string? ip, DateTime nowUtc)
    {
        var id = MatchPublicId.ForNick(nick);
        if (id == 0)
            return null;

        var normalized = ServerConfig.NormalizeNick(nick);
        lock (_gate)
        {
            var changed = false;
            if (!_byNick.TryGetValue(normalized, out var player))
            {
                player = new SeenPlayer { Id = id, Nick = normalized };
                _byNick[normalized] = player;
                _players.Add(player);
                TrimLocked();
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(ip) && !player.Ips.Contains(ip))
            {
                player.Ips.Add(ip);
                changed = true;
            }

            // LastSeen обновляем только в памяти: писать файл на каждый вход — это I/O
            // в пути подключения. На диск попадёт при ближайшем бане/разбане.
            player.LastSeenUtcMs = BanRecord.ToUnixMs(nowUtc);

            if (changed)
                NotifyChanged();
            return player;
        }
    }

    /// <summary>Найти личность по id (последняя замеченная, если коллизия).</summary>
    public SeenPlayer? Resolve(int id)
    {
        if (id == 0)
            return null;

        lock (_gate)
        {
            SeenPlayer? best = null;
            foreach (var player in _players)
            {
                if (player.Id != id)
                    continue;
                if (best is null || player.LastSeenUtcMs > best.LastSeenUtcMs)
                    best = player;
            }

            return best;
        }
    }

    /// <summary>Сколько личностей делят этот id (для честного предупреждения админу).</summary>
    public int CountById(int id)
    {
        if (id == 0)
            return 0;

        lock (_gate)
            return _players.Count(p => p.Id == id);
    }

    public IReadOnlyList<SeenPlayer> Export()
    {
        lock (_gate)
            return _players.ToList();
    }

    public void Load(IEnumerable<SeenPlayer> players)
    {
        lock (_gate)
        {
            _players.Clear();
            _byNick.Clear();

            foreach (var player in players)
            {
                if (player is null || string.IsNullOrWhiteSpace(player.Nick))
                    continue;

                player.Ips ??= new List<string>();
                player.Nick = ServerConfig.NormalizeNick(player.Nick);
                if (player.Nick.Length == 0)
                    continue;

                var id = MatchPublicId.ForNick(player.Nick);
                if (id != 0)
                    player.Id = id;

                _players.Add(player);
                _byNick[player.Nick] = player;
            }

            TrimLocked();
        }
    }

    private void TrimLocked()
    {
        if (_players.Count <= MaxEntries)
            return;

        var drop = _players
            .OrderBy(p => p.LastSeenUtcMs)
            .Take(_players.Count - MaxEntries)
            .ToList();
        foreach (var player in drop)
        {
            _players.Remove(player);
            _byNick.Remove(player.Nick);
        }
    }

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] справочник: сохранить не удалось: {ex.Message}");
        }
    }
}
