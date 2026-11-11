namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Recipient policy for relayed WorldObjectRpc packets. Kept separate from LiteNetLib peers so
/// the gold exceptSender behaviour can be regression-tested without opening sockets.
/// </summary>
internal static class WorldObjectRpcRelayPolicy
{
    /// <summary>
    /// AllCachedViaServer (3) and AllViaServer (4) wait for the host and therefore include the
    /// original sender. All (0), Others (1), and AllCached (2) already executed locally on the
    /// sender, so its echo must be excluded (notably WeaponDropManager RPCs).
    /// </summary>
    public static bool ExcludesSender(byte gaaTarget) => gaaTarget is not (3 or 4);
}
