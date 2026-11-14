namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Сколько людей помещается в комнату и как они делятся по командам.
/// </summary>
/// <remarks>
/// <para>
/// Это <b>не</b> ограничение движка (он пускает до 16 человек в любую комнату), а
/// правило игры: «союзники 1 на 1» — это два человека, а не шестнадцать, иначе
/// половина сидит зрителями и не понимает, почему не может играть.
/// </para>
/// <para>
/// Значения взяты из того, что режим реально умеет:
/// <list type="bullet">
/// <item><c>Ranked2v2</c> / <c>Ranked2v2Alt</c> — 1v1 или 2v2, размер выбирается;</item>
/// <item><c>Defuse</c> — 6 на команду;</item>
/// <item><c>RankedDefuse</c> — 5 на команду;</item>
/// <item><c>Duel</c> — 1v1 или 2v2, как у союзников;</item>
/// <item>остальные — без деления на команды, до 16.</item>
/// </list>
/// </para>
/// </remarks>
public static class RoomCapacity
{
    /// <summary>Потолок движка: больше шестнадцати в комнату не влезет.</summary>
    public const int EngineMaxPlayers = 16;

    /// <summary>Размер одной команды и общий потолок для режима.</summary>
    /// <param name="PerTeam">Сколько человек в команде.</param>
    /// <param name="Total">Сколько всего помещается.</param>
    /// <param name="Choosable">
    /// Варианты размера, которые человек выбирает при создании. Пусто — режим
    /// размер не выбирает, берётся <paramref name="PerTeam"/>.
    /// </param>
    public readonly record struct Shape(int PerTeam, int Total, IReadOnlyList<int> Choosable);

    /// <summary>Режимы, где размер команды выбирается при создании комнаты.</summary>
    public static readonly IReadOnlyList<int> OneOrTwo = new[] { 1, 2 };

    /// <summary>Размер по умолчанию и допустимые варианты для режима.</summary>
    public static Shape ForMode(string? gameModeId)
    {
        return gameModeId switch
        {
            // Союзники и дуэль: 1v1 либо 2v2, по выбору создающего.
            "Ranked2v2" or "Ranked2v2Alt" or "Duel" =>
                new Shape(PerTeam: 2, Total: 4, Choosable: OneOrTwo),

            // Бомбовые режимы: фиксированный размер команды, выбора нет.
            "Defuse" => new Shape(PerTeam: 6, Total: 12, Choosable: Array.Empty<int>()),
            "RankedDefuse" => new Shape(PerTeam: 5, Total: 10, Choosable: Array.Empty<int>()),

            // Остальные: без командного деления, движковый потолок.
            _ => new Shape(PerTeam: EngineMaxPlayers, Total: EngineMaxPlayers,
                Choosable: Array.Empty<int>()),
        };
    }

    /// <summary>
    /// Привести размер команды к допустимому для режима.
    /// </summary>
    /// <param name="gameModeId">Режим комнаты.</param>
    /// <param name="perTeam">Сколько просили; <c>0</c> — «как в режиме по умолчанию».</param>
    /// <param name="result">Итоговый размер команды.</param>
    /// <returns>
    /// <c>true</c> — размер подходит или исправлен на ближайший допустимый;
    /// <c>false</c> — режим не выбирает размер, а просили не то (честная ошибка).
    /// </returns>
    public static bool TryResolvePerTeam(string? gameModeId, int perTeam, out int result)
    {
        var shape = ForMode(gameModeId);
        result = shape.PerTeam;

        if (perTeam <= 0)
            return true;

        if (shape.Choosable.Count == 0)
        {
            // Режим размер не выбирает: либо точное совпадение, либо ошибка.
            return perTeam == shape.PerTeam;
        }

        if (shape.Choosable.Contains(perTeam))
        {
            result = perTeam;
            return true;
        }

        // Просили нечто среднее (например 3 для 1v1/2v2) — берём ближайший меньший,
        // чтобы комната не оказалась больше, чем человек ожидал.
        var smaller = shape.Choosable.Where(v => v < perTeam).DefaultIfEmpty(0).Max();
        result = smaller > 0 ? smaller : shape.Choosable.Min();
        return true;
    }

    /// <summary>Сколько всего людей помещается при таком размере команды.</summary>
    public static int TotalFor(string? gameModeId, int perTeam)
    {
        var shape = ForMode(gameModeId);
        if (shape.Choosable.Count == 0)
            return shape.Total;

        var team = shape.Choosable.Contains(perTeam) ? perTeam : shape.PerTeam;
        return team * 2;
    }

    /// <summary>
    /// Человеческая подпись размера: «1 на 1», «2 на 2», «6 на 6»,
    /// а для режимов без команд — «до 16».
    /// </summary>
    public static string Describe(string? gameModeId, int perTeam)
    {
        var shape = ForMode(gameModeId);
        if (shape.Choosable.Count == 0)
        {
            // Бомбовые режимы тоже командные — подписываем как «6 на 6».
            return shape.PerTeam * 2 == shape.Total
                ? $"{shape.PerTeam} на {shape.PerTeam}"
                : $"до {shape.Total}";
        }

        var team = shape.Choosable.Contains(perTeam) ? perTeam : shape.PerTeam;
        return $"{team} на {team}";
    }
}
