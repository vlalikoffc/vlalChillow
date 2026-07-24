namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Map+team spawn poses for ConnectAsClient fighter probe.
/// From <c>decompiled/MAP_SPAWN_POINTS.json</c> (APK data.unity3d SpawnArea under
/// Spawn_CT/TR + capture preferred overlays). Client <b>2.06 OBT F1</b>.
/// See <c>decompiled/PLAYER_SPAWN.md</c>.
/// </summary>
public static class MatchSpawnCatalog
{
    public readonly record struct Pose(
        float X, float Y, float Z,
        float Qx, float Qy, float Qz, float Qw,
        string Source);

    public static bool TryGet(string? map, MatchTeam team, out Pose pose)
    {
        pose = default;
        if (team is not (MatchTeam.Ct or MatchTeam.Tr))
            return false;
        if (string.IsNullOrWhiteSpace(map))
            return false;
        var key = ResolveMapKey(map.Trim());
        if (key is null)
            return false;
        return _byMapTeam.TryGetValue((key, team), out pose);
    }

    public static IReadOnlyCollection<string> KnownMaps => _mapAliases.Keys;

    private static string? ResolveMapKey(string map)
    {
        foreach (var (canonical, aliases) in _mapAliases)
        {
            if (aliases.Any(a => string.Equals(a, map, StringComparison.OrdinalIgnoreCase)))
                return canonical;
        }
        string? hit = null;
        foreach (var (canonical, aliases) in _mapAliases)
        {
            if (aliases.Any(a =>
                    map.Contains(a, StringComparison.OrdinalIgnoreCase)
                    || a.Contains(map, StringComparison.OrdinalIgnoreCase)))
            {
                if (hit is not null)
                    return null;
                hit = canonical;
            }
        }
        return hit;
    }

    private static readonly Dictionary<string, string[]> _mapAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Block"] = ["Block"],
        ["Breeze"] = ["Breeze", "Breeze 2x2", "Breeze 2x2 Alt"],
        ["Calypso"] = ["Calypso"],
        ["Dune"] = ["Dune", "Dune 2x2", "Dune 2x2 Alt"],
        ["Hanami"] = ["Hanami", "Hanami 2x2", "Hanami 2x2 Alt"],
        ["Perimeter"] = ["Perimeter"],
        ["Prison"] = ["Prison", "Prison 2x2", "Prison 2x2 Alt"],
        ["Province"] = ["Province", "Province 2x2", "Province 2x2 Alt"],
        ["Rust"] = ["Rust", "Rust 2x2", "Rust 2x2 Alt"],
        ["Sandstone"] = ["Sandstone", "Sandstone 2x2", "Sandstone 2x2 Alt"],
        ["Sandyards"] = ["Sandyards"],
    };

    private static readonly Dictionary<(string Map, MatchTeam Team), Pose> _byMapTeam = new()
    {
        [("Block", MatchTeam.Tr)] = new(
            -15.033900f, 0.040000f, -13.769800f,
            0.000000f, 0.500000f, 0.000000f, 0.866000f,
            "duel-probe 20260724_0415 early Tr"),
        [("Block", MatchTeam.Ct)] = new(
            7.949800f, 0.037000f, 7.793100f,
            0.000000f, 0.923900f, 0.000000f, -0.382700f,
            "duel-probe 20260724_0415 early Ct"),
        [("Breeze", MatchTeam.Tr)] = new(
            -30.148998f, 3.200000f, 14.330001f,
            0.000000f, 0.707107f, -0.000000f, 0.707107f,
            "asset median of mode zones"),
        [("Breeze", MatchTeam.Ct)] = new(
            69.759998f, 1.570000f, 7.990002f,
            0.000000f, -0.707107f, 0.000000f, 0.707107f,
            "asset median of mode zones"),
        [("Calypso", MatchTeam.Tr)] = new(
            -26.121999f, -14.541000f, 25.686501f,
            0.000000f, 0.991445f, 0.000000f, -0.130526f,
            "asset median of mode zones"),
        [("Calypso", MatchTeam.Ct)] = new(
            -27.159999f, -15.033000f, -30.533001f,
            0.000000f, 0.130526f, 0.000000f, 0.991445f,
            "asset median of mode zones"),
        [("Dune", MatchTeam.Tr)] = new(
            8.359995f, -28.617464f, -28.169987f,
            0.000000f, 0.990777f, 0.000000f, 0.135503f,
            "asset median of mode zones"),
        [("Dune", MatchTeam.Ct)] = new(
            13.894997f, -28.221352f, -86.711985f,
            0.000000f, -0.501600f, 0.000000f, 0.865100f,
            "asset median of mode zones"),
        [("Hanami", MatchTeam.Tr)] = new(
            33.847265f, 0.665000f, 32.261836f,
            0.000000f, -0.983080f, 0.000000f, 0.183177f,
            "asset median of mode zones"),
        [("Hanami", MatchTeam.Ct)] = new(
            -23.875000f, 1.495000f, -27.160000f,
            0.000000f, 0.305382f, 0.000000f, 0.952230f,
            "asset median of mode zones"),
        [("Perimeter", MatchTeam.Tr)] = new(
            15.250000f, 0.000000f, -6.200000f,
            0.000000f, 0.649448f, 0.000000f, 0.760406f,
            "asset SpawnArea 1 under Spawn_TR TDM/ARZones (+ capture cluster)"),
        [("Perimeter", MatchTeam.Ct)] = new(
            12.000000f, 0.000000f, 13.000000f,
            0.000000f, 0.608761f, 0.000000f, 0.793353f,
            "asset SpawnArea 1 under Spawn_CT TDM/ARZones"),
        [("Prison", MatchTeam.Tr)] = new(
            -5.500000f, 0.450000f, -30.600001f,
            0.000000f, 0.000000f, 0.000000f, 1.000000f,
            "asset median of mode zones"),
        [("Prison", MatchTeam.Ct)] = new(
            -3.000000f, 0.450000f, 31.950001f,
            0.000000f, 1.000000f, 0.000000f, 0.000000f,
            "asset median of mode zones"),
        [("Province", MatchTeam.Tr)] = new(
            10.889999f, 12.180000f, 38.790001f,
            -0.000000f, 0.995905f, 0.000000f, -0.090409f,
            "asset median of mode zones"),
        [("Province", MatchTeam.Ct)] = new(
            1.959999f, 13.560000f, -51.399996f,
            0.000000f, 0.000000f, 0.000000f, 1.000000f,
            "asset median of mode zones"),
        [("Rust", MatchTeam.Tr)] = new(
            22.109999f, 1.520000f, -13.449999f,
            0.000000f, -0.702690f, 0.000000f, 0.711496f,
            "asset median of mode zones"),
        [("Rust", MatchTeam.Ct)] = new(
            -36.355503f, 0.620000f, 19.934001f,
            0.000000f, 0.872236f, 0.000000f, 0.489085f,
            "asset median of mode zones"),
        [("Sandstone", MatchTeam.Tr)] = new(
            -15.273858f, 0.033578f, 10.161121f,
            0.000000f, 0.707107f, 0.000000f, 0.707107f,
            "gold CWO (Sandstone defuse/allies share observed)"),
        [("Sandstone", MatchTeam.Ct)] = new(
            34.326023f, 2.093881f, 0.083107f,
            0.000000f, -0.707107f, 0.000000f, 0.707107f,
            "gold CWO"),
        [("Sandyards", MatchTeam.Tr)] = new(
            -25.084999f, 1.648500f, -9.800000f,
            0.000000f, 0.837076f, 0.000000f, 0.547086f,
            "asset median of mode zones"),
        [("Sandyards", MatchTeam.Ct)] = new(
            21.493499f, 4.179998f, -7.765000f,
            0.000000f, 1.000000f, 0.000000f, 0.000000f,
            "asset median of mode zones"),
    };
}
