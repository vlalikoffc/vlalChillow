using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Ник → id: детерминированная функция без состояния. Один ник всегда даёт один id,
/// любые символы участвуют целиком, диапазон 1…100 000 000.
/// </summary>
public sealed class MatchPublicIdTests
{
    [Theory]
    // Зафиксированные значения: если алгоритм/нормализация случайно изменятся,
    // у всех игроков разом поменяются id — тест не даст этого не заметить.
    [InlineData("Ktulxy", 8799747)]
    [InlineData("Ktulxy2", 73197888)]
    [InlineData("влал", 6390243)]
    [InlineData("  влал  ", 6390243)]
    [InlineData("Ktu lxy", 65393474)]
    public void IdIsPinnedAcrossVersions(string nick, int expected)
    {
        Assert.Equal(expected, MatchPublicId.ForNick(nick));
    }

    [Fact]
    public void SameNickAlwaysGivesSameId()
    {
        var first = MatchPublicId.ForNick("Ktulxy");
        for (var i = 0; i < 100; i++)
            Assert.Equal(first, MatchPublicId.ForNick("Ktulxy"));
    }

    [Fact]
    public void IdIsStableForTheSameNickAfterNormalization()
    {
        // Пробелы по краям и невидимые символы — тот же ник.
        var plain = MatchPublicId.ForNick("влал");
        Assert.Equal(plain, MatchPublicId.ForNick("  влал  "));
        Assert.Equal(plain, MatchPublicId.ForNick("вла\u200Bл"));
        Assert.Equal(plain, MatchPublicId.ForNick("влал\uFEFF"));
    }

    [Fact]
    public void SpacesInsideNickAreKeptAndChangeTheId()
    {
        Assert.NotEqual(
            MatchPublicId.ForNick("Ktu lxy"),
            MatchPublicId.ForNick("Ktulxy"));
        Assert.Equal(MatchPublicId.ForNick("a  b"), MatchPublicId.ForNick("a b"));
    }

    [Fact]
    public void UnicodeEmojiAndCustomSymbolsArePartOfTheId()
    {
        var ids = new[]
        {
            MatchPublicId.ForNick("😈Читер😈"),
            MatchPublicId.ForNick("Ω≈ç√∫˜µ≤≥÷"),
            MatchPublicId.ForNick("日本語のニック"),
            MatchPublicId.ForNick("|/\\[]{}#$%^&*()"),
        };

        Assert.All(ids, id => Assert.InRange(id, MatchPublicId.Min, MatchPublicId.Max));
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public void DifferentNickDifferentId()
    {
        Assert.NotEqual(MatchPublicId.ForNick("влал"), MatchPublicId.ForNick("Влал"));
        Assert.NotEqual(MatchPublicId.ForNick("Ktulxy"), MatchPublicId.ForNick("Ktulxy2"));
    }

    [Fact]
    public void RangeIsOneToOneHundredMillion()
    {
        for (var i = 0; i < 2000; i++)
        {
            var id = MatchPublicId.ForNick($"player-{i}");
            Assert.InRange(id, 1, 100_000_000);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B")]
    public void EmptyNickHasNoId(string? nick)
    {
        Assert.Equal(0, MatchPublicId.ForNick(nick));
    }

    [Fact]
    public void TwoThousandsNicksCollideRarely()
    {
        // 2000 ников в 100 млн бакетов: ожидаемое число коллизий ~0.02.
        var ids = new HashSet<int>();
        var collisions = 0;
        for (var i = 0; i < 2000; i++)
            if (!ids.Add(MatchPublicId.ForNick($"ban-test-{i}")))
                collisions++;

        Assert.InRange(collisions, 0, 5);
    }
}
