using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Баны на канале матча (UDP 7777): IP на коннекте, ник + IP — до выдачи актора.
/// </summary>
/// <remarks>
/// <para>
/// Проверок ровно две и обе на входе: <c>ConnectionRequest</c> (знаем только адрес) и
/// <c>JoinRoom</c> (знаем ник). На пакеты состояния ничего не навешивается —
/// <c>WorldObjectState</c>/<c>Rpc</c> идут тем же путём, что и раньше.
/// </para>
/// <para>
/// Гейт в <c>JoinRoom</c> стоит <b>до</b> <c>TryFindRejoinActor</c> намеренно: реконнект
/// внутри матча не проходит обычный join, он усыновляет старого актора. Без проверки там
/// читер мог зайти под VPN, выключить его и вернуться реконнектом с уже забаненного адреса.
/// </para>
/// <para>
/// Стабильный токен установки намеренно <b>не</b> используется: только ник и IP, как в ТЗ,
/// чтобы не задеть нормального игрока.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Реестр банов (ставит <c>GameNetHost</c>; в пробе/тестах — null).</summary>
    public BanRegistry? Bans { get; set; }

    /// <summary>Предикат «ник админский»: на админов бан не распространяется.</summary>
    public Func<string?, bool>? IsAdminNick { get; set; }

    /// <summary>IP-гейт самого раннего входа: до handshake, до комнаты, до актора.</summary>
    internal bool IsBannedAddress(IPAddress? address)
    {
        if (Bans is null || address is null)
            return false;

        return Bans.IsIpBanned(address.ToString(), DateTime.UtcNow);
    }

    /// <summary>Полный гейт входа в матч и реконнекта (ник + IP).</summary>
    internal BanDecision InspectBanJoin(string? nick, IPAddress? address)
    {
        if (Bans is null)
            return BanDecision.Allowed;

        return Bans.Inspect(
            new BanProbe(nick, address?.ToString()),
            DateTime.UtcNow,
            IsAdminNick);
    }

    /// <summary>
    /// Отключить забаненного от канала матча (7777). Ищем по нику и адресу сразу:
    /// к моменту бана сокет мог ещё не прислать JoinRoom, и ника в состоянии не было.
    /// </summary>
    public int DisconnectBanned(string? nick, IPAddress? address)
    {
        var target = ServerConfig.NormalizeNick(nick);
        var kicked = 0;

        foreach (var (peer, state) in _peers)
        {
            var matched =
                (address is not null && peer.Address is not null && peer.Address.Equals(address))
                || (target.Length > 0
                    && string.Equals(ServerConfig.NormalizeNick(state.RosterName), target,
                        StringComparison.OrdinalIgnoreCase));

            if (!matched)
                continue;

            try
            {
                peer.Disconnect();
                kicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ban] match disconnect {peer.Address}: {ex.Message}");
            }
        }

        if (kicked > 0)
            Console.WriteLine($"[ban] матч: отключено сокетов {kicked} (ник '{nick}', IP {address?.ToString() ?? "—"})");

        return kicked;
    }
}
