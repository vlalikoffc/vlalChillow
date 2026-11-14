namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Кем подписать серверный кадр чата катки, чтобы клиент его нарисовал.
/// </summary>
/// <remarks>
/// <para>
/// Наблюдение, а не догадка: <c>/chattest</c> (latest-3.log, 2026-11-14, Ranked2v2 /
/// Sandstone) отправил один и тот же текст четырьмя кадрами всем пирам катки, и на экране
/// появилась только буква <b>C</b>:
/// </para>
/// <list type="bullet">
/// <item><description>A — автор 1 («Server»), <c>field=1</c>: не видно;</description></item>
/// <item><description>B — автор 1, <c>field=2</c>: не видно;</description></item>
/// <item><description>C — автор 4 (FinalSapper78, живой игрок), <c>field=1</c>:
/// <b>видно всем в чате катки</b>;</description></item>
/// <item><description>D — автор 4, <c>field=2</c>: не видно.</description></item>
/// </list>
/// <para>
/// A отличается от C только байтом автора, C от D — только <c>field</c>. Значит клиент
/// рисует сообщение при <c>field=1</c> и авторе, у которого в мире есть своё тело, а кадр
/// от актора 1 выделенного сервера («Server», команда Spectator, пешки нет) не рисует
/// никогда. В gold-капчурах чат подписан актором 1 — но там актор 1 это телефон-хост, у
/// которого свои пешки 129/130 (<c>owner=1</c>), то есть тот же класс авторов, что и C.
/// </para>
/// <para>
/// Расплата известна и честна: байт автора клиент превращает в имя и аватарку, поэтому
/// серверное сообщение рисуется под именем живого игрока. Чтобы оно не выглядело его
/// словами, текст уходит с пометкой <see cref="ServerPrefix"/>.
/// </para>
/// </remarks>
internal static class MatchChatAuthor
{
    /// <summary>Пометка серверного текста: имя в пузыре будет чужое (см. remarks класса).</summary>
    public const string ServerPrefix = "[Server] ";

    /// <summary>
    /// Актор, которым подписать серверный кадр для конкретного получателя: первый живой
    /// (с пешкой) актор, который не сам получатель, — своё сообщение клиент может не
    /// нарисовать. Живых нет вовсе (до матча, все ждут респавн) — остаётся актор сервера:
    /// это прежний, заведомо невидимый вариант, но рисовать всё равно некому.
    /// </summary>
    /// <param name="recipientActor">Номер актора получателя (0 — получатель неизвестен).</param>
    /// <param name="liveActors">Акторы, у которых в мире есть пешка.</param>
    /// <param name="hostActor">Актор сервера (<see cref="MatchHostActor.ActorNr"/>).</param>
    public static byte PickServerVoice(
        byte recipientActor,
        IEnumerable<byte> liveActors,
        byte hostActor)
    {
        var live = liveActors.Where(a => a != 0).OrderBy(a => a).ToList();
        var notRecipient = live.Where(a => a != recipientActor).ToList();
        if (notRecipient.Count > 0)
            return notRecipient[0];

        // Живой только сам получатель: его собственный актор всё же лучше актора сервера —
        // серверный клиент не рисует точно (буквы A/B пробы), а свой, возможно, нарисует.
        return live.Count > 0 ? live[0] : hostActor;
    }

    /// <summary>Серверный текст с пометкой. Повторно не помечает: текст уже мог прийти с ней.</summary>
    public static string Sign(string text) =>
        text.StartsWith(ServerPrefix, StringComparison.Ordinal) ? text : ServerPrefix + text;
}
