using System.Collections.Concurrent;
using System.Text;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Full-screen terminal dashboard for the dedicated LAN host — display-only.
/// </summary>
/// <remarks>
/// <para>It reads state the host already tracks (lobby roster, mode/map, match actors/props and
/// scores) plus events pushed through <see cref="DashboardHub"/>. It never builds, sends or
/// replays anything on the wire.</para>
///
/// <para><b>Rendering model.</b> Every tick builds a <see cref="DashboardModel"/>, renders it into
/// a fixed-size <see cref="DashboardFrame"/> and paints rows with absolute cursor addressing
/// (no newlines, every row clipped to the width). Frames that did not change are not repainted.
/// This is what stops the "flying rows": a frame can never scroll the terminal, a long chat line
/// can never wrap, and every line has exactly the width the layout assumed.</para>
///
/// <para><b>Input.</b> Raw keys are read on a background thread and echoed by the renderer, so the
/// input line lives at a fixed row instead of wherever the terminal happened to leave the caret.
/// Cyrillic typing works because the console is switched to UTF-8 before the first frame.</para>
/// </remarks>
public sealed class CliDashboard : IDisposable
{
    private const int TickMs = 200;
    private const int ChatCap = 200;
    private const int FeedCap = 40;

    private readonly GameNetHost _game;

    private readonly CancellationTokenSource _cts = new();
    private readonly BlockingCollection<string> _commands = new(new ConcurrentQueue<string>());

    private readonly object _stateGate = new();
    private readonly List<DashboardHub.ChatEntry> _chat = new();
    private readonly List<DashboardModel.FeedLine> _killFeed = new();
    private readonly ConcurrentQueue<DashboardHub.DeathEntry> _pendingDeaths = new();
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
    private bool _wasInMatch;
    private string? _notice;
    private DateTime _noticeUtc = DateTime.MinValue;

    private DashboardFrame? _lastFrame;
    private int _lastWidth;
    private int _lastHeight;

    // Visual dev aid only: STANDCHILLOW_DASH_DEMO=lobby|match seeds fake state to preview layout.
    private readonly string? _demo = Environment.GetEnvironmentVariable("STANDCHILLOW_DASH_DEMO");

    private sealed record KillerCredit(byte Nr, MatchTeam Team, string Name, DateTime WhenUtc);

    public CliDashboard(GameNetHost game)
    {
        _game = game;
    }

    // ── lifecycle ────────────────────────────────────────────────────────────
    /// <summary>Enter the alternate screen and start the render + input loops.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _running = true;

        // UTF-8 must be on before a single glyph is written, or the first frames are mojibake.
        TerminalCapabilities.ConfigureConsoleEncoding();
        DashboardLogSink.Install();

        DashboardHub.Chat += OnChat;
        DashboardHub.Death += OnDeath;
        DashboardHub.RoundResult += OnRoundResult;

        Console.CancelKeyPress += OnCancelKey;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

        var term = TerminalWriter.Instance;
        var sb = new StringBuilder();
        sb.Append(Ansi.EnterAltScreen);
        sb.Append(Ansi.ClearScreen);
        sb.Append(Ansi.MoveTo(1, 1));
        sb.Append(Ansi.HideCursor);
        term.Write(sb.ToString());
        term.Flush();

        _renderTask = Task.Run(RenderLoop);

        _inputThread = new Thread(InputLoop)
        {
            IsBackground = true,
            Name = "dashboard-input",
        };
        _inputThread.Start();
    }

    /// <summary>Block until the next submitted command line, or <c>null</c> when stopping.</summary>
    public string? NextCommand()
    {
        try
        {
            return _commands.Take(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Same path as typing <c>quit</c>: enqueue quit and wake <see cref="NextCommand"/>.
    /// Used by CTRL+C / SIGTERM so the main loop exits and <c>using</c> disposes hosts.
    /// </summary>
    public void RequestQuit()
    {
        try
        {
            if (!_commands.IsAddingCompleted)
                _commands.Add("quit");
        }
        catch (InvalidOperationException) { /* closed / disposed */ }

        try { _cts.Cancel(); } catch { /* ignore */ }
    }

    /// <summary>Post a one-line console feedback message above the input line.</summary>
    public void PostLocalNotice(string text)
    {
        _notice = Layout.SingleLine(text);
        _noticeUtc = DateTime.UtcNow;
    }

    public void Stop()
    {
        if (!_running && !_started)
            return;
        _running = false;
        try { _cts.Cancel(); } catch { /* ignore */ }

        Console.CancelKeyPress -= OnCancelKey;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        DashboardHub.Chat -= OnChat;
        DashboardHub.Death -= OnDeath;
        DashboardHub.RoundResult -= OnRoundResult;

        try { _renderTask?.Wait(1000); } catch { /* ignore */ }

        var term = TerminalWriter.Instance;
        try
        {
            term.Write(Ansi.ShowCursor + Ansi.Reset + Ansi.LeaveAltScreen);
            term.Flush();
        }
        catch { /* ignore */ }

        DashboardLogSink.Uninstall();
        _started = false;
        _lastFrame = null;
    }

    public void Dispose() => Stop();

    // ── event sinks (may be called from the network thread) ──────────────────
    private void OnChat(DashboardHub.ChatEntry e)
    {
        lock (_stateGate)
        {
            _chat.Add(e);
            if (_chat.Count > ChatCap)
                _chat.RemoveRange(0, _chat.Count - ChatCap);
        }
    }

    private void OnDeath(DashboardHub.DeathEntry e) => _pendingDeaths.Enqueue(e);

    private void OnRoundResult(DashboardHub.RoundResultEntry e)
    {
        lock (_stateGate)
        {
            _banner = e;
        }
    }

    // ── input ────────────────────────────────────────────────────────────────
    private void InputLoop()
    {
        while (_running)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch
            {
                // No console (service, detached session) — stop trying, keep rendering.
                return;
            }

            lock (_inputGate)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                    {
                        var line = _input.ToString().Trim();
                        _input.Clear();
                        if (line.Length > 0)
                            TryEnqueue(line);
                        break;
                    }
                    case ConsoleKey.Backspace:
                        if (_input.Length > 0)
                            _input.Length--;
                        break;
                    case ConsoleKey.Escape:
                        _input.Clear();
                        break;
                    case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        DashboardLogSink.Clear();
                        break;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        _input.Clear();
                        break;
                    case ConsoleKey.U when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        _input.Clear();
                        break;
                    case ConsoleKey.Tab:
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                            _input.Append(key.KeyChar);
                        break;
                }
            }
        }
    }

    private void TryEnqueue(string line)
    {
        try
        {
            if (!_commands.IsAddingCompleted)
                _commands.Add(line);
        }
        catch (InvalidOperationException) { /* stopping */ }
    }

    private void OnCancelKey(object? sender, ConsoleCancelEventArgs e)
    {
        // Refuse immediate process death — same graceful path as typing quit.
        e.Cancel = true;
        RequestQuit();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Stop();

    // ── render loop ──────────────────────────────────────────────────────────
    private async Task RenderLoop()
    {
        while (_running)
        {
            try
            {
                RenderFrame();
            }
            catch
            {
                // A render glitch must never take the host down.
            }

            try
            {
                await Task.Delay(TickMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Build one model, render it, paint it if anything changed. Public for the layout self-check.</summary>
    internal void RenderFrame()
    {
        var (width, height) = GetSize();
        var model = BuildModel(width, height);
        var frame = DashboardView.Render(model);

        if (_lastFrame is not null
            && _lastWidth == frame.Width
            && _lastHeight == frame.Height
            && _lastFrame.SameAs(frame))
        {
            return; // nothing changed — no repaint, no flicker
        }

        // Park the caret on the input line (prompt is 3 cells wide) so typing is visible.
        var cursorRow = frame.Height;
        var cursorColumn = 4 + Math.Min(120, Layout.CellWidth(model.InputBuffer));
        var payload = frame.Paint(cursorRow, cursorColumn, showCursorAt: false);
        var term = TerminalWriter.Instance;
        term.Write(payload);
        term.Flush();

        _lastFrame = frame;
        _lastWidth = frame.Width;
        _lastHeight = frame.Height;
    }

    private (int Width, int Height) GetSize()
    {
        var w = 120;
        var h = 40;
        try
        {
            if (!Console.IsOutputRedirected)
            {
                w = Math.Clamp(Console.WindowWidth, 60, 240);
                h = Math.Clamp(Console.WindowHeight, 16, 100);
            }
        }
        catch
        {
            // No console — keep defaults.
        }

        return (w, h);
    }

    private DashboardModel BuildModel(int width, int height)
    {
        var now = DateTime.UtcNow;
        var inMatch = _game.MatchStarted;
        MatchSnapshot? snap = null;

        if (inMatch)
        {
            try { snap = _game.Match.SnapshotActiveMatch(); }
            catch { snap = null; }
        }

        if (_demo is "match")
        {
            snap = DemoSnapshot();
            inMatch = true;
        }

        if (inMatch && !_wasInMatch)
        {
            // Fresh match view — reset kill-feed / cumulative death derivation.
            lock (_stateGate)
            {
                _killFeed.Clear();
                _banner = null;
            }
            _killBaseline.Clear();
            _recentKillers.Clear();
            _cumulativeDeaths.Clear();
        }
        _wasInMatch = inMatch;

        if (_demo is "match" or "lobby")
        {
            SeedDemoOnce();
            RefreshDemoTimestamps();
        }

        ProcessKillAttribution(snap);

        var model = new DashboardModel
        {
            Width = width,
            Height = height,
            NowUtc = now,
            LobbyName = Safe(() => _game.Session.LobbyName, ""),
            LobbyId = Safe(() => _game.Session.LobbyId, ""),
            Mode = Safe(() => _game.MatchStarted ? snap?.GameModeId ?? _game.Session.GameModeId : _game.Session.GameModeId, "-"),
            Map = Safe(() => _game.Session.SelectedLevels.Count > 0 ? _game.Session.SelectedLevels[0] : "-", "-"),
            Players = Safe(() => _game.JoinedCount, 0),
            Peers = Safe(() => _game.PeerCount, 0),
            MatchStarted = inMatch,
            InputRedirected = Safe(() => Console.IsInputRedirected, true),
            Notice = _notice,
            NoticeUtc = _noticeUtc,
            Snapshot = snap,
            Banner = CurrentBanner(),
        };

        if (inMatch && snap is not null)
        {
            model.Actors = snap.Actors;
            model.DeathsByActor = new Dictionary<byte, int>(_cumulativeDeaths);
            model.KillFeed = RecentFeed(FeedCap);
            model.Chat = RecentChat(ChatCap);
        }
        else
        {
            model.Roster = BuildRoster();
            model.Chat = RecentChat(ChatCap);
        }

        if (DashboardLogSink.Installed)
            model.Log = DashboardLogSink.Tail(200);

        lock (_inputGate)
            model.InputBuffer = _input.ToString();

        return model;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private IReadOnlyList<DashboardModel.RosterRow> BuildRoster()
    {
        var rows = new List<DashboardModel.RosterRow>();
        try
        {
            foreach (var (name, status) in _game.Session.SnapshotRoster())
            {
                var isHost = rows.Count == 0;
                var inMatch = status == Net.Lobby.PlayerLobbyStatus.InMatch;
                rows.Add(new DashboardModel.RosterRow(
                    Layout.SingleLine(name),
                    inMatch ? "в матче" : "в лобби",
                    isHost,
                    inMatch));
            }
        }
        catch
        {
            // Host shutting down / session gone — show an empty roster rather than dying.
        }

        return rows;
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
                if (a.IsHost)
                    continue;
                if (_killBaseline.TryGetValue(a.Nr, out var prev) && a.Kills > prev)
                {
                    for (var i = 0; i < a.Kills - prev; i++)
                        _recentKillers.Add(new KillerCredit(a.Nr, a.Team, a.Name, now));
                }

                _killBaseline[a.Nr] = a.Kills;
            }
        }

        while (_pendingDeaths.TryDequeue(out var death))
        {
            DashboardModel.FeedLine line;
            if (death.KillerNr is not null && !string.IsNullOrWhiteSpace(death.KillerName))
            {
                _recentKillers.RemoveAll(k => k.Nr == death.KillerNr);
                line = new DashboardModel.FeedLine(
                    death.KillerName, death.KillerTeam, true, death.VictimName, death.VictimTeam, now);
            }
            else
            {
                var idx = _recentKillers.FindLastIndex(k =>
                    IsFighting(k.Team) && k.Team != death.VictimTeam && k.Nr != death.VictimNr);
                if (idx >= 0)
                {
                    var killer = _recentKillers[idx];
                    _recentKillers.RemoveAt(idx);
                    line = new DashboardModel.FeedLine(
                        killer.Name, killer.Team, true, death.VictimName, death.VictimTeam, now);
                }
                else
                {
                    var enemyTeam = death.VictimTeam == MatchTeam.Tr ? MatchTeam.Ct
                        : death.VictimTeam == MatchTeam.Ct ? MatchTeam.Tr
                        : MatchTeam.None;
                    line = new DashboardModel.FeedLine(
                        "?", enemyTeam, false, death.VictimName, death.VictimTeam, now);
                }
            }

            lock (_stateGate)
            {
                _killFeed.Add(line);
                if (_killFeed.Count > FeedCap)
                    _killFeed.RemoveRange(0, _killFeed.Count - FeedCap);
            }

            _cumulativeDeaths[death.VictimNr] = _cumulativeDeaths.GetValueOrDefault(death.VictimNr) + 1;
        }
    }

    private static bool IsFighting(MatchTeam t) => t is MatchTeam.Tr or MatchTeam.Ct;

    // ── demo seeding (visual dev aid only) ───────────────────────────────────
    private bool _demoSeeded;

    private void SeedDemoOnce()
    {
        if (_demoSeeded)
            return;
        _demoSeeded = true;
        lock (_stateGate)
        {
            _chat.Add(new DashboardHub.ChatEntry("Server", "влал подключился!", true, DateTime.UtcNow));
            _chat.Add(new DashboardHub.ChatEntry("влал", "го катка", false, DateTime.UtcNow));
            _chat.Add(new DashboardHub.ChatEntry("фейк влал", "изи", false, DateTime.UtcNow));
            _killFeed.Add(new DashboardModel.FeedLine("фейк влал", MatchTeam.Ct, true, "влал", MatchTeam.Tr, DateTime.UtcNow));
            _killFeed.Add(new DashboardModel.FeedLine("влал", MatchTeam.Tr, true, "фейк влал", MatchTeam.Ct, DateTime.UtcNow));
            _banner = new DashboardHub.RoundResultEntry(
                "раунд 2: победа ОБОРОНЫ · 1:1", MatchTeam.Ct, 3, "фейк влал", false, DateTime.UtcNow);
        }

        _cumulativeDeaths[7] = 1;
        _cumulativeDeaths[3] = 2;
    }

    /// <summary>Demo preview only: keep sample chat/feed/banner inside their display windows.</summary>
    private void RefreshDemoTimestamps()
    {
        var now = DateTime.UtcNow;
        lock (_stateGate)
        {
            for (var i = 0; i < _killFeed.Count; i++)
                _killFeed[i] = _killFeed[i] with { WhenUtc = now };
            if (_banner is not null)
                _banner = _banner with { WhenUtc = now };
        }
    }

    private MatchSnapshot DemoSnapshot()
    {
        static MatchActorSnapshot A(byte nr, string name, MatchTeam t, int money, int k, int a, int sc, int ping,
            bool host = false, bool dead = false) =>
            new()
            {
                Nr = nr, Name = name, Team = t, Money = money, Kills = k, Assists = a,
                Score = sc, Ping = ping, IsHost = host, DeadThisRound = dead,
            };

        return new MatchSnapshot
        {
            GameModeId = "Ranked2v2",
            Map = "Sandstone 2x2",
            C2 = MatchC2States.PurchasePhase,
            Phase = MatchFlowPhase.PurchasePhase,
            Round = 2,
            ScoreCt = 1,
            ScoreTr = 1,
            PhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(109),
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

    // ── state accessors ──────────────────────────────────────────────────────
    private IReadOnlyList<DashboardHub.ChatEntry> RecentChat(int n)
    {
        lock (_stateGate)
            return _chat.Count <= n ? _chat.ToArray() : _chat.Skip(_chat.Count - n).ToArray();
    }

    private IReadOnlyList<DashboardModel.FeedLine> RecentFeed(int n)
    {
        lock (_stateGate)
            return _killFeed.Count <= n ? _killFeed.ToArray() : _killFeed.Skip(_killFeed.Count - n).ToArray();
    }

    private DashboardHub.RoundResultEntry? CurrentBanner()
    {
        lock (_stateGate)
        {
            if (_banner is null)
                return null;
            // Show a round banner for ~8s; keep a final match banner until the match view exits.
            if (!_banner.IsFinal && (DateTime.UtcNow - _banner.WhenUtc).TotalSeconds > 8)
                return null;
            return _banner;
        }
    }

    /// <summary>
    /// Render one frame at an explicit size into plain text (no escapes). Used by the layout
    /// self-check (<c>--dashboard-check</c>) and by tests; it never touches the terminal.
    /// </summary>
    internal static IReadOnlyList<string> RenderForCheck(DashboardModel model)
    {
        var frame = DashboardView.Render(model);
        return frame.ToPlainRows();
    }
}
