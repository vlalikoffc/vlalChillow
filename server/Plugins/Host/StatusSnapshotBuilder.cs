using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.Server.Plugins;

namespace StandChillow.LanServer.Plugins;

/// <summary>
/// Builds <see cref="PluginStatusSnapshot"/> from lobby + match display state
/// (tgbot <c>standchillow.status.v1</c>). Read-only; off the match poll path.
/// </summary>
internal static class StatusSnapshotBuilder
{
    public static PluginStatusSnapshot Build(GameNetHost game, DateTime? matchStartedUtc)
    {
        var now = DateTime.UtcNow;
        var session = game.Session;
        var modeId = session.GameModeId;
        var map = session.SelectedLevels.Count > 0
            ? session.SelectedLevels[0]
            : LobbyPropKeys.DefaultSelectedLevel;
        var display = GameModeCatalog.TryResolveMode(modeId, out var mi)
            ? mi.DisplayName
            : modeId;

        var snap = new PluginStatusSnapshot
        {
            Schema = "standchillow.status.v1",
            TsUtc = now.ToString("o"),
            Running = true,
            ServerName = session.LobbyName,
            LobbyId = session.LobbyId,
            MatchStarted = game.MatchStarted,
            AdvertiseIp = game.AdvertiseLanIp.ToString(),
            Ports = new PluginPortsSnapshot
            {
                Discovery = LanDiscoveryHost.DiscoveryPort,
                Lobby = game.Port,
                Match = GameMatchHost.DefaultMatchPort,
            },
            Mode = new PluginModeSnapshot { Id = modeId, Display = display },
            Map = map,
            LobbyRoster = BuildLobbyRoster(session),
            Teams = EmptyTeams(),
            Scores = new Dictionary<string, int> { ["tr"] = 0, ["ct"] = 0 },
        };

        if (matchStartedUtc is { } started)
        {
            snap.MatchStartedTs = new DateTimeOffset(started).ToUnixTimeSeconds()
                + started.Millisecond / 1000.0;
            snap.MatchDurationSec = Math.Max(0, (now - started).TotalSeconds);
        }

        if (!game.MatchStarted)
            return snap;

        var match = game.Match.SnapshotActiveMatch();
        if (match is null)
            return snap;

        snap.Mode = new PluginModeSnapshot
        {
            Id = match.GameModeId,
            Display = GameModeCatalog.TryResolveMode(match.GameModeId, out var mm)
                ? mm.DisplayName
                : match.GameModeId,
        };
        snap.Map = match.Map;
        snap.Phase = MapPhase(match.Phase);
        snap.Round = match.Round;
        snap.Scores = new Dictionary<string, int>
        {
            ["tr"] = match.ScoreTr,
            ["ct"] = match.ScoreCt,
        };
        snap.Teams = BuildTeams(match);
        return snap;
    }

    private static List<PluginLobbyRosterEntry> BuildLobbyRoster(LobbySession session)
    {
        var list = new List<PluginLobbyRosterEntry>();
        foreach (var (name, status) in session.SnapshotRoster())
        {
            list.Add(new PluginLobbyRosterEntry
            {
                Name = name,
                Status = status == PlayerLobbyStatus.InMatch ? "in_match" : "lobby",
            });
        }
        return list;
    }

    private static Dictionary<string, PluginTeamSnapshot> EmptyTeams() => new()
    {
        ["tr"] = new PluginTeamSnapshot { Name = "ATTACK", Score = 0 },
        ["ct"] = new PluginTeamSnapshot { Name = "DEFENSE", Score = 0 },
        ["spectator"] = new PluginTeamSnapshot { Name = "SPECTATOR", Score = 0 },
    };

    private static Dictionary<string, PluginTeamSnapshot> BuildTeams(MatchSnapshot match)
    {
        var teams = EmptyTeams();
        teams["tr"].Score = match.ScoreTr;
        teams["ct"].Score = match.ScoreCt;

        foreach (var a in match.Actors)
        {
            var key = TeamKey(a.Team);
            if (!teams.TryGetValue(key, out var block))
                continue;
            block.Players.Add(new PluginPlayerSnapshot
            {
                Nr = a.Nr,
                Name = a.Name,
                Team = key,
                Kills = a.Kills,
                Assists = a.Assists,
                Score = a.Score,
                Money = a.Money,
                Dead = a.DeadThisRound,
                Ping = a.Ping,
                IsHost = a.IsHost,
            });
        }
        return teams;
    }

    private static string TeamKey(MatchTeam team) => team switch
    {
        MatchTeam.Tr => "tr",
        MatchTeam.Ct => "ct",
        MatchTeam.Spectator => "spectator",
        _ => "spectator",
    };

    /// <summary>Map host phases to tgbot PHASE_DISPLAY keys.</summary>
    public static string MapPhase(MatchFlowPhase phase) => phase switch
    {
        MatchFlowPhase.WaitingPlayers => "Lobby",
        MatchFlowPhase.Warmup or MatchFlowPhase.WarmupWillFinish
            or MatchFlowPhase.DeathMatchWarmup => "PreStart",
        MatchFlowPhase.PurchasePhase => "Prep",
        MatchFlowPhase.RoundLive or MatchFlowPhase.DeathMatchLive => "RoundLive",
        MatchFlowPhase.BombPlanted => "BombPlanted",
        MatchFlowPhase.RoundEndPause => "RoundEnd",
        MatchFlowPhase.MatchOver or MatchFlowPhase.DeathMatchEnded
            or MatchFlowPhase.DeathMatchFinalHud => "MatchEnd",
        _ => phase.ToString(),
    };

    public static string TeamKeyPublic(MatchTeam team) => TeamKey(team);
}
