using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Формулировки, которые видит забаненный. Строки — часть поведения: игрок должен
/// понимать причину, кто забанил и сколько осталось.
/// </summary>
public sealed class BanMessagesTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static BanRecord Sample() => new()
    {
        Nick = "Ktulxy",
        Ips = new List<string> { "203.0.113.9" },
        Reason = "софт ебучий",
        BannedBy = "влал",
        CreatedUtcMs = BanRecord.ToUnixMs(Now),
        ExpiresUtcMs = BanRecord.ToUnixMs(Now.AddMinutes(30)),
        DurationSeconds = 1800,
    };

    [Fact]
    public void NoticeHasExactlyTheFourServerLinesInOrder()
    {
        var lines = BanMessages.NoticeLines(Sample(), Now);

        Assert.Equal(4, lines.Length);
        Assert.Equal("Вы были забанены!", lines[0]);
        Assert.Equal("Причина: софт ебучий", lines[1]);
        Assert.Equal("Забанил: влал", lines[2]);
        Assert.Equal("Разбан через: 0д 0ч 30м 00с", lines[3]);
    }

    [Fact]
    public void CountdownUsesExactRemainingTime()
    {
        var record = Sample();
        Assert.Equal(
            "Разбан через: 0д 0ч 17м 43с",
            BanMessages.CountdownLine(record, Now.AddMinutes(12).AddSeconds(17)));
    }

    [Fact]
    public void MissingReasonAndAdminGetHonestPlaceholders()
    {
        var record = Sample();
        record.Reason = "  ";
        record.BannedBy = "";

        var lines = BanMessages.NoticeLines(record, Now);

        Assert.Equal("Причина: не указана", lines[1]);
        Assert.Equal("Забанил: Server", lines[2]);
    }

    [Fact]
    public void PublicAnnouncementShowsWhoWhomTheIdAndWhy()
    {
        // Пользователь: «Server: Игрок влал забанил Ktulxy! / Айди: … / Длительность бана: 6д /
        // Причина: софт удали, тогда поговорим».
        var record = Sample();
        record.Reason = "софт удали, тогда поговорим";
        record.DurationSeconds = 6 * 24 * 60 * 60;
        record.MatchIds = new List<int> { 67776161 };

        var lines = BanMessages.PublicLines(record);

        Assert.Equal(4, lines.Length);
        Assert.Equal("Игрок влал забанил Ktulxy!", lines[0]);
        Assert.Equal("Айди: 67776161", lines[1]);
        Assert.Equal("Длительность бана: 6д", lines[2]);
        Assert.Equal("Причина: софт удали, тогда поговорим", lines[3]);
    }

    [Fact]
    public void AnnouncementIdFallsBackToTheDeterministicOneForOldRecords()
    {
        var record = Sample();
        record.MatchIds = new List<int>();

        Assert.Equal(MatchPublicId.ForNick(record.Nick), BanMessages.IdOf(record));
        Assert.Contains($"Айди: {MatchPublicId.ForNick(record.Nick)}", BanMessages.PublicLines(record));
    }

    [Fact]
    public void PublicLineNamesTheDurationAndReason()
    {
        Assert.Equal(
            "Ktulxy забанен на 30м (софт ебучий)",
            BanMessages.PublicLine(Sample()));

        var noReason = Sample();
        noReason.Reason = "";
        Assert.Equal("Ktulxy забанен на 30м", BanMessages.PublicLine(noReason));
    }
}
