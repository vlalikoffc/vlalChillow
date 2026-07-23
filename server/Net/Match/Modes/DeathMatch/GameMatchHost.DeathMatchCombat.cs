using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// DeathMatch / TDM combat-death authority. The dedicated host is the room <b>master</b>, and in
/// this game the victim client does <b>not</b> self-author its death on the wire — gold
/// (<c>/tmp/tdm-probe.log</c>) and the dedicated <c>latest.log</c> both show the master publishing
/// the victim <c>DestroyWorldObject</c> + <c>death</c>++ + team score, while the victim only keeps
/// streaming <c>WorldObjectState</c> (frozen at 0 HP) until it receives those signals. When the
/// old host merely relayed the attacker's damage RPC and the killer's <c>kills</c>, the victim got
/// stuck at 0 HP with no death HUD and no respawn (user report). This partial re-implements the
/// master death: record attacker→victim from the pawn damage RPC (<c>field=5</c>), then on the
/// killer's confirmed <c>kills</c>++ author Destroy + <c>death</c>++ + <c>TrScore</c>/<c>CtScore</c>.
/// </summary>
public sealed partial class GameMatchHost
{
    /// <summary>
    /// Record that <paramref name="attackerActorNr"/> hit the pawn <paramref name="victimPawnId"/>
    /// with a damage RPC (<c>field=5</c>). Stores attribution for a cross-team hit on a known
    /// living pawn during TDM live <b>or</b> bomb-mode Live/BombPlanted — the kill itself is
    /// confirmed later by the killer's <c>kills</c>++ (damage is per-hit and non-lethal).
    /// Shared by DeathMatch and Escalation/Ranked master-death paths.
    /// </summary>
    private void NoteDeathMatchDamage(MatchRoom room, byte attackerActorNr, short victimPawnId)
    {
        if (attackerActorNr == 0 || attackerActorNr == MatchHostActor.ActorNr)
            return;

        byte victim;
        MatchTeam at, vt;
        lock (_roomGate)
        {
            var phase = room.Flow.Phase;
            var combatOk = phase == MatchFlowPhase.DeathMatchLive
                || MatchFlowRules.AllowsWipeCheck(phase);
            if (!combatOk)
            {
                Console.WriteLine(
                    $"[combat-death] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    $"IGNORED — phase={phase} (not Live; WaitingPlayers needs /set start)");
                return;
            }
            if (!room.LivingPawns.TryGetValue(victimPawnId, out var pawn))
            {
                Console.WriteLine(
                    $"[combat-death] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    "IGNORED — pawn id not in LivingPawns (no owner to attribute)");
                return;
            }

            victim = pawn.OwnerActorNr;
            if (victim == 0 || victim == attackerActorNr || victim == MatchHostActor.ActorNr)
            {
                Console.WriteLine(
                    $"[combat-death] damage field=5 attacker={attackerActorNr} victimPawn={victimPawnId} " +
                    $"IGNORED — victimOwner={victim} (self/host/unowned)");
                return;
            }

            at = GetActorTeam(room, attackerActorNr);
            vt = GetActorTeam(room, victim);
            if (at is not (MatchTeam.Tr or MatchTeam.Ct)
                || vt is not (MatchTeam.Tr or MatchTeam.Ct)
                || at == vt)
            {
                Console.WriteLine(
                    $"[combat-death] damage field=5 attacker={attackerActorNr}/{at} victim={victim}/{vt} " +
                    "IGNORED — self/friendly/spectator (no attribution)");
                return; // ignore self / friendly / spectator damage for attribution
            }

            room.LastCombatDamage[attackerActorNr] = (victim, DateTime.UtcNow);
        }

        Console.WriteLine(
            $"[combat-death] damage field=5 attribution: attacker={attackerActorNr}/{at} → " +
            $"victim={victim}/{vt} (pawn={victimPawnId}); armed for next kills++ within " +
            $"{DeathMatchFlowParams.KillAttributionWindow.TotalSeconds:0.#}s");
    }

    /// <summary>
    /// The killer's <c>kills</c> counter incremented — a confirmed elimination. Resolve the victim
    /// from the most recent damage attribution and master-author its death (Destroy + <c>death</c>
    /// + team score). This is the primary TDM kill path; the Live no-respawn Destroy grace is a
    /// fallback for a kill that arrives without a fresh damage RPC (grenade / fall / relayed race).
    /// </summary>
    private void NoteDeathMatchKillByKiller(MatchRoom room, byte killerActorNr)
    {
        byte victim = 0;
        string how = "damage-attribution";
        bool alreadyAuthored = false;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.DeathMatchLive)
            {
                // Clear stale attribution outside Live; do not block WaitingPlayers kill relay.
                room.LastCombatDamage.Remove(killerActorNr);
                Console.WriteLine(
                    $"[tdm-death] kills++ killer={killerActorNr} IGNORED — phase={room.Flow.Phase} " +
                    "(not Live; attribution cleared)");
                return;
            }

            if (room.LastCombatDamage.TryGetValue(killerActorNr, out var dmg))
            {
                var age = DateTime.UtcNow - dmg.When;
                if (age <= DeathMatchFlowParams.KillAttributionWindow)
                {
                    victim = dmg.Victim;
                }
                else
                {
                    Console.WriteLine(
                        $"[tdm-death] kills++ killer={killerActorNr}: last damage victim={dmg.Victim} " +
                        $"is STALE ({age.TotalSeconds:0.#}s > " +
                        $"{DeathMatchFlowParams.KillAttributionWindow.TotalSeconds:0.#}s window)");
                }
            }
            room.LastCombatDamage.Remove(killerActorNr);

            // Fallback (no invention): if attribution was missing/stale but there is exactly ONE
            // living enemy fighter, the kill is unambiguous — resolve to it so the master death
            // sequence still runs (otherwise the kill is silently dropped: this game's victim does
            // not self-Destroy, so the Destroy-grace fallback never fires and the death HUD /
            // respawn / score never happen — the "silent kill" symptom).
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
                        $"[tdm-death] kills++ killer={killerActorNr}/{killerTeam}: no attribution and " +
                        $"{enemies.Count} living enemy fighters — cannot resolve victim unambiguously");
                }
            }

            if (victim != 0)
            {
                var now = DateTime.UtcNow;
                if (room.LastAuthoredDeath.TryGetValue(victim, out var last)
                    && now - last < DeathMatchFlowParams.DeathDedupWindow)
                    alreadyAuthored = true;
            }
        }

        if (victim == 0)
        {
            Console.WriteLine(
                $"[match-host] tdm-flow: killer={killerActorNr} kills++ but no victim resolved " +
                "([tdm-death] awaiting Destroy fallback — no master death authored)");
            return;
        }

        // Same elimination FSM as bomb: death/kills++ are inputs — late kills++ confirms killer only.
        if (alreadyAuthored)
        {
            Console.WriteLine(
                $"[tdm-death] kills++ killer={killerActorNr} → victim={victim} via {how} " +
                "CONFIRMED-KILLER (already authored — attribution cleared, no second Destroy)");
            return;
        }

        Console.WriteLine(
            $"[tdm-death] kills++ killer={killerActorNr} → victim={victim} via {how} " +
            "→ AuthorDeathMatchDeath");
        AuthorDeathMatchDeath(room, victim, killerActorNr, source: $"kills/{how}");
    }

    /// <summary>
    /// Master-author a TDM combat death for <paramref name="victimActorNr"/>: TX the victim pawn
    /// <c>DestroyWorldObject</c> (so the client leaves its alive pawn → death HUD → respawn),
    /// bump the victim's cumulative <c>death</c> prop, and bump the killer team's room score
    /// (<see cref="NoteDeathMatchKill"/>). De-duped per victim within
    /// <see cref="DeathMatchFlowParams.DeathDedupWindow"/> so the <c>kills</c>++ and Destroy-grace
    /// signals cannot double-count one kill. Order mirrors phone gold RX#3785–3793 / RX#4265–4269:
    /// <c>Destroy</c> → RadarManager <c>Rpc(7)</c> refresh → team score → <c>death</c> → RadarManager
    /// <c>Rpc(7)</c> refresh. The two radar refreshes are the only master-authored packets the old host
    /// omitted; without them the victim's pawn was destroyed (HUD gone, camera frozen) but the death /
    /// respawn view never engaged (user report), so they are re-sent here from a codec builder — never a
    /// blob replay. Gold sends the radar refresh in <b>every</b> kill (host-killer and guest-killer
    /// alike), so it is not tied to the <c>field=5</c> damage RPC.
    /// </summary>
    private void AuthorDeathMatchDeath(
        MatchRoom room, byte victimActorNr, byte? killerActorNr, string source)
    {
        var killerLabelIn = killerActorNr is { } kk ? kk.ToString() : "?";
        Console.WriteLine(
            $"[tdm-death] AuthorDeathMatchDeath ENTER victim={victimActorNr} killer={killerLabelIn} " +
            $"src={source}");

        if (victimActorNr == 0 || victimActorNr == MatchHostActor.ActorNr)
        {
            Console.WriteLine(
                $"[tdm-death] AuthorDeathMatchDeath SKIP — victim={victimActorNr} is 0/host");
            return;
        }

        List<short> victimPawnIds;
        int newDeath;
        MatchTeam victimTeam;
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.DeathMatchLive)
            {
                Console.WriteLine(
                    $"[tdm-death] AuthorDeathMatchDeath SKIP — phase={room.Flow.Phase} (not Live)");
                return;
            }

            var now = DateTime.UtcNow;
            if (room.LastAuthoredDeath.TryGetValue(victimActorNr, out var last)
                && now - last < DeathMatchFlowParams.DeathDedupWindow)
            {
                Console.WriteLine(
                    $"[tdm-death] AuthorDeathMatchDeath SKIP — de-dup, victim={victimActorNr} " +
                    $"already authored {(now - last).TotalMilliseconds:0}ms ago " +
                    $"(< {DeathMatchFlowParams.DeathDedupWindow.TotalMilliseconds:0}ms window)");
                return; // same elimination already authored (kills++ vs Destroy-grace race)
            }

            victimTeam = GetActorTeam(room, victimActorNr);
            if (victimTeam is not (MatchTeam.Tr or MatchTeam.Ct))
            {
                Console.WriteLine(
                    $"[tdm-death] AuthorDeathMatchDeath SKIP — victim={victimActorNr} team={victimTeam} " +
                    "(not a fighter)");
                return;
            }

            room.LastAuthoredDeath[victimActorNr] = now;

            // Leave the victim with no alive pawn — client shows death HUD, then respawns
            // (a fresh CreateWorldObject). Any stale ids for this owner go too.
            victimPawnIds = room.LivingPawns
                .Where(kv => kv.Value.OwnerActorNr == victimActorNr)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var id in victimPawnIds)
                room.LivingPawns.Remove(id);

            // Cumulative death counter (phone gold increments 1,2,3…; not reset on TDM respawn).
            var prev = ReadActorIntProp(room, victimActorNr, MatchRoomPropKeys.Death);
            newDeath = (prev < 0 ? 0 : prev) + 1;
            room.ActorProps[(victimActorNr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(newDeath);
        }

        Console.WriteLine(
            $"[tdm-death] AuthorDeathMatchDeath PROCEED victim={victimActorNr}/{victimTeam} " +
            $"death#{newDeath} livingPawnsToDestroy=[{string.Join(",", victimPawnIds)}]" +
            (victimPawnIds.Count == 0
                ? " (WARNING: victim had NO living pawn — victim likely already self-Destroyed; " +
                  "still authoring Radar7+score+death so HUD/respawn/feed engage)"
                : ""));

        // 1) Destroy the victim's alive pawn(s) — to ALL init-ready peers incl. the victim, so the
        // victim's own client leaves its live pawn and enters the death/respawn flow.
        foreach (var id in victimPawnIds)
        {
            var destroyPkt = MatchCodec.BuildDestroyWorldObject(NextServerTime(), id);
            var nD = BroadcastInitReady(room, destroyPkt, tag: "match_tx_DestroyWorldObject");
            Console.WriteLine(
                $"[match-host] tdm-flow: master death → TX DestroyWorldObject id={id} " +
                $"victim={victimActorNr} → peers={nD}");
        }

        // 2) RadarManager Rpc(7) refresh #1 — gold sends this immediately after the victim Destroy
        // (RX#3786 / RX#4266). Master-authored (scene object, no-owner); the old host omitted it,
        // which left the victim's pawn gone but the death/respawn view frozen.
        BroadcastRadarManagerDeathRefresh(room, victimActorNr, phase: "post-destroy");

        // 3) Team room score (killer side) — Tr victim → CtScore++, Ct victim → TrScore++.
        NoteDeathMatchKill(room, victimActorNr);

        // 4) Victim cumulative death prop — broadcast to all room peers (HasServerTime rebuild).
        var deathPkt = MatchCodec.BuildSetProperty(
            NextServerTime(), victimActorNr, MatchRoomPropKeys.Death, LobbyVariant.FromInt(newDeath));
        BroadcastRoom(room, deathPkt, tag: "match_tx_SetProperty");

        // 5) RadarManager Rpc(7) refresh #2 — gold sends a second refresh after the death prop
        // (RX#3793 / RX#4269), closing the master death signature Destroy→radar→score→death→radar.
        BroadcastRadarManagerDeathRefresh(room, victimActorNr, phase: "post-death");

        var killerLabel = killerActorNr is { } k ? k.ToString() : "?";
        Console.WriteLine(
            $"[match-host] tdm-flow: master death authored victim={victimActorNr}/{victimTeam} " +
            $"death={newDeath} killer={killerLabel} src={source} (death HUD + respawn now client-driven)");
        Dashboard.DashboardHub.PostDeath(
            victimActorNr, ResolveActorName(victimActorNr), victimTeam, source);
    }

    /// <summary>
    /// TX the master's <b>RadarManager</b> <c>Rpc(7)</c> full-radar/occlusion refresh to every
    /// init-ready peer (incl. the victim). Gold brackets each combat death with two of these — see
    /// <see cref="MatchCodec.BuildRadarManagerDeathRefreshRpc"/>. On our host the RadarManager is the
    /// bootstrap scene object <see cref="MatchSceneManagers.RadarManagerObjectId"/> (id=6); clients
    /// already RX/relay radar RPCs on that id, so authoring on it is correct.
    /// </summary>
    private void BroadcastRadarManagerDeathRefresh(MatchRoom room, byte victimActorNr, string phase)
    {
        var pkt = MatchCodec.BuildRadarManagerDeathRefreshRpc(NextServerTime());
        var n = BroadcastInitReady(room, pkt, tag: "match_tx_WorldObjectRpc");
        Console.WriteLine(
            $"[match-host] tdm-flow: master death → TX RadarManager Rpc(7) refresh ({phase}) " +
            $"id={MatchSceneManagers.RadarManagerObjectId} victim={victimActorNr} → peers={n}");
    }

    /// <summary>Int-ish read of a client counter prop (Int or Byte); 0 for anything else.</summary>
    private static int VariantAsInt(LobbyVariant v) => v.Kind switch
    {
        LobbyVariantKind.Int => v.Int,
        LobbyVariantKind.Byte => v.Byte,
        _ => 0,
    };
}
