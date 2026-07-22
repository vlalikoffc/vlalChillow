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
    /// <summary>Room bomber actor nr (<c>bcb.cadb</c> / <c>opg</c>) — Int.</summary>
    public const string BomberId = "bomberId";

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
    /// WarmUp (<c>GameState/WarmUp</c>) — movable разминка before first round.
    /// Must <b>not</b> be conflated with freeze countdown (that is C2=22).
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
    /// RankedDefuse registry id (<c>ckq.xvv=111</c>). Allies phone-host round end does
    /// <b>not</b> use this — gold captures keep C2=<see cref="MatchStarted"/>. Do not TX 111
    /// for Allies; do <b>not</b> use 201 (<c>FinalHud</c>) for round end either.
    /// </summary>
    public const byte RoundEnd = 111;
    /// <summary>
    /// FinalHud / match-итоги state (<c>cjf.xvv=201</c>). Reads <c>FinalWinTeam</c>, not round UI.
    /// Publishing C2=201 mid-match shows green match WIN + empty scores — never use for round end.
    /// </summary>
    public const byte FinalHud = 201;
    /// <summary>Match results / итоги всей катки (after all rounds) — <c>ckc.xvv=205</c>.</summary>
    public const byte MatchResults = 205;
}

/// <summary>
/// Allies test timings (user): warmup 10s (movable) → 3s pre-start countdown →
/// prep 10s every round → live 90s → silent round-end pause 5s × 3 rounds.
/// </summary>
public static class MatchFlowTestParams
{
    /// <summary>Movable WarmUp — C2=21.</summary>
    public static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);
    /// <summary>WarmupWillFinish freeze countdown — C2=22 («MATCH WILL START IN»).</summary>
    public static readonly TimeSpan WarmupWillFinish = TimeSpan.FromSeconds(3);
    /// <summary>Visible «подготовка к раунду» — C2=31, every round including first.</summary>
    public static readonly TimeSpan PurchasePhase = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RoundDuration = TimeSpan.FromSeconds(90);
    /// <summary>Silent pause after round end UI (победа/поражение) — no Time deadline.</summary>
    public static readonly TimeSpan RoundEndPause = TimeSpan.FromSeconds(5);
    /// <summary>Bomb planted fuse (<c>cnl(float)</c>) — ~40s.</summary>
    public static readonly TimeSpan BombFuse = TimeSpan.FromSeconds(40);
    public const int TotalRounds = 3;
    /// <summary>Money on match/round start — bootstrap still uses 10000 like phone.</summary>
    public const int RoundStartMoney = 800;
    public const int BootstrapMoney = 10000;
    /// <summary>BombManager scene object id (bootstrap catalog id=8).</summary>
    public const short BombManagerObjectId = 8;
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
        new(6, "RadarManager", 0xF7),
        new(7, "ChatManager", 0xFF),
        new(8, "BombManager", 0xFB),
    ];

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
