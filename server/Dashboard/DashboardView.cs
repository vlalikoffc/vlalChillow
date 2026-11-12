using System.Globalization;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Pure renderer: turns a <see cref="DashboardModel"/> into a fixed-size <see cref="DashboardFrame"/>.
/// </summary>
/// <remarks>
/// <para>Display-only. No host calls, no wire traffic, no timers of its own.</para>
/// <para><b>Layout contract:</b> the frame is exactly <c>model.Height</c> rows and every row is
/// clipped to <c>model.Width</c> cells, so nothing can wrap or scroll. Rows are assigned to
/// fixed slots (header / summary / scoreboard / feed / log / input) and the flexible ones
/// share whatever vertical space is left; when the window is short the log panel disappears
/// before the scoreboard does.</para>
/// </remarks>
internal static class DashboardView
{
    private const int MinNameCells = 8;

    // Exact cell budget of one scoreboard row, so a column can never overflow:
    //   rank " " + 2 + " " + chip 2 + " "        = 7
    //   name (n-1) + dead marker 1               = n
    //   money 6 + " " 1 + K 3 + A 3 + D 3 + score 5 + ping 4 = 25
    private const int PlayerFixedCells = 32;

    public static DashboardFrame Render(DashboardModel m)
    {
        var frame = new DashboardFrame(m.Width, m.Height);
        var width = frame.Width;
        var height = frame.Height;

        var row = 0;
        row = RenderHeader(frame, row, m);

        // Input line is always the last row; the notice sits directly above it.
        var inputRow = height - 1;
        var noticeRow = height - 2;
        var contentBottom = noticeRow - 1;

        row = m.MatchStarted && m.Snapshot is not null
            ? RenderMatchBody(frame, row, contentBottom, m)
            : RenderLobbyBody(frame, row, contentBottom, m);

        // Stale rows between the body and the notice must be blanked.
        frame.ClearRows(row);
        if (row <= noticeRow)
            frame.Set(noticeRow, RenderNotice(m, width));
        frame.Set(inputRow, RenderInput(m, width));
        return frame;
    }

    // ── header ───────────────────────────────────────────────────────────────
    private static int RenderHeader(DashboardFrame frame, int row, DashboardModel m)
    {
        var width = frame.Width;
        var brand = $"{Ansi.Bold}{Ansi.Text}влал{Ansi.Ct}Chillow{Ansi.Reset}";
        var right = m.MatchStarted
            ? $"{Ansi.Danger}{Ansi.Bold}МАТЧ ИДЁТ{Ansi.Reset}"
            : $"{Ansi.Accent}{Ansi.Bold}ЛОББИ ОТКРЫТО{Ansi.Reset}";

        var gap = Math.Max(1, width - Layout.CellWidth(brand) - Layout.CellWidth(right) - 2);
        frame.Set(row++, " " + brand + Layout.Bar(gap, ' ') + right + " ");
        frame.Set(row++, $"{Ansi.Faint}{Layout.Bar(width, TerminalCapabilities.CurrentGlyphs.Horizontal[0])}{Ansi.Reset}");

        var title = Layout.SingleLine(m.LobbyName);
        var info =
            $"{Ansi.Muted}«{Ansi.Reset}{Ansi.Text}{title}{Ansi.Reset}{Ansi.Muted}»{Ansi.Reset}   " +
            $"{Chrome.Badge("режим", m.Mode, Ansi.Ct)}   " +
            $"{Chrome.Badge("карта", m.Map, Ansi.Tr)}   " +
            $"{Chrome.Badge("игроки", Layout.Num(m.Players), Ansi.Accent)}";

        if (m.Wide)
        {
            var rightSide =
                $"{Chrome.Badge("пиры", Layout.Num(m.Peers), Ansi.Muted)}   " +
                $"{Chrome.Badge("id", ShortId(m.LobbyId), Ansi.Faint)}";
            var space = Math.Max(1, width - Layout.CellWidth(info) - Layout.CellWidth(rightSide) - 2);
            frame.Set(row++, " " + info + Layout.Bar(space, ' ') + rightSide + " ");
        }
        else
        {
            frame.Set(row++, " " + info);
            frame.Set(row++, " " + $"{Chrome.Badge("пиры", Layout.Num(m.Peers), Ansi.Muted)}");
        }

        return row;
    }

    private static string ShortId(string id) =>
        string.IsNullOrWhiteSpace(id) ? "-" : id.Length <= 8 ? id : id[..8];

    // ── lobby body ───────────────────────────────────────────────────────────
    private static int RenderLobbyBody(DashboardFrame frame, int row, int bottom, DashboardModel m)
    {
        var slots = new List<CardSlot>();
        var rosterRows = Math.Clamp(m.Roster.Count, 1, 10);

        slots.Add(new CardSlot("players", 2 + rosterRows));
        slots.Add(new CardSlot("chat", 3));
        if (m.RoomForLog)
            slots.Add(new CardSlot("log", 3));

        var plan = Allocate(slots, bottom - row);
        foreach (var (name, height) in plan)
        {
            var card = new Chrome.Card(frame.Width - 2, height);
            row = DrawCard(frame, row, 1, card, name switch
            {
                "players" => RenderPlayersCard(card, m),
                "chat" => RenderChatCard(card, m),
                _ => RenderLogCard(card, m),
            });
        }

        return row;
    }

    private static IEnumerable<string?> RenderPlayersCard(Chrome.Card card, DashboardModel m)
    {
        // Column widths: " " 1 + # 3 + " " 1 + (chip 2 + " " 1 + name) + status 12 + " " 1.
        const int statusCells = 12;
        var inner = card.Inner;
        var nameCells = Math.Max(MinNameCells, inner - (4 + statusCells + 4));

        var caption =
            $" {Ansi.Faint}{Layout.Fit("#", 3)}{Ansi.Reset} " +
            $"{Ansi.Faint}{Layout.Fit("ИМЯ", nameCells + 3)}{Ansi.Reset}" +
            $"{Ansi.Faint}{Layout.Fit("СТАТУС", statusCells, Layout.Align.Right)}{Ansi.Reset} ";
        var rows = new List<string?>(card.Rows)
        {
            Band(card, "ИГРОКИ", Chrome.Players(m.Roster.Count)),
            Header(card, caption),
        };

        var index = 0;
        foreach (var p in m.Roster)
        {
            if (rows.Count >= card.Rows - 1)
                break;

            var tag = p.IsHost ? "host" : "";
            var chip = p.IsHost ? $"{Ansi.Host}**{Ansi.Reset}" : AvatarChip(index);
            var nameColor = p.InMatch ? Ansi.Ct : Ansi.Text;
            var statusColor = p.InMatch ? Ansi.Accent : Ansi.Muted;

            var name = $"{chip}{Ansi.Reset} " +
                       $"{nameColor}{Layout.Fit(Layout.SingleLine(p.Name), nameCells)}{Ansi.Reset}" +
                       $"{(tag.Length > 0 ? $" {Ansi.Faint}{tag}{Ansi.Reset}" : "")}";

            var line =
                $" {Ansi.Faint}{Layout.Fit(Layout.Num(index), 3, Layout.Align.Right)}{Ansi.Reset} " +
                $"{Layout.Fit(name, nameCells + 3)}" +
                $"{statusColor}{Layout.Fit(p.Status, statusCells, Layout.Align.Right)}{Ansi.Reset} ";
            rows.Add(card.Row(line));
            index++;
        }

        while (rows.Count < card.Rows - 1)
            rows.Add(card.Row(m.Roster.Count == 0
                ? $" {Ansi.Faint}пока никого — ищите лобби на телефоне (LAN){Ansi.Reset}"
                : ""));

        rows.Add(card.Bottom());
        return rows;
    }

    private static IEnumerable<string?> RenderChatCard(Chrome.Card card, DashboardModel m)
    {
        var rows = new List<string?> { Band(card, "ЧАТ", "телефон + консоль") };
        var bodyRows = card.Inner <= 0 ? 0 : card.Rows - 3;
        var recent = m.Chat.Count > bodyRows ? m.Chat.Skip(m.Chat.Count - bodyRows) : m.Chat;
        foreach (var c in recent)
        {
            if (rows.Count >= card.Rows - 1)
                break;
            rows.Add(card.Row(FormatChat(c, null, card.Inner - 2)));
        }

        while (rows.Count < card.Rows - 1)
            rows.Add(card.Row(""));
        rows.Add(card.Bottom());
        return rows;
    }

    private static IEnumerable<string?> RenderLogCard(Chrome.Card card, DashboardModel m)
    {
        var rows = new List<string?>
        {
            Band(card, "ЛОГ",
                $"последние строки · {Ansi.Faint}{Layout.Num(DashboardLogSink.Count)}{Ansi.Reset}"),
        };
        var bodyRows = Math.Max(0, card.Rows - 3);
        var recent = m.Log.Count > bodyRows ? m.Log.Skip(m.Log.Count - bodyRows) : m.Log;
        foreach (var e in recent)
        {
            if (rows.Count >= card.Rows - 1)
                break;
            rows.Add(card.Row(" " + DashboardLogSink.Format(e, card.Inner - 1)));
        }

        while (rows.Count < card.Rows - 1)
            rows.Add(card.Row(""));
        rows.Add(card.Bottom());
        return rows;
    }

    // ── match body ───────────────────────────────────────────────────────────
    private static int RenderMatchBody(DashboardFrame frame, int row, int bottom, DashboardModel m)
    {
        var snap = m.Snapshot!;
        var width = frame.Width;

        // Phase + clock, centered. This is the single most important line for the operator.
        var (phaseText, clock, clockColor) = DescribePhase(snap, m.NowUtc);
        var head = $"{clockColor}{Ansi.Bold}{clock}{Ansi.Reset}   {Ansi.Muted}{phaseText}{Ansi.Reset}";
        frame.Set(row++, Layout.Fit(head, width, Layout.Align.Center));

        // Score tiles: CT 12 cells, gap, T 12 cells, all centered.
        var ct = snap.Actors.Where(a => a.Team == MatchTeam.Ct).ToList();
        var tr = snap.Actors.Where(a => a.Team == MatchTeam.Tr).ToList();
        frame.Set(row++, ScoreTiles(ct.Count, snap.ScoreCt, tr.Count, snap.ScoreTr, width));

        // Summary strip.
        var mvp = m.Banner is { } b && !string.IsNullOrWhiteSpace(b.MvpName)
            ? $"   {Ansi.Faint}MVP{Ansi.Reset} {Ansi.Text}{Layout.SingleLine(b.MvpName)}{Ansi.Reset}"
            : "";
        var strip =
            $" {Chrome.Badge("режим", snap.GameModeId, Ansi.Ct)}   " +
            $"{Chrome.Badge("карта", snap.Map, Ansi.Tr)}   " +
            $"{Chrome.Badge("раунд", Layout.Num(Math.Max(1, snap.Round)), Ansi.Text)}" +
            mvp;
        frame.Set(row++, strip);

        // Scoreboard, then chat + kill feed side by side, then (if there is room) the log.
        var slots = new List<CardSlot> { new("score", 3 + Math.Max(ct.Count, tr.Count)) };
        slots.Add(new CardSlot("chatfeed", 3));
        if (m.RoomForLog)
            slots.Add(new CardSlot("log", 3));

        var plan = Allocate(slots, bottom - row);
        foreach (var (name, height) in plan)
        {
            var card = new Chrome.Card(width - 2, height);
            row = DrawCard(frame, row, 1, card, name switch
            {
                "score" => RenderScoreCard(card, m, ct, tr),
                "chatfeed" => RenderChatFeedCard(card, m, ct, tr),
                _ => RenderLogCard(card, m),
            });
        }

        return row;
    }

    /// <summary>
    /// Two solid score tiles (CT left, T right) on one row, centered in the terminal.
    /// Colour is background-only (16 ANSI colours), so it works in cmd, PowerShell and bash.
    /// </summary>
    private static string ScoreTiles(int ctPlayers, int ctScore, int trPlayers, int trScore, int width)
    {
        const int tile = 20;
        string Side(string caption, int players, int score, string background) =>
            background + Ansi.Ink + Ansi.Bold +
            Layout.Fit($" {caption} {players}", tile - 4) +
            Layout.Fit(Layout.Num(score), 4, Layout.Align.Right) +
            Ansi.Reset;

        var ct = Side("ОБОРОНА", ctPlayers, ctScore, Ansi.OnCt);
        var tr = Side("АТАКА", trPlayers, trScore, Ansi.OnTr);
        return Layout.Fit(ct + "   " + tr, width, Layout.Align.Center);
    }

    private static IEnumerable<string?> RenderScoreCard(
        Chrome.Card card, DashboardModel m, List<MatchActorSnapshot> ct, List<MatchActorSnapshot> tr)
    {
        var rows = new List<string?>(card.Rows);
        var inner = card.Inner;
        var banner = m.Banner;
        var twoColumns = m.Wide && inner >= 88;
        var colWidth = twoColumns ? (inner - 3) / 2 : inner;

        rows.Add(twoColumns
            ? CardBandSplit(card,
                $"{Ansi.Bold}{Ansi.Ct}ОБОРОНА (CT) · {Layout.Num(ct.Count)}{Ansi.Reset}",
                $"{Ansi.Bold}{Ansi.Tr}АТАКА (T) · {Layout.Num(tr.Count)}{Ansi.Reset}")
            : Band(card, "СЧЁТ", $"CT {Layout.Num(ct.Count)} · T {Layout.Num(tr.Count)}"));

        var header = PlayerHeader(colWidth);
        rows.Add(twoColumns
            ? card.SplitRow(header, header, colWidth, highlight: true)
            : card.Row(header, highlight: true));

        if (twoColumns)
        {
            var lines = Math.Max(ct.Count, tr.Count);
            for (var i = 0; i < lines && rows.Count < card.Rows - 1; i++)
            {
                var left = i < ct.Count ? PlayerLine(i + 1, ct[i], MatchTeam.Ct, colWidth, m) : "";
                var right = i < tr.Count ? PlayerLine(i + 1, tr[i], MatchTeam.Tr, colWidth, m) : "";
                rows.Add(card.SplitRow(left, right, colWidth));
            }
        }
        else
        {
            // Narrow window: one team under the other so no column has to be squeezed away.
            for (var i = 0; i < ct.Count && rows.Count < card.Rows - 1; i++)
                rows.Add(card.Row(PlayerLine(i + 1, ct[i], MatchTeam.Ct, colWidth, m)));
            for (var i = 0; i < tr.Count && rows.Count < card.Rows - 1; i++)
                rows.Add(card.Row(PlayerLine(i + 1, tr[i], MatchTeam.Tr, colWidth, m)));
        }

        if (banner is not null && rows.Count < card.Rows - 1)
        {
            var (bg, fg) = banner.Winner switch
            {
                MatchTeam.Ct => (Ansi.OnCt, Ansi.Ink),
                MatchTeam.Tr => (Ansi.OnTr, Ansi.Ink),
                _ => (Ansi.OnBand, Ansi.Text),
            };
            var text = $"{fg}{Ansi.Bold}> {Layout.SingleLine(banner.Headline)}{Ansi.Reset}";
            rows.Add(card.Row(bg + Layout.Fit(text, inner) + Ansi.Reset));
        }

        while (rows.Count < card.Rows - 1)
            rows.Add(card.Row(""));
        rows.Add(card.Bottom());
        return rows;
    }

    private static int PlayerNameCells(int colWidth) =>
        Math.Max(MinNameCells, colWidth - PlayerFixedCells);

    /// <summary>Column captions that line up with <see cref="PlayerLine"/> cell for cell.</summary>
    private static string PlayerStatsHeader() =>
        $"{Ansi.Faint}" +
        $"{Layout.Fit("$", 6, Layout.Align.Right)} " +
        $"{Layout.Fit("У", 3, Layout.Align.Right)}" +
        $"{Layout.Fit("П", 3, Layout.Align.Right)}" +
        $"{Layout.Fit("С", 3, Layout.Align.Right)}" +
        $"{Layout.Fit("ОЧК", 5, Layout.Align.Right)}" +
        $"{Layout.Fit("PING", 4, Layout.Align.Right)}{Ansi.Reset}";

    /// <summary>Header line for one scoreboard side (same prefix as a player row).</summary>
    private static string PlayerHeader(int colWidth)
    {
        var nameCells = PlayerNameCells(colWidth);
        return $" {Ansi.Faint}{Layout.Fit("#", 2, Layout.Align.Right)}" +
               $"    {Layout.Fit("ИМЯ", nameCells)}{Ansi.Reset}" +
               PlayerStatsHeader();
    }

    private static string PlayerLine(
        int index, MatchActorSnapshot a, MatchTeam side, int colWidth, DashboardModel m)
    {
        var dead = a.DeadThisRound;
        var teamColor = side == MatchTeam.Ct ? Ansi.Ct : Ansi.Tr;
        var nameColor = dead ? Ansi.Faint : teamColor;
        var chip = a.IsHost ? $"{Ansi.Host}##{Ansi.Reset}" : AvatarChip(a.Nr);
        var deaths = m.DeathsByActor.TryGetValue(a.Nr, out var d) ? d : 0;
        var ping = a.IsHost ? "HOST"
            : a.Ping >= 0 ? Layout.Num(a.Ping)
            : "-";

        var nameCells = PlayerNameCells(colWidth);

        // Dead players are dimmed and marked with a dagger (ASCII-safe terminals get "+").
        var mark = dead ? (TerminalCapabilities.CurrentGlyphs == TerminalCapabilities.Glyphs.Ascii ? "+" : "†") : " ";

        return
            $" {Ansi.Faint}{Layout.Fit(Layout.Num(index), 2, Layout.Align.Right)}{Ansi.Reset}" +
            $" {chip} " +
            $"{nameColor}{(dead ? "" : Ansi.Bold)}{Layout.Fit(Layout.SingleLine(a.Name), nameCells - 1)}{Ansi.Reset}" +
            $"{mark}" +
            $"{(a.Money > 0 ? Ansi.Money : Ansi.Faint)}" +
            $"{Layout.Fit("$" + Layout.Num(a.Money), 6, Layout.Align.Right)}{Ansi.Reset} " +
            Stat(a.Kills, 3) + Stat(a.Assists, 3) + Stat(deaths, 3) +
            $"{Ansi.Text}{Layout.Fit(Layout.Num(a.Score), 5, Layout.Align.Right)}{Ansi.Reset}" +
            $"{Ansi.Muted}{Layout.Fit(ping, 4, Layout.Align.Right)}{Ansi.Reset}";
    }

    private static string Stat(int value, int cells) =>
        $"{(value > 0 ? Ansi.Text : Ansi.Faint)}{Layout.Fit(Layout.Num(value), cells, Layout.Align.Right)}{Ansi.Reset}";

    /// <summary>Chat (left) and kill feed (right) inside one card.</summary>
    private static IEnumerable<string?> RenderChatFeedCard(
        Chrome.Card card, DashboardModel m, List<MatchActorSnapshot> ct, List<MatchActorSnapshot> tr)
    {
        var inner = card.Inner;
        var leftWidth = Math.Max(20, (inner - 3) / 2);
        var nameTeam = BuildTeamMap(ct, tr);

        var rows = new List<string?>(card.Rows)
        {
            CardBandSplit(card,
                $"{Ansi.Bold}{Ansi.Text}ЧАТ{Ansi.Reset}",
                $"{Ansi.Bold}{Ansi.Text}ФИД УБИЙСТВ{Ansi.Reset}"),
        };

        var bodyRows = Math.Max(0, card.Rows - 3);
        var chat = m.Chat.Count > bodyRows ? m.Chat.Skip(m.Chat.Count - bodyRows).ToList() : m.Chat.ToList();
        var feed = m.KillFeed.Count > bodyRows ? m.KillFeed.Skip(m.KillFeed.Count - bodyRows).ToList() : m.KillFeed.ToList();

        for (var i = 0; i < bodyRows && rows.Count < card.Rows - 1; i++)
        {
            var left = i < chat.Count ? FormatChat(chat[i], nameTeam, leftWidth - 2) : "";
            var right = i < feed.Count ? FormatKill(feed[i], nameTeam, inner - leftWidth - 4) : "";
            rows.Add(card.SplitRow(left, right, leftWidth));
        }

        while (rows.Count < card.Rows - 1)
            rows.Add(card.Row(""));
        rows.Add(card.Bottom());
        return rows;
    }

    private static Dictionary<string, MatchTeam> BuildTeamMap(
        List<MatchActorSnapshot> ct, List<MatchActorSnapshot> tr)
    {
        var map = new Dictionary<string, MatchTeam>(StringComparer.Ordinal);
        foreach (var a in ct)
            map[a.Name] = MatchTeam.Ct;
        foreach (var a in tr)
            map[a.Name] = MatchTeam.Tr;
        return map;
    }

    // ── shared formatting ────────────────────────────────────────────────────
    public static string FormatChat(
        DashboardHub.ChatEntry c, IReadOnlyDictionary<string, MatchTeam>? nameTeam, int cells)
    {
        var speakerColor = c.FromServer
            ? Ansi.Host
            : nameTeam is not null && nameTeam.TryGetValue(c.Speaker, out var t)
                ? TeamColor(t)
                : Ansi.Neutral;
        var ts = c.WhenUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var head = $"{Ansi.Faint}{ts}{Ansi.Reset} {speakerColor}{Ansi.Bold}" +
                   $"{Layout.Fit(Layout.SingleLine(c.Speaker), 12)}{Ansi.Reset}{Ansi.Muted}:{Ansi.Reset} ";
        var room = Math.Max(0, cells - Layout.CellWidth(head));
        var body = $"{Ansi.Text}{Layout.ClipPlain(Layout.SingleLine(c.Text), room)}{Ansi.Reset}";
        return head + Layout.Fit(body, room);
    }

    public static string FormatKill(
        DashboardModel.FeedLine k, IReadOnlyDictionary<string, MatchTeam>? nameTeam, int cells)
    {
        var killerColor = TeamColor(k.KillerTeam);
        var victimColor = TeamColor(k.VictimTeam);
        var killer = k.KillerKnown
            ? $"{killerColor}{Ansi.Bold}{Layout.Fit(Layout.SingleLine(k.KillerName), 12)}{Ansi.Reset}"
            : $"{Ansi.Faint}?{Ansi.Reset}";
        var victim = $"{victimColor}{Layout.Fit(Layout.SingleLine(k.VictimName), 12)}{Ansi.Reset}";
        var arrow = $"{Ansi.Faint} -{TerminalCapabilities.CurrentGlyphs.Horizontal[0]}> {Ansi.Reset}";
        var line = " " + killer + arrow + victim;
        return Layout.Fit(line, cells);
    }

    private static string TeamColor(MatchTeam t) => t switch
    {
        MatchTeam.Ct => Ansi.Ct,
        MatchTeam.Tr => Ansi.Tr,
        _ => Ansi.Neutral,
    };

    private static readonly string[] AvatarPalette =
    {
        Ansi.Ct, Ansi.Tr, Ansi.Accent, "\x1b[95m", "\x1b[94m", "\x1b[96m", "\x1b[93m", "\x1b[92m",
    };

    private static string AvatarChip(int seed) =>
        $"{AvatarPalette[Math.Abs(seed) % AvatarPalette.Length]}||{Ansi.Reset}";

    private static string Band(Chrome.Card card, string title, string? right)
    {
        var caption =
            $" {Ansi.Bold}{Ansi.Text}{Layout.SingleLine(title)}{Ansi.Reset}" +
            $"{(string.IsNullOrEmpty(right) ? "" : $"  {Ansi.Muted}{Layout.SingleLine(right)}{Ansi.Reset}")} ";
        return Ansi.OnBand + Layout.Fit(caption, card.Inner) + Ansi.Reset;
    }

    private static string Header(Chrome.Card card, string content) =>
        Ansi.OnBand + Layout.Fit(content, card.Inner) + Ansi.Reset;

    private static string CardBandSplit(Chrome.Card card, string left, string right)
    {
        var inner = card.Inner;
        var leftCells = Math.Max(0, (inner - 3) / 2);
        var rightCells = Math.Max(0, inner - leftCells - 3);
        var divider = TerminalCapabilities.CurrentGlyphs.Vertical;
        var body = $" {left}{Layout.Bar(Math.Max(0, leftCells - Layout.CellWidth(left)), ' ')} {divider} " +
                   $"{right}{Layout.Bar(Math.Max(0, rightCells - Layout.CellWidth(right)), ' ')} ";
        return Ansi.OnBand + Layout.Fit(body, inner) + Ansi.Reset;
    }

    // ── phase description ────────────────────────────────────────────────────
    public static (string Text, string Clock, string ClockColor) DescribePhase(
        MatchSnapshot snap, DateTime nowUtc)
    {
        var bombFamily = !snap.IsDeathMatch
                         && snap.GameModeId is "Ranked2v2" or "Ranked2v2Alt" or "RankedDefuse"
                             or "Defuse" or "Escalation";

        var hasDeadline = snap.PhaseEndsUtc > DateTime.MinValue && snap.PhaseEndsUtc < DateTime.MaxValue;
        var remain = hasDeadline ? (snap.PhaseEndsUtc - nowUtc).TotalSeconds : double.NaN;

        string clock;
        string clockColor;
        if (hasDeadline && remain > -0.5)
        {
            clock = FormatClock(Math.Max(0, remain));
            clockColor = remain <= 10 ? Ansi.Danger : remain <= 30 ? Ansi.Tr : Ansi.Accent;
        }
        else if (!bombFamily && !hasDeadline && snap.TimeDeadline > 0
                 && snap.Phase is MatchFlowPhase.RoundLive or MatchFlowPhase.BombPlanted
                     or MatchFlowPhase.DeathMatchLive)
        {
            var left = snap.TimeDeadline - (nowUtc - DateTime.UnixEpoch).TotalSeconds;
            clock = FormatClock(Math.Max(0, left));
            clockColor = left <= 10 ? Ansi.Danger : left <= 30 ? Ansi.Tr : Ansi.Accent;
        }
        else
        {
            clock = "--:--";
            clockColor = Ansi.Faint;
        }

        var c2 = snap.C2 > 0 ? $"  ·  C2={snap.C2}" : "";
        var text = snap.Phase switch
        {
            MatchFlowPhase.WaitingPlayers => "ОЖИДАНИЕ ИГРОКОВ",
            MatchFlowPhase.AlliesPreWarmup => $"РАЗМИНКА (FFA){c2}",
            MatchFlowPhase.Warmup => $"РАЗМИНКА  ·  раунд {Math.Max(1, snap.Round)}{c2}",
            MatchFlowPhase.WarmupWillFinish => $"ЗАКУПКА  ·  раунд {snap.Round}{c2}",
            MatchFlowPhase.PurchasePhase when bombFamily => $"БОЙ  ·  раунд {snap.Round}{c2}",
            MatchFlowPhase.PurchasePhase => $"ПОДГОТОВКА  ·  раунд {snap.Round}{c2}",
            MatchFlowPhase.RoundLive => $"БОЙ  ·  раунд {snap.Round}{c2}",
            MatchFlowPhase.BombPlanted => $"БОМБА ЗАЛОЖЕНА  ·  раунд {snap.Round}{c2}",
            MatchFlowPhase.RoundEndPause => $"КОНЕЦ РАУНДА  ·  {snap.Round}{c2}",
            MatchFlowPhase.HalfTimeIntro => "ПЕРЕРЫВ  ·  СМЕНА СТОРОН",
            MatchFlowPhase.HalfTimeSwap => "ПЕРЕРЫВ  ·  СТОРОНЫ ПОМЕНЯЛИСЬ",
            MatchFlowPhase.HalfTimeTransition => "ПЕРЕРЫВ  ·  ПРОДОЛЖЕНИЕ",
            MatchFlowPhase.MatchOver => "МАТЧ ОКОНЧЕН",
            MatchFlowPhase.DeathMatchWarmup => "РАЗМИНКА",
            MatchFlowPhase.DeathMatchLive => "БОЙ  ·  TDM",
            MatchFlowPhase.DeathMatchEnded => "МАТЧ ОКОНЧЕН",
            MatchFlowPhase.DeathMatchFinalHud => "ИТОГИ МАТЧА",
            _ => snap.Phase.ToString(),
        };

        return (text, clock, clockColor);
    }

    public static string FormatClock(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var total = (int)Math.Ceiling(seconds - 1e-6);
        if (total < 0)
            total = 0;
        return $"{total / 60:00}:{total % 60:00}";
    }

    // ── notice + input ───────────────────────────────────────────────────────
    private static string RenderNotice(DashboardModel m, int width)
    {
        if (string.IsNullOrWhiteSpace(m.Notice))
            return "";

        // Notices fade after 12s so stale feedback does not look like current state.
        if (m.NoticeUtc != default && (m.NowUtc - m.NoticeUtc).TotalSeconds > 12)
            return "";

        var text = Layout.SingleLine(m.Notice);
        return Layout.Fit($" {Ansi.Accent}*{Ansi.Reset} {Ansi.Text}{text}{Ansi.Reset}", width);
    }

    private static string RenderInput(DashboardModel m, int width)
    {
        if (m.InputRedirected)
            return Layout.Fit(
                $" {Ansi.Faint}дашборд · ввод недоступен (stdin перенаправлен) · логи → latest.log{Ansi.Reset}",
                width);

        var prompt = $" {Ansi.Accent}{Ansi.Bold}> {Ansi.Reset}";
        var buffer = Layout.SingleLine(m.InputBuffer);
        var hint = $"{Ansi.Faint}   help · play · start · end · mode · map · set · status · quit{Ansi.Reset}";

        var available = Math.Max(4, width - Layout.CellWidth(prompt) - 1);
        var bufferCells = Math.Min(Layout.CellWidth(buffer), Math.Min(available, 48));
        var line = prompt + Ansi.Text + Layout.ClipPlain(buffer, bufferCells) + Ansi.Reset +
                   Ansi.Accent + "_" + Ansi.Reset;

        var hintRoom = width - Layout.CellWidth(line) - 1;
        if (hintRoom >= 16)
            line += Layout.ClipAnsi(hint, hintRoom);

        return Layout.Fit(line, width);
    }

    // ── layout allocation ────────────────────────────────────────────────────
    private readonly record struct CardSlot(string Name, int Preferred);

    private readonly record struct PlannedCard(string Name, int Height);

    /// <summary>
    /// Decide card heights for the space left. Preferred heights are honoured first; whatever
    /// remains is given back to the log card, then the last card, so the screen is always full
    /// and no card is ever taller than the budget.
    /// </summary>
    private static IReadOnlyList<PlannedCard> Allocate(IReadOnlyList<CardSlot> slots, int budget)
    {
        var plan = new List<PlannedCard>(slots.Count);
        if (budget < 3 || slots.Count == 0)
            return plan;

        var remaining = budget;
        foreach (var slot in slots)
        {
            if (remaining < 3)
                break;
            var height = Math.Min(slot.Preferred, remaining);
            plan.Add(new PlannedCard(slot.Name, Math.Max(3, height)));
            remaining -= Math.Max(3, height);
        }

        if (remaining > 0 && plan.Count > 0)
        {
            var last = plan[^1];
            plan[^1] = last with { Height = last.Height + remaining };
        }

        // Never draw past the budget.
        var total = plan.Sum(p => p.Height);
        if (total > budget)
        {
            var overflow = total - budget;
            for (var i = plan.Count - 1; i >= 0 && overflow > 0; i--)
            {
                var take = Math.Min(overflow, plan[i].Height - 3);
                if (take <= 0)
                    continue;
                plan[i] = plan[i] with { Height = plan[i].Height - take };
                overflow -= take;
            }
        }

        return plan;
    }

    /// <summary>Draw one card starting at <paramref name="row"/>; returns the next free row.</summary>
    private static int DrawCard(
        DashboardFrame frame, int row, int column, Chrome.Card card, IEnumerable<string?> lines)
    {
        foreach (var line in lines)
        {
            if (row >= frame.Height)
                break;
            var padded = " " + Layout.Fit(line, card.Width - 2) + " ";
            frame.Set(row, Layout.Fit(padded, frame.Width));
            row++;
        }

        return row;
    }
}
