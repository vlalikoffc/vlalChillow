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
    /// Snapshots + plant Rpc are the same packets it would have received piecemeal: a held
    /// <c>WorldObjectState</c> is superseded by the pawn snapshot, a held plant by
    /// <see cref="SyncBombPlantStateToPeer"/>. Nothing is invented — all three already exist for
    /// late joiners.
    /// </remarks>
    private void OpenWorldGate(NetPeer peer, MatchPeerState st, string reason)
    {
        st.WorldReady = true;
        var room = st.Room;
        Console.WriteLine(
            $"[match-host] world gate open actor={st.ActorNr} peer={peer.Address}:{peer.Port} " +
            $"reason={reason}" +
            (room is null ? " (no room — nothing to resync)" : " — resync props+pawns+plant"));
        if (room is null)
            return;

        SendMatchStateSnapshotToPeer(peer, room, excludeActorNr: st.ActorNr);
        SendLivingPawnSnapshotToPeer(peer, room, excludeOwnerActorNr: st.ActorNr);
        SyncBombPlantStateToPeer(peer, room);
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
