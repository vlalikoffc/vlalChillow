using System.Collections.Concurrent;
using System.Text;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Full-screen terminal dashboard for the dedicated LAN host. Display-only: it reads state the
/// host already tracks (lobby roster, mode/map, match actors/props/scores) and events pushed via
/// <see cref="DashboardHub"/> (chat, deaths, round results). It owns the terminal; all normal
/// <see cref="Console.WriteLine"/> logging is routed to <c>latest.log</c> by <see cref="RunLog"/>.
///
/// Views: a lobby view (players + mode/map + chat) before the match starts, and a Standoff-2 style
/// TAB scoreboard once the match is in progress, with chat + kill feed below.
/// </summary>
public sealed class CliDashboard : IDisposable
{
    private readonly GameNetHost _game;
    private readonly TextWriter _term;
    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<string> _commands = new(new ConcurrentQueue<string>());

    private readonly object _stateGate = new();
    private readonly List<DashboardHub.ChatEntry> _chat = new();
    private readonly List<KillFeedLine> _killFeed = new();
    private readonly ConcurrentQueue<DashboardHub.DeathEntry> _pendingDeaths = new();

    // Best-effort kill attribution: credit an enemy whose kill counter bumped near a victim death.
    private readonly Dictionary<byte, int> _killBaseline = new();
    private readonly List<KillerCredit> _recentKillers = new();
    private readonly Dictionary<byte, int> _cumulativeDeaths = new();

    private DashboardHub.RoundResultEntry? _banner;
    private readonly StringBuilder _input = new();
    private readonly object _inputGate = new();

    private Task? _renderTask;
    private Thread? _inputThread;
    private volatile bool _running;
    private bool _started;
    private int _lastFrameLines;
    private bool _wasInMatch;

    // Visual dev aid only: STANDCHILLOW_DASH_DEMO=lobby|match seeds fake state to preview layout.
    private readonly string? _demo = Environment.GetEnvironmentVariable("STANDCHILLOW_DASH_DEMO");

    private const int ChatCap = 200;
    private const int KillFeedCap = 40;

    private sealed record KillFeedLine(
        string KillerName, MatchTeam KillerTeam, bool KillerKnown,
        string VictimName, MatchTeam VictimTeam, DateTime WhenUtc);

    private sealed record KillerCredit(byte Nr, MatchTeam Team, string Name, DateTime WhenUtc);

    public CliDashboard(GameNetHost game)
    {
        _game = game;
        _term = RunLog.TerminalOut;
    }

    /// <summary>Enter the alternate screen and start the render + input loops.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        _running = true;

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* best effort */ }

        DashboardHub.Chat += OnChat;
        DashboardHub.Death += OnDeath;
        DashboardHub.RoundResult += OnRoundResult;

        Console.CancelKeyPress += OnCancelKey;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        _term.Write(Ansi.EnterAltScreen);
        _term.Write(Ansi.ClearScreen);
        _term.Write(Ansi.HideCursor);
        _term.Flush();

        _renderTask = Task.Run(RenderLoop);

        if (!Console.IsInputRedirected)
        {
            _inputThread = new Thread(InputLoop) { IsBackground = true, Name = "dashboard-input" };
            _inputThread.Start();
        }
    }

    /// <summary>Block until the next submitted command line, or <c>null</c> when stopping.</summary>
    public string? NextCommand()
    {
        try { return _commands.Take(_cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Current input buffer contents (for the caller to inspect if needed).</summary>
    public void PostLocalNotice(string text) =>
        DashboardHub.PostChat("dash", text, fromServer: true);

    public void Stop()
    {
        if (!_running && !_started) return;
        _running = false;
        try { _cts.Cancel(); } catch { /* ignore */ }

        DashboardHub.Chat -= OnChat;
        DashboardHub.Death -= OnDeath;
        DashboardHub.RoundResult -= OnRoundResult;

        try { _renderTask?.Wait(500); } catch { /* ignore */ }

        _term.Write(Ansi.ShowCursor);
        _term.Write(Ansi.LeaveAltScreen);
        _term.Flush();
        _started = false;
    }

    public void Dispose() => Stop();

    // ── event sinks ──────────────────────────────────────────────────────────
    private void OnChat(DashboardHub.ChatEntry e)
    {
        lock (_stateGate)
        {
            _chat.Add(e);
            if (_chat.Count > ChatCap) _chat.RemoveRange(0, _chat.Count - ChatCap);
        }
    }

    private void OnDeath(DashboardHub.DeathEntry e) => _pendingDeaths.Enqueue(e);

    private void OnRoundResult(DashboardHub.RoundResultEntry e)
    {
        lock (_stateGate) _banner = e;
    }

    // ── input ────────────────────────────────────────────────────────────────
    private void InputLoop()
    {
        while (_running)
        {
            ConsoleKeyInfo key;
            try { key = Console.ReadKey(intercept: true); }
            catch { break; }

            lock (_inputGate)
            {
                if (key.Key == ConsoleKey.Enter)
                {
                    var line = _input.ToString().Trim();
                    _input.Clear();
                    if (line.Length > 0)
                        _commands.Add(line);
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (_input.Length > 0) _input.Remove(_input.Length - 1, 1);
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    _input.Append(key.KeyChar);
                }
            }
        }
    }

    private void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
    {
        // Ctrl+C: restore terminal, then let default terminate.
        Stop();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Stop();

    // ── render loop ────────────────────────────────────────────────────────────
    private async Task RenderLoop()
    {
        while (_running)
        {
            try { RenderFrame(); }
            catch { /* never let a render glitch kill the host */ }
            try { await Task.Delay(150, _cts.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void RenderFrame()
    {
        var (width, height) = GetSize();
        var snap = _game.MatchStarted ? _game.Match.SnapshotActiveMatch() : null;
        var inMatch = _game.MatchStarted;

        if (_demo is "match")
        {
            snap = DemoSnapshot();
            inMatch = true;
        }

        if (inMatch && !_wasInMatch)
        {
            // Fresh match view — reset kill-feed / cumulative death derivation.
            lock (_stateGate) { _killFeed.Clear(); }
            _killBaseline.Clear();
            _recentKillers.Clear();
            _cumulativeDeaths.Clear();
        }
        _wasInMatch = inMatch;

        // Demo seeding runs after the match-enter reset so the sample feed/banner survive.
        if (_demo is "match" or "lobby")
            SeedDemoOnce();

        ProcessKillAttribution(snap);

        var lines = inMatch
            ? BuildMatchView(width, height, snap)
            : BuildLobbyView(width, height);

        WriteFrame(lines, width, height);
    }

    private (int Width, int Height) GetSize()
    {
        var w = 120;
        var h = 40;
        try { w = Math.Clamp(Console.WindowWidth, 90, 220); } catch { /* default */ }
        try { h = Math.Clamp(Console.WindowHeight, 24, 100); } catch { /* default */ }
        return (w, h);
    }

    private void WriteFrame(List<string> lines, int width, int height)
    {
        var sb = new StringBuilder();
        sb.Append(Ansi.CursorHome);
        var max = Math.Max(lines.Count, _lastFrameLines);
        for (var i = 0; i < max && i < height; i++)
        {
            var content = i < lines.Count ? lines[i] : "";
            sb.Append(content);
            sb.Append(Ansi.ClearToLineEnd);
            if (i < max - 1 && i < height - 1)
                sb.Append('\n');
        }
        _lastFrameLines = Math.Min(lines.Count, height);
        _term.Write(sb.ToString());
        _term.Flush();
    }

    // ── kill attribution ─────────────────────────────────────────────────────
    private void ProcessKillAttribution(MatchSnapshot? snap)
    {
        var now = DateTime.UtcNow;
        _recentKillers.RemoveAll(k => (now - k.WhenUtc).TotalSeconds > 2.5);

        if (snap is not null)
        {
            foreach (var a in snap.Actors)
            {
                if (a.IsHost) continue;
                if (_killBaseline.TryGetValue(a.Nr, out var prev))
                {
                    if (a.Kills > prev)
                        for (var i = 0; i < a.Kills - prev; i++)
                            _recentKillers.Add(new KillerCredit(a.Nr, a.Team, a.Name, now));
                }
                _killBaseline[a.Nr] = a.Kills;
            }
        }

        while (_pendingDeaths.TryDequeue(out var death))
        {
            // Attribute to the most recent enemy-team killer credit; consume it.
            var idx = _recentKillers.FindLastIndex(k =>
                IsFighting(k.Team) && k.Team != death.VictimTeam && k.Nr != death.VictimNr);
            KillFeedLine line;
            if (idx >= 0)
            {
                var killer = _recentKillers[idx];
                _recentKillers.RemoveAt(idx);
                line = new KillFeedLine(
                    killer.Name, killer.Team, KillerKnown: true,
                    death.VictimName, death.VictimTeam, now);
            }
            else
            {
                var enemyTeam = death.VictimTeam == MatchTeam.Tr ? MatchTeam.Ct
                    : death.VictimTeam == MatchTeam.Ct ? MatchTeam.Tr : MatchTeam.None;
                line = new KillFeedLine("?", enemyTeam, KillerKnown: false,
                    death.VictimName, death.VictimTeam, now);
            }

            lock (_stateGate)
            {
                _killFeed.Add(line);
                if (_killFeed.Count > KillFeedCap) _killFeed.RemoveRange(0, _killFeed.Count - KillFeedCap);
            }
            _cumulativeDeaths[death.VictimNr] = _cumulativeDeaths.GetValueOrDefault(death.VictimNr) + 1;
        }
    }

    private static bool IsFighting(MatchTeam t) => t is MatchTeam.Tr or MatchTeam.Ct;

    // ── demo seeding (visual dev aid only) ───────────────────────────────────────
    private bool _demoSeeded;
    private void SeedDemoOnce()
    {
        if (_demoSeeded) return;
        _demoSeeded = true;
        lock (_stateGate)
        {
            _chat.Add(new DashboardHub.ChatEntry("Server", "влал Подключился!", true, DateTime.UtcNow));
            _chat.Add(new DashboardHub.ChatEntry("влал", "го катка", false, DateTime.UtcNow));
            _chat.Add(new DashboardHub.ChatEntry("фейк влал", "изи", false, DateTime.UtcNow));
            _chat.Add(new DashboardHub.ChatEntry("Bot Zach", "gg", false, DateTime.UtcNow));
            _killFeed.Add(new KillFeedLine("фейк влал", MatchTeam.Ct, true, "влал", MatchTeam.Tr, DateTime.UtcNow));
            _killFeed.Add(new KillFeedLine("влал", MatchTeam.Tr, true, "фейк влал", MatchTeam.Ct, DateTime.UtcNow));
            _killFeed.Add(new KillFeedLine("Bot Stone", MatchTeam.Ct, true, "Bot Ivan", MatchTeam.Tr, DateTime.UtcNow));
            _killFeed.Add(new KillFeedLine("?", MatchTeam.Tr, false, "Bot Dennis", MatchTeam.Ct, DateTime.UtcNow));
            _banner = new DashboardHub.RoundResultEntry(
                "Round 2: DEFENSE (CT) win  ·  1:1  (wipe-tr)",
                MatchTeam.Ct, 3, "фейк влал", false, DateTime.UtcNow);
        }
        _cumulativeDeaths[7] = 1;
        _cumulativeDeaths[3] = 2;
    }

    private MatchSnapshot DemoSnapshot()
    {
        MatchActorSnapshot A(byte nr, string name, MatchTeam t, int money, int k, int a, int sc, int ping, bool host = false, bool dead = false) =>
            new() { Nr = nr, Name = name, Team = t, Money = money, Kills = k, Assists = a, Score = sc, Ping = ping, IsHost = host, DeadThisRound = dead };
        return new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Map = "Sandstone 2x2",
            Phase = MatchFlowPhase.RoundLive,
            Round = 2,
            ScoreCt = 1,
            ScoreTr = 1,
            TimeDeadline = Environment.TickCount / 1000.0 + 94,
            Actors = new List<MatchActorSnapshot>
            {
                A(2, "Bot Stone", MatchTeam.Ct, 3200, 4, 1, 12, 41),
                A(4, "фейк влал", MatchTeam.Ct, 5400, 7, 2, 21, 33),
                A(6, "Bot Dennis", MatchTeam.Ct, 1500, 1, 0, 4, 58, dead: true),
                A(1, "влал", MatchTeam.Tr, 10000, 5, 0, 16, 0, host: true),
                A(3, "Bot Ivan", MatchTeam.Tr, 2600, 3, 2, 9, 47),
                A(7, "Bot Ian", MatchTeam.Tr, 800, 0, 1, 2, 120, dead: true),
            },
        };
    }

    // ── lobby view ─────────────────────────────────────────────────────────────
    private List<string> BuildLobbyView(int width, int height)
    {
        var lines = new List<string>();
        var mode = _game.Session.GameModeId;
        var map = _game.Session.SelectedLevels.Count > 0 ? _game.Session.SelectedLevels[0] : "-";
        var roster = _game.Session.SnapshotRoster();
        var real = _game.JoinedCount;
        var cardW = width;

        lines.Add("");
        lines.Add(Ansi.CenterVisible(
            $"{Ansi.Bold}{Ansi.Text}влал{Ansi.Ct}Chillow{Ansi.Reset}  " +
            $"{Ansi.Faint}·{Ansi.Reset}  {Ansi.Muted}LAN DEDICATED HOST{Ansi.Reset}", width));
        lines.Add(Ansi.CenterVisible(
            $"{Ansi.Faint}“{Ansi.Reset}{Ansi.Text}{_game.Session.LobbyName}{Ansi.Reset}{Ansi.Faint}”{Ansi.Reset}", width));
        lines.Add(Ansi.CenterVisible(
            $"{Badge("MODE", mode, Ansi.Ct)}   {Badge("MAP", map, Ansi.Tr)}   " +
            $"{Badge("PLAYERS", real.ToString(), Ansi.Accent)}   " +
            $"{Badge("MATCH", _game.MatchStarted ? "IN PROGRESS" : "waiting…", Ansi.Muted)}", width));
        lines.Add("");

        // Players card.
        lines.Add(CardTop(cardW, Ansi.BgPanel));
        lines.Add(CardBand($"{Ansi.Bold}{Ansi.Text}PLAYERS{Ansi.Reset}",
            $"{Ansi.Muted}{roster.Count} online{Ansi.Reset}", cardW, Ansi.BgBand));
        lines.Add(CardRow(
            $" {Ansi.Muted}{Ansi.PadPlain("#", 3, false)}     {Ansi.PadPlain("Name", 28, false)}" +
            $"{Ansi.PadVisibleLeft("Status", 14)}{Ansi.Reset} ", cardW, Ansi.BgPanel));
        var idx = 0;
        foreach (var (name, status) in roster)
        {
            var isServer = idx == 0;
            var chip = isServer ? $"{Ansi.Host}◆◆{Ansi.Reset}" : AvatarChip(idx);
            var color = isServer ? Ansi.Host : Ansi.Text;
            var tag = isServer ? $" {Ansi.Faint}host{Ansi.Reset}" : "";
            var st = status == Net.Lobby.PlayerLobbyStatus.InMatch
                ? $"{Ansi.Accent}В матче{Ansi.Reset}"
                : $"{Ansi.Muted}Лобби{Ansi.Reset}";
            var nameCell = $"{color}{Ansi.PadPlain(name, 26, false)}{Ansi.Reset}{tag}";
            var interior =
                $" {Ansi.Faint}{Ansi.PadPlain(idx.ToString(), 2, true)}{Ansi.Reset} {chip} " +
                $"{Ansi.PadVisible(nameCell, 27)}{Ansi.PadVisibleLeft(st, 12)} ";
            lines.Add(CardRow(interior, cardW, idx % 2 == 0 ? Ansi.BgPanel : Ansi.BgZebra));
            idx++;
        }
        lines.Add(CardBottom(cardW, Ansi.BgPanel));

        // Chat card fills the rest.
        var used = lines.Count + 2; // chat top + input
        var chatRows = Math.Clamp(height - used - 1, 3, 14);
        lines.Add(CardTop(cardW, Ansi.BgPanel));
        lines.Add(CardBand($"{Ansi.Bold}{Ansi.Text}CHAT{Ansi.Reset}", "", cardW, Ansi.BgBand));
        var chat = RecentChat(chatRows).ToList();
        for (var r = 0; r < chatRows; r++)
        {
            var content = r < chat.Count ? " " + FormatChatLine(chat[r], null) : "";
            lines.Add(CardRow(content, cardW, Ansi.BgPanel));
        }
        lines.Add(CardBottom(cardW, Ansi.BgPanel));
        lines.Add(InputLine(width));
        return lines;
    }

    // ── match view ─────────────────────────────────────────────────────────────
    private List<string> BuildMatchView(int width, int height, MatchSnapshot? snap)
    {
        var lines = new List<string>();
        var mode = snap?.GameModeId ?? _game.Session.GameModeId;
        var map = snap?.Map ?? (_game.Session.SelectedLevels.Count > 0 ? _game.Session.SelectedLevels[0] : "-");
        var phase = snap is null ? "STARTING…" : DescribePhase(snap);
        var cardW = width;

        var ct = (snap?.Actors ?? new List<MatchActorSnapshot>())
            .Where(a => a.Team == MatchTeam.Ct).OrderBy(a => a.Nr).ToList();
        var tr = (snap?.Actors ?? new List<MatchActorSnapshot>())
            .Where(a => a.Team == MatchTeam.Tr).OrderBy(a => a.Nr).ToList();
        var scoreCt = snap?.ScoreCt ?? 0;
        var scoreTr = snap?.ScoreTr ?? 0;

        // Header: mode · map, then the phase/timer, then big score tiles (all centered).
        lines.Add("");
        lines.Add(Ansi.CenterVisible(
            $"{Ansi.Bold}{Ansi.Ct}{mode}{Ansi.Reset}  {Ansi.Faint}◆{Ansi.Reset}  " +
            $"{Ansi.Bold}{Ansi.Text}{map}{Ansi.Reset}", width));
        lines.Add(Ansi.CenterVisible($"{Ansi.Muted}{phase}{Ansi.Reset}", width));
        foreach (var l in ScoreTiles(scoreCt, scoreTr, width))
            lines.Add(l);

        // Scoreboard card.
        var colW = Math.Max(30, (width - 7) / 2);
        lines.Add(CardTop(cardW, Ansi.BgPanel));
        lines.Add(CardBand(
            $"{Ansi.Bold}{Ansi.Ct}DEFENSE (CT){Ansi.Reset}",
            $"{Ansi.Bold}{Ansi.Tr}ATTACK (T){Ansi.Reset}", cardW, Ansi.BgBand));
        lines.Add(CardRow(
            TwoColInterior(HeaderCol(colW), HeaderCol(colW), colW), cardW, Ansi.BgPanel));

        var rows = Math.Max(1, Math.Max(ct.Count, tr.Count));
        for (var r = 0; r < rows; r++)
        {
            var left = r < ct.Count ? PlayerCol(r + 1, ct[r], MatchTeam.Ct, colW) : "";
            var right = r < tr.Count ? PlayerCol(r + 1, tr[r], MatchTeam.Tr, colW) : "";
            lines.Add(CardRow(
                TwoColInterior(left, right, colW), cardW,
                r % 2 == 0 ? Ansi.BgPanel : Ansi.BgZebra));
        }
        lines.Add(CardBottom(cardW, Ansi.BgPanel));

        // Round / MVP banner.
        var banner = CurrentBanner();
        if (banner is not null)
            lines.Add(BannerBar(banner, width));

        // Chat (left) + Kill Feed (right) card.
        var used = lines.Count + 3; // chat top + band + input
        var bodyRows = Math.Clamp(height - used - 1, 3, 12);
        var chat = RecentChat(bodyRows).ToList();
        var feed = RecentKillFeed(bodyRows).ToList();
        var nameTeam = BuildNameTeamMap(snap);

        lines.Add(CardTop(cardW, Ansi.BgPanel));
        lines.Add(CardBand(
            $"{Ansi.Bold}{Ansi.Text}CHAT{Ansi.Reset}",
            $"{Ansi.Bold}{Ansi.Text}KILL FEED{Ansi.Reset}", cardW, Ansi.BgBand));
        for (var r = 0; r < bodyRows; r++)
        {
            var l = r < chat.Count ? FormatChatLine(chat[r], nameTeam) : "";
            var rr = r < feed.Count ? FormatKillFeed(feed[r]) : "";
            lines.Add(CardRow(TwoColInterior(l, rr, colW), cardW, Ansi.BgPanel));
        }
        lines.Add(CardBottom(cardW, Ansi.BgPanel));
        lines.Add(InputLine(width));
        return lines;
    }

    private static bool IsAlliesLike(MatchSnapshot snap) =>
        !snap.IsDeathMatch
        && string.Equals(snap.GameModeId, "Ranked2v2", StringComparison.Ordinal);

    private string DescribePhase(MatchSnapshot snap)
    {
        var allies = IsAlliesLike(snap);
        // Prefer host Flow.PhaseEndsUtc; fall back to wire Time deadline for round/fuse clocks.
        double remain = 0;
        if (snap.PhaseEndsUtc > DateTime.MinValue && snap.PhaseEndsUtc < DateTime.MaxValue)
            remain = (snap.PhaseEndsUtc - DateTime.UtcNow).TotalSeconds;
        else if (snap.TimeDeadline > 0
                 && snap.Phase is MatchFlowPhase.RoundLive or MatchFlowPhase.BombPlanted
                     or MatchFlowPhase.PurchasePhase
                     or MatchFlowPhase.DeathMatchLive or MatchFlowPhase.DeathMatchWarmup)
            remain = snap.TimeDeadline - Environment.TickCount / 1000.0;

        var clock = remain > 0.5 ? $" {FormatClock(remain)}" : "";
        var label = snap.Phase switch
        {
            MatchFlowPhase.WaitingPlayers => "WAITING PLAYERS",
            MatchFlowPhase.AlliesPreWarmup => allies ? "FREE-FOR-ALL · C2=11" : snap.Phase.ToString(),
            MatchFlowPhase.Warmup => allies ? $"WARM-UP · Round 1 · C2=21" : "WARM-UP",
            MatchFlowPhase.WarmupWillFinish => allies
                ? $"PREP · Round {snap.Round} · C2=22"
                : "MATCH STARTING",
            MatchFlowPhase.PurchasePhase => allies
                ? $"PREP · Round {snap.Round} · C2=22"
                : $"PREP · Round {snap.Round} · C2=31",
            MatchFlowPhase.RoundLive => allies
                ? $"LIVE · Round {snap.Round} · C2=101"
                : $"LIVE · Round {snap.Round}",
            MatchFlowPhase.BombPlanted => $"BOMB PLANTED · Round {snap.Round}",
            MatchFlowPhase.RoundEndPause => $"ROUND END · Round {snap.Round}",
            MatchFlowPhase.HalfTimeIntro => "HALF-TIME · INTRO · C2=111",
            MatchFlowPhase.HalfTimeSwap => "HALF-TIME · SWAP · C2=112",
            MatchFlowPhase.HalfTimeTransition => "HALF-TIME · RESUME · C2=113",
            MatchFlowPhase.MatchOver => "MATCH OVER",
            MatchFlowPhase.DeathMatchWarmup => "WARM-UP",
            MatchFlowPhase.DeathMatchLive => "LIVE",
            MatchFlowPhase.DeathMatchEnded => "MATCH OVER",
            MatchFlowPhase.DeathMatchFinalHud => "FINAL",
            _ => snap.Phase.ToString(),
        };
        return label + clock;
    }

    private static string FormatClock(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var m = (int)(seconds / 60);
        var s = (int)(seconds % 60);
        return $"{m:00}:{s:00}";
    }

    // Column widths inside a scoreboard side: rank(2) av(2) name(nameW) money(7) K A D(2) Sc(4) Ping(4).
    private const int StatsFixedWidth = 33;

    private string HeaderCol(int colW)
    {
        var nameW = Math.Max(6, colW - StatsFixedWidth);
        var sb = new StringBuilder();
        sb.Append(Ansi.Muted);
        sb.Append(Ansi.PadPlain("#", 2, true)).Append("    ");        // rank + avatar gap
        sb.Append(Ansi.PadPlain("Name", nameW, false)).Append(' ');
        sb.Append(Ansi.PadPlain("Money", 7, true)).Append(' ');
        sb.Append(Ansi.PadPlain("K", 2, true)).Append(' ');
        sb.Append(Ansi.PadPlain("A", 2, true)).Append(' ');
        sb.Append(Ansi.PadPlain("D", 2, true)).Append(' ');
        sb.Append(Ansi.PadPlain("Sc", 4, true)).Append(' ');
        sb.Append(Ansi.PadPlain("Ping", 4, true));
        sb.Append(Ansi.Reset);
        return sb.ToString();
    }

    private string PlayerCol(int index, MatchActorSnapshot a, MatchTeam side, int colW)
    {
        var nameW = Math.Max(6, colW - StatsFixedWidth);
        var dead = a.DeadThisRound;
        var nameColor = dead ? Ansi.Faint : side == MatchTeam.Ct ? Ansi.Ct : Ansi.Tr;
        var deaths = _cumulativeDeaths.GetValueOrDefault(a.Nr);
        var chip = a.IsHost ? $"{Ansi.Host}◆◆{Ansi.Reset}" : AvatarChip(a.Nr);
        var pingCell = a.IsHost
            ? $"{Ansi.Host}HOST{Ansi.Reset}"
            : a.Ping >= 0 ? $"{Ansi.Muted}{a.Ping}{Ansi.Reset}" : $"{Ansi.Faint}—{Ansi.Reset}";
        var moneyColor = a.Money > 0 ? Ansi.Money : Ansi.Faint;

        var sb = new StringBuilder();
        sb.Append(Ansi.Faint).Append(Ansi.PadPlain(index.ToString(), 2, true)).Append(Ansi.Reset);
        sb.Append(' ').Append(chip).Append(' ');
        sb.Append(nameColor).Append(dead ? "" : Ansi.Bold)
          .Append(Ansi.PadPlain(a.Name, nameW, false)).Append(Ansi.Reset).Append(' ');
        sb.Append(moneyColor).Append(Ansi.PadPlain("$" + a.Money, 7, true)).Append(Ansi.Reset).Append(' ');
        sb.Append(Stat(a.Kills, 2)).Append(' ');
        sb.Append(Stat(a.Assists, 2)).Append(' ');
        sb.Append(Stat(deaths, 2)).Append(' ');
        sb.Append(Ansi.Text).Append(Ansi.PadPlain(a.Score.ToString(), 4, true)).Append(Ansi.Reset).Append(' ');
        sb.Append(Ansi.PadVisibleLeft(pingCell, 4));
        return sb.ToString();
    }

    private static string Stat(int v, int w) =>
        (v > 0 ? Ansi.Text : Ansi.Faint) + Ansi.PadPlain(v.ToString(), w, true) + Ansi.Reset;

    // Distinct avatar chips so rows read like the game's per-player avatars.
    private static readonly string[] AvatarPalette =
    {
        Ansi.Fg(232, 116, 97), Ansi.Fg(236, 176, 92), Ansi.Fg(120, 200, 130),
        Ansi.Fg(96, 190, 214), Ansi.Fg(150, 150, 232), Ansi.Fg(214, 130, 200),
        Ansi.Fg(120, 210, 180), Ansi.Fg(200, 200, 120),
    };

    private static string AvatarChip(int seed) =>
        $"{AvatarPalette[Math.Abs(seed) % AvatarPalette.Length]}██{Ansi.Reset}";

    private static string Badge(string label, string value, string valueColor) =>
        $"{Ansi.Faint}{label}{Ansi.Reset} {valueColor}{Ansi.Bold}{value}{Ansi.Reset}";

    /// <summary>Three centered lines of solid team-colored score tiles (CT left, T right).</summary>
    private static List<string> ScoreTiles(int scoreCt, int scoreTr, int width)
    {
        const int tileW = 11;
        string Tile(string bg, string? mid) => Ansi.OnBg(
            mid is null
                ? new string(' ', tileW)
                : Ansi.Ink + Ansi.Bold + Ansi.CenterVisible(mid, tileW),
            bg);
        const string gap = "   ";
        return new List<string>
        {
            Ansi.CenterVisible(Tile(Ansi.BgCtTile, null) + gap + Tile(Ansi.BgTrTile, null), width),
            Ansi.CenterVisible(
                Tile(Ansi.BgCtTile, scoreCt.ToString()) + gap + Tile(Ansi.BgTrTile, scoreTr.ToString()), width),
            Ansi.CenterVisible(Tile(Ansi.BgCtTile, null) + gap + Tile(Ansi.BgTrTile, null), width),
        };
    }

    // ── chat + kill feed formatting ─────────────────────────────────────────────
    private IEnumerable<DashboardHub.ChatEntry> RecentChat(int n)
    {
        lock (_stateGate)
            return _chat.Skip(Math.Max(0, _chat.Count - n)).ToList();
    }

    private IEnumerable<KillFeedLine> RecentKillFeed(int n)
    {
        lock (_stateGate)
            return _killFeed.Skip(Math.Max(0, _killFeed.Count - n)).ToList();
    }

    private string FormatChatLine(DashboardHub.ChatEntry c, Dictionary<string, MatchTeam>? nameTeam)
    {
        var speakerColor = c.FromServer ? Ansi.Host : TeamColorForName(c.Speaker, nameTeam);
        var ts = c.WhenUtc.ToLocalTime().ToString("HH:mm");
        return $"{Ansi.Faint}{ts}{Ansi.Reset} {speakerColor}{Ansi.Bold}{Ansi.Truncate(c.Speaker, 14)}{Ansi.Reset}" +
               $"{Ansi.Muted}:{Ansi.Reset} {Ansi.Text}{c.Text}{Ansi.Reset}";
    }

    // Kill feed like the game: killer  Kill  ☠ victim — team colors, no red highlight.
    private string FormatKillFeed(KillFeedLine k)
    {
        var killerColor = TeamColor(k.KillerTeam);
        var victimColor = TeamColor(k.VictimTeam);
        var killer = k.KillerKnown
            ? $"{killerColor}{Ansi.Bold}{Ansi.Truncate(k.KillerName, 14)}{Ansi.Reset}"
            : $"{Ansi.Faint}?{Ansi.Reset}";
        return $"{killer} {Ansi.Muted}Kill{Ansi.Reset} " +
               $"{victimColor}☠ {Ansi.Truncate(k.VictimName, 14)}{Ansi.Reset}";
    }

    private Dictionary<string, MatchTeam> BuildNameTeamMap(MatchSnapshot? snap)
    {
        var map = new Dictionary<string, MatchTeam>(StringComparer.Ordinal);
        if (snap is null) return map;
        foreach (var a in snap.Actors)
            map[a.Name] = a.Team;
        return map;
    }

    private static string TeamColor(MatchTeam t) => t switch
    {
        MatchTeam.Ct => Ansi.Ct,
        MatchTeam.Tr => Ansi.Tr,
        _ => Ansi.Neutral,
    };

    private static string TeamColorForName(string name, Dictionary<string, MatchTeam>? nameTeam)
    {
        if (nameTeam is not null && nameTeam.TryGetValue(name, out var t))
            return TeamColor(t);
        return Ansi.Neutral;
    }

    private DashboardHub.RoundResultEntry? CurrentBanner()
    {
        lock (_stateGate)
        {
            if (_banner is null) return null;
            // Show a round banner for ~8s; keep a final match banner until match view exits.
            if (!_banner.IsFinal && (DateTime.UtcNow - _banner.WhenUtc).TotalSeconds > 8)
                return null;
            return _banner;
        }
    }

    private string BannerBar(DashboardHub.RoundResultEntry b, int width)
    {
        var (bg, fg) = b.Winner switch
        {
            MatchTeam.Ct => (Ansi.BgCtTile, Ansi.Ink),
            MatchTeam.Tr => (Ansi.BgTrTile, Ansi.Ink),
            _ => (Ansi.BgBand, Ansi.Text),
        };
        var mvp = b.MvpNr != 0 && !string.IsNullOrEmpty(b.MvpName)
            ? $"      ★ MVP  {b.MvpName}"
            : "";
        var text = $"{fg}{Ansi.Bold}▶  {b.Headline}{mvp}{Ansi.Reset}";
        return Ansi.OnBg(Ansi.CenterVisible(text, width), bg);
    }

    private string InputLine(int width)
    {
        if (Console.IsInputRedirected)
            return $"{Ansi.Faint} dashboard live · stdin redirected (commands off) · logs → latest.log{Ansi.Reset}";

        string buf;
        lock (_inputGate) buf = _input.ToString();
        return
            $"{Ansi.Accent}{Ansi.Bold} ❯ {Ansi.Reset}{Ansi.Text}{buf}{Ansi.Reset}{Ansi.Ct}▏{Ansi.Reset}" +
            $"   {Ansi.Faint}play | start·set start | mode <m> | map <l> | status | quit{Ansi.Reset}";
    }

    // ── card / layout primitives ────────────────────────────────────────────────
    private static string CardTop(int width, string bg) =>
        Ansi.OnBg($"{Ansi.Border}╭{new string('─', Math.Max(0, width - 2))}╮{Ansi.Reset}", bg);

    private static string CardBottom(int width, string bg) =>
        Ansi.OnBg($"{Ansi.Border}╰{new string('─', Math.Max(0, width - 2))}╯{Ansi.Reset}", bg);

    private static string CardRow(string interior, int width, string bg)
    {
        var inner = Ansi.PadVisible(interior, Math.Max(0, width - 2));
        return Ansi.OnBg($"{Ansi.Border}│{Ansi.Reset}{inner}{Ansi.Border}│{Ansi.Reset}", bg);
    }

    private static string CardBand(string left, string right, int width, string bg)
    {
        var interiorW = Math.Max(0, width - 2);
        var l = " " + left;
        var r = right.Length == 0 ? "" : right + " ";
        var fill = Math.Max(0, interiorW - Ansi.VisibleLength(l) - Ansi.VisibleLength(r));
        return CardRow(l + new string(' ', fill) + r, width, bg);
    }

    private static string TwoColInterior(string left, string right, int colW)
    {
        var l = Ansi.PadVisible(left, colW);
        var r = Ansi.PadVisible(right, colW);
        return $" {l} {Ansi.Border}│{Ansi.Reset} {r} ";
    }
}
