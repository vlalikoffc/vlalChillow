using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 3 плана <c>docs/ROOMS_AND_WEB_PLAN.md</c>: маршрутизация <c>JoinRoom</c> по нику.
/// Провод неизменен — клиент шлёт ник и <c>'Dedik'</c>; выбор комнаты целиком на сервере.
/// </summary>
public sealed class PendingRoomAssignmentsTests
{
    private sealed class ClockState
    {
        public DateTime Now { get; set; }
    }

    [Fact]
    public void NoReservationMeansLegacyRoom()
    {
        var reg = new PendingRoomAssignments();
        Assert.False(reg.TryResolve("влал", null, "10.0.0.2", out var key));
        Assert.Equal("", key);
    }

    [Fact]
    public void ReserveThenResolveSameNick()
    {
        var reg = new PendingRoomAssignments();
        Assert.True(reg.Reserve("room#3", "влал"));

        Assert.True(reg.TryResolve("влал", null, null, out var key));
        Assert.Equal("room#3", key);
    }

    [Fact]
    public void NickComparedCaseInsensitively()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "Vlal");
        Assert.True(reg.TryResolve("vLaL", null, null, out var key));
        Assert.Equal("room#1", key);
    }

    [Fact]
    public void NickNormalizedBeforeMatching()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#7", "  влал   chillow  ");
        Assert.True(reg.TryResolve("влал chillow", null, null, out var key));
        Assert.Equal("room#7", key);
    }

    [Fact]
    public void SecondReservationForSameNickReplaces()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал");
        reg.Reserve("room#2", "влал");

        Assert.True(reg.TryResolve("влал", null, null, out var key));
        Assert.Equal("room#2", key);
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void EmptyNickOrKeyIsRejected()
    {
        var reg = new PendingRoomAssignments();
        Assert.False(reg.Reserve("room#1", "   "));
        Assert.False(reg.Reserve("", "влал"));
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void ExpiredReservationIsNotUsed()
    {
        var clock = new ClockState { Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) };
        var reg = new PendingRoomAssignments(() => clock.Now);
        reg.Reserve("room#1", "влал", ttl: TimeSpan.FromSeconds(120));

        clock.Now = clock.Now.AddSeconds(121);

        Assert.False(reg.TryResolve("влал", null, null, out _));
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void ReservationAliveJustBeforeTtl()
    {
        var clock = new ClockState { Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) };
        var reg = new PendingRoomAssignments(() => clock.Now);
        reg.Reserve("room#1", "влал", ttl: TimeSpan.FromSeconds(120));

        clock.Now = clock.Now.AddSeconds(119);

        Assert.True(reg.TryResolve("влал", null, null, out var key));
        Assert.Equal("room#1", key);
    }

    [Fact]
    public void OneNickHoldsExactlyOneRoom()
    {
        // Правило из плана: 1 комната на игрока. Значит и назначение одно — два игрока
        // с одинаковым ником делят одно назначение, а не получают каждый своё.
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал", identityKey: "install-a");
        reg.Reserve("room#2", "влал", identityKey: "install-b");

        Assert.Equal(1, reg.Count);
        Assert.True(reg.TryResolve("влал", "install-a", null, out var key));
        Assert.Equal("room#2", key); // побеждает свежая бронь
    }

    [Fact]
    public void IdentityMismatchStillResolvesButIsLogged()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал", identityKey: "install-a");

        // Другой клиент с тем же ником: комната та же (выбора нет), вызывающий код
        // обязан помнить, что ник — не ключ безопасности.
        Assert.True(reg.TryResolve("влал", "install-b", "10.9.9.9", out var key));
        Assert.Equal("room#1", key);
    }

    [Fact]
    public void ReservationRemembersIdentityAndAddress()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал", identityKey: "install-a", address: "10.0.0.5");

        var item = Assert.Single(reg.Snapshot());
        Assert.Equal("install-a", item.IdentityKey);
        Assert.Equal("10.0.0.5", item.Address);
        Assert.Equal("room#1", item.PasswordKey);
    }

    [Fact]
    public void DifferentNicksKeepOwnRooms()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал");
        reg.Reserve("room#2", "chillow");

        Assert.True(reg.TryResolve("влал", null, null, out var a));
        Assert.True(reg.TryResolve("chillow", null, null, out var b));
        Assert.Equal("room#1", a);
        Assert.Equal("room#2", b);
    }

    [Fact]
    public void ReleaseByKeyDropsReservation()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал");

        Assert.True(reg.Release("room#1"));
        Assert.False(reg.TryResolve("влал", null, null, out _));
        Assert.False(reg.Release("room#1"));
    }

    [Fact]
    public void ReleaseByNickDropsReservation()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал");

        Assert.True(reg.ReleaseByNick("ВЛАЛ"));
        Assert.False(reg.TryResolve("влал", null, null, out _));
    }

    [Fact]
    public void CapacityIsEnforcedButNickReuseStillWorks()
    {
        var reg = new PendingRoomAssignments { Capacity = 2 };
        Assert.True(reg.Reserve("room#1", "a"));
        Assert.True(reg.Reserve("room#2", "b"));
        Assert.False(reg.Reserve("room#3", "c"));

        // Обновление существующего ника не требует свободного места.
        Assert.True(reg.Reserve("room#9", "a"));
        Assert.True(reg.TryResolve("a", null, null, out var key));
        Assert.Equal("room#9", key);
    }

    [Fact]
    public void PruneRemovesOnlyExpired()
    {
        var clock = new ClockState { Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) };
        var reg = new PendingRoomAssignments(() => clock.Now);
        reg.Reserve("room#1", "старый", ttl: TimeSpan.FromSeconds(10));

        clock.Now = clock.Now.AddSeconds(30);

        // Reserve сам чистит просроченные записи, поэтому к этому моменту
        // room#1 уже удалён — Prune не находит ничего нового.
        reg.Reserve("room#2", "новый", ttl: TimeSpan.FromSeconds(600));

        Assert.Equal(0, reg.Prune());
        Assert.Single(reg.Snapshot());
        Assert.True(reg.TryResolve("новый", null, null, out var key));
        Assert.Equal("room#2", key);

        // И старая бронь больше не резолвится.
        Assert.False(reg.TryResolve("старый", null, null, out _));
    }

    [Fact]
    public void PruneKeepsLiveReservationsAndDropsExpiredOnes()
    {
        // Prune должен снимать только истёкшие записи. Свежая и старая, но живая —
        // остаются. Проверяем без Reserve между шагами: Reserve чистит сам.
        var clock = new ClockState { Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) };
        var reg = new PendingRoomAssignments(() => clock.Now);
        reg.Reserve("room#expired", "старый", ttl: TimeSpan.FromSeconds(10));
        reg.Reserve("room#live", "живой", ttl: TimeSpan.FromSeconds(600));

        clock.Now = clock.Now.AddSeconds(30);

        Assert.Equal(1, reg.Prune());
        var snap = reg.Snapshot();
        Assert.Single(snap);
        Assert.Equal("room#live", snap[0].PasswordKey);

        // Повторный Prune ничего не находит — идемпотентность.
        Assert.Equal(0, reg.Prune());
    }

    [Fact]
    public void SnapshotIsNewestFirst()
    {
        var clock = new ClockState { Now = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc) };
        var reg = new PendingRoomAssignments(() => clock.Now);
        reg.Reserve("room#1", "первый");
        clock.Now = clock.Now.AddSeconds(5);
        reg.Reserve("room#2", "второй");

        var snap = reg.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("room#2", snap[0].PasswordKey);
        Assert.Equal("room#1", snap[1].PasswordKey);
    }

    [Fact]
    public void ParallelReservationsDoNotCorruptState()
    {
        var reg = new PendingRoomAssignments { Capacity = 512 };
        Parallel.For(0, 200, i =>
            reg.Reserve($"room#{i}", $"ник{i}"));

        Assert.Equal(200, reg.Count);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(reg.TryResolve($"ник{i}", null, null, out var key));
            Assert.Equal($"room#{i}", key);
        }
    }

    [Fact]
    public void NullClockValuesAreTolerated()
    {
        var reg = new PendingRoomAssignments();
        reg.Reserve("room#1", "влал", identityKey: null, address: null);

        Assert.True(reg.TryResolve("влал", null, null, out var key));
        Assert.Equal("room#1", key);
    }

    [Fact]
    public void DefaultTtlIsTwoMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), PendingRoomAssignment.DefaultTtl);
    }
}
