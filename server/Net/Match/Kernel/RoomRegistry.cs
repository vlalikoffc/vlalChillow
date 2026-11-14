using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Состояние комнаты с точки зрения «её видно снаружи» (веб-панель, лобби).
/// </summary>
public enum RoomState : byte
{
    /// <summary>Комната открыта, но в ней ещё никто не играет (или ждут вторую команду).</summary>
    Waiting = 0,

    /// <summary>Матч фактически идёт (первая фаза после WarmUp).</summary>
    Live = 1,

    /// <summary>Матч завершён, комната ждёт удаления по TTL.</summary>
    Finished = 2,
}

/// <summary>
/// Одна комната, созданная с веб-панели. Полей ровно столько, сколько сервер
/// действительно знает: номер, режим, карта, имя, владелец — ничего «на будущее».
/// </summary>
/// <remarks>
/// <para>
/// <b>Номер</b> — наименьший свободный. Комнаты #1 и #3 открыты, значит новая получит #2.
/// Так номер всегда короткий и не приходится помнить, какие ещё живы. Обратная сторона
/// честно покрывается в тексте команды: игроку показывают режим и карту комнаты, а не
/// только номер, поэтому «после закрытия #2 новая #2 — уже другая катка» не путает.
/// </para>
/// <para>
/// <b>Владелец</b> — это ник из лобби. Одна комната на ник: это не игровое правило, а
/// защита от «создал 500 пустых комнат кнопкой». Освобождается вместе с комнатой.
/// </para>
/// </remarks>
public sealed record RoomDescriptor(
    int Number,
    string ModeId,
    string Level,
    string Name,
    string PasswordKey,
    string OwnerNick,
    DateTime CreatedUtc,
    RoomState State,
    int Players,
    DateTime? EmptySinceUtc,
    int PerTeam);

/// <summary>
/// Верхняя граница номера комнаты. Номер печатается в команде <c>/join 2v2 #N</c>,
/// поэтому он остаётся коротким; 999 комнат на LAN-сервер — заведомо больше, чем нужно.
/// </summary>
public static class RoomLimits
{
    public const int MaxRoomNumber = 999;
}

/// <summary>Результат <see cref="RoomRegistry.TryCreate"/> — без исключений на потоке HTTP.</summary>
public enum RoomCreateError : byte
{
    None = 0,
    UnknownMode = 1,
    UnknownLevel = 2,
    EmptyNick = 3,
    DuplicateNick = 4,
    TooManyRooms = 5,
    RoomLimitReached = 6,
    UnknownTeamSize = 7,
}

/// <summary>Чья-то лимитная квота: сколько комнат ещё можно открыть.</summary>
public readonly record struct RoomQuota(bool Allowed, int OwnerRooms, int TotalRooms, RoomCreateError Error);

/// <summary>
/// Реестр комнат: номера, режим/карта, владелец, TTL. <b>Без сокетов и без HTTP</b> —
/// поэтому решение «можно ли создать комнату» проверяется в CI без единого пакета.
/// </summary>
/// <remarks>
/// <para>
/// Реестр — это <b>описание</b> комнат для людей (панель, <c>/join</c>). Сама комната живёт в
/// <c>GameMatchHost._roomsByPassword</c>; связь между ними — <see cref="RoomDescriptor.PasswordKey"/>.
/// Разделение сделано намеренно: реестр можно тестировать и показывать, не трогая сеть.
/// </para>
/// <para>
/// <b>Квоты.</b> Ограничение — «один ник = одна созданная комната». Общий потолок
/// (<see cref="MaxRooms"/>) — не игровое правило, а предохранитель: все комнаты делят один
/// UDP-сокет 7777 и один пул пакетов, поэтому бесконечности здесь быть не может.
/// </para>
/// <para>
/// Режим и карта берутся <b>только</b> из <see cref="GameModeCatalog"/>: карта, которой нет в
/// каталоге режима, — это не «своя карта», а опечатка.
/// </para>
/// </remarks>
public sealed class RoomRegistry
{
    /// <summary>Потолок одновременно открытых комнат по умолчанию (предохранитель, не правило).</summary>
    public const int DefaultMaxRooms = 64;

    private readonly object _gate = new();
    private readonly Dictionary<int, RoomDescriptor> _rooms = new();
    private readonly Dictionary<string, int> _byOwner = new(StringComparer.OrdinalIgnoreCase);
    private int _maxRooms = DefaultMaxRooms;

    /// <summary>Потолок открытых комнат. Значения вне 1..1024 подтягиваются в диапазон.</summary>
    public int MaxRooms
    {
        get { lock (_gate) return _maxRooms; }
        set
        {
            var clamped = value < 1 ? 1 : value > 1024 ? 1024 : value;
            lock (_gate) _maxRooms = clamped;
        }
    }

    /// <summary>
    /// Сколько людей сейчас онлайн (лобби + катка). Поставщик — игровой хост.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нужен для правила «комнат не больше, чем игроков»: одна комната на человека —
    /// это резерв места, а не потолок. Пустые комнаты сверх числа игроков означали бы
    /// «сайт открыл больше каток, чем есть людей» и превращали кнопку в спам-слоты.
    /// </para>
    /// <para>
    /// Значение по умолчанию <c>null</c> — правило выключено (тесты реестра, режим без
    /// хоста). Так реестр остаётся чистым и не зависит от сети.
    /// </para>
    /// </remarks>
    public Func<int>? OnlinePlayerCount { get; set; }

    /// <summary>Сколько комнат сейчас открыто.</summary>
    public int Count
    {
        get { lock (_gate) return _rooms.Count; }
    }

    /// <summary>
    /// Номер, который получит новая комната (для подсказок в UI).
    /// </summary>
    /// <remarks>
    /// Это именно наименьший свободный, а не «последний + 1»: нумерация теперь
    /// заполняет дыры, и подсказка обязана совпадать с тем, что реально выдаст
    /// <see cref="TryCreate"/>.
    /// </remarks>
    public int NextNumber
    {
        get { lock (_gate) return AllocateNumber(); }
    }

    /// <summary>
    /// Создаёт комнату. Всё, что можно проверить без сети, проверяется здесь: режим, карта,
    /// ник, квота владельца и общий потолок.
    /// </summary>
    /// <param name="modeToken">Режим как его набрал человек: <c>2v2</c>, <c>Duel</c>, <c>defuse</c>.</param>
    /// <param name="levelToken">Карта: <c>Sandstone</c> тоже подойдёт — каталог умеет префиксы.</param>
    /// <param name="name">Название комнаты; пустое допустимо (UI покажет «Комната #N»).</param>
    /// <param name="ownerNick">Ник из лобби, он же ключ квоты.</param>
    public bool TryCreate(
        string? modeToken,
        string? levelToken,
        string? name,
        string? ownerNick,
        out RoomDescriptor? room,
        out RoomCreateError error,
        int perTeam = 0)
    {
        room = null;
        error = RoomCreateError.None;

        if (!GameModeCatalog.TryResolveMode(modeToken, out var mode))
        {
            error = RoomCreateError.UnknownMode;
            return false;
        }

        var level = GameModeCatalog.DefaultLevelFor(mode.GameModeId);
        if (!string.IsNullOrWhiteSpace(levelToken)
            && !GameModeCatalog.TryResolveLevel(mode, levelToken!, out level, out _))
        {
            error = RoomCreateError.UnknownLevel;
            return false;
        }

        if (!RoomCapacity.TryResolvePerTeam(mode.GameModeId, perTeam, out var teamSize))
        {
            error = RoomCreateError.UnknownTeamSize;
            return false;
        }

        var nick = NormalizeNick(ownerNick);
        if (nick.Length == 0)
        {
            error = RoomCreateError.EmptyNick;
            return false;
        }

        lock (_gate)
        {
            if (_byOwner.ContainsKey(nick))
            {
                error = RoomCreateError.DuplicateNick;
                return false;
            }

            if (_rooms.Count >= _maxRooms)
            {
                error = RoomCreateError.RoomLimitReached;
                return false;
            }

            // «Комнат не больше, чем игроков онлайн»: каждый может открыть одну — значит
            // и потолок по числу людей, а не только предохранитель _maxRooms.
            // Ноль онлайн (никто ещё не зашёл) не блокирует: первый игрок пришёл с сайта
            // раньше, чем клиент достучался до лобби, и это нормальный порядок.
            var online = OnlinePlayerCount?.Invoke() ?? 0;
            if (online > 0 && _rooms.Count >= online)
            {
                error = RoomCreateError.TooManyRooms;
                return false;
            }

            // Наименьший свободный номер: комнаты #1 и #3 открыты → новая получит #2.
            // Раньше номера шли монотонно и дыр не заполняли, но тогда после закрытия
            // комнаты #2 следующая становилась #4, и людям приходилось держать в
            // голове «какие номера ещё живы». Свободный номер избавляет от этого.
            var number = AllocateNumber();
            var created = new RoomDescriptor(
                Number: number,
                ModeId: mode.GameModeId,
                Level: level,
                Name: CleanName(name, number),
                PasswordKey: BuildPasswordKey(number),
                OwnerNick: nick,
                CreatedUtc: DateTime.UtcNow,
                State: RoomState.Waiting,
                Players: 0,
                EmptySinceUtc: DateTime.UtcNow,
                PerTeam: teamSize);

            _rooms[number] = created;
            _byOwner[nick] = number;
            room = created;
            return true;
        }
    }

    /// <summary>
    /// Наименьший номер, которого сейчас нет среди открытых комнат.
    /// </summary>
    /// <remarks>
    /// Вызывается под <c>_gate</c>. Верхняя граница — <see cref="RoomLimits.MaxRoomNumber"/>:
    /// номер уходит в команду <c>/join 2v2 #N</c>, и он должен оставаться коротким.
    /// </remarks>
    private int AllocateNumber()
    {
        for (var candidate = 1; candidate <= RoomLimits.MaxRoomNumber; candidate++)
        {
            if (!_rooms.ContainsKey(candidate))
                return candidate;
        }

        // Теоретически недостижимо: _maxRooms (≤1024) меньше MaxRoomNumber.
        // Если случится — не выдаём номер за границей, а честно упираемся в потолок.
        return RoomLimits.MaxRoomNumber;
    }

    /// <summary>
    /// Ищет комнату по тому, что человек набрал в <c>/join</c>: режим (алиас или id) и номер.
    /// Несовпадение режима — это <b>не найдено</b>, а не «нашли соседнюю»: иначе
    /// <c>/join duel #3</c> увёл бы в бомбовую комнату #3.
    /// </summary>
    public bool TryFind(string? modeToken, int number, out RoomDescriptor room)
    {
        room = null!;
        if (!GameModeCatalog.TryResolveMode(modeToken, out var mode))
            return false;

        lock (_gate)
        {
            if (!_rooms.TryGetValue(number, out var found))
                return false;
            if (!string.Equals(found.ModeId, mode.GameModeId, StringComparison.Ordinal))
                return false;
            room = found;
            return true;
        }
    }

    /// <summary>Все открытые комнаты, новые сверху — порядок, в котором их ждёт панель.</summary>
    public IReadOnlyList<RoomDescriptor> Snapshot()
    {
        lock (_gate)
            return _rooms.Values.OrderByDescending(r => r.Number).ToList();
    }

    /// <summary>Сколько комнат ещё может создать этот ник.</summary>
    public RoomQuota Quota(string? ownerNick)
    {
        var nick = NormalizeNick(ownerNick);
        if (nick.Length == 0)
            return new RoomQuota(false, 0, Count, RoomCreateError.EmptyNick);

        lock (_gate)
        {
            var mine = _byOwner.ContainsKey(nick) ? 1 : 0;
            var total = _rooms.Count;
            var error = mine > 0 ? RoomCreateError.DuplicateNick
                : total >= _maxRooms ? RoomCreateError.RoomLimitReached
                : RoomCreateError.None;
            return new RoomQuota(error == RoomCreateError.None, mine, total, error);
        }
    }

    /// <summary>Кто владелец комнаты с этим номером (пусто — нет такой).</summary>
    public string OwnerOf(int number)
    {
        lock (_gate)
            return _rooms.TryGetValue(number, out var r) ? r.OwnerNick : string.Empty;
    }

    /// <summary>Обновление счётчиков от матч-хоста: сколько людей и какая фаза.</summary>
    public bool TryUpdate(int number, RoomState state, int players)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(number, out var current))
                return false;

            var clampedPlayers = players < 0 ? 0 : players;

            // Отсчёт «комната пустует» начинается с момента, когда из неё ушёл
            // последний человек, а не с создания. Иначе катка, в которой играли час,
            // удалялась бы мгновенно при выходе всех: возраст-то уже большой.
            var emptySince = current.EmptySinceUtc;
            if (clampedPlayers > 0)
                emptySince = null;
            else if (current.Players > 0)
                emptySince = DateTime.UtcNow;

            if (current.State == state && current.Players == clampedPlayers
                && current.EmptySinceUtc == emptySince)
                return true;

            _rooms[number] = current with
            {
                State = state,
                Players = clampedPlayers,
                EmptySinceUtc = emptySince,
            };
            return true;
        }
    }

    /// <summary>
    /// Удаляет комнату и освобождает ник. Номер при этом <b>не</b> возвращается в оборот —
    /// иначе старые команды <c>/join</c> начали бы означать другую комнату.
    /// </summary>
    public bool TryRemove(int number, out RoomDescriptor room)
    {
        room = null!;
        lock (_gate)
        {
            if (!_rooms.Remove(number, out var removed))
                return false;

            if (_byOwner.TryGetValue(removed.OwnerNick, out var owned) && owned == number)
                _byOwner.Remove(removed.OwnerNick);

            room = removed;
            return true;
        }
    }

    /// <summary>
    /// Кого можно удалить по TTL: комната пустая (0 игроков) и живёт дольше
    /// <paramref name="emptyTtl"/>. Завершённая катка удаляется сразу по <paramref name="finishedTtl"/>.
    /// </summary>
    public IReadOnlyList<int> FindExpired(DateTime nowUtc, TimeSpan emptyTtl, TimeSpan finishedTtl)
    {
        var emptySeconds = Math.Max(0, emptyTtl.TotalSeconds);
        var finishedSeconds = Math.Max(0, finishedTtl.TotalSeconds);

        lock (_gate)
        {
            var expired = new List<int>();
            foreach (var room in _rooms.Values)
            {
                if (room.State == RoomState.Finished)
                {
                    // Завершённая катка живёт от конца матча, а не от создания.
                    var finishedAge = (nowUtc - room.CreatedUtc).TotalSeconds;
                    if (finishedAge >= finishedSeconds)
                        expired.Add(room.Number);
                    continue;
                }

                // Пустая комната — ровно от того момента, когда в ней никого не стало.
                if (room.Players > 0)
                    continue;

                var since = room.EmptySinceUtc ?? room.CreatedUtc;
                if ((nowUtc - since).TotalSeconds >= emptySeconds)
                    expired.Add(room.Number);
            }
            return expired;
        }
    }

    /// <summary>
    /// Ключ комнаты в матч-хосте. Живёт рядом с номером, чтобы <c>/join #3</c> и запись в
    /// <c>GameMatchHost._roomsByPassword</c> всегда означали одну и ту же комнату.
    /// </summary>
    public static string BuildPasswordKey(int number) => "room#" + number;

    /// <summary>Номер комнаты из её ключа (<c>room#3</c> → 3); <c>false</c> — ключ не наш.</summary>
    public static bool TryParsePasswordKey(string? key, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        const string prefix = "room#";
        var k = key.Trim();
        if (!k.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return int.TryParse(k.AsSpan(prefix.Length), out number) && number > 0;
    }

    /// <summary>
    /// Один ник = одна комната. Сравнение как в остальном проекте
    /// (<c>ServerConfig.NormalizeNick</c>), чтобы «Влал» и «влал» были одним человеком.
    /// </summary>
    public static string NormalizeNick(string? nick) =>
        string.IsNullOrWhiteSpace(nick) ? string.Empty : ServerConfig.NormalizeNick(nick);

    private static string CleanName(string? name, int number)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return "Комната #" + number;

        // Имя приходит из HTTP: обрезаем длину, но не «исправляем» содержимое —
        // экранирование делает тот, кто рисует (страница), а не хранилище.
        return trimmed.Length <= 32 ? trimmed : trimmed[..32];
    }
}
