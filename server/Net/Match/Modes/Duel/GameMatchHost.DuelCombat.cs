using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Duel-only combat-death authority. Other modes keep their own paths untouched.
/// Scored rounds (C2=31) use the shared bomb-mode path
/// (<see cref="NoteBombModeKillByKiller"/> → Destroy + <c>death=1</c> → wipe).
/// C2=11 FFA PreWarmup matches phone gold (<c>MATCH_DUEL_PROBE.md</c> / <c>20260724_041*</c>):
/// Destroy + cumulative <c>death</c>++ then client <c>CreateWorldObject</c> respawn — no wipe,
/// no room TrScore/CtScore, no RadarManager (Duel scene omits Radar).
/// WaitingPlayers: field=5 still relays (warmup kills); no master death authorship.
/// </summary>
public sealed partial class GameMatchHost
{
    /// <summary>
    /// Shared kills++ entry: Duel FFA → <see cref="NoteDuelFfaKillByKiller"/>; otherwise
    /// identical to pre-Duel routing (TDM → <see cref="NoteDeathMatchKillByKiller"/>,
    /// bomb/Allies/Escalation → <see cref="NoteBombModeKillByKiller"/>).
    /// </summary>
    private void RouteConfirmedKillByKiller(MatchRoom room, byte killerActorNr)
    {
        if (IsDeathMatchRoom(room))
        {
            NoteDeathMatchKillByKiller(room, killerActorNr);
            return;
        }

        if (IsDuelRoom(room) && IsDuelFfaPhase(room))
        {
            NoteDuelFfaKillByKiller(room, killerActorNr);
            return;
        }

        NoteBombModeKillByKiller(room, killerActorNr);
    }

    private bool IsDuelFfaPhase(MatchRoom room)
    {
        lock (_roomGate)
            return IsDuelRoom(room) && room.Flow.Phase == MatchFlowPhase.AlliesPreWarmup;
    }

    /// <summary>
    /// Duel FFA (C2=11): client <c>death</c> props must not run Ranked
    /// <see cref="NoteActorDeath"/> (that path ignores PreWarmup). Note DeadActors only;
    /// master authorship stays on kills++ → <see cref="AuthorDuelFfaDeath"/>.
    /// Non-Duel / scored Duel: return false so shared NoteActorDeath runs.
    /// </summary>
    private bool TryHandleDuelDeathProp(
        MatchRoom room, byte actorNr, LobbyVariant value, string source)
    {
        if (!IsDuelRoom(room))
            return false;
        if (!IsDuelFfaPhase(room))
            return false;

        if (VariantAsInt(value) != 0)
        {
            lock (_roomGate)
                room.Flow.DeadActors.Add(actorNr);
            Console.WriteLine(
                $"[duel-ffa] death prop actor={actorNr} value={value} src={source} " +
                "(DeadActors noted; skip Ranked NoteActorDeath)");
        }
        return true;
    }

    /// <summary>
    /// Duel-only field=5 attribution. WaitingPlayers: no attribution (relay stays in
    /// WorldObjects so lobby/warmup kills work). FFA C2=11 and scored wipe phases: arm
    /// <c>LastCombatDamage</c> for kills++ → master Destroy+death.
    /// Does not use cumulative <c>death</c> as "already dead" (would block post-respawn FFA).
    /// </summary>
    private void NoteDuelPawnDamage(
        MatchRoom room, byte attackerActorNr, short victimPawnId, ReadOnlySpan<byte> damagePayload = default)
    {
        if (!IsDuelRoom(room))
            return;
        if (attackerActorNr == 0 || attackerActorNr == MatchHostActor.ActorNr)
            return;

        byte victim;
        MatchTeam at, vt;
        var weaponId = damagePayload.IsEmpty
            ? (byte)0
            : MatchEconomy.TryPeekDamageWeaponId(damagePayload);
        lock (_roomGate)
        {
            var phase = room.Flow.Phase;
            var duelFfa = phase == MatchFlowPhase.AlliesPreWarmup;
            var combatOk = duelFfa || MatchFlowRules.AllowsWipeCheck(phase);
            if (!combatOk)
            {
                Console.WriteLine(
                    $"[duel-combat] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    $"IGNORED for attribution — phase={phase} (relay continues; WaitingPlayers OK)");
                return;
            }

            if (!room.LivingPawns.TryGetValue(victimPawnId, out var pawn))
            {
                Console.WriteLine(
                    $"[duel-combat] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    "IGNORED — pawn id not in LivingPawns");
                return;
            }

            victim = pawn.OwnerActorNr;
            if (victim == 0 || victim == attackerActorNr || victim == MatchHostActor.ActorNr)
            {
                Console.WriteLine(
                    $"[duel-combat] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    $"IGNORED — victimOwner={victim} (self/host/unowned)");
                return;
            }

            if (room.Flow.DeadActors.Contains(victim))
            {
                Console.WriteLine(
                    $"[duel-combat] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    $"IGNORED — victim={victim} in DeadActors (await respawn CWO)");
                return;
            }

            at = GetActorTeam(room, attackerActorNr);
            vt = GetActorTeam(room, victim);
            if (at is not (MatchTeam.Tr or MatchTeam.Ct)
                || vt is not (MatchTeam.Tr or MatchTeam.Ct)
                || at == vt)
            {
                Console.WriteLine(
                    $"[duel-combat] damage field=5 attacker={attackerActorNr}/{at} victim={victim}/{vt} " +
                    "IGNORED — self/friendly/spectator (no attribution)");
                return;
            }

            room.LastCombatDamage[attackerActorNr] = (victim, DateTime.UtcNow, weaponId);
        }

        Console.WriteLine(
            $"[duel-combat] damage field=5 attribution: attacker={attackerActorNr}/{at} → " +
            $"victim={victim}/{vt} (pawn={victimPawnId} weapon={weaponId}); armed for next kills++ within " +
            $"{DeathMatchFlowParams.KillAttributionWindow.TotalSeconds:0.#}s");
    }

    /// <summary>
    /// Duel C2=11 FFA: killer <c>kills</c>++ → master Destroy + cumulative <c>death</c>++
    /// (gold Destroy → kills/round_kills/score → death → ~1.5s CreateWorldObject respawn).
    /// </summary>
    private void NoteDuelFfaKillByKiller(MatchRoom room, byte killerActorNr)
    {
        if (!IsDuelRoom(room))
            return;

        byte victim = 0;
        string how = "damage-attribution";
        bool alreadyAuthored = false;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.AlliesPreWarmup)
            {
                room.LastCombatDamage.Remove(killerActorNr);
                Console.WriteLine(
                    $"[duel-ffa] kills++ killer={killerActorNr} IGNORED — phase={room.Flow.Phase}");
                return;
            }

            if (room.LastCombatDamage.TryGetValue(killerActorNr, out var dmg))
            {
                var age = DateTime.UtcNow - dmg.When;
                if (age <= DeathMatchFlowParams.KillAttributionWindow)
                    victim = dmg.Victim;
                else
                {
                    Console.WriteLine(
                        $"[duel-ffa] kills++ killer={killerActorNr}: last damage victim={dmg.Victim} " +
                        $"STALE ({age.TotalSeconds:0.#}s)");
                }
            }
            room.LastCombatDamage.Remove(killerActorNr);

            if (victim == 0)
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
                else
                {
                    Console.WriteLine(
                        $"[duel-ffa] kills++ killer={killerActorNr}/{killerTeam}: no attribution and " +
                        $"{enemies.Count} living enemies — cannot resolve victim");
                }
            }

            if (victim != 0
                && room.LastAuthoredDeath.TryGetValue(victim, out var last)
                && DateTime.UtcNow - last < DeathMatchFlowParams.DeathDedupWindow)
                alreadyAuthored = true;
        }

        if (victim == 0)
            return;
        if (alreadyAuthored)
        {
            Console.WriteLine(
                $"[duel-ffa] kills++ killer={killerActorNr} → victim={victim} via {how} " +
                "CONFIRMED-KILLER (already authored)");
            return;
        }

        Console.WriteLine(
            $"[duel-ffa] kills++ killer={killerActorNr} → victim={victim} via {how} " +
            "→ AuthorDuelFfaDeath");
        AuthorDuelFfaDeath(room, victim, killerActorNr, source: $"kills/{how}");
    }

    /// <summary>
    /// Master-author Duel FFA death: Destroy victim pawn(s) to all peers + cumulative
    /// <c>death</c>++. No RadarManager (absent on Duel wire). Client respawns via CWO.
    /// </summary>
    private void AuthorDuelFfaDeath(
        MatchRoom room, byte victimActorNr, byte? killerActorNr, string source)
    {
        if (!IsDuelRoom(room))
            return;

        var killerLabel = killerActorNr is { } kk ? kk.ToString() : "?";
        Console.WriteLine(
            $"[duel-ffa] AuthorDuelFfaDeath ENTER victim={victimActorNr} killer={killerLabel} " +
            $"src={source}");

        if (victimActorNr == 0 || victimActorNr == MatchHostActor.ActorNr)
            return;

        List<short> victimPawnIds;
        int newDeath;
        MatchTeam victimTeam;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.AlliesPreWarmup)
            {
                Console.WriteLine(
                    $"[duel-ffa] AuthorDuelFfaDeath SKIP — phase={room.Flow.Phase}");
                return;
            }

            var now = DateTime.UtcNow;
            if (room.LastAuthoredDeath.TryGetValue(victimActorNr, out var last)
                && now - last < DeathMatchFlowParams.DeathDedupWindow)
            {
                Console.WriteLine(
                    $"[duel-ffa] AuthorDuelFfaDeath SKIP — de-dup victim={victimActorNr}");
                return;
            }

            victimTeam = GetActorTeam(room, victimActorNr);
            if (victimTeam is not (MatchTeam.Tr or MatchTeam.Ct))
            {
                Console.WriteLine(
                    $"[duel-ffa] AuthorDuelFfaDeath SKIP — victim={victimActorNr} team={victimTeam}");
                return;
            }

            room.LastAuthoredDeath[victimActorNr] = now;
            room.Flow.DeadActors.Add(victimActorNr);
            room.Flow.PendingCombatDestroy.Remove(victimActorNr);

            victimPawnIds = room.LivingPawns
                .Where(kv => kv.Value.OwnerActorNr == victimActorNr)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in victimPawnIds)
                room.LivingPawns.Remove(id);

            var prev = ReadActorIntProp(room, victimActorNr, MatchRoomPropKeys.Death);
            newDeath = (prev < 0 ? 0 : prev) + 1;
            room.ActorProps[(victimActorNr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(newDeath);
            if (killerActorNr is byte killer)
                room.LastCombatDamage.Remove(killer);
        }

        foreach (var id in victimPawnIds)
        {
            var destroyPkt = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var nD = BroadcastInitReady(room, destroyPkt, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[duel-ffa] master death → TX DestroyWorldObject id={id} " +
                $"victim={victimActorNr} → peers={nD}");
        }

        var deathPkt = MatchCodec.BuildSetProperty(
            NextServerTime(), victimActorNr, MatchRoomPropKeys.Death, LobbyVariant.FromInt(newDeath));
        BroadcastRoom(room, deathPkt, tag: "match_tx_SetProperty");

        Console.WriteLine(
            $"[duel-ffa] master death authored victim={victimActorNr}/{victimTeam} " +
            $"death={newDeath} killer={killerLabel} src={source} " +
            "(death HUD + FFA respawn client-driven; no wipe)");
        Dashboard.DashboardHub.PostDeath(
            victimActorNr, ResolveActorName(victimActorNr), victimTeam, source);
        var name = ResolveActorName(victimActorNr);
        PostServerDebugChat(
            string.IsNullOrEmpty(name)
                ? $"FFA убит actor={victimActorNr} ({DebugTeamTag(victimTeam)})"
                : $"{name} ({DebugTeamTag(victimTeam)}) FFA убит");
    }
}
