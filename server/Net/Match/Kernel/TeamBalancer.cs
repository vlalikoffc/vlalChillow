using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Выравнивание команд перед стартом катки.
/// </summary>
/// <remarks>
/// <para>
/// Команду выбирает <b>сам игрок</b> в клиенте — сервер получает готовое свойство
/// <c>team</c> и не может «не дать» ему зайти. Из-за этого на живом сервере получалось
/// 3 на 1 и 2 на 0: двое нажали одну кнопку, третий — другую, и катка стартовала
/// в перекос.
/// </para>
/// <para>
/// Здесь не выбирается команда за игрока (это сломало бы клиент и провод — он ждёт
/// своё собственное значение). Правило мягче и честнее: разницу больше, чем на одного
/// человека, не допускаем. Того, кто зашёл последним на переполненную сторону,
/// переводим на другую — до того, как катка начнётся.
/// </para>
/// <para>
/// Почему «последнего»: он ещё не успел сыграть ни раунда, его вещи не куплены, и
/// перенос для него безболезненнее, чем для того, кто уже стоит на точке. При равенстве
/// «последний» определяется по номеру актёра — он растёт при каждом входе.
/// </para>
/// </remarks>
public static class TeamBalancer
{
    /// <summary>
    /// Кого перевести и куда, чтобы команды выровнялись.
    /// </summary>
    /// <param name="states">Актор и его команда в комнате.</param>
    /// <param name="maxImbalance">
    /// Допустимая разница. <c>1</c> — «2 на 1» можно, «3 на 1» нет (значение по умолчанию).
    /// </param>
    /// <returns>Перенос или <c>null</c>, если всё честно.</returns>
    public static Rebalance? Plan(
        IReadOnlyList<(byte Actor, MatchTeam Team)> states,
        int maxImbalance = 1)
    {
        var tr = new List<byte>();
        var ct = new List<byte>();
        foreach (var (actor, team) in states)
        {
            if (team == MatchTeam.Tr)
                tr.Add(actor);
            else if (team == MatchTeam.Ct)
                ct.Add(actor);
        }

        var diff = tr.Count - ct.Count;
        if (Math.Abs(diff) <= maxImbalance)
            return null;

        // Переводим с переполненной стороны на пустую, начиная с последнего вошедшего.
        var from = diff > 0 ? MatchTeam.Tr : MatchTeam.Ct;
        var to = diff > 0 ? MatchTeam.Ct : MatchTeam.Tr;
        var surplus = diff > 0 ? tr : ct;
        if (surplus.Count == 0)
            return null;

        surplus.Sort();
        // Сколько человек перевести: ровно столько, чтобы разница стала ≤ допуска.
        // Обычно один — но при «4 на 0» нужен не один, а два.
        var moves = new List<byte>();
        var remaining = (Math.Abs(diff) - maxImbalance + 1) / 2;
        for (var i = 0; i < remaining && i < surplus.Count; i++)
            moves.Add(surplus[surplus.Count - 1 - i]);

        return new Rebalance(from, to, moves);
    }

    /// <summary>Один перенос: кого, откуда и куда.</summary>
    public readonly record struct Rebalance(
        MatchTeam From,
        MatchTeam To,
        IReadOnlyList<byte> Actors)
    {
        public override string ToString() =>
            $"{string.Join(",", Actors)}: {From} → {To}";
    }
}
