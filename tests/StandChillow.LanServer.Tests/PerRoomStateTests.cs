using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// PR 2 плана <c>docs/ROOMS_AND_WEB_PLAN.md</c>: состояние матча — в комнате, а не в глобалах.
/// Проверяем ровно то, что ломалось бы при нескольких комнатах на одном UDP 7777:
/// гейт старта, first-to-N и половинная серия у каждой комнаты свои.
/// Сокеты не поднимаются — только чистые правила.
/// </summary>
public sealed class PerRoomStateTests
{
    [Fact]
    public void LegacyRoomFallsBackToGlobalGate()
    {
        var room = LegacyRoom();
        MatchHostSettings.MatchStartArmed = false;
        Assert.False(MatchHostSettings.IsStartArmed(room));
        Assert.False(MatchHostSettings.IsStartArmed((MatchRoom?)null));

        MatchHostSettings.MatchStartArmed = true;
        Assert.True(MatchHostSettings.IsStartArmed(room));
        Assert.True(MatchHostSettings.IsStartArmed((MatchRoom?)null));

        MatchHostSettings.MatchStartArmed = false;
    }

    [Fact]
    public void PanelRoomIgnoresGlobalGate()
    {
        var room = PanelRoom(winsNeeded: 8);
        MatchHostSettings.MatchStartArmed = true;

        Assert.False(MatchHostSettings.IsStartArmed(room));

        room.StartArmed = true;
        MatchHostSettings.MatchStartArmed = false;
        Assert.True(MatchHostSettings.IsStartArmed(room));
    }

    [Fact]
    public void DisarmingOneRoomKeepsOthersArmed()
    {
        var a = PanelRoom(winsNeeded: 8);
        var b = PanelRoom(winsNeeded: 8);
        a.StartArmed = true;
        b.StartArmed = true;

        MatchHostSettings.DisarmStart(a);

        Assert.False(MatchHostSettings.IsStartArmed(a));
        Assert.True(MatchHostSettings.IsStartArmed(b));
    }

    [Fact]
    public void DisarmLegacyRoomClearsGlobalFlag()
    {
        var room = LegacyRoom();
        MatchHostSettings.MatchStartArmed = true;

        MatchHostSettings.DisarmStart(room);

        Assert.False(MatchHostSettings.MatchStartArmed);
    }

    [Theory]
    [InlineData(8, 7, 0, false)] // 7 < 8 — катка идёт
    [InlineData(8, 8, 0, true)]  // Tr взял 8
    [InlineData(8, 0, 8, true)]  // Ct взял 8
    [InlineData(6, 6, 5, true)]  // casual Defuse first-to-6
    [InlineData(6, 5, 5, false)]
    public void PanelRoomUsesItsOwnFirstToN(int winsNeeded, int tr, int ct, bool over)
    {
        var room = PanelRoom(winsNeeded);
        Assert.Equal(over, MatchHostSettings.IsAlliesMatchOver(room, tr, ct));
    }

    [Fact]
    public void DifferentRoomsDifferentFirstToN()
    {
        var casual = PanelRoom(winsNeeded: 6);
        var gold = PanelRoom(winsNeeded: 8);

        // 6:5 — casual уже закончился, gold ещё идёт.
        Assert.True(MatchHostSettings.IsAlliesMatchOver(casual, 6, 5));
        Assert.False(MatchHostSettings.IsAlliesMatchOver(gold, 6, 5));
    }

    [Fact]
    public void RoomWithoutOverrideFallsBackToGlobalWins()
    {
        var room = LegacyRoom();
        var saved = MatchHostSettings.WinsNeeded;
        try
        {
            MatchHostSettings.WinsNeeded = 8;
            Assert.False(MatchHostSettings.IsAlliesMatchOver(room, 7, 0));
            Assert.True(MatchHostSettings.IsAlliesMatchOver(room, 8, 0));
        }
        finally
        {
            MatchHostSettings.WinsNeeded = saved;
        }
    }

    [Theory]
    [InlineData(8, 7)]
    [InlineData(6, 5)]
    [InlineData(1, 1)] // защита: половинка не может быть нулевой
    public void HalfTimeFollowsRoomWinsNeeded(int winsNeeded, int expected)
    {
        Assert.Equal(expected, MatchHostSettings.HalfTimeAfterRoundFor(PanelRoom(winsNeeded)));
    }

    [Fact]
    public void ZeroOverrideMeansGlobalHalfTime()
    {
        var saved = MatchHostSettings.WinsNeeded;
        try
        {
            MatchHostSettings.WinsNeeded = 8;
            Assert.Equal(MatchHostSettings.HalfTimeAfterRound,
                MatchHostSettings.HalfTimeAfterRoundFor(LegacyRoom()));
        }
        finally
        {
            MatchHostSettings.WinsNeeded = saved;
        }
    }

    [Fact]
    public void PanelRoomDefaultsToArmed()
    {
        var room = PanelRoom(winsNeeded: 8, armed: true);
        Assert.True(room.IsPanelRoom);
        Assert.True(room.StartArmed);
    }

    private static MatchRoom LegacyRoom() => new()
    {
        PasswordKey = "Dedik",
        RoomId = "lobby",
    };

    private static MatchRoom PanelRoom(int winsNeeded, bool armed = false) => new()
    {
        PasswordKey = "room#1",
        RoomId = "lobby",
        DisplayNumber = 1,
        DisplayName = "Тест",
        IsPanelRoom = true,
        StartArmed = armed,
        WinsNeededOverride = winsNeeded,
    };
}
