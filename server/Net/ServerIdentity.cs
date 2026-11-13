using System.Globalization;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Личность самого сервера: его ник зарезервирован, у него есть свой id, и его нельзя забанить.
/// </summary>
/// <remarks>
/// <para>
/// Ник берётся из того же места, что и синтетический актор матча
/// (<see cref="LobbySession.ServerPlayerName"/>), а не из литерала в командах. Поэтому если
/// ник сервера когда-нибудь переименуют в коде, резерв и id переедут вместе с ним: копий
/// под новым именем тоже не будет.
/// </para>
/// <para>
/// Id сервера — обычная функция от ника (<see cref="MatchPublicId"/>), но он выведен из
/// пространства ключей бана: <c>/banid</c> и <c>/unbanid</c> по нему отказывают, а записи
/// банлиста им не тегируются. Игрок, чей ник случайно даёт тот же id, банится по нику/IP —
/// id сервера при этом никого не задевает.
/// </para>
/// </remarks>
public static class ServerIdentity
{
    /// <summary>Ник сервера («Server», см. <see cref="LobbySession.ServerPlayerName"/>).</summary>
    public const string Nick = LobbySession.ServerPlayerName;

    /// <summary>Серверный id сервера: та же функция от ника, что и у игроков.</summary>
    public static readonly int Uid = MatchPublicId.ForNick(Nick);

    /// <summary>Он же десятичной строкой — значение actor-свойства <c>uid</c>.</summary>
    public static string UidText { get; } =
        Uid.ToString(CultureInfo.InvariantCulture);

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

    /// <summary>Это id сервера? Такой id — не ключ бана, а сам сервер.</summary>
    public static bool IsServerUid(int id) => id == Uid;
}
