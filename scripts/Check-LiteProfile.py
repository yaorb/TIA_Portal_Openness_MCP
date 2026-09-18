# -*- coding: utf-8 -*-
"""Assert that the DEFAULT (lite) profile is a USABLE profile, and that full still differs.

lite is now the default roster for every host, so a model restricted to it must still be able
to walk the golden path end to end, and to reach everything outside it. It could not: ImportFromDocuments / ExportAsDocuments / GetBlocks / GetBlockInfo /
GetCrossReferences were all [L2], so a lite session could open a project and see the tree but
could neither list a block nor use the PREFERRED document import path. Nothing caught that,
because nothing checked it. This does.

It also guards the check itself. When lite became the default, "full" was still being requested
by *unsetting* the env var — so both probes returned the same ~48 tools and every assertion here
passed vacuously. full must now be requested explicitly AND come back strictly larger.

Usage:  python scripts/Check-LiteProfile.py [path-to-TiaMcpServer.exe]
                                        [--tia-major-version N] [--tia-portal-location DIR]
Exit 0 = lite is self-sufficient, fits the host cap, and can reach everything else.

多版本机器上的两个坑，这一版都堵上了：
* 引擎 exe 现在**自动挑**：显式给路径就用手给的，否则取 runtime\v<N> 里版本最高的那个。
  以前写死 runtime\v21，机器上只交付了 v20 时它连启动都到不了，报错还长得像「工具档位坏了」。
* 探测时**显式传 --tia-major-version**（默认取 exe 所在目录的版本号）。自动探测取的是
  「本机最高的 TIA 版本」，而交付引擎是按某个版本编的：实测一台 C 盘装 V18、E 盘装 V21 的机器，
  自动探测答 V18，v21 的 exe 于是死在 Siemens.Engineering.Base 的加载上 —— 报出来的却是
  「引擎没输出」，指不到真正的原因。TIA 装在非默认盘时再加 --tia-portal-location。
"""
import json
import os
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]

# The documented golden path: orientation -> connect/open -> read -> author -> compile -> save.
# Every name here is referenced by the server instructions, README or GetAuthoringGuide, so a
# profile that omits one is advertising a workflow it cannot perform.
REQUIRED = [
    # orientation / diagnostics
    "Bootstrap", "Doctor", "GetAuthoringGuide", "GetState",
    # session + project
    "Connect", "Disconnect", "OpenProject", "CreateProject", "AttachToOpenProject",
    "CloseProject", "SaveProject", "GetProject", "GetProjectTree", "GetSoftwareTree",
    # read / understand
    "GetBlocks", "GetBlocksWithHierarchy", "GetBlockInfo", "DescribeBlockLogic",
    "GetCrossReferences", "GetPlcTagTables",
    # author (golden path: SD documents preferred, SCL external source alternative)
    "ScaffoldProject", "PlcBuildAndImport", "WritePlcSclSourceFile",
    "ImportFromDocuments", "ExportAsDocuments",
    "ImportBlocksFromDocuments", "ExportBlocksAsDocuments",
    "GenerateBlocksFromExternalSource",
    # verify
    "CompileSoftware", "CompileAndDiagnosePlc",
    # the bridge out of lite — without these two, lite is a dead end for the other ~155 tools
    "FindTools", "CallTool",
]

# VS Code refuses to enable more tools than this; lite exists to stay under it.
HOST_TOOL_CAP = 128


def parse_args(argv):
    """[exe] plus two pass-throughs. Kept hand-rolled like the other scripts here (no argparse)."""
    exe = None
    major = None
    portal = None
    i = 0
    while i < len(argv):
        a = argv[i]
        if a in ("--tia-major-version", "--tia-portal-location", "--exe"):
            if i + 1 >= len(argv):
                raise SystemExit("missing value for " + a)
            if a == "--tia-major-version":
                major = int(argv[i + 1])
            elif a == "--tia-portal-location":
                portal = argv[i + 1]
            else:
                exe = pathlib.Path(argv[i + 1])
            i += 2
            continue
        if not a.startswith("-"):
            exe = pathlib.Path(a)
            i += 1
            continue
        raise SystemExit("unknown argument: " + a)
    return exe, major, portal


def engines_in_bundle():
    """Every engine the checkout ships, highest version first."""
    runtime = ROOT / "runtime"
    found = []
    if runtime.is_dir():
        for entry in runtime.iterdir():
            m = re.fullmatch(r"v(\d+)", entry.name)
            if m and (entry / "TiaMcpServer.exe").exists():
                found.append((int(m.group(1)), entry / "TiaMcpServer.exe"))
    return sorted(found, key=lambda t: t[0], reverse=True)


def resolve_engine(exe, major):
    """Returns (exe, tia major to request). The version comes from the exe's own folder name:
    the roster is a property of that binary, and pinning it also skips the auto-detection that
    answers with the machine's highest TIA instead of the one this exe can load."""
    if exe is not None:
        exe = exe.resolve()
        if major is None:
            m = re.search(r"v(\d+)", str(exe.parent))
            major = int(m.group(1)) if m else None
        return exe, major

    engines = engines_in_bundle()
    if not engines:
        raise SystemExit("[FAIL] no runtime\\v*\\TiaMcpServer.exe found under %s — build the engine "
                         "first, or pass the exe path." % (ROOT / "runtime"))
    if major is not None:
        for version, path in engines:
            if version == major:
                return path, major
        print("[warn] this checkout has no v%d engine; using the highest one it has (v%d)"
              % (major, engines[0][0]))
    return engines[0][1], engines[0][0]


def tools_for_profile(exe, major, portal, profile):
    """profile=None means 'whatever a user gets with no flag and no env var'."""
    # Always pass --profile explicitly when asking for a specific roster. Relying on
    # "unset the env var" silently stopped meaning "full" the day lite became the default.
    env = dict(os.environ)
    env.pop("TIA_MCP_PROFILE", None)
    args = [str(exe), "--logging", "0"]
    if major is not None:
        args += ["--tia-major-version", str(major)]
    if portal:
        args += ["--tia-portal-location", portal]
    if profile:
        args += ["--profile", profile]
    p = subprocess.Popen(args, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace",
                         bufsize=1, env=env)
    seq = [0]

    def send(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            msg["params"] = params
        if not notify:
            seq[0] += 1
            msg["id"] = seq[0]
        p.stdin.write(json.dumps(msg) + "\n")
        p.stdin.flush()
        if notify:
            return None
        while True:
            line = p.stdout.readline()
            if not line:
                # Say what the engine said and what to try, instead of just "closed stdout":
                # on a multi-TIA machine the reason is in its stderr (wrong version / install
                # folder it cannot see), and that is exactly the failure worth acting on.
                try:
                    code = p.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    code = p.poll()

                # Windows hands back the CLR's unhandled-exception code as a huge unsigned number
                # (0xE0434352); hex is recognisable where a 10-digit wall is not.
                code_text = ("0x%X" % code) if code is not None and code > 255 else str(code)
                raise SystemExit(
                    "engine closed stdout (exit=%s) for `%s`:\n%s\n"
                    "hint: pass --tia-major-version N for the TIA you actually want this engine to "
                    "use, and --tia-portal-location DIR (or set TiaPortalLocation) when TIA is "
                    "installed outside %%ProgramFiles%%."
                    % (code_text, " ".join(args), p.stderr.read()))
            line = line.strip()
            if not line:
                continue
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if d.get("id") == seq[0]:
                return d

    try:
        send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                            "clientInfo": {"name": "lite-check", "version": "1"}})
        send("notifications/initialized", {}, notify=True)
        return [t["name"] for t in send("tools/list", {})["result"]["tools"]]
    finally:
        try:
            p.stdin.close()
        except Exception:
            pass
        p.terminate()


def main():
    requested_exe, requested_major, portal = parse_args(sys.argv[1:])
    exe, major = resolve_engine(requested_exe, requested_major)
    if not exe.exists():
        print("[FAIL] engine not found:", exe)
        return 1
    print("engine:", exe, "(tia major: %s)" % (major if major is not None else "auto"))
    env_portal = os.environ.get("TiaPortalLocation")
    print("TiaPortalLocation: %s" % (portal or env_portal or "<unset>"))

    full = tools_for_profile(exe, major, portal, "full")
    lite = tools_for_profile(exe, major, portal, "lite")
    default = tools_for_profile(exe, major, portal, None)
    print("full profile   : %d tools" % len(full))
    print("lite profile   : %d tools" % len(lite))
    print("default profile: %d tools" % len(default))

    failures = []
    missing = [n for n in REQUIRED if n not in lite]
    if missing:
        failures.append("lite is missing golden-path tools: " + ", ".join(missing))

    unknown = [n for n in REQUIRED if n not in full]
    if unknown:
        failures.append("REQUIRED lists tools this engine does not expose at all: " + ", ".join(unknown))

    if len(lite) > HOST_TOOL_CAP:
        failures.append("lite exposes %d tools, over the %d host cap it exists to respect"
                        % (len(lite), HOST_TOOL_CAP))

    if not lite:
        failures.append("lite exposed no tools")

    # Sentinel: if these ever come back equal, the two probes are no longer probing two
    # different rosters and every assertion above is meaningless.
    if len(full) <= len(lite):
        failures.append("full (%d) is not larger than lite (%d) — the profile probes are not "
                        "distinguishing the two rosters, so this check proves nothing"
                        % (len(full), len(lite)))

    # lite must be what a user gets with no flags and no env var: every generated host
    # config now relies on that default rather than writing the flag out.
    if sorted(default) != sorted(lite):
        failures.append("the default profile (no flag, no env var) is not lite: %d tools vs %d"
                        % (len(default), len(lite)))

    for f in failures:
        print("[FAIL]", f)
    if failures:
        return 1
    print("[ ok ] default is lite; lite covers all %d golden-path tools, stays under the %d cap, "
          "and the other %d tools stay reachable via FindTools/CallTool"
          % (len(REQUIRED), HOST_TOOL_CAP, len(full) - len(lite)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
