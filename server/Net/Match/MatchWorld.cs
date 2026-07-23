using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Match room / actor property keys from decompile (<c>bbs</c>, <c>bbo</c>) + live phone-host capture.
/// </summary>
public static class MatchRoomPropKeys
{
    // bbs — room custom props on gap / SetProperties
    public const string C0 = "C0"; // GameModeId string
    public const string C1 = "C1"; // SelectedLevels entry string
    public const string C2 = "C2"; // game-state id (<c>cfy</c> / <c>bbs.omx</c>)
    public const string C3 = "C3";
    public const string Region = "region";
    public const string GameType = "game_type";
    public const string SessionId = "session_id";
    /// <summary>Room deadline / clock double (<c>bbs.cabt</c> / <c>omr</c>).</summary>
    public const string Time = "Time";
    /// <summary>Round index int (<c>bbs.cabu</c> / <c>omt</c>).</summary>
    public const string Round = "Round";
    /// <summary>Round start absolute double (<c>bbs.cabv</c> / <c>omv</c> via <c>NetManager.bfqt</c>).</summary>
    public const string RoundStartTime = "RoundStartTime";
    public const string RoundCount = "round_count";
    /// <summary>
    /// Nested score bag (<c>bbs.cacf</c> / <c>onq</c>) — FinalHud / other modes.
    /// Allies phone-host round end uses flat <see cref="TrScore"/> / <see cref="CtScore"/> instead.
    /// </summary>
    public const string Score = "Score";
    /// <summary>Flat T round wins — phone-host round end (<c>bbs.onf(Tr)+"Score"</c>). Int.</summary>
    public const string TrScore = "TrScore";
    /// <summary>Flat CT round wins — phone-host round end. Int.</summary>
    public const string CtScore = "CtScore";
    /// <summary>Flat T consecutive-loss counter — phone-host round end (<c>onf(Tr)+"CoLosses"</c>). Int.</summary>
    public const string TrCoLosses = "TrCoLosses";
    /// <summary>Flat CT consecutive-loss counter — phone-host round end. Int.</summary>
    public const string CtCoLosses = "CtCoLosses";
    public const string WinTeam = "WinTeam";
    public const string FinalWinTeam = "FinalWinTeam";
    /// <summary>
    /// DeathMatch match-end MVP actor nr — room prop <c>MvpPlayer</c> (Byte), phone TDM gold
    /// (<c>/tmp/tdm-probe.log</c> RX#18692). Distinct from nested Ranked <c>WinTeam.mvpPlayer</c>.
    /// </summary>
    public const string MvpPlayer = "MvpPlayer";
    /// <summary>
    /// DeathMatch match-end <c>FinalPlayers</c> — phone TDM gold serialized as a <b>ByteArray</b>
    /// (len 3 in the 3-actor probe). Inner structure not decoded (per-player summary bytes?), so
    /// the dedicated host <b>omits</b> it rather than fake a layout. See DeathMatch/README.md.
    /// </summary>
    public const string FinalPlayers = "FinalPlayers";
    /// <summary>Room bomber actor nr (<c>bcb.cadb</c> / <c>opg</c>) — Int.</summary>
    public const string BomberId = "bomberId";
    /// <summary>
    /// Active plant site byte — Escalation gold <c>run-20260723_140524</c> on C2=22 bag
    /// (<c>BombSite</c> key, values 0/1 on Prison). Allies/Ranked omit this key.
    /// </summary>
    public const string BombSite = "BombSite";
    /// <summary>
    /// CT roster size at round start — phone-host Prep bag (<c>run-20260722_100157</c> len≈90).
    /// Wire key <c>Ct_RoundStartPlayersCount</c> (team prefix + <c>_RoundStartPlayersCount</c>).
    /// </summary>
    public const string CtRoundStartPlayersCount = "Ct_RoundStartPlayersCount";
    /// <summary>
    /// T roster size at round start — phone-host Prep bag (same gold as
    /// <see cref="CtRoundStartPlayersCount"/>).
    /// </summary>
    public const string TrRoundStartPlayersCount = "Tr_RoundStartPlayersCount";
    /// <summary>
    /// Half-time team swap flag — phone gold C2=112 bag after round 7
    /// (<c>allies-probe</c> run-20260723_215727). Bool <c>true</c> once per match.
    /// </summary>
    public const string SwappedTeam = "swapped_team";

    // bbo — actor props on SetProperty / SetProperties
    public const string Team = "team"; // <c>bbo.caav</c> / <c>cux</c> byte
    public const string Uid = "uid";
    public const string BadgeId = "badgeId";
    public const string FromLobby = "from_lobby";
    public const string Avatar = "avatar";
    public const string Money = "money";
    public const string Ping = "ping";
    /// <summary>Actor death flag (<c>bbo.cabe</c>) — Int; non-zero = dead this round.</summary>
    public const string Death = "death";
    /// <summary>Cumulative MVP awards (<c>bbo.cabd</c>) — Int; phone TX before round-end WinTeam.</summary>
    public const string Mvp = "mvp";
    /// <summary>Per-round eliminations (<c>bbo.caba</c>) — Int; used for MVP MostEliminations.</summary>
    public const string RoundKills = "round_kills";
    /// <summary>Match eliminations (<c>bbo.caay</c>) — Int; fallback when round_kills missing.</summary>
    public const string Kills = "kills";
    /// <summary>DeathMatch per-player assists — client-owned Int (phone TDM gold RX#2196).</summary>
    public const string Assists = "assists";
    /// <summary>DeathMatch per-player fair kills — client-owned Int (phone TDM gold RX#3790).</summary>
    public const string FairKills = "fair_kills";
    /// <summary>DeathMatch per-player score — client-owned Int (phone TDM gold RX#3791).</summary>
    public const string Score2 = "score";
    public const string GlovesIdCt = "glovesId_Ct";
    public const string GlovesIdTr = "glovesId_Tr";
}

/// <summary>
/// MVP reason byte on nested <c>WinTeam.mvpCode</c> — DiffableCs <c>cns</c>
/// (<c>PlantingBomb=1</c>, <c>DefusingBomb=2</c>, <c>MostEliminations=3</c>).
/// Written by phone master <c>cnp.WinTeam(..., cns mvp)</c> → <c>bbs.onv</c>.
/// </summary>
public static class MatchMvpCodes
{
    public const byte None = 0;
    public const byte PlantingBomb = 1;
    public const byte DefusingBomb = 2;
    public const byte MostEliminations = 3;
}

/// <summary>
/// Nested <c>WinTeam</c> PropertiesRecord keys — phone-host gold
/// (<c>run-20260722_100157</c> len≈151 SetProperties). Wire values are <b>Byte</b>.
/// Keys on wire: <c>team</c>, <c>mvpPlayer</c>, <c>mvpCode</c>, <c>resultRoundType</c>,
/// <c>resultRoundActor</c> (not <c>winTeam</c>/<c>resultActor</c> — those were wrong guesses).
/// Outer room key is <see cref="MatchRoomPropKeys.WinTeam"/>.
/// Per-player победа/поражение is client-side from own team vs nested <c>team</c>.
/// </summary>
public static class MatchWinTeamKeys
{
    /// <summary>Winning <c>cux</c> byte — gold key name <c>team</c>.</summary>
    public const string Team = "team";
    public const string MvpPlayer = "mvpPlayer";
    public const string MvpCode = "mvpCode";
    public const string ResultRoundType = "resultRoundType";
    /// <summary>Gold key <c>resultRoundActor</c>; Allies captures always Byte 0.</summary>
    public const string ResultRoundActor = "resultRoundActor";
}

/// <summary>
/// Nested <c>FinalWinTeam</c> PropertiesRecord keys — DeathMatch / TDM match-end bag,
/// phone gold <c>/tmp/tdm-probe.log</c> RX#18692 (<c>{ isDraw, isGiveUp, team }</c>).
/// Wire types: <c>isDraw</c>/<c>isGiveUp</c> = Bool, <c>team</c> = Byte (<see cref="MatchTeam"/>,
/// gold <c>team=1</c> = Tr, the side with more kills). Outer room key
/// <see cref="MatchRoomPropKeys.FinalWinTeam"/>. Distinct from Ranked <c>WinTeam</c> round bag.
/// </summary>
public static class MatchFinalWinTeamKeys
{
    public const string IsDraw = "isDraw";
    public const string IsGiveUp = "isGiveUp";
    public const string Team = "team";
}

/// <summary>
/// In-match team (<c>cux</c>) written as fzq.Byte on actor prop <c>team</c>.
/// DiffableCs <c>Client/cux.cs</c>; live capture used <c>Tr=1</c> for fighting host.
/// </summary>
public enum MatchTeam : byte
{
    None = 0,
    Tr = 1,
    Ct = 2,
    Spectator = 3,
}

/// <summary>
/// RankedDefuse / Defuse <c>cga</c> state ids from <c>RankedDefuseController.wkf</c>
/// (Allies / Ranked2v2). Labels from registry + <c>GameState/*</c> + class <c>xvv</c>
/// (<c>cnq=31</c> PurchasePhase, <c>cnl=40</c> bomb planted, <c>cnp=101</c>).
/// </summary>
public static class MatchC2States
{
    /// <summary>Confirmed: InitWaiting unlock + WaitingPlayers banner.</summary>
    public const byte WaitingPlayers = 10;
    /// <summary>
    /// WarmUp (<c>GameState/WarmUp</c>) — C2=21. DeathMatch: <c>_startingDuration</c> (~3s gold).
    /// Defuse: <c>_startingTime</c> (~10s probe). Must <b>not</b> be conflated with freeze
    /// countdown (that is C2=22 / <c>_roundStartingTime</c>).
    /// </summary>
    public const byte WarmUp = 21;
    /// <summary>
    /// WarmupWillFinish (<c>cnr.xvv=22</c>, <c>GameState/WarmupWillFinish</c>) —
    /// short freeze countdown («MATCH WILL START IN»). Phone <c>cnr</c> sets
    /// <c>bomberId</c> via <c>bcb.opg</c> and master calls <c>BombManager.nyn</c> here.
    /// </summary>
    public const byte WarmupWillFinish = 22;
    /// <summary>PurchasePhase / round prep (<c>cnq.xvv=31</c>, <c>GameState/PurchasePhase</c>).</summary>
    public const byte PurchasePhase = 31;
    /// <summary>Bomb planted (<c>cnl.xvv=40</c>); fuse ~40s.</summary>
    public const byte BombPlanted = 40;
    /// <summary>
    /// MatchStarted / round live (<c>cnp.xvv=101</c>). Allies phone-host also re-TX C2=101
    /// on round end together with <c>WinTeam</c>/<c>TrScore</c> (gold len≈151) — not 111.
    /// </summary>
    public const byte MatchStarted = 101;
    /// <summary>
    /// DeathMatch early wait+Time after first fighter spawn (phone probe
    /// <c>run-20260723_050240</c> / tdm-probe). Freeforall before official WarmUp;
    /// dedicated TDM skips this and goes WaitingPlayers→WarmUp when both teams ready.
    /// </summary>
    public const byte DeathMatchPreWarmup = 11;
    /// <summary>
    /// DeathMatch / TDM live after WarmUp — phone gold C2=<c>30</c> + <c>Time</c> deadline
    /// (<c>/tmp/tdm-probe.log</c>). Team scores via room <c>TrScore</c>/<c>CtScore</c> per kill.
    /// </summary>
    public const byte DeathMatchLive = 30;
    /// <summary>
    /// DeathMatch match-end bag: <c>FinalWinTeam</c> + <c>MvpPlayer</c> (+ optional
    /// <c>FinalPlayers</c>) then C2=<c>200</c> — phone gold before FinalHud.
    /// </summary>
    public const byte DeathMatchEnded = 200;
    /// <summary>
    /// RankedDefuse registry id (<c>ckq.xvv=111</c>). Allies phone-host <b>round</b> end uses
    /// C2=<see cref="MatchStarted"/> (101) + WinTeam — not 111. C2=111 appears only for
    /// <b>half-time intro</b> after round 7 (<c>allies-probe</c> run-20260723_215727).
    /// </summary>
    public const byte RoundEnd = 111;
    /// <summary>
    /// Half-time swap bag (<c>allies-probe</c>): <c>swapped_team=true</c>, flipped
    /// <see cref="MatchRoomPropKeys.TrScore"/>/<see cref="MatchRoomPropKeys.CtScore"/>,
    /// CoLosses reset, host SetProperty team on every fighter.
    /// </summary>
    public const byte HalfTimeSwap = 112;
    /// <summary>Half-time transition — brief pause before round 8 PreStart C2=22.</summary>
    public const byte HalfTimeTransition = 113;
    /// <summary>
    /// FinalHud / match-итоги state (<c>cjf.xvv=201</c>). Reads <c>FinalWinTeam</c>, not round UI.
    /// Publishing C2=201 mid-match shows green match WIN + empty scores — never use for round end.
    /// DeathMatch phone gold: C2=200 end bag → then C2=201 FinalHud.
    /// </summary>
    public const byte FinalHud = 201;
    /// <summary>Match results / итоги всей катки (after all rounds) — <c>ckc.xvv=205</c>.</summary>
    public const byte MatchResults = 205;
    /// <summary>
    /// Post-FinalHud teardown (phone TDM gold C2=<c>255</c> then disconnect).
    /// </summary>
    public const byte MatchTeardown = 255;
}

/// <summary>
/// Generic Ranked fallback timings (non-Allies). Allies uses <see cref="AlliesFlowParams"/>.
/// WarmUp C2=21 → PreStart C2=22 → Prep C2=31 → live → RoundEnd C2=101+WinTeam → pause → C2=22 again.
/// Do <b>not</b> skip C2=22 between rounds (client never enters next Prep).
/// </summary>
public static class MatchFlowTestParams
{
    /// <summary>
    /// WarmUp C2=21 — ≈3s. Overridable via <see cref="MatchHostSettings.WarmupSeconds"/>.
    /// </summary>
    public static TimeSpan Warmup => MatchHostSettings.Warmup;
    /// <summary>
    /// WarmupWillFinish / PreStart C2=22 — ≈3s freeze (<c>_roundStartingTime</c>).
    /// Overridable via settings. Not Prep's 10s.
    /// </summary>
    public static TimeSpan WarmupWillFinish => MatchHostSettings.PreStart;
    /// <summary>PurchasePhase C2=31 — overridable via <see cref="MatchHostSettings.PrepSeconds"/>.</summary>
    public static TimeSpan PurchasePhase => MatchHostSettings.Prep;
    public static TimeSpan RoundDuration => MatchHostSettings.RoundDuration;
    /// <summary>Silent pause after RoundEnd — gold ≈6s. Overridable.</summary>
    public static TimeSpan RoundEndPause => MatchHostSettings.RoundEndPause;
    /// <summary>Bomb planted fuse — ~40s default. Overridable.</summary>
    public static TimeSpan BombFuse => MatchHostSettings.BombFuse;
    /// <summary>
    /// Grace after a Live/BombPlanted pawn Destroy with no respawn before it is counted as a
    /// combat elimination. Real kills usually also SetProperty <c>death=1</c>
    /// (<c>run-20260722_105939</c> line 896) which resolves immediately on the same tick;
    /// this is the fallback for a missing death prop. 250ms — short enough that wipe after
    /// Destroy is near-immediate at 128 Hz, long enough that same-frame Destroy→Create
    /// respawn is never mis-read as a kill (not the older 1.5s doc figure).
    /// </summary>
    public static readonly TimeSpan DestroyDeathGrace = TimeSpan.FromMilliseconds(250);
    /// <summary>MR-N series length — default 8 via <see cref="MatchHostSettings.TotalRounds"/>.</summary>
    public static int TotalRounds => MatchHostSettings.TotalRounds;
    /// <summary>First to this many round wins ends the match early.</summary>
    public static int WinsNeeded => MatchHostSettings.WinsNeeded;
    /// <summary>Default money constant (settings bootstrap / docs).</summary>
    public const int DefaultRoundStartMoney = 800;
    /// <summary>Money on match/round start — overridable via <c>/set money</c>.</summary>
    public static int RoundStartMoney => MatchHostSettings.RoundStartMoney;
    public const int BootstrapMoney = 10000;
    /// <summary>BombManager scene object id (bootstrap catalog id=8).</summary>
    public const short BombManagerObjectId = 8;
    /// <summary>
    /// Wire <c>WorldObjectRpc.field</c> = DiffableCs <c>[Rpc(N)]</c> method index on
    /// <c>BombManager</c>. The separate <c>rpc</c> body byte is a per-call sender token
    /// (often actor nr) — never use it as the method id (latest.log: CT nzu was
    /// <c>rpc=3 field=6</c>; host wrongly keyed off <c>rpc</c>).
    /// </summary>
    public const short BombManagerFieldPlantNyo = 1;
    public const short BombManagerFieldPlantNyu = 2;
    /// <summary>
    /// Escalation host auto-plant — phone gold field=3 every round ~8.1s after C2=22
    /// (<c>MATCH_ESCALATION_PROBE.md</c>). Not Ranked carry plant field=1/2.
    /// </summary>
    public const short BombManagerFieldEscalationAutoPlant = 3;
    /// <summary>Escalation/Ranked defuse pose progress — observe+relay (gold field=4).</summary>
    public const short BombManagerFieldDefusePose = 4;
    public const short BombManagerFieldNzg = 5;
    public const short BombManagerFieldNzu = 6;
}

/// <summary>
/// Escalation phone-host timings — <c>MATCH_ESCALATION_PROBE.md</c> stime deltas
/// (Prison ConnectAsClient 2026-07-23). Distinct from Ranked Prep→Live.
/// </summary>
public static class EscalationFlowParams
{
    /// <summary>C2=22 → auto-plant C2=40 — gold ≈8.1s every round.</summary>
    public static readonly TimeSpan PreStart = TimeSpan.FromSeconds(8);
    /// <summary>C2=40 → C2=31 combat — gold ≈3.1s.</summary>
    public static readonly TimeSpan PostPlantAnnounce = TimeSpan.FromSeconds(3);
    /// <summary>Bomb fuse after auto-plant — gold family default ≈40s. Round ends earlier via defuse/wipe.</summary>
    public static TimeSpan BombFuse => MatchHostSettings.BombFuse;
    /// <summary>
    /// Scene manager ids recreated on every Escalation PreStart (gold ReCreateSceneManager
    /// ×4: WeaponDrop=4, Grenade=5, Radar=6, Bomb=8).
    /// </summary>
    public static readonly short[] RecreateSceneManagerIds = [4, 5, 6, 8];
    /// <summary>Auto-plant payload planter sentinel — gold i32=-2 (no pawn planter).</summary>
    public const int AutoPlantPlanterSentinel = -2;
    /// <summary>Prison BombSite=0 plant point (gold field=3 decode).</summary>
    public static readonly (float X, float Y, float Z) PrisonBombSite0 =
        (-31.96489f, 0.558f, 10.419069f);
    /// <summary>Prison BombSite=1 plant point (gold field=3 decode).</summary>
    public static readonly (float X, float Y, float Z) PrisonBombSite1 =
        (26.324486f, -0.41500017f, 21.995567f);
}

/// <summary>
/// Allies / Ranked2v2 («союзники») — first-to-<see cref="MatchHostSettings.WinsNeeded"/> wins
/// (default 8), half-time team swap after round 7. Gold stime deltas:
/// <c>allies-probe</c> run-20260723_215727 RX captures — see
/// <c>MATCH_ALLIES_PROBE.md</c>. Distinct from generic <see cref="MatchFlowTestParams"/> /
/// Escalation — do not copy Ranked 3s PreStart or 90s round clock onto Allies.
/// </summary>
public static class AlliesFlowParams
{
    /// <summary>Round index after which half-time 111→112→113 runs (not match end).</summary>
    public const int HalfTimeAfterRound = 7;
    /// <summary>ReCreateSceneManager ids on every PreStart C2=22 (same as Escalation gold).</summary>
    public static readonly short[] RecreateSceneManagerIds = EscalationFlowParams.RecreateSceneManagerIds;

    /// <summary>
    /// Phone gold C2=11 freeforall after C2=10 (~8s). Dedicated skips C2=11 —
    /// <c>/set start</c> goes straight to C2=21. Kept for probe documentation only.
    /// </summary>
    public static readonly TimeSpan PreWarmup = TimeSpan.FromSeconds(8);
    /// <summary>C2=21 WarmUp (first round only) — gold RX 407472265→407475405 ≈3.1s.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(3);
    /// <summary>
    /// C2=22 buy — host wait + wire <c>Time</c> deadline. Fixed <b>10s</b> (not Prep settings —
    /// `/set prep` no longer shrinks the visible buy window to 5s).
    /// </summary>
    public static readonly TimeSpan BuyPhase = TimeSpan.FromSeconds(10);
    /// <summary>Alias — C2=22 buy.</summary>
    public static TimeSpan PreStart => BuyPhase;
    /// <summary>
    /// C2=31 background bag only — <b>1ms</b>, anchor <c>Time</c>, no visible countdown; then Live.
    /// </summary>
    public static readonly TimeSpan PostBuyPhase = TimeSpan.FromMilliseconds(1);
    /// <summary>Alias — C2=31 flash.</summary>
    public static TimeSpan Prep => PostBuyPhase;
    /// <summary>Silent pause after C2=101 round-end WinTeam bag — gold RX 101→22 ≈6.0s.</summary>
    public static readonly TimeSpan RoundEndPause = TimeSpan.FromSeconds(6);
    /// <summary>Bomb fuse after manual plant C2=40 — gold family ≈40s.</summary>
    public static TimeSpan BombFuse => MatchHostSettings.BombFuse;
    /// <summary>
    /// Host-only Live round timeout (no Live <c>Time</c> TX) — default via
    /// <see cref="MatchHostSettings.RoundDuration"/> (90s). Ends round so client 00:00 cannot hang.
    /// </summary>
    public static TimeSpan RoundDuration => MatchHostSettings.RoundDuration;

    /// <summary>C2=111 half-time intro — gold RX R7 end→111 ≈6s; 111→112 ≈5s hold.</summary>
    public static readonly TimeSpan HalfTimeIntro = TimeSpan.FromSeconds(5);
    /// <summary>C2=112 → C2=113 — gold RX ≈1.1s.</summary>
    public static readonly TimeSpan HalfTimeSwapHold = TimeSpan.FromSeconds(1);
    /// <summary>C2=113 → round 8 PreStart C2=22 — gold RX ≈7.0s.</summary>
    public static readonly TimeSpan HalfTimeTransition = TimeSpan.FromSeconds(7);
}

/// <summary>
/// DeathMatch / TDM timings — phone gold <c>/tmp/tdm-probe.log</c> +
/// <c>MATCH_PHASES_TIMERS.md</c> (DeathmatchController: <c>_warmupDuration</c>→C2=11,
/// <c>_startingDuration</c>→C2=21, <c>_deathMatchDuration</c>→C2=30). No C2=22 / prep /
/// multi-round on TDM (those are Defuse). Dedicated skips phone C2=11 PreWarmup freeforall.
/// </summary>
public static class DeathMatchFlowParams
{
    /// <summary>
    /// WarmUp C2=21 = DeathmatchController <c>_startingDuration</c>. Phone gold wall-clock
    /// RX#2192→RX#2383 ≈3.1s (stime 346782658→346785782). <b>Not</b> 30s — that was a
    /// mislabel of PreWarmup / lobby WarmUpTime onto C2=21 (MATCH_PHASES_TIMERS callout).
    /// TDM has no C2=22; this short window is the pre-live lock on DeathMatch.
    /// </summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Live C2=30 = <c>_deathMatchDuration</c>. Phone gold RX#2383→RX#18692 ≈300s.
    /// </summary>
    public static readonly TimeSpan MatchDuration = TimeSpan.FromMinutes(5);
    /// <summary>
    /// Hold on the C2=200 end bag (FinalWinTeam+MvpPlayer) before FinalHud C2=201.
    /// Phone TDM gold: 200 → 201 ≈3s (RX#18692→RX#18879).
    /// </summary>
    public static readonly TimeSpan EndBagHold = TimeSpan.FromSeconds(3);
    /// <summary>
    /// Brief FinalHud (C2=201) before teardown C2=255. Phone TDM gold: 201 → 255 ≈6s
    /// (RX#18879→RX#18889).
    /// </summary>
    public static readonly TimeSpan FinalHudPause = TimeSpan.FromSeconds(6);

    /// <summary>
    /// WorldObjectRpc <c>field</c> for a pawn damage/hit RPC (large 77/78B payload) — phone gold
    /// <c>/tmp/tdm-probe.log</c> RX#3782 (the attacker fires <c>field=5</c> on the <b>victim's</b>
    /// pawn) confirmed on the dedicated host (<c>latest.log</c>: RX from the killer's peer, pawn
    /// owner = victim). Multiple hits precede one kill (non-lethal per hit) — used only to record
    /// attacker→victim attribution, never as a kill by itself.
    /// </summary>
    public const byte PawnDamageRpcField = 5;

    /// <summary>
    /// How recently the killer must have damaged an enemy (<c>field=5</c>) for a <c>kills</c>++
    /// to attribute the death to that victim. Kill immediately follows the lethal hit in gold.
    /// </summary>
    public static readonly TimeSpan KillAttributionWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Suppress a second master-authored death for the same victim within this window (the
    /// <c>kills</c>++ signal and the no-respawn Destroy fallback can both fire for one kill).
    /// A victim cannot legitimately die twice this fast (respawn takes ≈2.5s in gold).
    /// </summary>
    public static readonly TimeSpan DeathDedupWindow = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// Dedicated synthetic host = lobby fake player <see cref="LobbySession.ServerPlayerName"/>.
/// Always match actor #1 (master). Team is set on the wire via host <c>SetProperty team=3</c>
/// (no UI team-select required — OBT may hide Spectator for human joiners).
/// </summary>
public static class MatchHostActor
{
    public const byte ActorNr = 1;
    public const string Name = LobbySession.ServerPlayerName; // "Server"
    /// <summary>Preferred: Spectator so Server stays out of T/CT fights. Fallback if clients break: leave unset (None).</summary>
    public const MatchTeam Team = MatchTeam.Spectator;

    /// <summary>
    /// Room <c>C2</c> after scene managers — phone-host SetProperties actor=0, value byte <c>10</c>
    /// (<c>*_SetProperties_len14.bin</c> / probe <c>20260722_003532_*</c>). Unlocks InitWaiting.
    /// </summary>
    public const byte C2AfterManagers = MatchC2States.WaitingPlayers;

    /// <summary>
    /// Phone-host bootstrap uses uid <c>Offline</c> on actor1 before managers
    /// (<c>20260722_003532_007_*</c>), not the display nick.
    /// </summary>
    public const string BootstrapUid = "Offline";

    /// <summary>
    /// Synthetic 1×1 JPEG for host <c>avatar</c> SetProperty (phone sends JPEG ByteArray before
    /// managers). Codec-built placeholder — not a capture replay.
    /// Built via <see cref="BuildPlaceholderAvatarJpeg"/> so a bad base64 never takes down
    /// the type (run-20260722_074123: invalid b64 → TypeInitializationException mid-bootstrap
    /// after SIP+uid/badge/from_lobby — consts are inlined, only this field runs the cctor).
    /// </summary>
    public static readonly byte[] PlaceholderAvatarJpeg = BuildPlaceholderAvatarJpeg();

    /// <summary>
    /// Prefer verified base64; on any failure return a minimal SOI…EOI stub so bootstrap
    /// can still reach managers + C2=10.
    /// </summary>
    private static byte[] BuildPlaceholderAvatarJpeg()
    {
        // 1×1 grey JPEG; base64 length is a multiple of 4 (unlike the broken 074123 string).
        const string b64 =
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDABALDA4MChAODQ4SERATGCgaGBYWGDEjJR0oOjM9PDkzODdA" +
            "SFxOQERXRTc4UG1RV19iZ2hnPk1xeXBkeFxlZ2P/2wBDARESEhgVGC8aGi9jQjhCY2NjY2NjY2NjY2Nj" +
            "Y2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2NjY2P/wAARCAABAAEDASIAAhEBAxEB/8QAH" +
            "wAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhM" +
            "UEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV" +
            "1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx" +
            "8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFB" +
            "gcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYn" +
            "LRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhY" +
            "aHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8v" +
            "P09fb3+Pn6/9oADAMBAAIRAxEAPwAooooA/9k=";
        try
        {
            var jpeg = Convert.FromBase64String(b64);
            if (jpeg.Length >= 2 && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
                return jpeg;
        }
        catch (FormatException)
        {
            // fall through — never throw from type initializer
        }

        // Absolute fallback: JFIF APP0 stub (still JPEG-shaped).
        return
        [
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9,
        ];
    }
}

/// <summary><c>gac</c> — CreateWorldObject kind (<c>fus.cwgb</c>).</summary>
public enum WorldObjectKind : byte
{
    Entity = 0,
    SceneManager = 255,
}

/// <summary>
/// Snapshot actor entry inside <c>gap</c> (<c>gak</c>): nr + nick + optional typed props + flag.
/// Phone-host thin Found (<c>20260722_003523_*</c>) uses empty props; identity/avatar are post-Found.
/// </summary>
public readonly struct MatchGapActor
{
    public byte ActorNr { get; init; }
    public string Name { get; init; }
    /// <summary>Optional typed props (<c>gak</c> PropertiesRecord). Empty for thin early Found.</summary>
    public IReadOnlyList<(string Key, LobbyVariant Value)>? Props { get; init; }
    /// <summary><c>gak.cwzj</c> — live capture false for both host + joiner.</summary>
    public bool Flag { get; init; }
}

/// <summary>
/// Scene-manager CreateWorldObject catalog from phone-host capture (names + fus trailing tag byte).
/// Tag byte is the last <c>fus.cwgf</c> field — stable per type name in capture, not a replay blob.
/// </summary>
public static class MatchSceneManagers
{
    public readonly record struct Entry(short Id, string Name, byte FusTag, byte[]? CatalogIds = null);

    /// <summary>
    /// RadarManager scene-object id in <b>this host's</b> bootstrap catalog (see <see cref="Bootstrap"/>).
    /// The phone host used id=4; ours assigns id=6 — clients learn the id from the bootstrap /
    /// ReCreateSceneManager, so authoring on our own id is correct (live: clients TX radar RPCs on id=6).
    /// The master fires <c>Rpc(7)</c> (<c>oet(dwa)</c>, 0-payload full radar/occlusion refresh) as part of
    /// every combat death — gold <c>/tmp/tdm-probe.log</c> RX#3786/3793 and RX#4266/4269 bracket the
    /// victim <c>Destroy</c> + <c>death</c> with two radar refreshes.
    /// </summary>
    public const short RadarManagerObjectId = 6;

    /// <summary>
    /// Weapon/grenade drop catalogs (byte item ids) observed on phone host WeaponDropManager / GrenadeManager.
    /// Rebuilt as int32 count + id bytes — same structure, not a memcpy of the packet.
    /// </summary>
    public static readonly byte[] DropCatalogIds =
    [
        11, 12, 13, 15, 16, 17, 18, 32, 33, 34, 35, 36, 37, 42, 43, 44, 45, 46, 47, 48, 49,
        51, 52, 53, 54, 62, 63, 64, 65, 70, 71, 72, 73, 75, 77, 78, 79, 80, 81, 82, 83, 85,
        86, 88, 89, 91, 92, 93, 94, 95, 100, 102, 101,
    ];

    public static readonly Entry[] Bootstrap =
    [
        new(1, "GameRpcHelper", 0xF8),
        new(2, "NextLevelVoteRpcHelper", 0xF9),
        new(3, "WaitForNextGameRpcHelper", 0xFA),
        new(4, "WeaponDropManager", 0xFC, DropCatalogIds),
        new(5, "GrenadeManager", 0xFD, DropCatalogIds),
        new(RadarManagerObjectId, "RadarManager", 0xF7),
        new(ChatManagerObjectId, "ChatManager", 0xFF),
        new(8, "BombManager", 0xFB),
    ];

    /// <summary>Dedicated bootstrap ChatManager scene id (phone gold Create uses id=5; Dedik TX id=7).</summary>
    public const short ChatManagerObjectId = 7;

    /// <summary>
    /// Wire type name for T-side pawn CreateWorldObject — length-prefixed <c>Tr_Tr</c>
    /// (ASCII dump often looks like <c>Tr_Tre</c> because fus tag <c>0x65</c>='e' follows).
    /// Joiner capture <c>20260722_004652_042_*</c> also uses <c>Tr_Tr</c> (id=257, not 129).
    /// </summary>
    public const string PlayerPawnNameTr = "Tr_Tr";

    /// <summary>
    /// Wire type name for CT-side pawn — length-prefixed <c>Ct_Ct</c>
    /// (ASCII dump often looks like <c>Ct_Cte</c> for the same fus-tag reason).
    /// Capture: <c>20260722_003536_024_*</c>.
    /// </summary>
    public const string PlayerPawnNameCt = "Ct_Ct";

    /// <summary>Obsolete alias — prefer <see cref="PlayerPawnNameTr"/>.</summary>
    public const string PlayerPawnName = PlayerPawnNameTr;

    /// <summary>
    /// Gold-observed <b>cosmetics-only</b> tag appended (length-prefixed) after the pawn spawn pose
    /// in a <c>Ct_Ct</c> CreateWorldObject trailing (capture <c>rx223</c> — 59B Ct trailing). It is
    /// just the agent/skins the player has equipped in their <b>locker/inventory</b> — a look, not
    /// a spawn or mesh-activation signal. An <b>empty</b> tag spawns a fully visible, killable pawn
    /// (gold <c>player_1547</c> uses a 45B empty-tag trailing), so this is <b>never</b> required for
    /// visibility/radar/damage. It is also <b>NOT</b> the TDM buy-menu weapon pick (separate,
    /// still-unreversed wire). Kept only so the probe can optionally mimic an equipped-locker look.
    /// </summary>
    public const string PawnAgentTagCt = "AgentCTLincoln";

    /// <summary>
    /// Gold-observed T-side cosmetics-only tag (capture <c>20260722_023514</c> — 70B <c>Tr_Tr</c>
    /// trailing). Same locker-cosmetics semantics as <see cref="PawnAgentTagCt"/>; optional and
    /// irrelevant to whether the pawn is visible/on-radar/damageable.
    /// </summary>
    public const string PawnAgentTagTr = "AgentTMarco";

    /// <summary>Phone-host player object id for first fighting pawn (<c>fus.cwgd</c>=129).</summary>
    public const short PlayerPawnId = 129;

    /// <summary><c>fus.cwgf</c> for fighting pawns in capture (both Tr and Ct = 101).</summary>
    public const byte PlayerPawnFusTag = 101;

    /// <summary>
    /// Sandstone 2x2 spawn trailing after fus tag — decoded from phone-host len=63 CreateWorldObject
    /// (not a blob replay). Layout: u8 flags, Vector3 pos, u8 rotFlags, Quaternion, u8, 8×0, u8, float, u8.
    /// Tr + Ct share trailing shape; pos/quat differ by team spawn point.
    /// </summary>
    public static class PawnSpawn
    {
        public const byte Flags = 0x11;
        public const byte RotFlags = 0x12;
        public const byte TrailingU8A = 100;
        public const byte TrailingU8B = 1;
        public const byte TrailingU8C = 0;
        public const float TrailingFloat = 3f;

        /// <summary>Tr Sandstone spawn — <c>20260721_234843_*_CreateWorldObject_len63</c>.</summary>
        public static readonly (float X, float Y, float Z) SandstonePosTr =
            (-15.273858f, 0.03357771f, 10.161121f);

        /// <summary>Tr Sandstone rotation (Y≈+90°).</summary>
        public static readonly (float X, float Y, float Z, float W) SandstoneQuatTr =
            (0f, 0.70710683f, 0f, 0.70710683f);

        /// <summary>Ct Sandstone spawn — <c>20260722_000923_023_*_CreateWorldObject_len63</c>.</summary>
        public static readonly (float X, float Y, float Z) SandstonePosCt =
            (34.326023f, 2.0938807f, 0.08310747f);

        /// <summary>Ct Sandstone rotation (Y≈−90°).</summary>
        public static readonly (float X, float Y, float Z, float W) SandstoneQuatCt =
            (0f, -0.70710683f, 0f, 0.70710683f);

        /// <summary>Obsolete alias — Tr side; prefer <see cref="SandstonePosTr"/>.</summary>
        public static readonly (float X, float Y, float Z) SandstonePos = SandstonePosTr;

        /// <summary>Obsolete alias — Tr side; prefer <see cref="SandstoneQuatTr"/>.</summary>
        public static readonly (float X, float Y, float Z, float W) SandstoneQuat = SandstoneQuatTr;
    }

    /// <summary>
    /// Standing WorldObjectState header constants from phone-host ticks
    /// (<c>20260722_003536_027_*</c> / Tr twin). Full avatar extension still opaque —
    /// host emits pose + equal-run prefix only (codec-built).
    /// </summary>
    public static class PawnState
    {
        /// <summary>Repeated extension time float (<c>5bfb073c</c>).</summary>
        public const float BaseTime = 0.008299674f;

        /// <summary>LE bytes <c>8b7cfd67</c> — stable across Tr/Ct standing ticks.</summary>
        public const uint Marker = 0x67FD7C8B;

        /// <summary>Phone u16 after marker advances ≈0x12 each ~50ms tick.</summary>
        public const ushort MarkerClockStep = 0x12;
    }
}

/// <summary>Default match gap room props matching live Ranked2v2 / Sandstone capture.</summary>
public static class MatchGapDefaults
{
    /// <summary><c>gap.booo</c> — live = 4 (likely max actors).</summary>
    public const byte MaxActorsHint = 4;

    /// <summary><c>gap.boou</c> — live = 1.</summary>
    public const byte TrailingByte = 1;

    public static List<(string Key, LobbyVariant Value)> BuildRoomProps(
        string gameModeId,
        string selectedLevel,
        byte c2 = 0)
    {
        return
        [
            (MatchRoomPropKeys.C0, LobbyVariant.FromString(gameModeId)),
            (MatchRoomPropKeys.C1, LobbyVariant.FromString(selectedLevel)),
            (MatchRoomPropKeys.C2, LobbyVariant.FromByte(c2)),
            (MatchRoomPropKeys.C3, LobbyVariant.FromByte(2)),
            (MatchRoomPropKeys.Region, LobbyVariant.FromString("local")),
            (MatchRoomPropKeys.GameType, LobbyVariant.FromByte(2)),
            (MatchRoomPropKeys.SessionId, LobbyVariant.FromInt(0)),
        ];
    }
}
