using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Regression tests for the CLI dashboard rewrite. The old renderer emitted <c>"\n"</c> after
/// every row and never clipped to the terminal width, so a chat line or a long nickname made the
/// terminal wrap and scroll — the frame then crept upwards ("летящие строки") and the layout
/// visibly fell apart on cmd, PowerShell and bash alike. These tests pin the invariants that
/// prevent that, and the UTF-8 handling that made Russian text unreadable.
/// </summary>
public sealed class DashboardLayoutTests
{
    public static TheoryData<int, int> Sizes => new()
    {
        { 60, 16 },
        { 80, 24 },
        { 100, 30 },
        { 120, 40 },
        { 200, 60 },
        { 60, 60 },   // narrow but tall
        { 220, 18 },  // wide but short
    };

    private static DashboardModel LobbyModel(int w, int h) => new()
    {
        Width = w,
        Height = h,
        LobbyName = "влал хостит рялна",
        LobbyId = "9F2C41AB77E10D55",
        Mode = "Ranked2v2",
        Map = "Sandstone 2x2",
        Players = 4,
        Peers = 5,
        InputBuffer = "set start",
        Notice = "режим → Ranked2v2",
        NoticeUtc = DateTime.UtcNow,
        Roster = new List<DashboardModel.RosterRow>
        {
            new("Server", "в лобби", true, false),
            new("ДлинныйНикнеймИзТелефонаКоторыйРаньшеЛомалРаскладку", "в матче", false, true),
            new("влал", "в лобби", false, false),
        },
        Chat = new List<DashboardHub.ChatEntry>
        {
            new("Bot",
                "очень длинное сообщение чата, которое раньше переносилось на вторую строку " +
                "и из-за этого весь дашборд уезжал вниз\nа это вообще вторая строка",
                false, DateTime.UtcNow),
        },
        Log = new List<DashboardLogSink.Entry>
        {
            new(1, "[health] peers=4 reliableQueue=0 slowPeers=0 droppedSends=0 rttMax=33", DateTime.UtcNow),
        },
    };

    private static DashboardModel MatchModel(int w, int h)
    {
        var actors = new List<MatchActorSnapshot>
        {
            new() { Nr = 2, Name = "Bot Stone", Team = MatchTeam.Ct, Money = 3200, Kills = 4, Assists = 1, Score = 12, Ping = 41 },
            new() { Nr = 6, Name = "ОченьДлинныйНикИгрока", Team = MatchTeam.Ct, Money = 1500, Kills = 1, Assists = 0, Score = 4, Ping = 58, DeadThisRound = true },
            new() { Nr = 1, Name = "влал", Team = MatchTeam.Tr, Money = 10000, Kills = 5, Assists = 0, Score = 16, Ping = 0, IsHost = true },
            new() { Nr = 7, Name = "Bot Ian", Team = MatchTeam.Tr, Money = 800, Kills = 0, Assists = 1, Score = 2, Ping = -1, DeadThisRound = true },
        };
        return new DashboardModel
        {
            Width = w,
            Height = h,
            LobbyName = "влал хостит рялна",
            Mode = "Ranked2v2",
            Map = "Sandstone 2x2",
            Players = 4,
            MatchStarted = true,
            InputBuffer = "/play",
            Snapshot = new MatchSnapshot
            {
                GameModeId = "Ranked2v2",
                Map = "Sandstone 2x2",
                C2 = MatchC2States.PurchasePhase,
                Phase = MatchFlowPhase.PurchasePhase,
                Round = 2,
                ScoreCt = 1,
                ScoreTr = 1,
                PhaseEndsUtc = DateTime.UtcNow.AddSeconds(109),
                Actors = actors,
            },
            Actors = actors,
            DeathsByActor = new Dictionary<byte, int> { [6] = 1, [7] = 3 },
            Banner = new DashboardHub.RoundResultEntry(
                "раунд 2: победа ОБОРОНЫ · 1:1", MatchTeam.Ct, 2, "Bot Stone", false, DateTime.UtcNow),
            KillFeed = new List<DashboardModel.FeedLine>
            {
                new("Bot Stone", MatchTeam.Ct, true, "влал", MatchTeam.Tr, DateTime.UtcNow),
                new("?", MatchTeam.Tr, false, "Bot Ian", MatchTeam.Ct, DateTime.UtcNow),
            },
            Chat = new List<DashboardHub.ChatEntry>
            {
                new("влал", "го", false, DateTime.UtcNow),
            },
            Log = new List<DashboardLogSink.Entry>
            {
                new(1, "[match-host] join ok", DateTime.UtcNow),
            },
        };
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Frame_IsFixedSize_WithExactRowWidths(int width, int height)
    {
        foreach (var model in new[] { LobbyModel(width, height), MatchModel(width, height) })
        {
            var frame = DashboardView.Render(model);

            Assert.Equal(Math.Max(DashboardFrame.MinWidth, width), frame.Width);
            Assert.Equal(Math.Max(DashboardFrame.MinHeight, height), frame.Height);
            Assert.Equal(frame.Height, frame.Rows.Count);

            for (var i = 0; i < frame.Rows.Count; i++)
                Assert.Equal(frame.Width, Layout.CellWidth(frame.Rows[i]));
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Frame_ContainsNoControlCharacters_SoItCannotScroll(int width, int height)
    {
        foreach (var row in DashboardView.Render(MatchModel(width, height)).ToPlainRows())
        {
            Assert.DoesNotContain('\n', row);
            Assert.DoesNotContain('\r', row);
            Assert.All(row, c => Assert.False(char.IsControl(c), "control char in a rendered row"));
        }
    }

    [Fact]
    public void ChatText_NewlinesAreCollapsed_NotWrapped()
    {
        // A newline from a phone message used to become a real line break in the frame:
        // the row counter desynchronised and the whole dashboard shifted down by one row.
        Assert.Equal("первая вторая", Layout.SingleLine("первая\nвторая"));
        Assert.Equal("a b", Layout.SingleLine("a\tb"));
        Assert.Equal("ab", Layout.SingleLine("a\u0007b"));

        var model = LobbyModel(100, 30);
        var frame = DashboardView.Render(model);
        var rows = frame.ToPlainRows();

        var joined = string.Join("\n", rows);
        Assert.Contains("очень длинное сообщение", joined);
        // No row may be a stray fragment of the second message line.
        Assert.DoesNotContain(rows, r => r.Trim() == "а это вообще вторая строка");
    }

    [Fact]
    public void Cyrillic_RoundTripsThroughTheRenderer()
    {
        Assert.True(DashboardSelfCheck.CyrillicRoundTrip());

        var rows = string.Join("\n", DashboardView.Render(LobbyModel(140, 40)).ToPlainRows());
        Assert.Contains("влал хостит рялна", rows);
        Assert.Contains("Ranked2v2", rows);
        // No replacement characters: the dashboard must never lose a Cyrillic name.
        Assert.DoesNotContain('\uFFFD', rows);
    }

    [Fact]
    public void CellWidth_CountsCyrillicAsOneAndAnsiAsZero()
    {
        Assert.Equal(4, Layout.CellWidth("влал"));
        Assert.Equal(4, Layout.CellWidth($"{Ansi.Ct}влал{Ansi.Reset}"));
        Assert.Equal(6, Layout.CellWidth("привет"));
        Assert.Equal(Layout.CellWidth("Найм"), Layout.CellWidth("Name"));
    }

    [Fact]
    public void Fit_ProducesExactlyTheRequestedCellCount()
    {
        Assert.Equal(10, Layout.CellWidth(Layout.Fit("влал", 10)));
        Assert.Equal(10, Layout.CellWidth(Layout.Fit("влал влал влал влал", 10)));
        Assert.Equal(10, Layout.CellWidth(Layout.Fit("", 10)));
        Assert.Equal(0, Layout.CellWidth(Layout.Fit("влал", 0)));
        // Wide glyphs are measured as two cells so the layout cannot drift.
        Assert.Equal(2, Layout.CodePointWidth('世'));
        Assert.Equal(1, Layout.CodePointWidth('ы'));
    }

    [Fact]
    public void TinyWindow_IsClamped_NotCrashed()
    {
        var frame = DashboardView.Render(MatchModel(10, 5));
        Assert.True(frame.Width >= DashboardFrame.MinWidth);
        Assert.True(frame.Height >= DashboardFrame.MinHeight);
        Assert.All(frame.Rows, r => Assert.Equal(frame.Width, Layout.CellWidth(r)));
    }

    [Fact]
    public void EmptyMatch_RendersWithoutActors()
    {
        var model = MatchModel(90, 24);
        model.Actors = Array.Empty<MatchActorSnapshot>();
        model.Snapshot = new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Map = "Sandstone 2x2",
            Phase = MatchFlowPhase.WaitingPlayers,
            Actors = Array.Empty<MatchActorSnapshot>(),
        };

        var frame = DashboardView.Render(model);
        Assert.Equal(frame.Height, frame.Rows.Count);
        Assert.Contains("ОЖИДАНИЕ", string.Join("\n", frame.ToPlainRows()));
    }

    [Fact]
    public void SelfCheck_ReportsNoLayoutViolations()
    {
        var writer = new StringWriter();
        var exitCode = DashboardSelfCheck.Run(writer);
        Assert.Equal(0, exitCode);
        Assert.Contains("dashboard layout OK", writer.ToString());
    }

    [Fact]
    public void PhaseDescription_UsesHostDeadline_AndNeverInventsAClock()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var snap = new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Phase = MatchFlowPhase.PurchasePhase,
            PhaseEndsUtc = now.AddSeconds(9),
        };

        var (text, clock, _) = DashboardView.DescribePhase(snap, now);
        Assert.Equal("00:09", clock);
        Assert.Contains("БОЙ", text);

        // No host deadline (PhaseEndsUtc = MinValue, Time prop not a deadline for bomb modes):
        // the dashboard must show "--:--" instead of inventing a countdown.
        var noDeadline = new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Phase = MatchFlowPhase.PurchasePhase,
            PhaseEndsUtc = DateTime.MinValue,
        };
        var (_, noClock, _) = DashboardView.DescribePhase(noDeadline, now);
        Assert.Equal("--:--", noClock);
    }

    [Fact]
    public void LogSink_KeepsOnlySanitisedLines()
    {
        DashboardLogSink.Uninstall();
        DashboardLogSink.Install();
        try
        {
            DashboardLogSink.Ingest("\x1b[96m[health] peers=3\x1b[0m\n[lobby]\twarn: skip VPN\n");
            var tail = DashboardLogSink.Tail(10);
            Assert.Equal(2, tail.Count);
            Assert.All(tail, e =>
            {
                Assert.DoesNotContain('\u001b', e.Text);
                Assert.DoesNotContain('\t', e.Text);
            });
            Assert.StartsWith("[health] peers=3", tail[0].Text);
        }
        finally
        {
            DashboardLogSink.Uninstall();
        }
    }
}
