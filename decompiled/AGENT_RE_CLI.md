# Agent CLI reverse stack (StandChillow) — **fast path first**

Daily reverse for protocol/match logic must finish in **seconds–minutes**, not hours.
**Never** start a full Ghidra auto-analysis of all of `libil2cpp.so` as the default path.

Wire / packet truth still comes from **ConnectAsClient probes** — decompile explains *logic*; probes define *bytes*.  
**Do not invent wire fields/opcodes. Do not paste capture blobs as host replies.**

---

## PRIMARY workflow (use this)

```text
1. Cpp2IL dumps (DiffableCs / ISIL / DummyDLL)  → method names & managed shape
2. Il2CppInspectorRedux JSON                    → name ↔ RVA (seconds–minutes once)
3. Rizin / strings                              → find string xrefs (WinTeam, bomberId, …)
4. Ghidra ONLY for one RVA                      → -noanalysis import + decompile that addr
```

### 1) Cpp2IL (already in tree) — first stop

| Artifact | Path |
|----------|------|
| Cpp2IL binary | `decompiled/tools/Cpp2IL-pr21-Linux` (metadata v31 / Unity ~2022) |
| DiffableCs | `decompiled/cpp2il_cs/` |
| ISIL disasm | `decompiled/cpp2il_isil/IsilDump/` |
| DummyDLL log | `decompiled/cpp2il_dummydll.log` |

Example — LAN lobby client body already dumped:

```bash
less decompiled/cpp2il_isil/IsilDump/Chillow.StandChillow.LanLobby/Chillow/StandChillow/LanLobby/Client/LobbyClient.txt
# Method est bjnv() starts at VA 0x03248F8C
```

Prefer reading ISIL/DiffableCs before opening Ghidra.

### 2) Il2CppInspectorRedux — name ↔ RVA map

| Item | Path |
|------|------|
| CLI | `decompiled/tools/Il2CppInspectorRedux/Il2CppInspectorRedux.CLI-linux-x64/Il2CppInspector.Redux.CLI` |
| Needs | .NET **10** runtime (+ AspNetCore) under `~/.dotnet` |
| Inputs | `decompiled/apk_extract/lib/arm64-v8a/libil2cpp.so` + `…/Metadata/global-metadata.dat` |
| Output | `decompiled/inspector_out/` → `il2cpp.json`, `il2cpp.py`, `il2cpp.h` |

Re-run (already done once):

```bash
INS=decompiled/tools/Il2CppInspectorRedux/Il2CppInspectorRedux.CLI-linux-x64/Il2CppInspector.Redux.CLI
$INS process \
  decompiled/apk_extract/lib/arm64-v8a/libil2cpp.so \
  decompiled/apk_extract/assets/bin/Data/Managed/Metadata/global-metadata.dat \
  --disassembler Ghidra -m -o decompiled/inspector_out
```

Lookup RVA (example: discovery host listen `gpe.bqkg`):

```bash
python3 - <<'PY'
import json
mds = json.load(open("decompiled/inspector_out/il2cpp.json"))["addressMap"]["methodDefinitions"]
for m in mds:
    if m.get("name", "").endswith("4bqkgEv"):
        print(m["virtualAddress"], m.get("dotNetSignature"))
        break
# → 0x0323BA54  Void bqkg()
PY
```

Full `il2cpp.py` naming over the whole binary is **optional** and slow; agents should query JSON for the few RVAs they need.

### 3) Rizin / strings — triage in seconds

| Item | Path |
|------|------|
| Rizin (static, no root) | `/home/vlal/tools/rizin/bin/rizin` (v0.9.1) |
| Binary | `decompiled/apk_extract/lib/arm64-v8a/libil2cpp.so` |

Prefer **targeted** commands. Cold-open of the 119MB ELF can take tens of seconds even for `pd` — still better than multi-hour Ghidra AA, but **Cpp2IL/Inspector first** when you already have a name.

```bash
RIZIN=/home/vlal/tools/rizin/bin/rizin
SO=decompiled/apk_extract/lib/arm64-v8a/libil2cpp.so

# Avoid full-file `izz` scans as the first move (slow on this SO).
# After Inspector RVA lookup, disassemble a small window:
$RIZIN -q -c 's 0x0323BA54; pd 40' "$SO"

# Property names may live in metadata, not as plain C strings in .rodata —
# use DiffableCs / probe logs for WinTeam, bomberId, GameModeId text.
```

Fedora package `dnf install rizin` also works if you have sudo; static tarball is already under `/home/vlal/tools/rizin/`.

### 4) Ghidra — **single function only** (optional native C)

| Item | Path |
|------|------|
| Ghidra | `/home/vlal/tools/ghidra_12.1.2_PUBLIC` |
| JDK | `/home/vlal/tools/jdk-21.0.11+10` (`JAVA_HOME_OVERRIDE` set in Ghidra `support/launch.properties`) |
| Smoke project (no full AA) | `decompiled/ghidra_proj/StandChillowSmoke` |
| Smoke script | `decompiled/ghidra_scripts/SmokeDecompileBqkg.java` |
| Smoke output | `decompiled/ghidra_smoke_bqkg.c` (`gpe.bqkg` @ `0x0323BA54`) |

**Allowed default:**

```bash
export JAVA_HOME=/home/vlal/tools/jdk-21.0.11+10
GHIDRA=/home/vlal/tools/ghidra_12.1.2_PUBLIC
PROJ=decompiled/ghidra_proj
SO=decompiled/apk_extract/lib/arm64-v8a/libil2cpp.so
SCRIPTS=decompiled/ghidra_scripts

# Import once with -noanalysis (minutes). Reuse project afterward.
$GHIDRA/support/analyzeHeadless "$PROJ" StandChillowSmoke \
  -import "$SO" -processor "AARCH64:LE:64:v8A" -loader ElfLoader \
  -noanalysis -scriptPath "$SCRIPTS" -postScript SmokeDecompileBqkg.java
```

To decompile a **different** RVA: copy the smoke script, change `TARGET`, run `-process libil2cpp.so -noanalysis -postScript …` (do **not** re-import if the program is already in the project).

**Forbidden as daily default:** full auto-analysis of entire `libil2cpp.so` (multi-hour). That is an optional overnight luxury only if someone explicitly wants a fully analyzed DB for browsing — **not** required to answer “how does X work?”.

---

## MCP (on-demand decompile of a known RVA)

Wired in `~/.cursor/mcp.json` and `standdrochillov/.cursor/mcp.json`:

- Server: `ghidra_headless_mcp`
- Binary: `decompiled/tools/ghidra-headless-mcp/.venv/bin/ghidra-headless-mcp` (Python 3.12 + pyghidra)
- `GHIDRA_INSTALL_DIR` / `--ghidra-install-dir`: `/home/vlal/tools/ghidra_12.1.2_PUBLIC`
- `JAVA_HOME`: `/home/vlal/tools/jdk-21.0.11+10`

**Agent rule:** resolve RVA via Inspector JSON or Cpp2IL/ISIL **first**, then ask MCP/Ghidra to decompile **that address only**. Do not ask MCP to “analyze the whole binary.”

Restart Cursor (or reload MCP) after config changes.

Smoke without Cursor:

```bash
# fake backend (no Ghidra) — tooling check only
decompiled/tools/ghidra-headless-mcp/.venv/bin/ghidra-headless-mcp --fake-backend --help
```

---

## Smoke checklist (done / reproducible in minutes)

| Step | Result |
|------|--------|
| Inspector `bqkg` | VA `0x0323BA54` (`Void bqkg()` / `gpe.bqkg`) |
| Ghidra `-noanalysis` + script | `decompiled/ghidra_smoke_bqkg.c` written |
| Cpp2IL ISIL | `LobbyClient` methods with VAs under `cpp2il_isil/…` |
| Full Ghidra AA | **Aborted / not required** — do not restart unless explicitly requested |

---

## Install paths (reference)

| Tool | Location |
|------|----------|
| Ghidra 12.1.2 | `/home/vlal/tools/ghidra_12.1.2_PUBLIC` |
| Temurin JDK 21 | `/home/vlal/tools/jdk-21.0.11+10` |
| Rizin 0.9.1 static | `/home/vlal/tools/rizin/bin/rizin` |
| Il2CppInspectorRedux 2026.2 CLI | `decompiled/tools/Il2CppInspectorRedux/…` |
| ghidra-headless-mcp | `decompiled/tools/ghidra-headless-mcp/` |
| APK extract | `decompiled/apk_extract/` |
| Inspector export | `decompiled/inspector_out/` |

---

## Protocol fidelity (hard rules)

1. Probes / captures = source of truth for packets.
2. Decompile / DiffableCs / ISIL = source of truth for *client logic* and names/RVAs.
3. Never invent opcodes, envelope fields, or timers into live host replies.
4. Never replay capture blobs as canned host responses — decode → understand → re-implement.
