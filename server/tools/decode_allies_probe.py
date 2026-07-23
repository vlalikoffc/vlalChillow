#!/usr/bin/env python3
"""Decode ConnectAsClient allies-probe SetProperties captures → C2/stime timeline."""
import struct
import sys
from pathlib import Path

try:
    import lz4.block as lz4block
except ImportError:
    lz4block = None

HAS_SERVER_TIME = 4
IS_COMPRESSED = 2
OP_SET_PROPERTIES = 100
OP_SET_PROPERTY = 101

C2_NAMES = {
    10: "WaitingPlayers",
    11: "DeathMatchPreWarmup",
    21: "WarmUp",
    22: "PreStart/WarmupWillFinish",
    31: "PurchasePhase/Prep",
    40: "BombPlanted",
    101: "MatchStarted/RoundEnd",
    111: "HalfTimeIntro",
    112: "HalfTimeSwap",
    113: "HalfTimeTransition",
    200: "MatchEndBag",
    201: "FinalHud",
    255: "Teardown",
}


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def remaining(self) -> int:
        return len(self.data) - self.pos

    def read(self, n: int) -> bytes:
        if self.pos + n > len(self.data):
            raise EOFError(f"need {n} at {self.pos} len={len(self.data)}")
        b = self.data[self.pos : self.pos + n]
        self.pos += n
        return b

    def read_u8(self) -> int:
        return self.read(1)[0]

    def read_i16(self) -> int:
        return struct.unpack_from("<h", self.read(2))[0]

    def read_i32(self) -> int:
        return struct.unpack_from("<i", self.read(4))[0]

    def read_f64(self) -> float:
        return struct.unpack_from("<d", self.read(8))[0]

    def read_varint(self) -> int:
        result = 0
        shift = 0
        while True:
            b = self.read_u8()
            result |= (b & 0x7F) << shift
            if (b & 0x80) == 0:
                return result
            shift += 7
            if shift > 35:
                raise ValueError("varint too long")

    def read_string(self) -> str:
        ln = self.read_varint()
        if ln < 0:
            raise ValueError(f"bad string len {ln}")
        return self.read(ln).decode("utf-8", errors="replace")

    def read_fzp(self):
        code = self.read_u8()
        if code == 6:
            return ("bool", self.read_u8() != 0)
        if code == 1:
            return ("int", self.read_i32())
        if code == 2:
            return ("short", self.read_i16())
        if code == 5:
            return ("double", self.read_f64())
        if code == 255:
            return ("byte", self.read_u8())
        if code == 0:
            return ("null", None)
        if code == 10:
            return read_fzu(self)
        self.pos -= 1
        return read_fzu(self)


def read_fzu(r: Reader):
    code = r.read_u8()
    if code == 2:
        return ("bool", r.read_u8() != 0)
    if code == 3:
        return ("byte", r.read_u8())
    if code == 6:
        return ("short", r.read_i16())
    if code == 9:
        return ("int", r.read_i32())
    if code == 13:
        return ("string", r.read_string())
    if code == 14:
        n = r.read_i16()
        return ("strings", [r.read_string() for _ in range(n)])
    if code == 10:
        n = r.read_i16()
        props = []
        for _ in range(n):
            props.append((r.read_string(), r.read_fzp()))
        return ("props", props)
    if code == 4:
        ln = r.read_i32()
        return ("bytes", r.read(ln))
    if code == 255:
        return ("null", None)
    raise ValueError(f"unsupported fzu {code} at {r.pos}")


def open_body(payload: bytes):
    if len(payload) < 2:
        return None
    flags = payload[0]
    opcode = payload[1]
    off = 2
    stime = None
    if flags & HAS_SERVER_TIME:
        if len(payload) < off + 4:
            return None
        stime = struct.unpack_from("<i", payload, off)[0]
        off += 4
    if flags & IS_COMPRESSED:
        if len(payload) < off + 4:
            return None
        unc_len = struct.unpack_from("<i", payload, off)[0]
        comp = payload[off + 4 :]
        if lz4block is None:
            raise RuntimeError("pip install lz4 for compressed captures")
        try:
            body = lz4block.decompress(comp, uncompressed_size=unc_len)
        except Exception as exc:
            raise RuntimeError(f"lz4 decompress failed: {exc}") from exc
        return flags, opcode, stime, body
    body = payload[off:]
    return flags, opcode, stime, body


def parse_set_properties(body: bytes):
    r = Reader(body)
    actor = r.read_u8()
    count = r.read_i16()
    props = []
    for _ in range(count):
        props.append((r.read_string(), r.read_fzp()))
    return actor, props


def parse_set_property(body: bytes):
    r = Reader(body)
    actor = r.read_u8()
    key = r.read_string()
    val = r.read_fzp()
    return actor, key, val


def double_val(v):
    return v[1] if v and v[0] == "double" else None


def collect_events(cap_dir: Path, patterns):
    files = []
    for pat in patterns:
        files.extend(cap_dir.glob(f"{pat}*SetPropert*.bin"))
    files = sorted(set(files), key=lambda p: p.name)

    events = []
    for path in files:
        payload = path.read_bytes()
        try:
            opened = open_body(payload)
        except Exception as e:
            print(f"# skip {path.name}: {e}", file=sys.stderr)
            continue
        if not opened:
            continue
        _flags, opcode, stime, body = opened
        try:
            if opcode == OP_SET_PROPERTIES:
                actor, props = parse_set_properties(body)
                if actor != 0:
                    continue
                d = {k: v for k, v in props}
                c2 = d.get("C2")
                if c2 and c2[0] == "byte":
                    events.append({
                        "file": path.name,
                        "stime": stime,
                        "c2": c2[1],
                        "round": d.get("Round"),
                        "time": double_val(d.get("Time")),
                        "rst": double_val(d.get("RoundStartTime")),
                        "bomber": d.get("bomberId"),
                        "tr_score": d.get("TrScore"),
                        "ct_score": d.get("CtScore"),
                        "swapped": d.get("swapped_team"),
                        "win": d.get("WinTeam"),
                        "len": len(payload),
                        "keys": list(d.keys()),
                    })
            elif opcode == OP_SET_PROPERTY and path.name.endswith("_len14.bin"):
                actor, key, val = parse_set_property(body)
                if actor == 0 and key == "C2" and val[0] == "byte":
                    events.append({
                        "file": path.name,
                        "stime": stime,
                        "c2": val[1],
                        "round": None,
                        "time": None,
                        "rst": None,
                        "bomber": None,
                        "tr_score": None,
                        "ct_score": None,
                        "swapped": None,
                        "win": None,
                        "len": len(payload),
                        "keys": ["C2"],
                    })
        except Exception as e:
            print(f"# skip {path.name}: {e}", file=sys.stderr)

    events.sort(key=lambda e: (e["stime"] or 0, e["file"]))
    return events


def print_table(events):
    prev_stime = None
    prev_time = None
    print(
        "stime\tΔms\tC2\tname\tRound\tTime\tRST\tΔTime-RST\t"
        "ΔTime-prev\tlen\tkeys\tfile"
    )
    for e in events:
        st = e["stime"]
        delta = ""
        if st is not None and prev_stime is not None:
            delta = f"{st - prev_stime}"
        if st is not None:
            prev_stime = st
        rnd = e["round"][1] if e["round"] else ""
        tm = e["time"]
        rst = e["rst"]
        dtr = f"{tm - rst:.3f}" if tm is not None and rst is not None else ""
        dtp = ""
        if tm is not None and prev_time is not None:
            dtp = f"{tm - prev_time:.3f}"
        if tm is not None:
            prev_time = tm
        tm_s = f"{tm:.3f}" if tm is not None else ""
        rst_s = f"{rst:.3f}" if rst is not None else ""
        name = C2_NAMES.get(e["c2"], "?")
        print(
            f"{st}\t{delta}\t{e['c2']}\t{name}\t{rnd}\t{tm_s}\t{rst_s}\t{dtr}\t"
            f"{dtp}\t{e['len']}\t{','.join(e['keys'])}\t{e['file']}"
        )


def main():
    cap_dir = Path(sys.argv[1] if len(sys.argv) > 1 else "bin/Release/net8.0/captures")
    patterns = [
        "20260723_2157*",
        "20260723_2200*",
        "20260723_2201*",
        "20260723_2202*",
        "20260723_2203*",
        "20260723_2204*",
        "20260723_2205*",
        "20260723_2206*",
    ]
    events = collect_events(cap_dir, patterns)
    print_table(events)
    print(f"# events={len(events)}", file=sys.stderr)


if __name__ == "__main__":
    main()
