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
    // Ranked2v2 / Allies phase transitions (Warmup→…→Live/Bomb).
    private void TryBeginMatchFlowIfBothTeams(MatchRoom room)
    {
        lock (_roomGate)
        {
            if (room.Flow.Phase != MatchFlowPhase.WaitingPlayers)
                return;
            if (!BothFightingTeamsPresent(room))
                return;
        }

        if (!MatchHostSettings.MatchStartArmed)
        {
            Console.WriteLine(
                "[match-host] match-flow: both teams have ≥1 player — " +
                "WaitingPlayers C2=10 until /set start (not auto WarmUp)");
            return;
        }

        Console.WriteLine(
            "[match-host] match-flow: both teams have ≥1 player + start armed — " +
            "will start Warmup on next tick");
    }

    /// <summary>After <c>/set start</c>: re-check WaitingPlayers rooms for both-teams WarmUp.</summary>
    public void NudgeMatchStartGate()
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.ToList();
        if (rooms.Count == 0)
        {
            Console.WriteLine(
                "[match-host] match-flow: /set start armed but no Dedik room yet — " +
                "WarmUp waits until both teams join WaitingPlayers");
            return;
        }

        foreach (var room in rooms)
        {
            MatchFlowPhase phase;
            bool both;
            lock (_roomGate)
            {
                phase = room.Flow.Phase;
                both = BothFightingTeamsPresent(room);
            }

            if (phase != MatchFlowPhase.WaitingPlayers)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: /set start armed but phase={phase} " +
                    "(WarmUp gate only applies in WaitingPlayers — ignored)");
                continue;
            }

            if (!both)
            {
                var (tr, ct) = CountFightingTeamRoster(room);
                Console.WriteLine(
                    $"[match-host] match-flow: /set start armed but teams incomplete " +
                    $"Tr={tr} Ct={ct} — WarmUp waits for both ≥1");
                continue;
            }

            TryBeginMatchFlowIfBothTeams(room);
        }
    }

    private static bool BothFightingTeamsPresent(MatchRoom room)
    {
        var hasTr = false;
        var hasCt = false;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team == MatchTeam.Tr) hasTr = true;
            if (team == MatchTeam.Ct) hasCt = true;
        }
        return hasTr && hasCt;
    }

    private void TickMatchFlow()
    {
        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.ToList();

        foreach (var room in rooms)
        {
            ProcessPendingCombatDestroys(room);
            TickMatchFlowRoom(room);
        }
    }

    private void TickMatchFlowRoom(MatchRoom room)
    {
        // Branch by GameModeId (C0): DeathMatch/TDM has its own single-round flow
        // (Modes/DeathMatch/); everything below is the Ranked2v2 / Allies bomb loop.
        if (IsDeathMatchRoom(room))
        {
            TickDeathMatchFlowRoom(room);
            return;
        }

        if (IsEscalationRoom(room))
        {
            TickEscalationFlowRoom(room);
            return;
        }

        MatchFlowPhase phase;
        DateTime ends;
        DateTime bombPlantedUtc;
        string? pendingReason;
        MatchTeam pendingWinner;
        bool bombPlanted;
        lock (_roomGate)
        {
            phase = room.Flow.Phase;
            ends = room.Flow.PhaseEndsUtc;
            bombPlantedUtc = room.Flow.BombPlantedUtc;
            pendingReason = room.Flow.PendingEndReason;
            pendingWinner = room.Flow.PendingWinner;
            bombPlanted = room.Flow.BombPlanted;
            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                if (!BothFightingTeamsPresent(room))
                    return;
                if (!MatchHostSettings.MatchStartArmed)
                    return;
            }
            else if (phase == MatchFlowPhase.MatchOver)
                return;
        }

        // Event outcomes preempt phase timers: wipe / pending end every tick while combat
        // or Prep/PreStart can produce proven deaths (AllowsWipeCheck). Do not wait for
        // round/prep clocks when a team is already wiped or defuse already decided.
        if (MatchFlowRules.AllowsWipeCheck(phase))
        {
            if (pendingReason is not null)
            {
                EnterRoundEndPause(room, pendingWinner, pendingReason);
                return;
            }

            if (TryResolveWipeImmediate(room, bombPlanted || phase == MatchFlowPhase.BombPlanted))
                return;
        }

        // Bomb fuse: explode ONLY with real plantUtc from observed plant Rpc + elapsed≥fuse.
        // Never invent plantUtc / re-anchor / explode from PhaseEndsUtc alone.
        if (phase == MatchFlowPhase.BombPlanted || bombPlanted)
        {
            if (!bombPlanted || bombPlantedUtc == DateTime.MinValue)
            {
                Console.WriteLine(
                    "[match-host] match-flow: BombPlanted fantasy cleared — " +
                    $"BombPlanted={bombPlanted} plantUtc={(bombPlantedUtc == DateTime.MinValue ? "null" : bombPlantedUtc.ToString("O"))} " +
                    "(no explode without real plantUtc; if unsure → do nothing)");
                ClearBombAuthority(room, "fantasy-missing-plantUtc");
                lock (_roomGate)
                {
                    // Drop fantasy BombPlanted phase; keep existing PhaseEndsUtc (do not invent).
                    if (room.Flow.Phase == MatchFlowPhase.BombPlanted)
                        room.Flow.Phase = MatchFlowPhase.RoundLive;
                }
                return;
            }

            var fuseEnd = bombPlantedUtc + MatchFlowTestParams.BombFuse;
            var now = DateTime.UtcNow;
            if (now < fuseEnd)
            {
                if (ends != fuseEnd)
                {
                    lock (_roomGate)
                        room.Flow.PhaseEndsUtc = fuseEnd;
                }
                return;
            }

            var elapsed = (now - bombPlantedUtc).TotalSeconds;
            Console.WriteLine(
                "[match-host] match-flow: bomb fuse expired → T win " +
                $"(plantUtc={bombPlantedUtc:O} explodeUtc={now:O} " +
                $"elapsed={elapsed:F3}s need={MatchFlowTestParams.BombFuse.TotalSeconds:0}s)");
            PostServerDebugChat(
                $"взрыв fuse={elapsed:F1}s (plant+{MatchFlowTestParams.BombFuse.TotalSeconds:0}s)");
            EnterRoundEndPause(room, MatchTeam.Tr, "bomb-explode");
            return;
        }

        if (phase == MatchFlowPhase.RoundLive)
        {
            if (DateTime.UtcNow < ends)
                return;
            EnterRoundEndPause(room, MatchTeam.Ct, "timeout");
            return;
        }

        if (DateTime.UtcNow < ends)
            return;

        switch (phase)
        {
            case MatchFlowPhase.WaitingPlayers:
                EnterWarmup(room);
                break;
            case MatchFlowPhase.Warmup:
                EnterWarmupWillFinish(room);
                break;
            case MatchFlowPhase.WarmupWillFinish:
                // C2=22 (~3s) → C2=31 Prep (never skip 22 between rounds).
                EnterPurchasePhase(room);
                break;
            case MatchFlowPhase.PurchasePhase:
                if (TryExtendPrepForAwaitingSpawn(room))
                    break;
                EnterRoundLive(room);
                break;
            case MatchFlowPhase.RoundEndPause:
                ContinueAfterRoundEnd(room);
                break;
        }
    }

    /// <summary>
    /// Evidence-only: wipe BombPlanted / plantUtc / PendingBombPlant. Never leave fuse
    /// fantasy across WarmUp / PreStart / HardReset / RoundEnd.
    /// </summary>
    private void ClearBombAuthority(MatchRoom room, string reason)
    {
        lock (_roomGate)
        {
            room.Flow.BombPlanted = false;
            room.Flow.BombPlantedUtc = DateTime.MinValue;
            room.Flow.PendingBombPlant = false;
            room.Flow.EscalationCombatStarted = false;
        }
        Console.WriteLine(
            $"[match-host] match-flow: bomb authority clear ({reason}) " +
            "BombPlanted=false BombPlantedUtc=cleared PendingBombPlant=false");
    }

    private void EnterWarmup(MatchRoom room)
    {
        var dur = MatchFlowTestParams.Warmup;
        var ends = DateTime.UtcNow + dur;
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        lock (_roomGate)
        {
            room.Flow.Phase = MatchFlowPhase.Warmup;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.RoundIndex = 0;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.Flow.RoundEndCommittedRound = 0;
        }
        ClearBombAuthority(room, "WarmUp C2=21");
        // Consumed: next WaitingPlayers (rematch) needs /set start again.
        MatchHostSettings.MatchStartArmed = false;
        // Time/RoundStartTime are doubles in NetManager.bfqt units (TickCount/1000 seconds).
        // FetchServerTime HasServerTime header stays raw TickCount ms — client dws.bfwi divides.
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmUp)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ], reason: "WarmUp", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] match-flow: WarmUp C2={MatchC2States.WarmUp} " +
            $"dur={dur.TotalSeconds:0}s deadline={deadline:0.###} src=MatchHostSettings.Warmup " +
            "(first round only; then PreStart C2=22)");
        PostServerDebugChat("WarmUp");
    }

    /// <summary>
    /// WarmupWillFinish / PreStart C2=22 — <c>Time</c>, <c>Round</c>, <c>RoundStartTime</c>,
    /// <c>bomberId</c>, <c>C2=22</c>. Wire <c>Time</c> = phase deadline (= host
    /// <see cref="MatchFlowState.PhaseEndsUtc"/>), not "now" while a private timer runs longer.
    /// Fired after WarmUp <b>and</b> after every RoundEnd pause — clients Destroy+Create pawns
    /// here. Skipping this between rounds leaves the phone stuck on round-end UI.
    /// </summary>
    private void EnterWarmupWillFinish(MatchRoom room)
    {
        if (IsEscalationRoom(room))
        {
            EnterEscalationPreStart(room);
            return;
        }

        int bomberId;
        int round;
        var dur = MatchFlowTestParams.WarmupWillFinish;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            // Round advances on C2=22 (gold Round=1…N), not on Prep.
            round = room.Flow.RoundIndex + 1;
            if (round < 1) round = 1;
            room.Flow.RoundIndex = round;
            room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            // Clear last round's death locally before bomber pick (same smoking gun as Prep).
            foreach (var a in room.Actors)
            {
                if (a.Nr != MatchHostActor.ActorNr)
                    room.ActorProps[(a.Nr, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);
            }
            bomberId = PickBomberActorNr(room);
            room.Flow.BomberActorNr = bomberId;
        }

        ClearBombAuthority(room, $"PreStart C2=22 round={round} bomberId={bomberId}");
        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        // One clock: wire Time == PhaseEndsUtc deadline (not Time=now + longer private timer).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.WarmupWillFinish)),
        ], reason: $"PreStart round={round}", phaseDeadlineSec: deadline);
        ClearFighterDeathFlags(room);
        DestroyTrackedRoundEntities(room, reason: "PreStart");
        Console.WriteLine(
            $"[match-host] match-flow: PreStart C2={MatchC2States.WarmupWillFinish} " +
            $"dur={dur.TotalSeconds:0}s deadline={deadline:0.###} " +
            $"src=_roundStartingTime/MATCH_PHASES_TIMERS " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} bomberId={bomberId} " +
            "(clients Destroy+Create here; BombManager.nyn master-local — no host plant invent; " +
            "plant Rpc ignored until RoundLive)");
    }

    /// <summary>
    /// PurchasePhase C2=31 — phone-host gold <c>run-20260722_100157</c> len≈90:
    /// <c>Time</c>, <c>Ct_RoundStartPlayersCount</c>, <c>Tr_RoundStartPlayersCount</c>,
    /// <c>C2=31</c>. Round/bomberId already published on preceding C2=22.
    /// </summary>
    private void EnterPurchasePhase(MatchRoom room)
    {
        // Real Live plant already owns C2=40 — do not clobber with Prep.
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    "[match-host] match-flow: Prep skipped — real BombPlanted (C2=40 owns phase)");
                return;
            }
            // Stale fantasy from a prior invent path — clear, continue into Prep.
            if (room.Flow.BombPlanted || room.Flow.PendingBombPlant
                || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    "[match-host] match-flow: Prep — clearing stale bomb fantasy before C2=31");
            }
        }
        ClearBombAuthority(room, "Prep entry");

        int round;
        int ctCount;
        int trCount;
        int bomberId;
        var dur = MatchFlowTestParams.PurchasePhase;
        var ends = DateTime.UtcNow + dur;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            if (round < 1) round = 1;
            room.Flow.Phase = MatchFlowPhase.PurchasePhase;
            room.Flow.PhaseEndsUtc = ends;
            room.Flow.PendingEndReason = null;
            room.Flow.PrepSpawnExtensionUsed = false;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
            room.LastCombatDamage.Clear();
            (trCount, ctCount) = CountFightingTeamRoster(room);
            bomberId = room.Flow.BomberActorNr;
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + dur.TotalSeconds;
        // One clock: wire Time == PhaseEndsUtc deadline (same as PreStart / WarmUp).
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.CtRoundStartPlayersCount, LobbyVariant.FromInt(ctCount)),
            (MatchRoomPropKeys.TrRoundStartPlayersCount, LobbyVariant.FromInt(trCount)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.PurchasePhase)),
        ], reason: $"Prep round={round}", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[match-host] match-flow: Prep C2={MatchC2States.PurchasePhase} " +
            $"dur={dur.TotalSeconds:0}s deadline={deadline:0.###} " +
            $"src=MatchHostSettings.Prep " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} " +
            $"TrCount={trCount} CtCount={ctCount} bomberId={bomberId} (after PreStart C2=22)");
    }

    /// <summary>Tr/Ct roster sizes for gold Prep <c>*_RoundStartPlayersCount</c> keys.</summary>
    private static (int TrCount, int CtCount) CountFightingTeamRoster(MatchRoom room)
    {
        var tr = 0;
        var ct = 0;
        foreach (var ((actor, key), value) in room.ActorProps)
        {
            if (key != MatchRoomPropKeys.Team || actor == MatchHostActor.ActorNr)
                continue;
            if (value.Kind != LobbyVariantKind.Byte)
                continue;
            var team = (MatchTeam)value.Byte;
            if (team == MatchTeam.Tr) tr++;
            else if (team == MatchTeam.Ct) ct++;
        }
        return (tr, ct);
    }

    private void EnterRoundLive(MatchRoom room)
    {
        // Real plant during Live already owns C2=40 — do not clobber fuse.
        lock (_roomGate)
        {
            if (room.Flow.BombPlanted
                && room.Flow.BombPlantedUtc != DateTime.MinValue
                && room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    "[match-host] match-flow: RoundLive skipped — real BombPlanted " +
                    "(C2=40; fuse continues from BombPlantedUtc)");
                return;
            }
            if (room.Flow.BombPlanted || room.Flow.PendingBombPlant
                || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    "[match-host] match-flow: RoundLive — clearing stale bomb fantasy before C2=101");
            }
        }
        ClearBombAuthority(room, "RoundLive entry");

        // Sync DeadActors from actor death props so Prep combat kills are not forgotten
        // when the prep clock expires — wipe must preempt the 90s round fantasy.
        SyncDeadActorsFromDeathProps(room);
        if (TryResolveWipeImmediate(room, bombPlanted: false))
            return;

        int round;
        int bomberId;
        var txBomberFix = false;
        lock (_roomGate)
        {
            round = room.Flow.RoundIndex;
            // Last chance: living T on roster but bomberId still 0 (late Tr join race).
            if (room.Flow.BomberActorNr <= 0)
            {
                var pick = PickBomberActorNr(room);
                if (pick > 0)
                {
                    room.Flow.BomberActorNr = pick;
                    txBomberFix = true;
                }
            }
            bomberId = room.Flow.BomberActorNr;
            room.Flow.Phase = MatchFlowPhase.RoundLive;
            room.Flow.PhaseEndsUtc = DateTime.UtcNow + MatchFlowTestParams.RoundDuration;
            room.Flow.PendingEndReason = null;
            room.Flow.DeadActors.Clear();
            room.Flow.PendingCombatDestroy.Clear();
        }

        if (txBomberFix && bomberId > 0)
        {
            BroadcastRoomProps(room,
            [
                (MatchRoomPropKeys.BomberId, LobbyVariant.FromInt(bomberId)),
            ], reason: $"RoundLive bomberId-fix={bomberId}");
            Console.WriteLine(
                $"[match-host] match-flow: bomberId={bomberId} (fixed 0→T at RoundLive edge)");
        }

        var nowSec = ServerTimeSeconds();
        var deadline = nowSec + MatchFlowTestParams.RoundDuration.TotalSeconds;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.MatchStarted)),
            (MatchRoomPropKeys.Round, LobbyVariant.FromInt(round)),
            (MatchRoomPropKeys.RoundCount, LobbyVariant.FromInt(MatchFlowTestParams.TotalRounds)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
        ], reason: $"RoundLive round={round}", phaseDeadlineSec: deadline);
        SetAllFightersMoney(room, MatchFlowTestParams.RoundStartMoney);
        ClearFighterDeathFlags(room);
        Console.WriteLine(
            $"[match-host] match-flow: RoundLive C2={MatchC2States.MatchStarted} " +
            $"round={round}/{MatchFlowTestParams.TotalRounds} " +
            $"duration={MatchFlowTestParams.RoundDuration.TotalSeconds:0}s deadline={deadline:0.###} " +
            $"money={MatchFlowTestParams.RoundStartMoney} bomberId={bomberId}");
        PostServerDebugChat($"Live · раунд {round}");
    }

    /// <summary>
    /// Evidence-only plant: <c>BombManager</c> field=1/2 observed during <b>RoundLive only</b>.
    /// PreStart/Prep plant Rpc is ignored (latest.log: WarmupWillFinish field=1 → invented
    /// C2=40 + free T round). No deferred PendingBombPlant. No host plant TX invent.
    /// </summary>
    private void TryEnterBombPlanted(MatchRoom room, byte sourceField)
    {
        MatchFlowPhase fromPhase;
        lock (_roomGate)
        {
            fromPhase = room.Flow.Phase;
            if (room.Flow.BombPlanted || room.Flow.Phase == MatchFlowPhase.BombPlanted)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: BombManager plant field={sourceField} " +
                    "IGNORED — already BombPlanted this round");
                return;
            }
            if (room.Flow.PendingEndReason is not null)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: BombManager plant field={sourceField} " +
                    $"IGNORED — pendingEnd={room.Flow.PendingEndReason}");
                return;
            }
            if (fromPhase is not MatchFlowPhase.RoundLive)
            {
                Console.WriteLine(
                    $"[match-host] match-flow: BombManager plant field={sourceField} " +
                    $"IGNORED — phase={fromPhase} (evidence-only: RoundLive only; " +
                    "no PreStart/Prep plant→C2=40 invent)");
                return;
            }

            var plantUtc = DateTime.UtcNow;
            room.Flow.PendingBombPlant = false;
            room.Flow.BombPlanted = true;
            room.Flow.BombPlantedUtc = plantUtc;
            room.Flow.Phase = MatchFlowPhase.BombPlanted;
            room.Flow.PhaseEndsUtc = plantUtc + MatchFlowTestParams.BombFuse;
        }

        DateTime plantedAt;
        lock (_roomGate)
            plantedAt = room.Flow.BombPlantedUtc;
        var nowSec = ServerTimeSeconds();
        var fuseSec = MatchFlowTestParams.BombFuse.TotalSeconds;
        var deadline = nowSec + fuseSec;
        BroadcastRoomProps(room,
        [
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(MatchC2States.BombPlanted)),
            (MatchRoomPropKeys.Time, LobbyVariant.FromDouble(deadline)),
            (MatchRoomPropKeys.RoundStartTime, LobbyVariant.FromDouble(nowSec)),
        ], reason: $"BombPlanted field={sourceField}", phaseDeadlineSec: deadline);
        Console.WriteLine(
            $"[observe] BombManager plant field={sourceField} → BombPlanted C2=40 " +
            $"plantUtc={plantedAt:O} fuse={fuseSec:0}s (explode only at plant+{fuseSec:0}s) " +
            $"fromPhase={fromPhase}");
        PostServerDebugChat($"бомба установлена (fuse {fuseSec:0}s)");
    }

    /// <summary>
    /// BombManager field=6 <c>nzu(bool, byte, float)</c> — payload starts with bool via <c>fzr.bojd</c>.
    /// true → defuse (<c>vxr</c>) → CT win; false → explode FX (<c>vxq</c>) → T win.
    /// Observed on RX (not relay-only): cancels host fuse immediately. Must enter RoundEnd
    /// before any PreStart/Prep — never skip the round-end UI.
    /// Wire method id is <c>field</c> (DiffableCs <c>[Rpc(6)]</c>), not the rpc sender byte.
    /// </summary>
    private void HandleBombManagerNzu(MatchRoom room, byte[] payload)
    {
        if (payload.Length < 1)
        {
            Console.WriteLine(
                "[observe] BombManager field=6 nzu — empty payload, ignore");
            return;
        }

        var defused = payload[0] != 0;
        double? fuseElapsed = null;
        MatchTeam winner;
        string reason;
        lock (_roomGate)
        {
            if (room.Flow.BombPlantedUtc != DateTime.MinValue)
                fuseElapsed = (DateTime.UtcNow - room.Flow.BombPlantedUtc).TotalSeconds;

            if (room.Flow.Phase is MatchFlowPhase.RoundEndPause or MatchFlowPhase.MatchOver)
            {
                Console.WriteLine(
                    $"[observe] BombManager field=6 nzu defused={defused} " +
                    $"IGNORED — already {room.Flow.Phase} (fuseElapsed={fuseElapsed:F3}s)");
                return;
            }

            // Ranked: RoundLive/BombPlanted. Escalation: PurchasePhase combat with bomb planted.
            if (room.Flow.Phase is not MatchFlowPhase.BombPlanted
                && !(room.Flow.BombPlanted
                    && (room.Flow.Phase == MatchFlowPhase.RoundLive
                        || room.Flow.Phase == MatchFlowPhase.PurchasePhase)))
            {
                Console.WriteLine(
                    $"[observe] BombManager field=6 nzu defused={defused} " +
                    $"IGNORED — phase={room.Flow.Phase} BombPlanted={room.Flow.BombPlanted}");
                return;
            }

            // Defuse/explode Rpc preempts any pending fuse/timeout invent — cancel fuse now.
            if (room.Flow.PendingEndReason is not null
                && room.Flow.PendingEndReason is not ("bomb-explode" or "timeout"))
            {
                Console.WriteLine(
                    $"[observe] BombManager field=6 nzu defused={defused} " +
                    $"IGNORED — pending={room.Flow.PendingEndReason}");
                return;
            }

            if (!room.Flow.BombPlanted
                || room.Flow.BombPlantedUtc == DateTime.MinValue)
            {
                Console.WriteLine(
                    $"[observe] BombManager field=6 nzu defused={defused} " +
                    "IGNORED — no real BombPlanted+plantUtc (if unsure → do nothing)");
                return;
            }

            winner = defused ? MatchTeam.Ct : MatchTeam.Tr;
            reason = defused ? "bomb-defuse" : "bomb-explode-rpc";
            room.Flow.PendingWinner = winner;
            room.Flow.PendingEndReason = reason;
            room.Flow.PendingBombPlant = false;
            // Cancel host fuse countdown — event outcome wins.
            room.Flow.BombPlantedUtc = DateTime.MinValue;
            room.Flow.BombPlanted = false;
        }

        Console.WriteLine(
            $"[observe] BombManager field=6 nzu defused={defused} " +
            $"fuseElapsed={fuseElapsed:F3}s → {(defused ? "CT" : "T")} win " +
            "(immediate RoundEnd; fuse cancelled)");
        if (defused)
            PostServerDebugChat($"дефьюз ({fuseElapsed:F1}s)");
        else
            PostServerDebugChat($"взрыв rpc ({fuseElapsed:F1}s)");
        EnterRoundEndPause(room, winner, reason);
    }

    /// <summary>
    /// Rebuild <see cref="MatchFlowState.DeadActors"/> from observed actor <c>death</c> props
    /// so Prep combat kills are not dropped when the prep clock expires.
    /// </summary>
    private void SyncDeadActorsFromDeathProps(MatchRoom room)
    {
        lock (_roomGate)
        {
            foreach (var ((actor, key), value) in room.ActorProps)
            {
                if (key != MatchRoomPropKeys.Death || actor == MatchHostActor.ActorNr)
                    continue;
                var dead = value.Kind switch
                {
                    LobbyVariantKind.Int => value.Int != 0,
                    LobbyVariantKind.Byte => value.Byte != 0,
                    LobbyVariantKind.Bool => value.Bool,
                    _ => false,
                };
                if (dead)
                    room.Flow.DeadActors.Add(actor);
            }
        }
    }

    /// <summary>
    /// Immediate full remove on peer disconnect / timeout: drop roster slot, props, pawns,
    /// TX <c>ActorLeftEvent</c> (fuo=51), then wipe-check remaining fighters via phone-host
    /// round-end (C2=101 + WinTeam/TrScore…) — never leave ghosts that force rejoin to actorNr=4/5.
    /// </summary>
}
