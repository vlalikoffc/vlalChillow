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
    // Death vs respawn, wipe checks.
    private void NotePawnDestroyed(MatchRoom room, byte owner)
    {
        if (owner == 0 || owner == MatchHostActor.ActorNr)
            return;

        MatchFlowPhase phase;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            if (!MatchFlowRules.DestroyMayBeCombatDeath(phase))
            {
                // Phase-transition respawn window — Destroy is not a kill; drop any stale arm.
                room.Flow.PendingCombatDestroy.Remove(owner);
                return;
            }
            room.Flow.PendingCombatDestroy[owner] = DateTime.UtcNow;
        }
        Console.WriteLine(
            $"[match-host] match-flow: pawn Destroy owner={owner} in {phase} — armed " +
            "combat-death grace (await death prop or no-respawn; not an immediate wipe)");
    }

    /// <summary>
    /// Tick step: resolve armed Live/BombPlanted pawn Destroys that never respawned and were not
    /// already covered by a <c>death</c> prop. This is the fallback for a missing death prop —
    /// the primary combat-death path is the actor <c>death</c> SetProperty. Cleared on respawn,
    /// on real death, and on any non-combat phase.
    /// </summary>
    private void ProcessPendingCombatDestroys(MatchRoom room)
    {
        List<byte> eliminated = new();
        lock (_roomGate)
        {
            if (room.Flow.PendingCombatDestroy.Count == 0)
                return;
            if (!MatchFlowRules.DestroyMayBeCombatDeath(room.Flow.Phase))
            {
                room.Flow.PendingCombatDestroy.Clear();
                return;
            }
            var now = DateTime.UtcNow;
            foreach (var owner in room.Flow.PendingCombatDestroy.Keys.ToList())
            {
                var respawned = room.LivingPawns.Values.Any(p => p.OwnerActorNr == owner);
                if (respawned || room.Flow.DeadActors.Contains(owner))
                {
                    room.Flow.PendingCombatDestroy.Remove(owner);
                    continue;
                }
                if (now - room.Flow.PendingCombatDestroy[owner] >= MatchFlowTestParams.DestroyDeathGrace)
                {
                    eliminated.Add(owner);
                    room.Flow.PendingCombatDestroy.Remove(owner);
                }
            }
        }

        foreach (var owner in eliminated)
        {
            Console.WriteLine(
                $"[match-host] match-flow: combat elimination actor={owner} " +
                "(Live pawn Destroy, no respawn within grace, no death prop) → NoteActorDeath");
            NoteActorDeath(room, owner, LobbyVariant.FromInt(1), source: "DestroyNoRespawn");
        }
    }

    private void NoteActorDeath(MatchRoom room, byte actorNr, LobbyVariant value, string source)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        var dead = value.Kind switch
        {
            LobbyVariantKind.Int => value.Int != 0,
            LobbyVariantKind.Byte => value.Byte != 0,
            LobbyVariantKind.Bool => value.Bool,
            _ => false,
        };
        if (!dead)
            return;

        MatchTeam team;
        MatchFlowPhase phase;
        bool bombPlanted;
        lock (_roomGate)
        {
            room.Flow.PendingCombatDestroy.Remove(actorNr); // real death supersedes the grace arm
            if (!room.Flow.DeadActors.Add(actorNr))
                return; // already counted
            phase = room.Flow.Phase;
            bombPlanted = room.Flow.BombPlanted;
            team = GetActorTeam(room, actorNr);
        }

        Console.WriteLine(
            $"[match-host] match-flow: death actor={actorNr} team={team} via {source}");

        if (!MatchFlowRules.AllowsWipeCheck(phase))
            return;

        TryResolveWipeImmediate(room, bombPlanted);
    }

    private static void LogWipeImmediate(MatchTeam winner, string reason)
    {
        var side = reason == "wipe-tr" ? "T" : "CT";
        var winSide = winner == MatchTeam.Tr ? "T" : "CT";
        Console.WriteLine(
            $"[match-host] match-flow: wipe {side} → {winSide} win (immediate RoundEnd)");
    }

    /// <summary>
    /// Wipe resolve + immediate RoundEnd TX — do not wait for PollLoop tick (weapon-drop
    /// floods were delaying wipe→banner under load).
    /// </summary>
    /// <returns>True when RoundEnd was entered.</returns>
    private bool TryResolveWipeImmediate(MatchRoom room, bool bombPlanted)
    {
        if (!TryResolveWipe(room, bombPlanted, out var winner, out var reason))
            return false;
        LogWipeImmediate(winner, reason);
        EnterRoundEndPause(room, winner, reason);
        return true;
    }

    private bool TryResolveWipe(
        MatchRoom room, bool bombPlanted, out MatchTeam winner, out string reason)
    {
        winner = MatchTeam.None;
        reason = "";
        lock (_roomGate)
        {
            if (!MatchFlowRules.TryResolveWipe(
                    room.Flow, room.ActorProps, bombPlanted, out winner, out reason))
                return false;

            if (reason == "wipe-tr" && (bombPlanted || room.Flow.BombPlanted))
            {
                room.Flow.PendingEndReason = null;
                room.Flow.PendingWinner = MatchTeam.None;
                winner = MatchTeam.None;
                reason = "";
                Console.WriteLine(
                    "[match-host] match-flow: wipe T with bomb planted — wait fuse/defuse");
                return false;
            }
        }

        return true;
    }

    private static MatchTeam GetActorTeam(MatchRoom room, byte actorNr) =>
        MatchFlowRules.GetActorTeam(room.ActorProps, actorNr);

}
