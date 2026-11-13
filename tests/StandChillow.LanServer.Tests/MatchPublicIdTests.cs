using System.Security.Cryptography;
using System.Text;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
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
    // Как проверить вручную: printf 'KTULXY' | sha256sum → a0e6302b6118a8d2…
    // → первые 8 байт little-endian → 15179409349667776160 → % 100000000 + 1 = 67776161.
    [InlineData("Ktulxy", 67776161)]
    [InlineData("Ktulxy2", 67196325)]
    [InlineData("влал", 25354325)]
    [InlineData("  влал  ", 25354325)]
    [InlineData("Ktu lxy", 87780421)]
    public void IdIsPinnedAcrossVersions(string nick, int expected)
    {
        Assert.Equal(expected, MatchPublicId.ForNick(nick));
    }

    [Fact]
    public void WireUidIsTheDecimalId()
    {
        // Клиент присылает uid строкой «Offline» — сервер отдаёт то же самое поле числом.
        Assert.True(MatchPublicId.TryWireUid("Ktulxy", out var latin));
        Assert.Equal("67776161", latin);

        Assert.True(MatchPublicId.TryWireUid("влал", out var cyrillic));
        Assert.Equal("25354325", cyrillic);
    }

    [Fact]
    public void WireUidIsRefusedForEmptyNick()
    {
        Assert.False(MatchPublicId.TryWireUid(null, out var none));
        Assert.Equal(string.Empty, none);
        Assert.False(MatchPublicId.TryWireUid("   ", out _));
    }

    [Fact]
    public void IndependentComputationAgreesForFiveHundredNicks()
    {
        // Вторая реализация того же контракта: тот же SHA-256, но uint64 собирается из
        // digest побайтово, без BinaryPrimitives. Ловит ошибку порядка байт и «плывущую»
        // нормализацию — то есть ровно то, из-за чего id разошёлся бы между серверами.
        for (var i = 0; i < 500; i++)
        {
            var nick = $"proof-{i}";
            var normalized = ServerConfig.NormalizeNick(nick).ToUpperInvariant();
            normalized = normalized.Normalize(NormalizationForm.FormC);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            ulong value = 0;
            for (var b = 7; b >= 0; b--)
                value = (value << 8) | hash[b];

            var expected = (int)(value % 100_000_000UL) + 1;
            Assert.Equal(expected, MatchPublicId.ForNick(nick));
        }
    }

    [Fact]
    public void FiveIndependentServersGiveTheSameIdForVlal()
    {
        // Пять «серверов»: у каждого свой справочник, свой реестр и свой мусор —
        // разные игроки, разные баны, разный порядок входов. Общего состояния нет,
        // поэтому «влал» обязан получить один и тот же id на всех пяти.
        var ids = new List<int>();
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

        for (var server = 0; server < 5; server++)
        {
            var directory = new PlayerDirectory();
            var registry = new BanRegistry { LearnAliases = true };

            for (var i = 0; i < server * 40; i++)
                directory.Remember($"server{server}-player{i}", $"10.0.{server}.{i}", now);

            for (var b = 0; b < server; b++)
            {
                registry.Add(
                    new BanProbe($"server{server}-banned{b}", $"10.9.{server}.{b}"),
                    TimeSpan.FromMinutes(30), "шум", "влал", now);
            }

            directory.Remember("влал", $"192.168.1.{server}", now);
            ids.Add(MatchPublicId.ForNick("влал"));
        }

        Assert.All(ids, id => Assert.Equal(25354325, id));
        Assert.Single(ids.Distinct());
    }

    [Fact]
    public void IdSurvivesServerReinstall()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var directory = new PlayerDirectory();
        directory.Remember("влал", "192.168.1.5", now);
        var before = directory.Resolve(MatchPublicId.ForNick("влал"))!.Id;

        // «Переустановка»: новый справочник, данные только из файла (а файла нет —
        // пустой список). Id всё равно тот же: он не хранится, а считается из ника.
        var reinstalled = new PlayerDirectory();
        reinstalled.Load(Array.Empty<SeenPlayer>());

        Assert.Equal(before, MatchPublicId.ForNick("влал"));
        Assert.Equal(before, reinstalled.Remember("влал", "10.8.0.2", now)!.Id);
        Assert.Equal(25354325, before);
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
        Assert.NotEqual(MatchPublicId.ForNick("Ktulxy"), MatchPublicId.ForNick("Ktulxy2"));
        Assert.NotEqual(MatchPublicId.ForNick("влал"), MatchPublicId.ForNick("влал2"));
    }

    [Fact]
    public void CaseDoesNotSplitThePerson()
    {
        // Бан по нику и так сравнивается без учёта регистра, поэтому id обязан означать
        // того же человека: «Влал» и «влал» — один id.
        Assert.Equal(MatchPublicId.ForNick("влал"), MatchPublicId.ForNick("Влал"));
        Assert.Equal(MatchPublicId.ForNick("Ktulxy"), MatchPublicId.ForNick("KTULXY"));
    }

    [Fact]
    public void CaseFoldSurvivesSurroundingNoise()
    {
        Assert.Equal(
            MatchPublicId.ForNick("Ktulxy"),
            MatchPublicId.ForNick("  kTuLxY\u200B "));
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
