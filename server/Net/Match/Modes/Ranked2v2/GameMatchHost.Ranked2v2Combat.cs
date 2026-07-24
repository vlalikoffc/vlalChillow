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
        bool bombPlanted;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            bombPlanted = room.Flow.BombPlanted;
            var alliesCombat = IsAlliesRoom(room);
            if (!MatchFlowRules.DestroyMayBeCombatDeath(phase, bombPlanted, alliesCombat))
            {
                // Phase-transition respawn window — Destroy is not a kill; drop any stale arm.
                room.Flow.PendingCombatDestroy.Remove(owner);
                return;
            }
            room.Flow.PendingCombatDestroy[owner] = DateTime.UtcNow;
        }
        var dmTag = IsDeathMatchRoom(room)
            ? "[tdm-death] client-relayed Destroy path: "
            : "";
        Console.WriteLine(
            $"[match-host] match-flow: {dmTag}pawn Destroy owner={owner} in {phase} — armed " +
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
            if (!MatchFlowRules.DestroyMayBeCombatDeath(
                    room.Flow.Phase, room.Flow.BombPlanted, IsAlliesRoom(room)))
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
            phase = room.Flow.Phase;
            team = GetActorTeam(room, actorNr);
            bombPlanted = room.Flow.BombPlanted;
        }

        // DeathMatch/TDM: team kill, not round wipe — handle before Ranked PreStart/Prep gate.
        if (IsDeathMatchRoom(room))
        {
            Console.WriteLine(
                $"[match-host] match-flow: death actor={actorNr} team={team} via {source}");
            var dmName = ResolveActorName(actorNr);
            PostServerDebugChat(
                string.IsNullOrEmpty(dmName)
                    ? $"убит actor={actorNr} ({DebugTeamTag(team)})"
                    : $"{dmName} ({DebugTeamTag(team)}) убит");
            AuthorDeathMatchDeath(room, victimActorNr: actorNr, killerActorNr: null, source: source);
            return;
        }

        // Proven combat death (death prop / kills++ authored) arms DeadActors in Live,
        // BombPlanted, Prep, and PreStart — wipe must preempt remaining phase timers.
        // Bare Destroy during Prep is still a respawn (DestroyMayBeCombatDeath).
        if (!MatchFlowRules.AllowsWipeCheck(phase))
        {
            Console.WriteLine(
                $"[observe] death actor={actorNr} via {source} " +
                $"IGNORED for wipe (phase={phase}; DeadActors not armed)");
            return;
        }

        lock (_roomGate)
        {
            if (!room.Flow.DeadActors.Add(actorNr))
                return; // already counted
        }

        Console.WriteLine(
            $"[observe] death actor={actorNr} team={team} via {source} " +
            "(DeadActors armed → same-tick wipe check)");

        Dashboard.DashboardHub.PostDeath(actorNr, ResolveActorName(actorNr), team, source);

        var name = ResolveActorName(actorNr);
        PostServerDebugChat(
            string.IsNullOrEmpty(name)
                ? $"убит actor={actorNr} ({DebugTeamTag(team)})"
                : $"{name} ({DebugTeamTag(team)}) убит");

        TryResolveWipeImmediate(room, bombPlanted);
    }

    private static void LogWipeImmediate(MatchTeam winner, string reason)
    {
        var side = reason == "wipe-tr" ? "T" : "CT";
        var winSide = winner == MatchTeam.Tr ? "T" : "CT";
        Console.WriteLine(
            $"[observe] wipe {side} → {winSide} win (immediate RoundEnd; events > round clock)");
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
        PostServerDebugChat(
            reason == "wipe-tr" ? "вайп T → CT" : "вайп CT → T");
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

    /// <summary>
    /// Escalation / Ranked / Allies: killer <c>kills</c>++ is a confirmed-elimination <b>input</b>
    /// to one master FSM (alongside the victim <c>death</c> prop). Dedicated host is room master —
    /// without master-authored Destroy+death when the victim is still alive, peers desync.
    /// If wipe/death already armed the victim → confirm killer + clear attribution only
    /// (no ORPHAN, no second Destroy). TDM uses <see cref="NoteDeathMatchKillByKiller"/>.
    /// WaitingPlayers: leave combat/kills relay alone (clients may still kill in lobby).
    /// </summary>
    private void NoteBombModeKillByKiller(MatchRoom room, byte killerActorNr)
    {
        byte victim = 0;
        byte weaponId = 0;
        string how = "damage-attribution";
        MatchFlowPhase phase;
        bool alreadyDead = false;
        bool victimAliveInLivingPawns = false;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;

            if (room.LastCombatDamage.TryGetValue(killerActorNr, out var dmg))
            {
                var age = DateTime.UtcNow - dmg.When;
                if (age <= DeathMatchFlowParams.KillAttributionWindow)
                {
                    victim = dmg.Victim;
                    weaponId = dmg.WeaponId;
                }
                else
                {
                    Console.WriteLine(
                        $"[combat-death] kills++ killer={killerActorNr}: last damage victim={dmg.Victim} " +
                        $"is STALE ({age.TotalSeconds:0.#}s > " +
                        $"{DeathMatchFlowParams.KillAttributionWindow.TotalSeconds:0.#}s window)");
                }
            }

            if (victim == 0 && MatchFlowRules.AllowsWipeCheck(phase))
            {
                var killerTeam = GetActorTeam(room, killerActorNr);
                var enemies = room.LivingPawns.Values
                    .Select(p => p.OwnerActorNr)
                    .Where(o => o != 0 && o != MatchHostActor.ActorNr && o != killerActorNr)
                    .Distinct()
                    .Where(o =>
                    {
                        var t = GetActorTeam(room, o);
                        return t is (MatchTeam.Tr or MatchTeam.Ct) && t != killerTeam;
                    })
                    .ToList();
                if (enemies.Count == 1)
                {
                    victim = enemies[0];
                    how = "single-enemy-fallback";
                }
                else if (victim == 0)
                {
                    Console.WriteLine(
                        $"[combat-death] kills++ killer={killerActorNr}/{killerTeam}: no attribution and " +
                        $"{enemies.Count} living enemy fighters — cannot resolve victim unambiguously");
                }
            }

            if (victim != 0)
            {
                alreadyDead = MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, victim);
                victimAliveInLivingPawns = room.LivingPawns.Values.Any(p => p.OwnerActorNr == victim);
            }

            // Confirmed-killer / resolve path always clears this killer's attribution slot.
            room.LastCombatDamage.Remove(killerActorNr);
        }

        if (victim == 0)
        {
            // Truly unknown victim — clear was already done; ORPHAN only when we cannot confirm.
            if (!MatchFlowRules.AllowsWipeCheck(phase))
            {
                Console.WriteLine(
                    $"[observe] kills++ killer={killerActorNr} ORPHAN — phase={phase} " +
                    "(no victim resolved; attribution cleared)");
            }
            else
            {
                Console.WriteLine(
                    $"[combat-death] killer={killerActorNr} kills++ but no victim resolved " +
                    "(await Destroy-grace fallback — no master death authored yet)");
            }
            return;
        }

        // Instant kill money on confirmed elimination (alive or already-dead confirm path).
        ApplyKillEconomy(room, killerActorNr, weaponId);

        // death/wipe already owned this elimination — confirm killer only, no second Destroy.
        if (alreadyDead)
        {
            Console.WriteLine(
                $"[combat-death] kills++ killer={killerActorNr} → victim={victim} via {how} " +
                "CONFIRMED-KILLER (already dead — attribution cleared, no second Destroy)");
            return;
        }

        if (!victimAliveInLivingPawns)
        {
            Console.WriteLine(
                $"[combat-death] kills++ killer={killerActorNr} → victim={victim} via {how} " +
                "SKIP AuthorBombModeDeath (not in LivingPawns — attribution cleared; " +
                "death prop / Destroy-grace owns)");
            return;
        }

        if (!MatchFlowRules.AllowsWipeCheck(phase))
        {
            // Victim still alive outside wipe phases (e.g. WaitingPlayers): do not author master
            // death here — leave lobby combat alone (relay stays; no "block lethal until start").
            Console.WriteLine(
                $"[observe] kills++ killer={killerActorNr} → victim={victim} phase={phase} " +
                "(no master Destroy+death — non-wipe phase; attribution cleared)");
            return;
        }

        Console.WriteLine(
            $"[combat-death] kills++ killer={killerActorNr} → victim={victim} via {how} " +
            "→ AuthorBombModeDeath");
        AuthorBombModeDeath(room, victim, killerActorNr, source: $"kills/{how}");
    }

    /// <summary>
    /// Master-author bomb-mode combat death: <c>DestroyWorldObject</c> victim pawn(s) to
    /// <b>all</b> init-ready peers (incl. victim — no exceptSender), then actor
    /// <c>death=1</c> (MATCH_WORLD Live gold). Triggers wipe via <see cref="NoteActorDeath"/>.
    /// Gated: victim must still be in <c>LivingPawns</c> and not already in <c>DeadActors</c>.
    /// </summary>
    private void AuthorBombModeDeath(
        MatchRoom room, byte victimActorNr, byte? killerActorNr, string source)
    {
        var killerLabel = killerActorNr is { } kk ? kk.ToString() : "?";
        Console.WriteLine(
            $"[combat-death] AuthorBombModeDeath ENTER victim={victimActorNr} killer={killerLabel} " +
            $"src={source}");

        if (victimActorNr == 0 || victimActorNr == MatchHostActor.ActorNr)
            return;

        List<short> victimPawnIds;
        MatchTeam victimTeam;
        lock (_roomGate)
        {
            if (!MatchFlowRules.AllowsWipeCheck(room.Flow.Phase))
            {
                Console.WriteLine(
                    $"[combat-death] AuthorBombModeDeath SKIP — phase={room.Flow.Phase}");
                return;
            }

            if (room.Flow.DeadActors.Contains(victimActorNr)
                || MatchFlowRules.IsActorDead(room.Flow, room.ActorProps, victimActorNr))
            {
                Console.WriteLine(
                    $"[combat-death] AuthorBombModeDeath SKIP — victim={victimActorNr} already dead " +
                    "(confirm-killer path owns late kills++)");
                if (killerActorNr is byte k)
                    room.LastCombatDamage.Remove(k);
                return;
            }

            victimPawnIds = room.LivingPawns
                .Where(kv => kv.Value.OwnerActorNr == victimActorNr)
                .Select(kv => kv.Key)
                .ToList();
            if (victimPawnIds.Count == 0)
            {
                Console.WriteLine(
                    $"[combat-death] AuthorBombModeDeath SKIP — victim={victimActorNr} " +
                    "not in LivingPawns (no Destroy; await death prop / grace)");
                if (killerActorNr is byte k)
                    room.LastCombatDamage.Remove(k);
                return;
            }

            var now = DateTime.UtcNow;
            if (room.LastAuthoredDeath.TryGetValue(victimActorNr, out var last)
                && now - last < DeathMatchFlowParams.DeathDedupWindow)
            {
                Console.WriteLine(
                    $"[combat-death] AuthorBombModeDeath SKIP — de-dup victim={victimActorNr}");
                if (killerActorNr is byte k)
                    room.LastCombatDamage.Remove(k);
                return;
            }

            victimTeam = GetActorTeam(room, victimActorNr);
            if (victimTeam is not (MatchTeam.Tr or MatchTeam.Ct))
            {
                Console.WriteLine(
                    $"[combat-death] AuthorBombModeDeath SKIP — victim={victimActorNr} team={victimTeam}");
                return;
            }

            room.LastAuthoredDeath[victimActorNr] = now;
            foreach (var id in victimPawnIds)
                room.LivingPawns.Remove(id);

            // Round death flag (MATCH_WORLD: death=1; cleared on Prep respawn, not cumulative).
            room.ActorProps[(victimActorNr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(1);
            if (killerActorNr is byte killer)
                room.LastCombatDamage.Remove(killer);
        }

        Console.WriteLine(
            $"[combat-death] AuthorBombModeDeath PROCEED victim={victimActorNr}/{victimTeam} " +
            $"livingPawnsToDestroy=[{string.Join(",", victimPawnIds)}]");

        // Destroy to ALL peers incl. victim (BroadcastInitReady — never exceptSender on death).
        foreach (var id in victimPawnIds)
        {
            var destroyPkt = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var nD = BroadcastInitReady(room, destroyPkt, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[combat-death] master death → TX DestroyWorldObject id={id} " +
                $"victim={victimActorNr} → peers={nD}");
        }

        var deathPkt = MatchCodec.BuildSetProperty(
            NextServerTime(), victimActorNr, MatchRoomPropKeys.Death, LobbyVariant.FromInt(1));
        BroadcastRoom(room, deathPkt, tag: "match_tx_SetProperty");
        Console.WriteLine(
            $"[combat-death] master death → TX death=1 actor={victimActorNr} killer={killerLabel} " +
            $"src={source}");

        NoteActorDeath(room, victimActorNr, LobbyVariant.FromInt(1), source: source);
    }

}
