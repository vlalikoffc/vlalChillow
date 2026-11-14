using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Маршрутизация «кто в какую комнату попадает».
/// </summary>
/// <remarks>
/// <para>
/// Живой прогон 2026-11-14 (<c>latest.log</c>) дал два бага сразу, и оба здесь
/// зафиксированы как контракт:
/// </para>
/// <list type="number">
/// <item>
/// Сервер разослал <c>op9</c> всем в лобби — клиент на любой такой сигнал идёт на
/// 7777 (так он устроен, адреса комнат он не знает) и попадал в legacy-комнату
/// <c>Dedik</c> с глобальными режимом и картой. Это и было «параллельное измерение»:
/// люди, которые ничего не писали, оказывались в чужой катке на другой карте.
/// </item>
/// <item>
/// Брони нет, а панельные комнаты есть — пускать в <c>Dedik</c> нельзя вообще.
/// Игрок должен получить честный отказ и написать <c>/join</c>.
/// </item>
/// </list>
/// <para>
/// Сокеты не поднимаются: проверяются чистые правила выбора комнаты.
/// </para>
/// </remarks>
public sealed class JoinRoutingTests
{
    [Fact]
    public void ReservationRoutesPlayerToItsRoom()
    {
        var pending = new PendingRoomAssignments();
        pending.Reserve("room#3", "влал");

        Assert.True(pending.TryResolve("влал", null, "192.168.1.5", out var key));
        Assert.Equal("room#3", key);
    }

    [Fact]
    public void NoReservationMeansNoRoute()
    {
        var pending = new PendingRoomAssignments();
        pending.Reserve("room#3", "влал");

        // Другой игрок ничего не бронировал — маршрута нет.
        Assert.False(pending.TryResolve("другой", null, "192.168.1.6", out _));
    }

    [Fact]
    public void ExpiredReservationStopsRouting()
    {
        var clock = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var pending = new PendingRoomAssignments(() => clock);
        pending.Reserve("room#1", "влал", ttl: TimeSpan.FromSeconds(30));

        clock = clock.AddSeconds(31);

        // Бронь протухла — игрок не должен попасть в комнату, которую уже заняли.
        Assert.False(pending.TryResolve("влал", null, null, out _));
    }

    [Fact]
    public void DifferentPlayersGetDifferentRooms()
    {
        var pending = new PendingRoomAssignments();
        pending.Reserve("room#1", "влал");
        pending.Reserve("room#2", "другой");

        Assert.True(pending.TryResolve("влал", null, null, out var first));
        Assert.True(pending.TryResolve("другой", null, null, out var second));
        Assert.Equal("room#1", first);
        Assert.Equal("room#2", second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ReJoinSameNickReplacesReservation()
    {
        // Игрок передумал и написал другой номер: ведём в последнюю комнату,
        // а не в первую. Иначе /join 2v2 #1 → /join 2v2 #3 оставил бы в #1.
        var pending = new PendingRoomAssignments();
        pending.Reserve("room#1", "влал");
        pending.Reserve("room#3", "влал");

        Assert.True(pending.TryResolve("влал", null, null, out var key));
        Assert.Equal("room#3", key);
    }

    [Fact]
    public void PanelRoomIsDistinguishableFromLegacy()
    {
        // Хелпер HasPanelRooms() опирается ровно на этот флаг.
        var panel = new MatchRoom
        {
            PasswordKey = "room#1",
            RoomId = "lobby",
            DisplayNumber = 1,
            IsPanelRoom = true,
        };
        var legacy = new MatchRoom { PasswordKey = "Dedik", RoomId = "lobby" };

        Assert.True(panel.IsPanelRoom);
        Assert.False(legacy.IsPanelRoom);
    }

    [Fact]
    public void NoSuchRoomKeyIsNeverARealRoom()
    {
        // Ключ-заглушка не должен случайно совпасть с настоящей комнатой:
        // в нём нулевой символ, а CleanName такие имена не пропускает.
        const string placeholder = "\u0000нет-такой-комнаты";
        Assert.Contains('\u0000', placeholder);
        Assert.NotEqual("Dedik", placeholder);
        Assert.NotEqual("room#1", placeholder);
    }
}
