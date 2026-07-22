namespace StandChillow.LanServer.Net.Lobby;

/// <summary>
/// Shared GameModeId × SelectedLevels catalog from ConnectAsClient probe
/// <c>run-20260722_213859</c>. Used by lobby <c>/mode</c> / <c>/map</c> and defaults.
/// </summary>
public static class GameModeCatalog
{
    public sealed record ModeInfo(
        string GameModeId,
        string DisplayName,
        IReadOnlyList<string> Levels,
        IReadOnlyList<string> Aliases);

    /// <summary>Nine LAN modes — level lists exact from probe PropChanged bags.</summary>
    public static readonly IReadOnlyList<ModeInfo> Modes = new ModeInfo[]
    {
        new(
            "Ranked2v2",
            "Союзники / 2v2",
            new[]
            {
                "Prison 2x2", "Hanami 2x2", "Rust 2x2", "Dune 2x2",
                "Breeze 2x2", "Province 2x2", "Sandstone 2x2",
            },
            new[] { "allies", "2v2", "ranked2v2", "союзники" }),
        new(
            "Ranked2v2Alt",
            "Союзники Alt / 2v2 Alt",
            new[]
            {
                "Prison 2x2 Alt", "Hanami 2x2 Alt", "Rust 2x2 Alt", "Dune 2x2 Alt",
                "Breeze 2x2 Alt", "Province 2x2 Alt", "Sandstone 2x2 Alt",
            },
            new[] { "allies-alt", "alliesalt", "2v2alt", "ranked2v2alt" }),
        new(
            "RankedDefuse",
            "Ranked Defuse / Comp",
            new[] { "Prison", "Hanami", "Rust", "Dune", "Breeze", "Province", "Sandstone" },
            new[] { "comp", "ranked", "rankeddefuse", "ranked-defuse" }),
        new(
            "Defuse",
            "Defuse",
            new[] { "Prison", "Hanami", "Rust", "Dune", "Breeze", "Province", "Sandstone" },
            new[] { "defuse" }),
        new(
            "Escalation",
            "Escalation",
            new[] { "Prison", "Hanami", "Rust", "Dune", "Breeze", "Province", "Sandstone" },
            new[] { "escalation", "esc" }),
        new(
            "DeathMatch",
            "Team Deathmatch / командный бой (TDM)",
            new[]
            {
                "Perimeter", "Hanari", "Favelas", "Arena",
                "Calypso", "Sandyards", "TrainingOutside", "Village",
            },
            new[]
            {
                "tdm", "teamdm", "team-deathmatch", "teamdeathmatch",
                "dm", "deathmatch", "командный", "командныйбой",
            }),
        new(
            "ArmsRace",
            "Arms Race",
            new[]
            {
                "Perimeter", "Hanari", "Favelas", "Arena",
                "Calypso", "Sandyards", "TrainingOutside", "Village",
            },
            new[] { "arms", "armsrace", "arms-race" }),
        new(
            "FreeForAll",
            "Free For All / FFA",
            new[] { "Prison", "Hanami", "Rust", "Dune", "Breeze", "Province", "Sandstone" },
            new[] { "ffa", "freeforall", "free-for-all" }),
        new(
            "Duel",
            "Duel",
            new[] { "Block", "Cableway", "Pipeline", "Bridge", "Pool", "Temple", "Yard" },
            new[] { "duel" }),
    };

    private static readonly Dictionary<string, ModeInfo> ById =
        Modes.ToDictionary(m => m.GameModeId, StringComparer.Ordinal);

    private static readonly Dictionary<string, ModeInfo> ByAlias;

    static GameModeCatalog()
    {
        ByAlias = new Dictionary<string, ModeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var mode in Modes)
        {
            ByAlias[mode.GameModeId] = mode;
            foreach (var a in mode.Aliases)
                ByAlias[a] = mode;
        }
    }

    public static bool TryGetMode(string gameModeId, out ModeInfo mode) =>
        ById.TryGetValue(gameModeId, out mode!);

    public static bool TryResolveMode(string? token, out ModeInfo mode)
    {
        mode = null!;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        return ByAlias.TryGetValue(token.Trim(), out mode!);
    }

    public static IReadOnlyList<string> LevelsFor(string gameModeId) =>
        ById.TryGetValue(gameModeId, out var m) ? m.Levels : Array.Empty<string>();

    public static string DefaultLevelFor(string gameModeId)
    {
        var levels = LevelsFor(gameModeId);
        return levels.Count > 0 ? levels[0] : LobbyPropKeys.DefaultSelectedLevel;
    }

    /// <summary>
    /// Resolve a user map token against <paramref name="mode"/>'s SelectedLevels catalog.
    /// Prefer exact (case-insensitive), then unique prefix/contains (e.g. Sandstone → Sandstone 2x2).
    /// </summary>
    public static bool TryResolveLevel(ModeInfo mode, string token, out string level, out string? error)
    {
        level = "";
        error = null;
        var t = token.Trim();
        if (t.Length == 0)
        {
            error = "empty map name";
            return false;
        }

        foreach (var L in mode.Levels)
        {
            if (L.Equals(t, StringComparison.OrdinalIgnoreCase))
            {
                level = L;
                return true;
            }
        }

        var prefix = mode.Levels
            .Where(L => L.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (prefix.Count == 1)
        {
            level = prefix[0];
            return true;
        }

        var contains = mode.Levels
            .Where(L => L.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (contains.Count == 1)
        {
            level = contains[0];
            return true;
        }

        if (prefix.Count > 1 || contains.Count > 1)
        {
            var amb = (prefix.Count > 1 ? prefix : contains);
            error = $"ambiguous '{t}' → {string.Join(", ", amb)}";
            return false;
        }

        error = $"unknown map '{t}' for {mode.GameModeId}";
        return false;
    }

    public static string FormatModeHelp()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("режимы (/mode <alias>):");
        foreach (var m in Modes)
        {
            sb.Append('\n');
            var aliases = string.Join(", ", m.Aliases.Take(4));
            sb.Append("  ");
            sb.Append(aliases);
            sb.Append(" → ");
            sb.Append(m.GameModeId);
            sb.Append(" (");
            sb.Append(m.DisplayName);
            sb.Append(')');
        }
        return sb.ToString();
    }

    public static string FormatMapHelp(string gameModeId, IReadOnlyList<string> selected)
    {
        if (!TryGetMode(gameModeId, out var mode))
        {
            return $"режим '{gameModeId}' неизвестен — /mode для списка";
        }

        var sel = selected.Count > 0 ? string.Join(", ", selected) : "(нет)";
        var sb = new System.Text.StringBuilder();
        sb.Append("режим: ");
        sb.Append(mode.GameModeId);
        sb.Append(" (");
        sb.Append(mode.DisplayName);
        sb.Append(")\nвыбрано: ");
        sb.Append(sel);
        sb.Append("\nкарты (/map a, b, …):");
        foreach (var L in mode.Levels)
        {
            sb.Append('\n');
            sb.Append("  ");
            sb.Append(L);
        }
        return sb.ToString();
    }
}
