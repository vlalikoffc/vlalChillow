namespace StandChillow.LanServer.Dashboard;

/// <summary>
/// Card / panel primitives shared by the lobby and match views.
/// </summary>
/// <remarks>
/// Every card line is built to an exact, known cell width, and the height of a card is decided by
/// the caller (never by the content). The old renderer let content decide the line count, which
/// combined with <c>'\n'</c> output pushed frames past the bottom of the window and made the
/// screen scroll — the "flying rows" symptom. Here a card is a fixed list of
/// <see cref="Width"/>-cell strings; <see cref="DashboardFrame"/> paints them at absolute rows.
/// </remarks>
internal static class Chrome
{
    /// <summary>One bordered card of exact size.</summary>
    public sealed class Card
    {
        public Card(int width, int rows, string? title = null, string? rightTitle = null)
        {
            Width = Math.Max(4, width);
            Rows = Math.Max(2, rows); // top border + at least one interior row
            Title = title;
            RightTitle = rightTitle;
        }

        public int Width { get; }

        /// <summary>Total rows including top and bottom border.</summary>
        public int Rows { get; }

        public string? Title { get; }

        public string? RightTitle { get; }

        /// <summary>Interior width (between the side borders).</summary>
        public int Inner => Width - 2;

        /// <summary>Interior rows (between the top and bottom border).</summary>
        public int InteriorRows => Rows - 2;

        private static TerminalCapabilities.Glyphs G => TerminalCapabilities.CurrentGlyphs;

        /// <summary>Top border, with the optional band title folded into it.</summary>
        public string Top()
        {
            var g = G;
            var inner = Inner;
            if (string.IsNullOrEmpty(Title) && string.IsNullOrEmpty(RightTitle))
                return g.TopLeft + Layout.Bar(inner, g.Horizontal[0]) + g.TopRight;

            var left = string.IsNullOrEmpty(Title) ? "" : $" {Title} ";
            var right = string.IsNullOrEmpty(RightTitle) ? "" : $" {RightTitle} ";
            var fill = Math.Max(0, inner - Layout.CellWidth(left) - Layout.CellWidth(right));
            var middle = Layout.Bar(fill, g.Horizontal[0]);
            var line = left + middle + right;
            return g.TopLeft + Layout.Fit(line, inner) + g.TopRight;
        }

        public string Bottom() => G.Bottom(Inner);

        /// <summary>
        /// One interior row. <paramref name="content"/> is padded/clipped to the interior width;
        /// <paramref name="highlight"/> paints a full-width band (used for headers and the
        /// selected input row) instead of the normal background.
        /// </summary>
        public string Row(string? content, bool highlight = false)
        {
            var g = G;
            var body = Layout.Fit(content, Inner);
            if (highlight)
                return g.Vertical + Ansi.OnBand + body + Ansi.Reset + g.Vertical;
            return g.Vertical + body + g.Vertical;
        }

        /// <summary>Left/right split row (two columns inside one card).</summary>
        public string SplitRow(string? left, string? right, int leftWidth, bool highlight = false)
        {
            var g = G;
            var divider = $" {g.Vertical} ";
            var rightWidth = Math.Max(0, Inner - leftWidth - Layout.CellWidth(divider));
            var body = Layout.Fit(left, leftWidth) + divider + Layout.Fit(right, rightWidth);
            if (highlight)
                return g.Vertical + Ansi.OnBand + Layout.Fit(body, Inner) + Ansi.Reset + g.Vertical;
            return g.Vertical + Layout.Fit(body, Inner) + g.Vertical;
        }

        /// <summary>Header row with a dim background (column captions).</summary>
        public string HeaderRow(string? content, int leftWidth = 0, string? right = null)
        {
            if (leftWidth > 0)
                return SplitRow(content, right, leftWidth, highlight: true);
            return Row(content, highlight: true);
        }
    }

    /// <summary>Section caption: dim label + bright value, safe on every terminal.</summary>
    public static string Badge(string label, string value, string valueColor) =>
        $"{Ansi.Faint}{label}{Ansi.Reset} {valueColor}{Ansi.Bold}{Layout.SingleLine(value)}{Ansi.Reset}";

    /// <summary>Separator dot used between inline badges.</summary>
    public static string Dot => $"{Ansi.Faint}·{Ansi.Reset}";

    /// <summary>English/Russian plural helper for player counts.</summary>
    public static string Players(int n) => n switch
    {
        1 => "1 игрок",
        >= 2 and <= 4 => $"{n} игрока",
        _ => $"{n} игроков",
    };
}
