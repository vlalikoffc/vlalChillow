using System.Buffers.Binary;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    // Prep bomber assign / spawn-wait extension.
    private void TryAssignBomberOnTrJoin(MatchRoom room, byte actorNr)
    {
        int bomberId;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return;
            if (room.Flow.BomberActorNr > 0)
                return;
            if (GetActorTeam(room, actorNr) != MatchTeam.Tr)
                return;
            room.Flow.BomberActorNr = actorNr;
            bomberId = actorNr;
        }

        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
        ]);
        Console.WriteLine(
            $"[match-host] match-flow: bomberId={bomberId} (Tr joined during Prep, was 0)");
    }

    /// <summary>
    /// Fighter on Tr/Ct picked team but has not TX CreateWorldObject yet — defer Live once.
    /// </summary>
    private bool TryExtendPrepForAwaitingSpawn(MatchRoom room)
    {
        if (!FightersAwaitingSpawn(room))
            return false; // includes dead fighters — corpses must not extend prep

        double deadline;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.PurchasePhase)
                return false;
            if (room.Flow.PrepSpawnExtensionUsed)
                return false;
            room.Flow.PrepSpawnExtensionUsed = true;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            // Re-pick bomber if still 0 but a living T is now on roster.
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                    room.Flow.BomberActorNr = pick;
            }
        }

        var nowSec = ServerTimeSeconds();
        deadline = nowSec + 5.0;
        var props = new List<(string Key, LobbyVariant Value)>
        {
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        };
        lock (_roomGate)
        {
            if (room.Flow.BomberActorNr > 0)
                props.Add((MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(room.Flow.BomberActorNr)));
        }
        BroadcastRoomProps(room, props);
        Console.WriteLine(
            "[match-host] match-flow: Prep extended 5s — living fighter awaiting spawn " +
            $"(client Time refreshed; bomberId={(room.Flow.BomberActorNr)})");
        return true;
    }

    private bool FightersAwaitingSpawn(MatchRoom room)
    {
        lock (_roomGate)
        {
            return MatchFlowRules.FightersAwaitingSpawn(
                room.Flow,
                room.ActorProps,
                room.LivingPawns.Values.Select(p => p.OwnerActorNr));
        }
    }

    private static int PickBomberActorNr(MatchRoom room)
    {
        var candidates = new List<int>();
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            if ((MatchTeam)value.Byte != MatchTeam.Tr)
                continue;
            if (MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, (byte)actor))
                continue;
            candidates.Add(actor);
        }
        if (candidates.Count == 0)
            return 0;
        return candidates[Random.Shared.Next(candidates.Count)];
    }

    private void ClearFighterDeathFlags(MatchRoom room)
    {
        List<byte> actors;
        lock (_roomGate)
        {
            actors = room.Actors
                .Select(a => a.Nr)
                .Where(nr => nr != MatchHostActor.ActorNr)
                .ToList();
            foreach (var nr in actors)
                room.ActorProps[(nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
        }
        foreach (var nr in actors)
        {
            var pkt = MatchCodec.BuildSetProperty(
                NextServerTime(), nr, MatchRoomPropKeys.Death, LobbyVariant.FromInt(0));
            BroadcastRoom(room, pkt, tag: "match_tx_SetProperty");
        }
    }

    /// <summary>
    /// A LivingPawn was <c>DestroyWorldObject</c>'d. This is <b>not</b> a death by itself:
    /// Warmup/PreStart/Prep clients Destroy+Create their pawn on every phase transition
    /// (respawn), and the phone host does the same (gold <c>run-20260722_100157</c>: Destroy id
    /// then Create id, no round end). Combat death is the actor <c>death</c> SetProperty
    /// (<c>run-20260722_105939</c> line 896 <c>death=1</c> right after a real kill).
    /// <para>
    /// Root cause of the false RoundEnd at round=0 (b0b0918): Destroy was routed straight to
    /// <see cref="NoteActorDeath"/> and PreStart/Prep are in <see cref="MatchFlowRules.AllowsWipeCheck"/>,
    /// so the phase-transition respawn Destroy wiped T before round 1 even started.
    /// </para>
    /// Only in RoundLive/BombPlanted do we arm a short grace: if no respawn CWO arrives for this
    /// owner within <see cref="MatchFlowTestParams.DestroyDeathGrace"/> (and no <c>death</c> prop
    /// already resolved it), <see cref="ProcessPendingCombatDestroys"/> counts it as an elimination.
    /// </summary>
}
