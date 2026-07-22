using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Dedicated match phase machine — Allies phone-host loop (Warmup → PreStart → Prep → Live → RoundEnd).
/// Extracted from GameMatchHost for clearer transitions and future mode hooks.
/// </summary>
public enum MatchFlowPhase : byte
{
    WaitingPlayers = 0,
    Warmup = 1,
    /// <summary>C2=22 short freeze countdown + bomberId assign (phone <c>cnr</c>).</summary>
    WarmupWillFinish = 2,
    PurchasePhase = 3,
    RoundLive = 4,
    BombPlanted = 5,
    RoundEndPause = 6,
    MatchOver = 7,
}

/// <summary>Per-room match clock — C2 / Time / Round / scores / wipe state.</summary>
public sealed class MatchFlowState
{
    public MatchFlowPhase Phase { get; set; } = MatchFlowPhase.WaitingPlayers;
    public DateTime PhaseEndsUtc { get; set; } = DateTime.MinValue;
    /// <summary>1-based round index while prep / live / between rounds.</summary>
    public int RoundIndex { get; set; }
    public int ScoreTr { get; set; }
    public int ScoreCt { get; set; }
    /// <summary>Consecutive round losses — phone-host <c>TrCoLosses</c>/<c>CtCoLosses</c>.</summary>
    public int CoLossesTr { get; set; }
    public int CoLossesCt { get; set; }
    public bool BombPlanted { get; set; }
    public int BomberActorNr { get; set; }
    /// <summary>Fighter actor nrs marked dead this round (<c>death</c> prop or pawn Destroy).</summary>
    public HashSet<byte> DeadActors { get; } = new();
    /// <summary>Pending round-end reason when wipe/plant fires mid-phase.</summary>
    public string? PendingEndReason { get; set; }
    public MatchTeam PendingWinner { get; set; }
    /// <summary>One 5s prep extension when a living fighter picked team but has no pawn yet.</summary>
    public bool PrepSpawnExtensionUsed { get; set; }
}

/// <summary>Shared wipe / spawn / alive-count rules for match flow.</summary>
public static class MatchFlowRules
{
    /// <summary>Phases where eliminations can end the round (prep kills count).</summary>
    public static bool AllowsWipeCheck(MatchFlowPhase phase) =>
        phase is MatchFlowPhase.WarmupWillFinish
            or MatchFlowPhase.PurchasePhase
            or MatchFlowPhase.RoundLive
            or MatchFlowPhase.BombPlanted;

    public static bool IsActorDead(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        byte actorNr)
    {
        if (flow.DeadActors.Contains(actorNr))
            return true;
        if (!actorProps.TryGetValue((actorNr, MatchRoomPropKeys.Death), out var death))
            return false;
        return death.Kind switch
        {
            LobbyVariantKind.Int => death.Int != 0,
            LobbyVariantKind.Byte => death.Byte != 0,
            LobbyVariantKind.Bool => death.Bool,
            _ => false,
        };
    }

    /// <summary>
    /// Living fighter on Tr/Ct picked team, is not dead, and has not TX CreateWorldObject this prep window.
    /// Dead corpses with no pawn must <b>not</b> trigger prep extension.
    /// </summary>
    public static bool FightersAwaitingSpawn(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        IEnumerable<byte> livingPawnOwners)
    {
        var spawnedOwners = livingPawnOwners.ToHashSet();
        foreach (var ((actor, key), value) in actorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;
            if (IsActorDead(flow, actorProps, actor))
                continue;
            if (!spawnedOwners.Contains(actor))
                return true;
        }
        return false;
    }

    public static (int AliveTr, int AliveCt, int TotalTr, int TotalCt) CountAliveFighters(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps)
    {
        var aliveTr = 0;
        var aliveCt = 0;
        var totalTr = 0;
        var totalCt = 0;
        foreach (var ((actor, key), value) in actorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team is not (MatchTeam.Tr or MatchTeam.Ct))
                continue;
            if (team == MatchTeam.Tr) totalTr++;
            else totalCt++;
            if (IsActorDead(flow, actorProps, actor))
                continue;
            if (team == MatchTeam.Tr) aliveTr++;
            else aliveCt++;
        }
        return (aliveTr, aliveCt, totalTr, totalCt);
    }

    /// <summary>
    /// Phone-host wipe rules: all CT dead → T; all T dead → CT unless bomb planted;
    /// empty team after disconnect counts as wipe.
    /// </summary>
    public static bool TryResolveWipe(
        MatchFlowState flow,
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        bool bombPlanted,
        out MatchTeam winner,
        out string reason)
    {
        winner = MatchTeam.None;
        reason = "";
        if (!AllowsWipeCheck(flow.Phase))
            return false;
        if (flow.PendingEndReason is not null)
            return false;

        var (aliveTr, aliveCt, totalTr, totalCt) = CountAliveFighters(flow, actorProps);
        var planted = bombPlanted || flow.BombPlanted;

        if (totalTr > 0 && totalCt == 0)
        {
            flow.PendingWinner = MatchTeam.Tr;
            flow.PendingEndReason = "wipe-ct";
            winner = MatchTeam.Tr;
            reason = "wipe-ct";
            return true;
        }

        if (totalCt > 0 && totalTr == 0)
        {
            if (planted)
                return false;
            flow.PendingWinner = MatchTeam.Ct;
            flow.PendingEndReason = "wipe-tr";
            winner = MatchTeam.Ct;
            reason = "wipe-tr";
            return true;
        }

        if (totalCt > 0 && aliveCt == 0)
        {
            flow.PendingWinner = MatchTeam.Tr;
            flow.PendingEndReason = "wipe-ct";
            winner = MatchTeam.Tr;
            reason = "wipe-ct";
            return true;
        }

        if (totalTr > 0 && aliveTr == 0)
        {
            if (planted)
                return false;
            flow.PendingWinner = MatchTeam.Ct;
            flow.PendingEndReason = "wipe-tr";
            winner = MatchTeam.Ct;
            reason = "wipe-tr";
            return true;
        }

        return false;
    }

    public static MatchTeam GetActorTeam(
        IReadOnlyDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        byte actorNr)
    {
        if (actorProps.TryGetValue((actorNr, MatchRoomPropKeys.Team), out var v)
            && v.Kind == LobbyVariantKind.Byte)
            return (MatchTeam)v.Byte;
        return MatchTeam.None;
    }
}
