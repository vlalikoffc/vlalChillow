namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Who gets a BombManager plant <c>WorldObjectRpc</c>, and whether the planter's own packet is
/// still relayed afterwards. Kept free of sockets so the delivery rule is regression-tested.
/// </summary>
/// <remarks>
/// A live session (latest-2.log, round 2) showed the same plant reaching every peer <b>twice</b>:
/// the host-rebuilt fan-out (<c>plant fan-out … → peers=3</c>) and then, on the same tick, the
/// exceptSender relay of the planter's original Rpc (<c>TX relay WorldObjectRpc id=8 rpc=2 →
/// peers=2</c>). The second copy is applied to a client that already holds the bomb, and the
/// phone answers with «Invalid bomber» inside <c>BombManager</c> (planter stack, 2026-11-13),
/// which kills the rest of that WorldObjectRpc batch.
/// <para>
/// The fan-out used to be droppable, so the relay was a safety net («peers often miss the
/// host-rebuilt Rpc»). It now goes through <c>BroadcastInitReadyCritical</c>, which never drops,
/// so exactly one copy per peer is the correct delivery: fan-out to everyone except the planter
/// (<c>gaa=AllCached</c> — the planter already executed it locally), no extra relay.
/// </para>
/// </remarks>
internal static class MatchBombRpcDelivery
{
    /// <summary>
    /// Should this peer receive the host-rebuilt plant Rpc? INIT-ready peers only, and never the
    /// planter — it applied the plant locally before the host ever saw the packet.
    /// </summary>
    public static bool ShouldSendPlantRpc(bool bootstrapSent, bool isPlanter) =>
        bootstrapSent && !isPlanter;

    /// <summary>
    /// Must the planter's original Rpc still be relayed to the other peers? Only when the host
    /// did <b>not</b> fan the plant out itself (classic bomb modes); for the Allies family the
    /// fan-out is the single delivery.
    /// </summary>
    public static bool SuppressOriginalRelay(bool alliesFamily, bool plantAccepted) =>
        alliesFamily && plantAccepted;
}
