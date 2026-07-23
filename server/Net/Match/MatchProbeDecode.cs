using System.Globalization;
using System.Text;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Human-readable ConnectAsClient match decode: every packet tagged
/// <c>MATCH</c> (room / game flow) vs <c>PLAYER</c> (actor / pawn) vs <c>SCENE</c>
/// (managers), with objectId→owner roster so WorldObjectState(204) shows who it is.
/// </summary>
public sealed class MatchProbeDecode
{
    private readonly Dictionary<byte, ActorInfo> _actors = new();
    private readonly Dictionary<short, ObjectInfo> _objects = new();
    private readonly Dictionary<string, string> _roomPropSnap = new(StringComparer.Ordinal);
    private byte? _localActor;

    public void SetLocalActor(byte? actorNr) => _localActor = actorNr;

    public void NoteActor(byte nr, string? name = null, MatchTeam? team = null)
    {
        if (!_actors.TryGetValue(nr, out var info))
            info = new ActorInfo { Nr = nr };
        if (!string.IsNullOrWhiteSpace(name))
            info.Name = name!;
        if (team is { } t)
            info.Team = t;
        _actors[nr] = info;
    }

    public void NoteObject(short id, byte? owner, string typeName, WorldObjectKind kind)
    {
        _objects[id] = new ObjectInfo
        {
            Id = id,
            Owner = owner,
            TypeName = typeName,
            Kind = kind,
        };
        if (owner is { } o && !_actors.ContainsKey(o))
            NoteActor(o);
    }

    public void ForgetObject(short id) => _objects.Remove(id);

    public void ForgetActor(byte nr)
    {
        _actors.Remove(nr);
        foreach (var kv in _objects.Where(kv => kv.Value.Owner == nr).Select(kv => kv.Key).ToList())
            _objects.Remove(kv);
    }

    /// <summary>Full multi-line decode for one match datagram. Empty when nothing useful.</summary>
    public string FormatPacket(
        string direction,
        int seq,
        MatchFrameFlags flags,
        MatchOpcode opcode,
        int? serverTime,
        ReadOnlySpan<byte> fullPayload,
        int bodyOffset,
        bool includeNoisyDetail)
    {
        var sb = new StringBuilder(512);
        var opName = MatchCodec.OpcodeName(opcode);
        sb.Append(CultureInfo.InvariantCulture,
            $"[{direction}#{seq}] {ClassifyHeader(opcode)} op={opName}({(byte)opcode}) " +
            $"flags={flags}");
        if (serverTime is int st)
            sb.Append(CultureInfo.InvariantCulture, $" stime={st}");
        sb.Append(CultureInfo.InvariantCulture, $" len={fullPayload.Length}");

        try
        {
            if (!MatchCodec.TryOpenBody(fullPayload, out _, out _, out _, out var body))
            {
                sb.Append(" — body open failed (encrypted/bad LZ4?)");
                return sb.ToString();
            }

            var r = new LobbyReader(body);
            switch (opcode)
            {
                case MatchOpcode.HandshakeResponse:
                    AppendHandshake(sb, r);
                    break;
                case MatchOpcode.JoinRoomResponse:
                    AppendJoinRoom(sb, r);
                    break;
                case MatchOpcode.ActorJoinedEvent:
                    AppendActorJoined(sb, r);
                    break;
                case MatchOpcode.ActorLeftEvent:
                    AppendActorLeft(sb, r);
                    break;
                case MatchOpcode.SetProperty:
                    AppendSetProperty(sb, r);
                    break;
                case MatchOpcode.SetProperties:
                    AppendSetProperties(sb, r);
                    break;
                case MatchOpcode.SetInternalProperty:
                    AppendSetInternal(sb, r);
                    break;
                case MatchOpcode.CreateWorldObject:
                    AppendCreateWorldObject(sb, r);
                    break;
                case MatchOpcode.DestroyWorldObject:
                    AppendDestroyWorldObject(sb, r);
                    break;
                case MatchOpcode.WorldObjectRpc:
                    AppendWorldObjectRpc(sb, r);
                    break;
                case MatchOpcode.WorldObjectState:
                    if (!includeNoisyDetail)
                        return ""; // caller rate-limits; empty = skip console
                    AppendWorldObjectState(sb, r);
                    break;
                case MatchOpcode.FetchServerTimeRequest:
                case MatchOpcode.FetchServerTimeResponse:
                    sb.Append(" — clock sync");
                    break;
                default:
                    sb.Append(CultureInfo.InvariantCulture,
                        $" — undecoded body={body.Length}B (kept in .bin capture)");
                    break;
            }
        }
        catch (Exception ex)
        {
            sb.Append(CultureInfo.InvariantCulture, $" — decode ERR: {ex.Message}");
        }

        return sb.ToString();
    }

    public string FormatRoster()
    {
        if (_actors.Count == 0 && _objects.Count == 0)
            return "[roster] (empty)";
        var sb = new StringBuilder();
        sb.AppendLine("[roster] actors:");
        foreach (var a in _actors.Values.OrderBy(x => x.Nr))
        {
            var me = _localActor == a.Nr ? " ★local" : "";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  actor={a.Nr} name='{a.Name}' team={FormatTeam(a.Team)}{me}");
        }
        sb.AppendLine("[roster] world objects (id → owner):");
        foreach (var o in _objects.Values.OrderBy(x => x.Id))
        {
            var who = DescribeOwner(o.Owner);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  id={o.Id} kind={o.Kind} type='{o.TypeName}' → {who}");
        }
        return sb.ToString().TrimEnd();
    }

    private string ClassifyHeader(MatchOpcode op) => op switch
    {
        MatchOpcode.SetProperty or MatchOpcode.SetProperties or MatchOpcode.SetInternalProperty
            => "PROP",
        MatchOpcode.CreateWorldObject or MatchOpcode.DestroyWorldObject
            or MatchOpcode.WorldObjectRpc or MatchOpcode.WorldObjectState
            => "WORLD",
        MatchOpcode.ActorJoinedEvent or MatchOpcode.ActorLeftEvent
            or MatchOpcode.JoinRoomResponse or MatchOpcode.HandshakeResponse
            => "ROOM",
        _ => "PKT",
    };

    private void AppendHandshake(StringBuilder sb, LobbyReader r)
    {
        var result = MatchCodec.ParseHandshakeResponseBody(r);
        sb.Append(CultureInfo.InvariantCulture, $" — MATCH handshake={result}");
    }

    private void AppendJoinRoom(StringBuilder sb, LobbyReader r)
    {
        var body = MatchCodec.ParseJoinRoomResponseBody(r);
        sb.Append(CultureInfo.InvariantCulture,
            $" — MATCH JoinRoom {MatchCodec.JoinRoomResultName(body.Result)} " +
            $"localActor={body.ActorNr?.ToString() ?? "-"} debug='{body.DebugMessage ?? ""}'");
        _localActor = body.ActorNr;

        if (!body.HasRoomProperties || !body.IsSuccess)
            return;

        // Gap left after ParseJoinRoomResponseBody — parse here.
        if (!TryReadGap(r, out var gap))
        {
            sb.Append(" gap=unparsed");
            return;
        }

        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture,
            $"  MATCH gap room='{gap.RoomName}' open={gap.Open} maxActors={gap.MaxActorsHint} trail={gap.TrailingByte}");
        sb.AppendLine();
        sb.Append("  MATCH room props:");
        foreach (var (k, v) in gap.RoomProps)
        {
            NoteRoomProp(k, v);
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"    {FormatPropLine(scope: "MATCH", actor: 0, k, v)}");
        }
        sb.AppendLine();
        sb.Append("  MATCH actors in Found:");
        foreach (var a in gap.Actors)
        {
            NoteActor(a.ActorNr, a.Name);
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"    actor={a.ActorNr} name='{a.Name}' flag={a.Flag} props={a.Props?.Count ?? 0}");
            if (a.Props is { Count: > 0 })
            {
                foreach (var (k, v) in a.Props)
                {
                    NoteActorProp(a.ActorNr, k, v);
                    sb.AppendLine();
                    sb.Append(CultureInfo.InvariantCulture,
                        $"      {FormatPropLine(scope: "PLAYER", actor: a.ActorNr, k, v)}");
                }
            }
        }
    }

    private void AppendActorJoined(StringBuilder sb, LobbyReader r)
    {
        if (!TryReadGak(r, out var actor))
        {
            sb.Append(" — PLAYER join (parse fail)");
            return;
        }
        NoteActor(actor.ActorNr, actor.Name);
        string? guid = null;
        if (r.Remaining > 0 && r.ReadBool())
            guid = r.ReadString();
        sb.Append(CultureInfo.InvariantCulture,
            $" — PLAYER JOIN actor={actor.ActorNr} name='{actor.Name}' guid='{guid ?? ""}'");
        if (actor.Props is { Count: > 0 })
        {
            foreach (var (k, v) in actor.Props)
            {
                NoteActorProp(actor.ActorNr, k, v);
                sb.AppendLine();
                sb.Append(CultureInfo.InvariantCulture,
                    $"    {FormatPropLine("PLAYER", actor.ActorNr, k, v)}");
            }
        }
    }

    private void AppendActorLeft(StringBuilder sb, LobbyReader r)
    {
        var nr = r.ReadByte();
        var name = ActorLabel(nr);
        ForgetActor(nr);
        sb.Append(CultureInfo.InvariantCulture, $" — PLAYER LEFT actor={nr} ({name})");
    }

    private void AppendSetProperty(StringBuilder sb, LobbyReader r)
    {
        var (actor, key, value) = MatchCodec.ParseSetPropertyBody(r);
        var scope = actor == 0 ? "MATCH" : "PLAYER";
        if (actor == 0)
            NoteRoomProp(key, value);
        else
            NoteActorProp(actor, key, value);
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"  {FormatPropLine(scope, actor, key, value)}");
        AppendDeltaHint(sb, actor, key, value);
    }

    private void AppendSetProperties(StringBuilder sb, LobbyReader r)
    {
        var (actor, props) = MatchCodec.ParseSetPropertiesBody(r);
        var scope = actor == 0 ? "MATCH" : "PLAYER";
        sb.Append(CultureInfo.InvariantCulture,
            $" — {scope} SetProperties actor={actor} ({ActorLabel(actor)}) count={props.Count}");
        foreach (var (k, v) in props)
        {
            if (actor == 0)
                NoteRoomProp(k, v);
            else
                NoteActorProp(actor, k, v);
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture, $"  {FormatPropLine(scope, actor, k, v)}");
            AppendDeltaHint(sb, actor, k, v);
        }
    }

    private void AppendSetInternal(StringBuilder sb, LobbyReader r)
    {
        // Host SetInternalProperty: actor + code + fzp — treat as PLAYER identity.
        var actor = r.ReadByte();
        var code = r.ReadByte();
        var value = r.ReadFzp();
        sb.Append(CultureInfo.InvariantCulture,
            $" — PLAYER SIP actor={actor} ({ActorLabel(actor)}) code={code} value={DescribeVariant(value)}");
    }

    private void AppendCreateWorldObject(StringBuilder sb, LobbyReader r)
    {
        var parsed = MatchCodec.ParseCreateWorldObjectBody(r);
        NoteObject(parsed.ObjectId, parsed.OwnerActorNr, parsed.TypeName, parsed.Kind);
        var scope = parsed.Kind == WorldObjectKind.SceneManager
            ? "SCENE"
            : (IsPlayerPawn(parsed.TypeName) ? "PLAYER" : "WORLD");
        var who = DescribeOwner(parsed.OwnerActorNr);
        sb.Append(CultureInfo.InvariantCulture,
            $" — {scope} Create id={parsed.ObjectId} type='{parsed.TypeName}' " +
            $"fus={parsed.FusTag} owner={who} trail={parsed.Trailing.Length}B");
        if (IsPlayerPawn(parsed.TypeName) && parsed.OwnerActorNr is { } o)
            NoteActor(o);
    }

    private void AppendDestroyWorldObject(StringBuilder sb, LobbyReader r)
    {
        var id = r.ReadInt16();
        var info = LookupObject(id);
        ForgetObject(id);
        sb.Append(CultureInfo.InvariantCulture,
            $" — {(info.Kind == WorldObjectKind.SceneManager ? "SCENE" : "PLAYER")} " +
            $"Destroy id={id} was type='{info.TypeName}' owner={DescribeOwner(info.Owner)}");
    }

    private void AppendWorldObjectRpc(StringBuilder sb, LobbyReader r)
    {
        var parsed = MatchCodec.ParseWorldObjectRpcBody(r);
        var info = LookupObject(parsed.ObjectId);
        var scope = info.Kind == WorldObjectKind.SceneManager
            ? "SCENE"
            : (IsPlayerPawn(info.TypeName) ? "PLAYER" : "WORLD");
        sb.Append(CultureInfo.InvariantCulture,
            $" — {scope} Rpc id={parsed.ObjectId} type='{info.TypeName}' " +
            $"owner={DescribeOwner(info.Owner)} rpc={parsed.RpcId} gaa={parsed.GaaTarget} " +
            $"field={parsed.Field} t={parsed.TimeValue:0.###} payload={parsed.Payload.Length}B");
    }

    private void AppendWorldObjectState(StringBuilder sb, LobbyReader r)
    {
        // Standing / live layout: objectId i16, pad i32, seq i32, tick float, pad i32, xyz…
        if (r.Remaining < 2 + 4 + 4 + 4)
        {
            sb.Append(" — PLAYER? State (too short)");
            return;
        }
        var id = r.ReadInt16();
        _ = r.ReadInt32();
        var seq = r.ReadInt32();
        var tick = r.ReadFloat();
        _ = r.ReadInt32();
        float x = 0, y = 0, z = 0;
        if (r.Remaining >= 12)
        {
            x = r.ReadFloat();
            y = r.ReadFloat();
            z = r.ReadFloat();
        }
        var info = LookupObject(id);
        var scope = info.Kind == WorldObjectKind.SceneManager ? "SCENE" : "PLAYER";
        sb.Append(CultureInfo.InvariantCulture,
            $" — {scope} State id={id} type='{info.TypeName}' owner={DescribeOwner(info.Owner)} " +
            $"seq={seq} tick={tick:0.##} pos=({x:0.##},{y:0.##},{z:0.##})");
    }

    private void NoteRoomProp(string key, LobbyVariant value)
    {
        _roomPropSnap[key] = DescribeVariantAnnotated(key, value);
    }

    private void NoteActorProp(byte actor, string key, LobbyVariant value)
    {
        if (key == MatchRoomPropKeys.Team && value.Kind == LobbyVariantKind.Byte)
            NoteActor(actor, team: (MatchTeam)value.Byte);
        else if (key == MatchRoomPropKeys.Uid && value.Kind == LobbyVariantKind.String)
            NoteActor(actor); // ensure slot
        // keep actor entry
        if (!_actors.ContainsKey(actor))
            NoteActor(actor);
    }

    private void AppendDeltaHint(StringBuilder sb, byte actor, string key, LobbyVariant value)
    {
        if (actor != 0)
            return;
        if (key == MatchRoomPropKeys.C2 && value.Kind == LobbyVariantKind.Byte)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  ★ MATCH PHASE → C2={value.Byte} ({FormatC2(value.Byte)})");
        }
        else if (key is MatchRoomPropKeys.Time or MatchRoomPropKeys.Round
                 or MatchRoomPropKeys.RoundStartTime or MatchRoomPropKeys.RoundCount
                 or MatchRoomPropKeys.TrScore or MatchRoomPropKeys.CtScore
                 or MatchRoomPropKeys.WinTeam or MatchRoomPropKeys.BomberId
                 or MatchRoomPropKeys.FinalWinTeam)
        {
            sb.AppendLine();
            sb.Append(CultureInfo.InvariantCulture,
                $"  ★ MATCH FLOW field '{key}' = {DescribeVariantAnnotated(key, value)}");
        }
    }

    private string FormatPropLine(string scope, byte actor, string key, LobbyVariant value)
    {
        var who = actor == 0
            ? "room"
            : $"actor={actor} ({ActorLabel(actor)})";
        return $"{scope} {who}  {key} = {DescribeVariantAnnotated(key, value)}";
    }

    private string DescribeVariantAnnotated(string key, LobbyVariant v)
    {
        var raw = DescribeVariant(v);
        if (key == MatchRoomPropKeys.C2 && v.Kind == LobbyVariantKind.Byte)
            return $"{raw}  ← phase {FormatC2(v.Byte)}";
        if (key == MatchRoomPropKeys.Team && v.Kind == LobbyVariantKind.Byte)
            return $"{raw}  ← {FormatTeam((MatchTeam)v.Byte)}";
        if (key == MatchWinTeamKeys.MvpCode || key == "mvpCode")
        {
            if (v.Kind == LobbyVariantKind.Byte)
                return $"{raw}  ← {FormatMvp(v.Byte)}";
        }
        if (key == MatchRoomPropKeys.WinTeam && v.Kind == LobbyVariantKind.Properties && v.Props is { } nested)
        {
            var parts = nested.Select(p =>
                $"{p.Key}={DescribeVariantAnnotated(p.Key, p.Value)}");
            return "{ " + string.Join(", ", parts) + " }";
        }
        if (key == MatchRoomPropKeys.Death)
            return $"{raw}  ← {(IsNonZero(v) ? "DEAD" : "alive")}";
        if (key is MatchRoomPropKeys.Kills or MatchRoomPropKeys.RoundKills or MatchRoomPropKeys.Mvp
            or MatchRoomPropKeys.Money or MatchRoomPropKeys.Ping
            or MatchRoomPropKeys.TrScore or MatchRoomPropKeys.CtScore
            or MatchRoomPropKeys.BomberId or MatchRoomPropKeys.Round
            or MatchRoomPropKeys.RoundCount)
            return raw;
        if (key == MatchRoomPropKeys.Time || key == MatchRoomPropKeys.RoundStartTime)
        {
            if (v.Kind == LobbyVariantKind.Double)
                return $"{raw}  ← deadline/clock";
        }
        return raw;
    }

    private static string DescribeVariant(LobbyVariant v) => v.Kind switch
    {
        LobbyVariantKind.Null => "null",
        LobbyVariantKind.Bool => v.Bool ? "true" : "false",
        LobbyVariantKind.Int => v.Int.ToString(CultureInfo.InvariantCulture),
        LobbyVariantKind.Byte => $"byte:{v.Byte}",
        LobbyVariantKind.Double => v.Double.ToString("G", CultureInfo.InvariantCulture),
        LobbyVariantKind.String => $"'{v.String}'",
        LobbyVariantKind.StringArray =>
            "[" + string.Join(", ", v.Strings ?? Array.Empty<string>()) + "]",
        LobbyVariantKind.ByteArray => $"bytes[{v.Bytes?.Length ?? 0}]",
        LobbyVariantKind.Properties when v.Props is { } p =>
            "{ " + string.Join(", ", p.Select(x => $"{x.Key}={DescribeVariant(x.Value)}")) + " }",
        LobbyVariantKind.Properties => "{}",
        _ => v.ToString(),
    };

    private static bool IsNonZero(LobbyVariant v) => v.Kind switch
    {
        LobbyVariantKind.Int => v.Int != 0,
        LobbyVariantKind.Byte => v.Byte != 0,
        LobbyVariantKind.Bool => v.Bool,
        _ => false,
    };

    public static string FormatC2(byte c2) => c2 switch
    {
        MatchC2States.WaitingPlayers => "WaitingPlayers",
        MatchC2States.DeathMatchPreWarmup => "DeathMatchPreWarmup / freeforall",
        MatchC2States.WarmUp => "WarmUp (разминка)",
        MatchC2States.WarmupWillFinish => "WarmupWillFinish (freeze countdown)",
        MatchC2States.DeathMatchLive => "DeathMatchLive",
        MatchC2States.PurchasePhase => "PurchasePhase (prep/buy)",
        MatchC2States.BombPlanted => "BombPlanted",
        MatchC2States.MatchStarted => "MatchStarted / Live (or round-end bag on Allies)",
        MatchC2States.RoundEnd => "RoundEnd(111) — Allies phone uses 101, not this",
        MatchC2States.DeathMatchEnded => "DeathMatchEnded",
        MatchC2States.FinalHud => "FinalHud (match WIN screen)",
        MatchC2States.MatchResults => "MatchResults (итоги катки)",
        MatchC2States.MatchTeardown => "MatchTeardown",
        _ => $"unknown-C2-{c2}",
    };

    public static string FormatTeam(MatchTeam t) => t switch
    {
        MatchTeam.None => "None",
        MatchTeam.Tr => "Tr (T)",
        MatchTeam.Ct => "Ct (CT)",
        MatchTeam.Spectator => "Spectator",
        _ => $"team={(byte)t}",
    };

    public static string FormatMvp(byte code) => code switch
    {
        MatchMvpCodes.None => "None",
        MatchMvpCodes.PlantingBomb => "PlantingBomb",
        MatchMvpCodes.DefusingBomb => "DefusingBomb",
        MatchMvpCodes.MostEliminations => "MostEliminations",
        _ => $"mvpCode={code}",
    };

    private static bool IsPlayerPawn(string typeName) =>
        typeName is MatchSceneManagers.PlayerPawnNameCt or MatchSceneManagers.PlayerPawnNameTr
            or "Ct_Ct" or "Tr_Tr";

    private string ActorLabel(byte nr)
    {
        if (_actors.TryGetValue(nr, out var a) && !string.IsNullOrEmpty(a.Name))
            return $"'{a.Name}'/{FormatTeam(a.Team)}";
        if (nr == MatchHostActor.ActorNr)
            return "'Server'/Spectator";
        if (_localActor == nr)
            return "local-probe";
        return "unknown";
    }

    private string DescribeOwner(byte? owner) =>
        owner is { } o ? $"actor={o} ({ActorLabel(o)})" : "no-owner";

    private ObjectInfo LookupObject(short id) =>
        _objects.TryGetValue(id, out var o)
            ? o
            : new ObjectInfo { Id = id, TypeName = "?", Kind = WorldObjectKind.Entity };

    private static bool TryReadGap(LobbyReader r, out MatchGapRoom gap)
    {
        gap = default;
        try
        {
            var roomName = r.ReadString();
            var open = r.ReadBool();
            var maxActors = r.ReadByte();
            List<(string Key, LobbyVariant Value)> props = [];
            if (r.ReadBool())
                props = r.ReadBareProps();
            var actorCount = r.ReadByte();
            var actors = new List<MatchGapActor>(actorCount);
            for (var i = 0; i < actorCount; i++)
            {
                if (!TryReadGak(r, out var a))
                    return false;
                actors.Add(a);
            }
            var trail = r.Remaining > 0 ? r.ReadByte() : (byte)0;
            gap = new MatchGapRoom
            {
                RoomName = roomName,
                Open = open,
                MaxActorsHint = maxActors,
                RoomProps = props,
                Actors = actors,
                TrailingByte = trail,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadGak(LobbyReader r, out MatchGapActor actor)
    {
        actor = default;
        try
        {
            var nr = r.ReadByte();
            var name = r.ReadString();
            var hasTyped = r.ReadBool();
            List<(string Key, LobbyVariant Value)>? props = null;
            if (hasTyped)
                props = r.ReadTypedProps();
            var flag = r.ReadBool();
            actor = new MatchGapActor
            {
                ActorNr = nr,
                Name = name,
                Props = props,
                Flag = flag,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class ActorInfo
    {
        public byte Nr;
        public string Name = "";
        public MatchTeam Team = MatchTeam.None;
    }

    private sealed class ObjectInfo
    {
        public short Id;
        public byte? Owner;
        public string TypeName = "?";
        public WorldObjectKind Kind;
    }
}
