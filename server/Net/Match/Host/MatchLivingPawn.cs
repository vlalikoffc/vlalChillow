namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>Registered fighting pawn tracked for State ticks / Destroy relay.</summary>
internal sealed class MatchLivingPawn
{
    public short ObjectId { get; init; }
    public byte OwnerActorNr { get; init; }
    public string TypeName { get; init; } = "";
    public byte FusTag { get; set; } = MatchSceneManagers.PlayerPawnFusTag;
    public byte[] TrailingPayload { get; set; } = [];
    public float PosX { get; set; }
    public float PosY { get; set; }
    public float PosZ { get; set; }
    public uint Seq { get; set; }
    public float TickTime { get; set; } = MatchSceneManagers.PawnState.BaseTime;
    public int StateCaptures { get; set; }
    /// <summary>Owner already TX fat WorldObjectState — prefer relay, skip host thin ticks.</summary>
    public bool OwnerSendsState { get; set; }
}
