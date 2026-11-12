using System.Text;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Renders dashboard frames without a terminal, at several window sizes, and reports layout
/// violations. Backs <c>--dashboard-check</c>, so the layout can be verified in CI and over SSH
/// without a real console.
/// </summary>
/// <remarks>
/// The check enforces the invariants that used to break the live dashboard:
/// every row is exactly the frame width in cells, no row carries an ANSI escape in the plain
/// dump, no row is empty at the top and — most importantly — nothing depends on how many rows
/// the content wanted.
/// </remarks>
public static class DashboardSelfCheck
{
    /// <summary>Sizes from "small ssh window" to "wide desktop terminal".</summary>
    private static readonly (int Width, int Height)[] Sizes =
    {
        (60, 16), (80, 24), (100, 30), (120, 40), (160, 50), (240, 100),
    };

    /// <summary>Run every check; returns the process exit code (0 = all good).</summary>
    public static int Run(TextWriter output)
    {
        var failures = 0;

        foreach (var (width, height) in Sizes)
        {
            foreach (var variant in new[] { "lobby", "match", "match-empty" })
            {
                var model = BuildModel(variant, width, height);
                var frame = DashboardView.Render(model);
                var errors = Validate(frame, model);
                var header = $"[{variant,-11}] {width,3}x{height,-3} rows={frame.Height,-3} ";

                if (errors.Count == 0)
                {
                    output.WriteLine($"{header}OK");
                    continue;
                }

                failures += errors.Count;
                output.WriteLine($"{header}FAIL");
                foreach (var e in errors)
                    output.WriteLine($"    ! {e}");
            }
        }

        // Wrong window sizes must be clamped, not crash or overflow.
        var tiny = DashboardView.Render(BuildModel("match", 10, 5));
        if (tiny.Width < DashboardFrame.MinWidth || tiny.Height < DashboardFrame.MinHeight)
        {
            failures++;
            output.WriteLine($"    ! tiny window not clamped: {tiny.Width}x{tiny.Height}");
        }

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "dashboard layout OK — frames are fixed-size, rows are width-exact, no escapes in text"
            : $"dashboard layout FAILED — {failures} problem(s)");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Print the frames themselves (useful to eyeball the layout in a plain terminal).</summary>
    public static void Dump(TextWriter output, int width = 100, int height = 30)
    {
        foreach (var variant in new[] { "lobby", "match", "match-empty" })
        {
            output.WriteLine($"──── {variant} ({width}x{height}) ────────────────────────");
            foreach (var row in DashboardView.Render(BuildModel(variant, width, height)).ToPlainRows())
                output.WriteLine(row.TrimEnd());
            output.WriteLine();
        }
    }

    private static List<string> Validate(DashboardFrame frame, DashboardModel model)
    {
        var errors = new List<string>();
        if (frame.Width != Math.Max(DashboardFrame.MinWidth, model.Width))
            errors.Add($"width {frame.Width} != requested {model.Width}");
        if (frame.Height != Math.Max(DashboardFrame.MinHeight, model.Height))
            errors.Add($"height {frame.Height} != requested {model.Height}");

        var rows = frame.Rows;
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = Layout.CellWidth(rows[i]);
            if (cells != frame.Width)
                errors.Add($"row {i} has {cells} cells, expected {frame.Width}");

            var plain = Ansi.StripAnsi(rows[i]);
            if (plain.IndexOf('\n') >= 0 || plain.IndexOf('\r') >= 0)
                errors.Add($"row {i} contains a newline (would scroll the frame)");
            if (plain.Length > 0 && plain.Any(char.IsControl))
                errors.Add($"row {i} contains control characters");
        }

        // No frame may exceed the terminal or be entirely empty at the top.
        if (rows.Count > frame.Height)
            errors.Add("frame has more rows than the terminal height");
        if (rows.Count > 0 && Ansi.StripAnsi(rows[0]).Trim().Length == 0)
            errors.Add("first row is blank (header missing)");

        return errors;
    }

    private static DashboardModel BuildModel(string variant, int width, int height)
    {
        var now = DateTime.UtcNow;
        var model = new DashboardModel
        {
            Width = width,
            Height = height,
            NowUtc = now,
            LobbyName = "влал хостит рялна — LAN",
            LobbyId = "9F2C41AB77E10D55",
            Mode = "Ranked2v2",
            Map = "Sandstone 2x2",
            Players = 5,
            Peers = 6,
            MatchStarted = variant != "lobby",
            InputRedirected = false,
            InputBuffer = "/set start",
            Notice = "сервер: режим → Ranked2v2, карта → Sandstone 2x2",
            NoticeUtc = now,
            Roster = new List<DashboardModel.RosterRow>
            {
                new("Server", "в лобби", true, false),
                new("влал", "в матче", false, true),
                new("фейк влал", "в матче", false, true),
                new("ДлинныйНикнеймИзТелефона", "в лобби", false, false),
                new("Bot Ivan", "в лобби", false, false),
            },
            Chat = new List<DashboardHub.ChatEntry>
            {
                new("Server", "влал подключился!", true, now.AddMinutes(-3)),
                new("влал", "го катка, Prison?", false, now.AddMinutes(-2)),
                new("фейк влал", "давай Sandstone 2x2", false, now.AddMinutes(-1)),
                new("Bot Ivan",
                    "очень длинное сообщение которое раньше ломало раскладку переносом строки и уезжало вниз",
                    false, now.AddSeconds(-30)),
                new("Server", "матч начнётся, когда обе команды будут готовы", true, now.AddSeconds(-10)),
            },
            Log = new List<DashboardLogSink.Entry>
            {
                new(1, "[lan] KEEP eth0: 192.168.1.5 br=192.168.1.255", now.AddSeconds(-30)),
                new(2, "[discovery] WAITING — cxbl=1 Extra1='Sandstone 2x2' Extra2='Ranked2v2', members=5", now.AddSeconds(-20)),
                new(3, "[match-host] LiteNetLib listen UDP 7777 key='MyVerySecretKey'", now.AddSeconds(-15)),
                new(4, "[mode:Ranked2v2] warmup → prep round 1 C2=22", now.AddSeconds(-8)),
                new(5, "[health] peers=6 reliableQueue=0 slowPeers=0 droppedSends=0 rttMax=41 captures=off", now.AddSeconds(-2)),
            },
        };

        if (variant == "lobby")
            return model;

        var actors = new List<MatchActorSnapshot>
        {
            new() { Nr = 2, Name = "Bot Stone", Team = MatchTeam.Ct, Money = 3200, Kills = 4, Assists = 1, Score = 12, Ping = 41 },
            new() { Nr = 4, Name = "фейк влал", Team = MatchTeam.Ct, Money = 5400, Kills = 7, Assists = 2, Score = 21, Ping = 33 },
            new() { Nr = 6, Name = "Bot Dennis", Team = MatchTeam.Ct, Money = 1500, Kills = 1, Assists = 0, Score = 4, Ping = 58, DeadThisRound = true },
            new() { Nr = 1, Name = "влал", Team = MatchTeam.Tr, Money = 10000, Kills = 5, Assists = 0, Score = 16, Ping = 0, IsHost = true },
            new() { Nr = 3, Name = "ДлинныйНикнеймИзТелефона", Team = MatchTeam.Tr, Money = 2600, Kills = 3, Assists = 2, Score = 9, Ping = 47 },
            new() { Nr = 7, Name = "Bot Ian", Team = MatchTeam.Tr, Money = 800, Kills = 0, Assists = 1, Score = 2, Ping = -1, DeadThisRound = true },
        };

        if (variant == "match-empty")
        {
            actors.Clear();
            model.Chat = Array.Empty<DashboardHub.ChatEntry>();
            model.Log = Array.Empty<DashboardLogSink.Entry>();
        }

        model.Actors = actors;
        model.Snapshot = new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Map = "Sandstone 2x2",
            C2 = MatchC2States.PurchasePhase,
            Phase = MatchFlowPhase.PurchasePhase,
            Round = 2,
            ScoreCt = 1,
            ScoreTr = 1,
            PhaseEndsUtc = now.AddSeconds(109),
            Actors = actors,
        };
        model.DeathsByActor = new Dictionary<byte, int> { [6] = 1, [3] = 2 };
        model.Banner = new DashboardHub.RoundResultEntry(
            "раунд 2: победа ОБОРОНЫ · 1:1", MatchTeam.Ct, 3, "фейк влал", false, now);
        model.KillFeed = new List<DashboardModel.FeedLine>
        {
            new("фейк влал", MatchTeam.Ct, true, "влал", MatchTeam.Tr, now.AddSeconds(-12)),
            new("влал", MatchTeam.Tr, true, "фейк влал", MatchTeam.Ct, now.AddSeconds(-8)),
            new("?", MatchTeam.Tr, false, "Bot Ivan", MatchTeam.Ct, now.AddSeconds(-3)),
        };
        return model;
    }

    /// <summary>UTF-8 smoke test: Russian text survives the renderer byte-for-byte.</summary>
    public static bool CyrillicRoundTrip()
    {
        const string sample = "влалChillow — лобби, матч, игрок";
        var clip = Layout.ClipPlain(sample, 100);
        var fitted = Layout.Fit(clip, 100);
        return fitted.Contains("влалChillow", StringComparison.Ordinal)
               && Layout.CellWidth(fitted) == 100
               && Encoding.UTF8.GetByteCount(fitted) > fitted.Length;
    }
}
