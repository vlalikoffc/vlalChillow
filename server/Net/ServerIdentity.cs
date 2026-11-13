using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Личность самого сервера: его ник зарезервирован, его id — захардкоженная строка
/// <see cref="Uid"/>, и его нельзя забанить.
/// </summary>
/// <remarks>
/// <para>
/// Ник берётся из того же места, что и синтетический актор матча
/// (<see cref="LobbySession.ServerPlayerName"/>), а не из литерала в командах. Поэтому если
/// ник сервера когда-нибудь переименуют в коде, резерв и id переедут вместе с ним: копий
/// под новым именем тоже не будет.
/// </para>
/// <para>
/// Id сервера — не число, а строка <c>Server</c>: сервер стоит выше игроков и админов, и
/// этот id не участвует в нумерации игроков вообще. Клиент не может его присвоить: у
/// актора сервера хост всегда пишет своё значение, а игроку — его собственный числовой id.
/// Забанить <c>Server</c> нельзя ни командой <c>/ban</c>, ни через <c>/banid</c> (тот
/// принимает только числа), и никто из игроков не может получить этот id.
/// </para>
/// </remarks>
public static class ServerIdentity
{
    /// <summary>Ник сервера («Server», см. <see cref="LobbySession.ServerPlayerName"/>).</summary>
    public const string Nick = LobbySession.ServerPlayerName;

    /// <summary>
    /// Id сервера — захардкоженная строка, а не число: это не «ещё один игрок в нумерации»,
    /// а отдельная сущность выше админов. Именно это значение уходит в actor-свойство
    /// <c>uid</c> у актора сервера.
    /// </summary>
    public const string Uid = "Server";

    /// <summary>
    /// Ник занят сервером? Сравнение после <see cref="ServerConfig.NormalizeNick"/> и без
    /// учёта регистра: «server», « SERVER » и «Server» — один и тот же зарезервированный ник.
    /// </summary>
    public static bool IsServerNick(string? nick)
    {
        var normalized = ServerConfig.NormalizeNick(nick);
        return normalized.Length > 0
            && string.Equals(normalized, Nick, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Это id сервера (строка <c>Server</c>)? Такой id не банится и игрокам не выдаётся.
    /// </summary>
    public static bool IsServerUid(string? uid) =>
        !string.IsNullOrEmpty(uid)
        && string.Equals(uid, Uid, StringComparison.OrdinalIgnoreCase);
}
