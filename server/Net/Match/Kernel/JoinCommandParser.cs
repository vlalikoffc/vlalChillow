namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Разбор команды лобби <c>/join &lt;режим&gt; #&lt;номер&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Строго лобби-команда: в матче её обрабатывать нечем (игрок уже в комнате), поэтому
/// разбор чистый и не знает ни о сокетах, ни о реестре.
/// </para>
/// <para>
/// Формат ровно тот, что печатает панель в кнопке «скопировать»: режим, пробел, решётка,
/// номер. Режим — любой алиас каталога (<c>2v2</c>, <c>tdm</c>, <c>duel</c>, …), номер —
/// целое &gt; 0. Хвостовой мусор не «прощается»: если человек скопировал строку не целиком,
/// лучше сказать «не понял», чем пустить не туда.
/// </para>
/// </remarks>
public static class JoinCommandParser
{
    /// <summary>Разобранная команда.</summary>
    /// <param name="ModeToken">Режим как его напечатал игрок (для резолва каталогом).</param>
    /// <param name="RoomNumber">Номер комнаты; 0 — не разобрали.</param>
    public readonly record struct Parsed(string ModeToken, int RoomNumber)
    {
        public bool Ok => RoomNumber > 0 && ModeToken.Length > 0;
    }

    /// <summary>Максимальный номер, который принимаем (защита от «#999999999999»).</summary>
    public const int MaxRoomNumber = 999_999;

    /// <summary>
    /// Разобрать аргументы после <c>/join</c>.
    /// </summary>
    /// <param name="args">Например <c>2v2 #3</c>, <c>tdm #12</c>, <c>2v2 #3 лишнее</c>.</param>
    public static Parsed Parse(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return default;

        var body = args.Trim();

        // Решётка — последний токен, режим — всё до неё (режимы бывают с пробелом:
        // «ranked defuse» каталог тоже принимает).
        var hash = body.LastIndexOf('#');
        if (hash < 0)
            return default;

        var modeToken = body[..hash].Trim();
        var numberToken = body[(hash + 1)..].Trim();

        if (modeToken.Length == 0 || numberToken.Length == 0)
            return default;

        // Только цифры: «#3a» и «#-1» — не номер комнаты.
        foreach (var ch in numberToken)
            if (ch < '0' || ch > '9')
                return default;

        if (!int.TryParse(numberToken, out var number))
            return default;
        if (number <= 0 || number > MaxRoomNumber)
            return default;

        return new Parsed(modeToken, number);
    }

    /// <summary>
    /// Текст «комната не найдена» ровно в том виде, что требует спека.
    /// </summary>
    /// <remarks>
    /// Адрес не выдумывается: он приходит снаружи (см. <c>SiteAddressResolver</c>) —
    /// в коде и тестах не должно быть настоящего домена/IP (см. CONTRIBUTING.md).
    /// </remarks>
    public static string FormatNotFound(string modeDisplayName, int number, string siteAddress) =>
        $"Комната «{modeDisplayName} * {number}» не найдена. " +
        $"Создать можно здесь: {siteAddress}";
}
