using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Определяет, кто из игроков лобби написал <c>/команду</c> в катке (ChatManager).
/// </summary>
/// <remarks>
/// Ник — основной ключ: ровно так же админка сверяет автора с <c>admin.nicknames</c>,
/// поэтому «команда от влала» и «влал — админ» смотрят на одно и то же имя.
/// IP — запасной путь и <b>только когда он однозначен</b>: за NAT/Radmin несколько
/// телефонов приходят с одного адреса, и выбор «первого попавшегося» молча подменял
/// автора (а с ним — и права), чего в логах пользователя и не хватало понять.
/// Если игрока найти нельзя, возвращается <c>null</c> и причина в <paramref name="reason"/>,
/// которую вызывающий обязан залогировать, — отклонять команду молча запрещено.
/// </remarks>
public static class MatchChatAuthorResolver
{
    public readonly record struct Hit(ConnectedPlayer Player, string ResolvedBy);

    public static Hit? Resolve(
        string? actorName,
        IReadOnlyList<ConnectedPlayer> joined,
        IReadOnlyList<ConnectedPlayer> joinedWithMatchIp,
        out string reason)
    {
        reason = string.Empty;

        if (joined.Count == 0)
        {
            reason = "в лобби никого нет";
            return null;
        }

        var nick = ServerConfig.NormalizeNick(actorName);
        if (nick.Length > 0)
        {
            foreach (var candidate in joined)
            {
                if (string.Equals(
                        ServerConfig.NormalizeNick(candidate.Name),
                        nick,
                        StringComparison.OrdinalIgnoreCase))
                    return new Hit(candidate, "nick");
            }

            // Actor-имя иногда приходит обрезанным — сравниваем ещё и по префиксу,
            // но только когда совпадение единственное.
            ConnectedPlayer? prefix = null;
            var hits = 0;
            foreach (var candidate in joined)
            {
                var normalized = ServerConfig.NormalizeNick(candidate.Name);
                if (normalized.Length > 0
                    && normalized.StartsWith(nick, StringComparison.OrdinalIgnoreCase))
                {
                    prefix = candidate;
                    hits++;
                }
            }

            if (prefix is not null && hits == 1)
                return new Hit(prefix, "nick-prefix");
        }

        if (joinedWithMatchIp.Count == 1)
            return new Hit(joinedWithMatchIp[0], "ip");

        if (joinedWithMatchIp.Count > 1)
        {
            reason = $"с этого IP несколько игроков ({joinedWithMatchIp.Count}), " +
                     $"а ник '{actorName}' не совпал — угадывать автора нельзя";
            return null;
        }

        if (joined.Count == 1)
            return new Hit(joined[0], "only-player");

        reason = nick.Length == 0
            ? "пустое имя автора, и IP не помог"
            : $"ник '{actorName}' не найден среди {joined.Count} игроков лобби";
        return null;
    }
}
