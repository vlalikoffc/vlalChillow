using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Реестр банов: связка ник ↔ IP ↔ токен, выученные алиасы, админ-исключение, срок.
/// </summary>
/// <remarks>
/// Никаких сокетов: реестр — чистая логика входа. Именно эти тесты держат обещания
/// «сменил ник, но IP тот же — тоже бан» и «админа не банят».
/// </remarks>
public sealed class BanRegistryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static BanRegistry NewRegistry() => new() { LearnAliases = true };

    private static BanRecord Ban(
        BanRegistry registry,
        string nick,
        string? ip,
        DateTime? now = null,
        int minutes = 30)
    {
        return registry.Add(
            new BanProbe(nick, ip),
            TimeSpan.FromMinutes(minutes),
            "софт",
            "влал",
            now ?? Now);
    }

    [Fact]
    public void BanByNickAndIp_BlocksBothKeys()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "192.168.1.37");

        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "192.168.1.37"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Другой", "192.168.1.37"), Now).Banned);
        Assert.False(registry.Inspect(new BanProbe("Другой", "192.168.1.99"), Now).Banned);
    }

    [Fact]
    public void AnyAddressKindIsBanned_LanHamachiVpnPublic()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "192.168.1.37");

        // Проверок «только локальный» нет: запись по адресу работает для любого.
        Assert.True(registry.IsIpBanned("192.168.1.37", Now));
        Assert.False(registry.IsIpBanned("203.0.113.9", Now));

        Ban(registry, "HamachiGuy", "25.14.3.9");
        Ban(registry, "VpnGuy", "10.8.0.2");
        Ban(registry, "PublicGuy", "203.0.113.9");

        Assert.True(registry.IsIpBanned("25.14.3.9", Now));
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
        Assert.True(registry.IsIpBanned("203.0.113.9", Now));
    }

    [Fact]
    public void NewIpUnderSameNick_IsLearnedAndBlocked()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now);

        Assert.True(decision.Banned);
        Assert.Equal("ник", decision.MatchedBy);
        Assert.True(decision.LearnedAlias);
        Assert.Contains("10.8.0.2", record.Ips);
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
    }

    [Fact]
    public void NewNickOnBannedIp_IsLearnedAndBlocked()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy2", "203.0.113.9"), Now);

        Assert.True(decision.Banned);
        Assert.Equal("IP", decision.MatchedBy);
        Assert.True(decision.LearnedAlias);
        Assert.Contains("Ktulxy2", record.Aliases);

        // И следующий заход уже под ником-алиасом с нового адреса тоже закрыт.
        Assert.True(registry.Inspect(new BanProbe("Ktulxy2", "198.51.100.7"), Now).Banned);
    }

    [Fact]
    public void AdminIsNeverBannedAndNoAliasIsLearned()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(
            new BanProbe("влал", "203.0.113.9"), Now, nick => nick == "влал");

        Assert.False(decision.Banned);
        Assert.True(decision.AdminExempt);
        Assert.DoesNotContain("влал", record.Aliases);
    }

    [Fact]
    public void ExpiredBanStopsBlockingAndIsPruned()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9", minutes: 30);

        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);

        var later = Now.AddMinutes(31);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), later).Banned);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void RepeatedBanExtendsTheSameRecordInsteadOfDuplicating()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9", minutes: 30);
        var record = Ban(registry, "Ktulxy", "203.0.113.9", minutes: 180);

        Assert.Equal(1, registry.Count);
        Assert.Single(registry.Export());
        Assert.Equal(TimeSpan.FromMinutes(180), record.Remaining(Now));
    }

    [Fact]
    public void MatchIdTagAllowsUnbanById()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("Ktulxy", "203.0.113.9"),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 671488);

        Assert.True(record.HasMatchId(671488));

        var removed = registry.RemoveByMatchId(671488);

        Assert.Single(removed);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);
    }

    [Fact]
    public void KnownIpsFromDirectoryAreBannedWithThePerson()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("Ktulxy", null),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 1,
            knownIps: new[] { "203.0.113.9", "10.8.0.2" });

        Assert.Equal(2, record.Ips.Count);
        Assert.True(registry.IsIpBanned("203.0.113.9", Now));
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
    }

    [Fact]
    public void TimeAndTimezoneAreStoredTimezoneProof()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        // Срок считается от unix ms, а не от локальной строки: смена пояса не влияет.
        Assert.Equal(BanRecord.ToUnixMs(Now.AddMinutes(30)), record.ExpiresUtcMs);
        Assert.Equal(TimeSpan.FromMinutes(30), record.Remaining(Now));
        Assert.False(string.IsNullOrWhiteSpace(record.CreatedLocalIso));
    }

    [Fact]
    public void LearnAliasesCanBeSwitchedOff()
    {
        var registry = new BanRegistry { LearnAliases = false };
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now);

        Assert.True(decision.Banned);
        Assert.False(decision.LearnedAlias);
        Assert.DoesNotContain("10.8.0.2", record.Ips);
        Assert.False(registry.IsIpBanned("10.8.0.2", Now));
    }

    [Fact]
    public void RemoveByNickAndByIp()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9");

        Assert.True(registry.Remove("ktulxy", out var byNick));
        Assert.Equal("Ktulxy", byNick!.DisplayNick);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);

        Ban(registry, "Ktulxy", "203.0.113.9");
        Assert.True(registry.Remove("203.0.113.9", out var byIp));
        Assert.NotNull(byIp);
        Assert.False(registry.IsIpBanned("203.0.113.9", Now));
    }

    [Fact]
    public void ActiveListShowsNearestExpiryFirstAndSkipsExpired()
    {
        var registry = NewRegistry();
        Ban(registry, "Долгий", "203.0.113.1", minutes: 600);
        Ban(registry, "Короткий", "203.0.113.2", minutes: 30);

        var active = registry.Active(Now);

        Assert.Equal(2, active.Count);
        Assert.Equal("Короткий", active[0].DisplayNick);
        Assert.Equal("Долгий", active[1].DisplayNick);

        Assert.Single(registry.Active(Now.AddMinutes(60)));
    }

    [Fact]
    public void ChangedFiresWhenTheSetMutates()
    {
        var registry = NewRegistry();
        var saves = 0;
        registry.Changed = () => saves++;

        Ban(registry, "Ktulxy", "203.0.113.9");
        Assert.True(saves > 0);

        var before = saves;
        registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now); // выучил новый IP
        Assert.True(saves > before);

        before = saves;
        registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now); // уже знает
        Assert.Equal(before, saves);
    }
}
