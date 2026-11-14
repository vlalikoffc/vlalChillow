namespace StandChillow.LanServer.Net.Match.Kernel;

/// <summary>
/// Ожидающее назначение: «когда в матч-хост придёт игрок с таким ником — отдать ему
/// комнату <see cref="PasswordKey"/>».
/// </summary>
/// <remarks>
/// <para>
/// Провод <b>не меняется</b>: клиент по-прежнему шлёт <c>JoinRoomRequest{room=nick,
/// password='Dedik'}</c> — этот же пакет он слал в 2020 году. Выбор комнаты делается на
/// сервере: панель запоминает, кто создал комнату, и когда этот ник появляется в
/// <c>JoinRoom</c>, его пускают именно туда, а не в общий <c>Dedik</c>.
/// </para>
/// <para>
/// Ник — единственное, что стабильно до перезахода: IP меняется на мобильной сети,
/// токен живёт одну сессию. Поэтому ключ — нормализованный ник, а
/// <see cref="IdentityKey"/> / <see cref="Address"/> лишь уточняют выбор при совпадении.
/// </para>
/// <para>
/// TTL ограничивает «вечную» бронь: зашёл, передумал, ушёл — назначение протухает само.
/// </para>
/// </remarks>
public sealed class PendingRoomAssignment
{
    /// <summary>Сколько живёт назначение по умолчанию.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(120);

    public required string PasswordKey { get; init; }

    /// <summary>Нормализованный ник (см. <c>ServerConfig.NormalizeNick</c>).</summary>
    public required string Nick { get; init; }

    /// <summary>Install-stable идентичность, если клиент уже её прислал; иначе пусто.</summary>
    public string IdentityKey { get; init; } = "";

    /// <summary>IPv4/IPv6 адрес лобби-пира; последний фильтр при совпадении ников.</summary>
    public string Address { get; init; } = "";

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public TimeSpan Ttl { get; init; } = DefaultTtl;

    public bool IsExpired(DateTime nowUtc) => nowUtc - CreatedUtc > Ttl;

    public DateTime ExpiresUtc => CreatedUtc + Ttl;

    public override string ToString() =>
        $"{Nick} → {PasswordKey} (ttl {(int)Ttl.TotalSeconds}s, expires {ExpiresUtc:HH:mm:ss}Z)";
}
