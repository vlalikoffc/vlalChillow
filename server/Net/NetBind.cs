using System.Net;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Адрес, на котором открываются UDP-сокеты лобби и матча.
/// </summary>
/// <remarks>
/// Задаётся блоком <c>network</c> конфига и применяется только выделенным сервером.
/// В пробе (<c>--client</c>) остаётся <c>0.0.0.0</c>: проба — клиент, она никуда
/// не слушает, а уходящие сокеты привязываются отдельно через
/// <c>LanInterfacePicker.PickLanBindAddress</c>.
/// </remarks>
public static class NetBind
{
    /// <summary><c>0.0.0.0</c> — все интерфейсы (Wi‑Fi, Ethernet, VPN, Hamachi).</summary>
    public static IPAddress Address { get; private set; } = IPAddress.Any;

    /// <summary>Строка для логов.</summary>
    public static string Describe => Address.ToString();

    /// <summary>Применить <c>network.bindAddress</c>. Пусто/бито → <c>0.0.0.0</c>.</summary>
    public static void Apply(NetworkConfig network)
    {
        Address = IPAddress.TryParse(network.BindAddress?.Trim(), out var ip)
            ? ip
            : IPAddress.Any;
    }
}
