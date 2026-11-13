using LiteNetLib;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Right after a bootstrap: hold relayed world traffic for this peer until it proves its
    /// scene is up (or <see cref="MatchWorldGate.HoldMs"/> passes). See <see cref="MatchWorldGate"/>.
    /// </summary>
    private static void HoldWorldTrafficForNewScene(MatchPeerState st) =>
        st.HoldWorldTraffic(MatchWorldGate.HoldMs);

    /// <summary>
    /// A world-level packet from the peer means its managers/objects exist — open the gate and
    /// hand it the state it missed while it was held.
    /// </summary>
    private void NoteClientProvedWorld(NetPeer peer, MatchOpcode opcode)
    {
        if (!MatchWorldGate.ProvesWorldReady(opcode))
            return;
        if (!_peers.TryGetValue(peer, out var st))
            return;
        if (!MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
            return;
        OpenWorldGate(peer, st, $"client-world:{MatchCodec.OpcodeName(opcode)}");
    }

    /// <summary>Open the gate and push the aggregate state this peer was held back from.</summary>
    /// <remarks>
    /// Issue #40: the world snapshot is claimed exactly once per bootstrap. Bootstrap itself
    /// sends only room/actor properties; CWO/Rpc objects wait here until the scene is proven.
    /// Old reconnect pawn ids are destroyed for this peer before any replacement snapshot, so
    /// a retained client scene cannot keep the previous body as a local ghost.
    /// </remarks>
    private void OpenWorldGate(NetPeer peer, MatchPeerState st, string reason)
    {
        st.WorldReady = true;
        var room = st.Room;
        Console.WriteLine(
            $"[match-host] world gate open actor={st.ActorNr} peer={peer.Address}:{peer.Port} " +
            $"reason={reason}" +
            (room is null ? " (no room — nothing to resync)" : " — resync props+world"));
        if (room is null)
            return;

        SendMatchStateSnapshotToPeer(
            peer, room, excludeActorNr: st.ReconnectIdentityTrusted ? (byte?)null : st.ActorNr);
        TryDeliverWorldSnapshot(peer, st, room, reason);
    }

    /// <summary>
    /// Deliver the one owed aggregate world snapshot: reconnect cleanup Destroys, tracked round
    /// entities, living pawns, then the planted-bomb Rpc. Latched so gate-open, hold-expiry and
    /// restore paths cannot double-create the same objects in a retained client scene.
    /// </summary>
    private void TryDeliverWorldSnapshot(
        NetPeer peer, MatchPeerState st, MatchRoom room, string reason)
    {
        List<short> cleanupIds;
        lock (_roomGate)
        {
            if (!MatchWorldGate.TryClaimSnapshot(st))
                return;
            cleanupIds = MatchWorldGate.ClaimCleanupIds(st, room, st.ActorNr);
        }

        foreach (var id in cleanupIds)
        {
            var destroy = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            SendAndDump(peer, destroy);
            Console.WriteLine(
                $"[match-host] TX reconnect cleanup DestroyWorldObject id={id} → actor={st.ActorNr} " +
                $"(old body removed for returning peer, reason={reason})");
        }

        SendTrackedRoundEntitySnapshotToPeer(peer, room);
        SendLivingPawnSnapshotToPeer(peer, room, excludeOwnerActorNr: st.ActorNr);
        var suppressPlant = st.SuppressPlantSyncOnce;
        st.SuppressPlantSyncOnce = false;
        if (!suppressPlant)
            SyncBombPlantStateToPeer(peer, room);
        Console.WriteLine(
            $"[match-host] world snapshot delivered actor={st.ActorNr} reason={reason} " +
            $"cleanup={cleanupIds.Count} plantSync={(suppressPlant ? "suppressed(planter)" : "ok")} " +
            "(once per bootstrap)");
    }

    /// <summary>Force-open gates whose hold window expired (silent/stuck peer).</summary>
    private void TickWorldReadyGates()
    {
        var now = Environment.TickCount64;
        foreach (var (peer, st) in _peers)
        {
            if (!MatchWorldGate.HoldsTraffic(st.BootstrapSent, st.WorldReady))
                continue;
            if (!MatchWorldGate.DeadlineExpired(st.WorldGateUntilTick, now))
                continue;
            OpenWorldGate(peer, st, "hold-window-expired");
        }
    }
}
