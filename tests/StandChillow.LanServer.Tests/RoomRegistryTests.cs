using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Реестр комнат: номера, валидация режима/карты, квота «один ник = одна комната»,
/// освобождение ника при удалении и TTL. Сокетов нет — проверяется только решение.
/// </summary>
public sealed class RoomRegistryTests
{
    private static bool Create(
        RoomRegistry reg,
        string mode,
        string? level,
        string nick,
        out RoomDescriptor? room,
        out RoomCreateError error) =>
        reg.TryCreate(mode, level, name: null, ownerNick: nick, out room, out error);

    [Fact]
    public void FirstRoom_GetsNumberOne()
    {
        var reg = new RoomRegistry();

        Assert.True(Create(reg, "2v2", "Sandstone", "влал", out var room, out var error));
        Assert.Equal(RoomCreateError.None, error);
        Assert.NotNull(room);
        Assert.Equal(1, room!.Number);
        Assert.Equal("Ranked2v2", room.ModeId);
        Assert.Equal("Sandstone 2x2", room.Level);   // префикс каталога → полное имя
        Assert.Equal(RoomState.Waiting, room.State);
        Assert.Equal(0, room.Players);
    }

    [Fact]
    public void Numbers_FillTheLowestHole()
    {
        var reg = new RoomRegistry();

        Assert.True(Create(reg, "2v2", null, "a", out var first, out _));
        Assert.True(Create(reg, "duel", null, "b", out var second, out _));
        Assert.Equal(1, first!.Number);
        Assert.Equal(2, second!.Number);

        // Освободили #2 — следующая комната займёт именно её, а не #3.
        // Номер остаётся коротким, и человеку не нужно помнить, какие ещё живы.
        Assert.True(reg.TryRemove(2, out _));
        Assert.True(Create(reg, "defuse", null, "c", out var third, out _));
        Assert.Equal(2, third!.Number);
    }

    [Fact]
    public void LowestFreeNumberIsUsedEvenWithGaps()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out _, out _));   // #1
        Assert.True(Create(reg, "2v2", null, "b", out _, out _));   // #2
        Assert.True(Create(reg, "2v2", null, "c", out _, out _));   // #3

        // Убираем середину: #2 должна достаться следующей комнате.
        Assert.True(reg.TryRemove(2, out _));
        Assert.True(Create(reg, "2v2", null, "d", out var next, out _));
        Assert.Equal(2, next!.Number);

        // #1 и #3 на месте, #2 заняли — следующая #4.
        Assert.True(Create(reg, "2v2", null, "e", out var after, out _));
        Assert.Equal(4, after!.Number);
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        var reg = new RoomRegistry();

        Assert.False(Create(reg, "не-режим", null, "влал", out var room, out var error));
        Assert.Null(room);
        Assert.Equal(RoomCreateError.UnknownMode, error);
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void UnknownLevel_IsRejected()
    {
        var reg = new RoomRegistry();

        // Block есть в каталоге Duel, но не в Ranked2v2 — это опечатка, а не «своя карта».
        Assert.False(Create(reg, "2v2", "Block", "влал", out _, out var error));
        Assert.Equal(RoomCreateError.UnknownLevel, error);
        Assert.Equal(0, reg.Count);
    }

    [Fact]
    public void AmbiguousLevel_IsRejected()
    {
        var reg = new RoomRegistry();

        // «2x2» входит во все семь карт Ranked2v2 → неоднозначно, значит отказ,
        // а не «взяли первую попавшуюся».
        Assert.False(Create(reg, "2v2", "2x2", "влал", out _, out var error));
        Assert.Equal(RoomCreateError.UnknownLevel, error);
    }

    [Fact]
    public void EmptyNick_IsRejected()
    {
        var reg = new RoomRegistry();

        Assert.False(Create(reg, "2v2", null, "   ", out _, out var error));
        Assert.Equal(RoomCreateError.EmptyNick, error);
    }

    [Fact]
    public void OneNick_OneRoom()
    {
        var reg = new RoomRegistry();

        Assert.True(Create(reg, "2v2", null, "влал", out _, out _));
        Assert.False(Create(reg, "duel", null, "влал", out _, out var error));
        Assert.Equal(RoomCreateError.DuplicateNick, error);
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void NickMatch_IsCaseAndWidthInsensitive()
    {
        var reg = new RoomRegistry();

        Assert.True(Create(reg, "2v2", null, "Влал", out _, out _));
        // Другое написание того же ника — тот же владелец (нормализация как в банах).
        Assert.False(Create(reg, "duel", null, "вЛАЛ", out _, out var error));
        Assert.Equal(RoomCreateError.DuplicateNick, error);
    }

    [Fact]
    public void Removing_RoomFreesTheNick()
    {
        var reg = new RoomRegistry();

        Assert.True(Create(reg, "2v2", null, "влал", out var room, out _));
        Assert.False(reg.Quota("влал").Allowed);

        Assert.True(reg.TryRemove(room!.Number, out var removed));
        Assert.Equal("влал", removed.OwnerNick);

        // Ник свободен — можно создать новую комнату (номер будет следующим).
        Assert.True(reg.Quota("влал").Allowed);
        Assert.True(Create(reg, "duel", null, "влал", out var second, out _));
        Assert.Equal(2, second!.Number);
    }

    [Fact]
    public void Removing_UnknownRoom_IsFalse()
    {
        var reg = new RoomRegistry();
        Assert.False(reg.TryRemove(99, out _));
    }

    [Fact]
    public void TryFind_RequiresMatchingMode()
    {
        var reg = new RoomRegistry();
        Assert.True(reg.TryCreate("defuse", "Prison", "по фану", "влал", out var room, out _));

        // Правильный режим (алиас и id) — находим.
        Assert.True(reg.TryFind("defuse", room!.Number, out var byAlias));
        Assert.Equal(room.Number, byAlias.Number);
        Assert.True(reg.TryFind("Defuse", room.Number, out _));

        // Чужой режим — «не найдено», а не «соседняя комната».
        Assert.False(reg.TryFind("duel", room.Number, out _));
        Assert.False(reg.TryFind("2v2", room.Number, out _));

        // Несуществующий номер.
        Assert.False(reg.TryFind("defuse", 77, out _));
    }

    [Fact]
    public void TryFind_AcceptsAliases()
    {
        var reg = new RoomRegistry();
        Assert.True(reg.TryCreate("2v2", null, null, "влал", out var room, out _));

        Assert.True(reg.TryFind("2v2", room!.Number, out _));
        Assert.True(reg.TryFind("allies", room.Number, out _));
        Assert.True(reg.TryFind("Ranked2v2", room.Number, out _));
    }

    [Fact]
    public void RoomLimit_IsEnforced()
    {
        var reg = new RoomRegistry { MaxRooms = 2 };

        Assert.True(Create(reg, "2v2", null, "a", out _, out _));
        Assert.True(Create(reg, "2v2", null, "b", out _, out _));
        Assert.False(Create(reg, "2v2", null, "c", out _, out var error));
        Assert.Equal(RoomCreateError.RoomLimitReached, error);
        Assert.Equal(2, reg.Count);
    }

    [Fact]
    public void MaxRooms_IsClamped()
    {
        var reg = new RoomRegistry { MaxRooms = 0 };
        Assert.Equal(1, reg.MaxRooms);

        reg.MaxRooms = 99999;
        Assert.Equal(1024, reg.MaxRooms);
    }

    [Fact]
    public void DefaultName_UsesNumber()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out var room, out _));
        Assert.Equal("Комната #1", room!.Name);
    }

    [Fact]
    public void LongName_IsTruncated()
    {
        var reg = new RoomRegistry();
        var longName = new string('я', 100);
        Assert.True(reg.TryCreate("2v2", null, longName, "a", out var room, out _));
        Assert.Equal(32, room!.Name.Length);
    }

    [Fact]
    public void PasswordKey_RoundTrips()
    {
        var key = RoomRegistry.BuildPasswordKey(3);
        Assert.Equal("room#3", key);
        Assert.True(RoomRegistry.TryParsePasswordKey(key, out var number));
        Assert.Equal(3, number);

        Assert.False(RoomRegistry.TryParsePasswordKey("Dedik", out _));
        Assert.False(RoomRegistry.TryParsePasswordKey(null, out _));
        Assert.False(RoomRegistry.TryParsePasswordKey("room#", out _));
        Assert.False(RoomRegistry.TryParsePasswordKey("room#0", out _));
        Assert.False(RoomRegistry.TryParsePasswordKey("room#abc", out _));
    }

    [Fact]
    public void Update_ReflectsStateAndPlayers()
    {
        var reg = new RoomRegistry();
        Assert.True(reg.TryCreate("2v2", null, null, "a", out var room, out _));

        Assert.True(reg.TryUpdate(room!.Number, RoomState.Live, 4));
        var snapshot = reg.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal(RoomState.Live, snapshot[0].State);
        Assert.Equal(4, snapshot[0].Players);

        // Отрицательное число игроков — не «магия», а 0.
        Assert.True(reg.TryUpdate(room.Number, RoomState.Live, -5));
        Assert.Equal(0, reg.Snapshot()[0].Players);

        Assert.False(reg.TryUpdate(999, RoomState.Live, 1));
    }

    [Fact]
    public void Snapshot_IsNewestFirst()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out _, out _));
        Assert.True(Create(reg, "duel", null, "b", out _, out _));
        Assert.True(Create(reg, "defuse", null, "c", out _, out _));

        var numbers = reg.Snapshot().Select(r => r.Number).ToList();
        Assert.Equal(new[] { 3, 2, 1 }, numbers);
    }

    [Fact]
    public void FindExpired_EmptyRoom_AfterTtl()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out var room, out _));
        var created = room!.CreatedUtc;

        var ttl = TimeSpan.FromMinutes(10);

        // Через 9 минут — ещё жива.
        Assert.Empty(reg.FindExpired(created.AddMinutes(9), ttl, ttl));

        // Через 10 — пора.
        Assert.Single(reg.FindExpired(created.AddMinutes(10), ttl, ttl));
    }

    [Fact]
    public void FindExpired_BusyRoom_IsKept()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out var room, out _));
        Assert.True(reg.TryUpdate(room!.Number, RoomState.Live, 3));

        var created = room.CreatedUtc;
        var ttl = TimeSpan.FromMinutes(10);

        // В комнате играют — по TTL её не сносим, даже если она старая.
        Assert.Empty(reg.FindExpired(created.AddHours(5), ttl, ttl));
    }

    [Fact]
    public void FindExpired_FinishedRoom_UsesFinishedTtl()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "a", out var room, out _));
        Assert.True(reg.TryUpdate(room!.Number, RoomState.Finished, 0));

        var created = room.CreatedUtc;
        var emptyTtl = TimeSpan.FromMinutes(10);
        var finishedTtl = TimeSpan.FromSeconds(30);

        Assert.Empty(reg.FindExpired(created.AddSeconds(20), emptyTtl, finishedTtl));
        Assert.Single(reg.FindExpired(created.AddSeconds(31), emptyTtl, finishedTtl));
    }

    [Fact]
    public void Quota_ReportsOwnershipAndLimit()
    {
        var reg = new RoomRegistry { MaxRooms = 1 };

        var before = reg.Quota("влал");
        Assert.True(before.Allowed);
        Assert.Equal(0, before.OwnerRooms);
        Assert.Equal(RoomCreateError.None, before.Error);

        Assert.True(Create(reg, "2v2", null, "влал", out _, out _));

        var mine = reg.Quota("влал");
        Assert.False(mine.Allowed);
        Assert.Equal(1, mine.OwnerRooms);
        Assert.Equal(RoomCreateError.DuplicateNick, mine.Error);

        // Другому нику места уже нет — но причина другая: потолок, а не «у тебя уже есть».
        var other = reg.Quota("другой");
        Assert.False(other.Allowed);
        Assert.Equal(RoomCreateError.RoomLimitReached, other.Error);

        Assert.Equal(RoomCreateError.EmptyNick, reg.Quota("  ").Error);
    }

    [Fact]
    public void OwnerOf_ReturnsOwnerOrEmpty()
    {
        var reg = new RoomRegistry();
        Assert.True(Create(reg, "2v2", null, "влал", out var room, out _));

        Assert.Equal("влал", reg.OwnerOf(room!.Number));
        Assert.Equal(string.Empty, reg.OwnerOf(999));
    }

    [Fact]
    public void NextNumber_TracksAllocation()
    {
        var reg = new RoomRegistry();
        Assert.Equal(1, reg.NextNumber);

        Assert.True(Create(reg, "2v2", null, "a", out _, out _));
        Assert.Equal(2, reg.NextNumber);

        Assert.True(Create(reg, "duel", null, "b", out _, out _));
        Assert.Equal(3, reg.NextNumber);
    }

    [Fact]
    public void ConcurrentCreates_NeverCollideOnNumbers()
    {
        var reg = new RoomRegistry { MaxRooms = 512 };
        var created = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 200, i =>
        {
            if (reg.TryCreate("2v2", null, null, "nick" + i, out var room, out _))
                created.Add(room!.Number);
        });

        var numbers = created.ToList();
        Assert.Equal(200, numbers.Count);
        Assert.Equal(200, numbers.Distinct().Count());
        Assert.Equal(201, reg.NextNumber);
    }
}
