using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Кто из игроков в какую комнату должен попасть при следующем <c>JoinRoom</c>.
/// </summary>
/// <remarks>
/// <para>
/// Нужен потому, что провод неизменен: клиент приходит с ником и <c>'Dedik'</c>, а комнат
/// на одном 7777 теперь много. Панель при создании комнаты кладёт сюда назначение
/// «этот ник → эта комната», и разбор <c>JoinRoom</c> спрашивает реестр.
/// </para>
/// <para>
/// <b>Один ник — одно назначение.</b> Игрок не может держать две комнаты: пока бронь
/// жива, повторный <see cref="Reserve"/> её заменяет. Поэтому поиск — точный по нику,
/// без «угадывания» между кандидатами: двух кандидатов с одним ником не существует.
/// </para>
/// <para>
/// <see cref="PendingRoomAssignment.IdentityKey"/> и <see cref="PendingRoomAssignment.Address"/>
/// пишутся для лога и диагностики («тем же клиентом? тем же адресом?»), но на выбор
/// комнаты не влияют — выбора нет, комната одна.
/// </para>
/// <para>
/// Все методы потокобезопасны (<c>lock</c> на внутреннем списке): к реестру обращаются
/// и UDP-цикл, и HTTP-поток панели.
/// </para>
/// </remarks>
public sealed class PendingRoomAssignments
{
    /// <summary>
    /// Ник сравниваем без учёта регистра: <c>NormalizeNick</c> выравнивает пробелы и
    /// width-формы, но не регистр, а «Vlal» и «vlal» — один игрок.
    /// </summary>
    private const StringComparison NickComparison = StringComparison.OrdinalIgnoreCase;

    private readonly object _gate = new();
    private readonly List<PendingRoomAssignment> _items = new();
    private readonly Func<DateTime> _clock;

    public PendingRoomAssignments(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Максимальное число ожидающих назначений (защита от роста списка).</summary>
    public int Capacity { get; init; } = 256;

    /// <summary>
    /// Диагностическое сообщение (ник не уникален, бронь выдана другому клиенту).
    /// Реестр — слой Kernel и сам не печатает: подписчик решает, писать ли в лог.
    /// </summary>
    public event Action<string>? Warning;

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    /// <summary>
    /// Забронировать комнату за ником. Повторная бронь того же ника заменяет прежнюю:
    /// игрок, создавший вторую комнату, не должен попадать в первую.
    /// </summary>
    /// <returns><c>false</c> — мест нет (см. <see cref="Capacity"/>).</returns>
    public bool Reserve(
        string passwordKey,
        string nick,
        string? identityKey = null,
        string? address = null,
        TimeSpan? ttl = null)
    {
        if (string.IsNullOrWhiteSpace(passwordKey) || string.IsNullOrWhiteSpace(nick))
            return false;

        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        var now = _clock();
        var item = new PendingRoomAssignment
        {
            PasswordKey = passwordKey.Trim(),
            Nick = normalized,
            Id = MatchPublicId.ForNick(normalized),
            IdentityKey = identityKey?.Trim() ?? "",
            Address = address?.Trim() ?? "",
            CreatedUtc = now,
            Ttl = ttl ?? PendingRoomAssignment.DefaultTtl,
        };

        lock (_gate)
        {
            PruneLocked(now);

            // Замена прежней брони того же игрока: id — устойчивый ключ, ник
            // подтверждает его на случай теоретической коллизии хеша.
            var sameNick = _items.FindIndex(a =>
                a.Id == item.Id && string.Equals(a.Nick, item.Nick, NickComparison));
            if (sameNick >= 0)
            {
                _items[sameNick] = item;
                return true;
            }

            if (_items.Count >= Capacity)
                return false;

            _items.Add(item);
            return true;
        }
    }

    /// <summary>
    /// Найти комнату для приходящего в <c>JoinRoom</c> игрока.
    /// </summary>
    /// <param name="nick">Ник из пакета (<c>JoinRoomRequest.room</c>).</param>
    /// <param name="passwordKey">Ключ комнаты, если нашли.</param>
    /// <returns><c>true</c> — идти в <paramref name="passwordKey"/>; <c>false</c> — в legacy.</returns>
    /// <remarks>
    /// <paramref name="identityKey"/> и <paramref name="address"/> принимаются для
    /// симметрии с вызывающим кодом и логируются, но не сужают выбор: назначение на ник
    /// всегда единственное (см. <see cref="Reserve"/>).
    /// </remarks>
    public bool TryResolve(
        string? nick,
        string? identityKey,
        string? address,
        out string passwordKey)
    {
        passwordKey = "";
        if (string.IsNullOrWhiteSpace(nick))
            return false;

        var normalized = ServerConfig.NormalizeNick(nick);
        if (normalized.Length == 0)
            return false;

        var now = _clock();
        lock (_gate)
        {
            PruneLocked(now);

            foreach (var a in _items)
            {
                if (!string.Equals(a.Nick, normalized, NickComparison))
                    continue;

                passwordKey = a.PasswordKey;
                if (identityKey is { Length: > 0 } id && a.IdentityKey.Length > 0
                    && !string.Equals(a.IdentityKey, id, StringComparison.Ordinal))
                    Warning?.Invoke(
                        $"[match-host] бронь нику '{normalized}' выдана другому клиенту " +
                        $"(identity '{a.IdentityKey}' ≠ '{id}') — ник не уникален, " +
                        "команда /join может привести в чужую комнату");
                return true;
            }

            return false;
        }
    }

    /// <summary>Снять бронь (комнату закрыли).</summary>
    public bool Release(string passwordKey)
    {
        if (string.IsNullOrWhiteSpace(passwordKey))
            return false;
        lock (_gate)
        {
            return _items.RemoveAll(a => string.Equals(
                a.PasswordKey, passwordKey, StringComparison.Ordinal)) > 0;
        }
    }

    /// <summary>
    /// Есть ли у этого ника бронь прямо сейчас (без выдачи ключа комнаты).
    /// </summary>
    /// <remarks>
    /// Нужно лобби: <c>hasHosting</c> в JoinResponse и push «матч идёт» положены
    /// только тому, кто уже написал <c>/join</c>. Без этой проверки клиент любого
    /// нового игрока получал адрес 7777 и сам прыгал в катку, где его ждал
    /// RoomNotFound.
    /// </remarks>
    public bool HasReservation(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick ?? "");
        if (normalized.Length == 0)
            return false;

        var now = _clock();
        lock (_gate)
        {
            PruneLocked(now);
            return _items.Any(a => string.Equals(a.Nick, normalized, NickComparison));
        }
    }

    /// <summary>Снять бронь по нику (игрок ушёл из лобби).</summary>
    public bool ReleaseByNick(string nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick ?? "");
        if (normalized.Length == 0)
            return false;
        lock (_gate)
        {
            return _items.RemoveAll(a => string.Equals(
                a.Nick, normalized, NickComparison)) > 0;
        }
    }

    /// <summary>Удалить протухшие назначения; вернуть, сколько удалено.</summary>
    public int Prune()
    {
        var now = _clock();
        lock (_gate)
            return PruneLocked(now);
    }

    /// <summary>Снимок для диагностики/тестов (новые первыми).</summary>
    public IReadOnlyList<PendingRoomAssignment> Snapshot()
    {
        var now = _clock();
        lock (_gate)
        {
            PruneLocked(now);
            return _items.OrderByDescending(a => a.CreatedUtc).ToList();
        }
    }

    private int PruneLocked(DateTime now) => _items.RemoveAll(a => a.IsExpired(now));
}
