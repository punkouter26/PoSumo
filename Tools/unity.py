#!/usr/bin/env python3
"""Drive the Unity Editor from a shell, with no MCP client and no login.

    python Tools/unity.py ping
    python Tools/unity.py scene SCN_TOURNAMENT
    python Tools/unity.py play | stop | pause
    python Tools/unity.py errors [--warnings]
    python Tools/unity.py shot [Temp/shot.png]
    python Tools/unity.py exec 'return UnityEngine.Application.unityVersion;'
    python Tools/unity.py raw manage_scene '{"action":"get_hierarchy"}'
    python Tools/unity.py tools

WHY THIS EXISTS. The `ai-game-developer` MCP server in `.mcp.json` points at
`https://ai-game.dev/mcp/p/fc108679` — a REMOTE relay that requires an OAuth flow,
so it is unusable from any headless or non-interactive session. The Besty
UnitySkills REST server on 8090 is a second option but is easy to lose: closing the
Editor while a process it spawned (VS Code, as the external script editor) is still
alive leaks the listening socket to that child, and the next Editor cannot rebind
the port.

This talks to a third thing that is already running and needs neither: the
CoplayDev `com.coplaydev.unity-mcp` package's `StdioBridgeHost`, a plain TCP socket
on 127.0.0.1. Standard library only — any Python 3 will do, the training venv
is not required.

THE PORT IS NOT FIXED — DO NOT HARDCODE IT. The bridge picks a free port at
startup, so it moves whenever the Editor restarts or a domain reload restarts the
host. On 2026-08-06 an MCP package upgrade reloaded the domain and the bridge came
back on 6400 while this file still assumed 6401; every call hung for the full
timeout instead of failing, because the OLD listener was still bound by the same
Unity process and answered the TCP connect — it just never sent the handshake.
That is the signature to recognise: connect succeeds, handshake times out.

So the port is DISCOVERED, by probing for the handshake rather than for an open
socket (an open socket is exactly what the stale listener fakes). `POSUMO_UNITY_PORT`
still forces one explicitly and skips the scan.

PROTOCOL (read out of StdioBridgeHost.cs, not documented anywhere):
  1. connect TCP to the bridge port
  2. the server sends a RAW, UNFRAMED handshake line: "WELCOME UNITY-MCP 1 FRAMING=1\n"
     — it is not length-prefixed, so it must be read up to the newline and no
     further, or you swallow the head of the first real frame
  3. every message after that is 8-byte BIG-ENDIAN length, then a UTF-8 payload
  4. the payload is JSON: {"type": "<tool>", "params": {...}}
     (the bare string "ping" is special-cased and answers {"message":"pong"})

Tool names are auto-derived by the package from [McpForUnityTool] attributes in
snake_case, so ManageScene -> manage_scene. `tools` prints the ones confirmed
present on 10.1.0.
"""
import argparse
import json
import os
import socket
import struct
import sys
import time

HOST = os.environ.get("POSUMO_UNITY_HOST", "127.0.0.1")

# An explicit port skips discovery entirely; otherwise these are probed in order.
# The bridge has been seen on both 6400 and 6401; the spread covers several Editors.
_PORT_ENV = os.environ.get("POSUMO_UNITY_PORT")
PORT_SCAN = range(6400, 6411)
_HANDSHAKE = b"WELCOME"

# Left None even when _PORT_ENV is set: seeding it here would satisfy the cache in
# resolve_port() and skip the probe that catches a stale forced port.
_resolved_port = None

# Confirmed on MCP-FOR-UNITY server 10.1.0.
TOOLS = [
    "batch_execute", "execute_code", "execute_menu_item", "find_gameobjects",
    "generate_audio", "generate_image", "generate_model", "get_test_job",
    "import_model", "import_model_file", "manage_animation", "manage_asset",
    "manage_build", "manage_camera", "manage_components", "manage_editor",
    "manage_gameobject", "manage_graphics", "manage_material", "manage_packages",
    "manage_physics", "manage_prefabs", "manage_probuilder", "manage_profiler",
    "manage_scene", "manage_script", "manage_scriptable_object", "manage_shader",
    "manage_texture", "manage_ui", "manage_vfx", "read_console", "refresh_unity",
    "run_tests", "unity_reflect",
]

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class BridgeError(RuntimeError):
    pass


def _read_exact(sock, count):
    buf = b""
    while len(buf) < count:
        chunk = sock.recv(count - len(buf))
        if not chunk:
            raise BridgeError(f"socket closed after {len(buf)}/{count} bytes")
        buf += chunk
    return buf


def _speaks_bridge(port, timeout=1.5):
    """True only if `port` actually sends the handshake.

    Deliberately not a "can I connect" test: a stale listener left over from a
    previous StdioBridgeHost accepts the connection and then says nothing, which is
    indistinguishable from a live bridge until you wait for the greeting.
    """
    try:
        with socket.create_connection((HOST, port), timeout=timeout) as sock:
            sock.settimeout(timeout)
            return sock.recv(len(_HANDSHAKE)) == _HANDSHAKE
    except OSError:
        return False


def resolve_port(force=False):
    """Return the live bridge port, probing the scan range once and caching it."""
    global _resolved_port
    if _resolved_port is not None and not force:
        return _resolved_port
    if _PORT_ENV:
        # Still probe a forced port. Skipping the check is how you get a 300 s hang
        # against a stale listener instead of an error naming the problem.
        port = int(_PORT_ENV)
        if not _speaks_bridge(port):
            raise BridgeError(
                f"POSUMO_UNITY_PORT={port} does not speak the bridge protocol.\n"
                "  - it accepted the connection but sent no handshake => stale listener\n"
                "  - unset POSUMO_UNITY_PORT to auto-discover the live port instead"
            )
        _resolved_port = port
        return port
    for port in PORT_SCAN:
        if _speaks_bridge(port):
            _resolved_port = port
            return port
    raise BridgeError(
        f"no Unity bridge answered on {HOST}:{PORT_SCAN.start}-{PORT_SCAN.stop - 1}.\n"
        "  - is the Editor running?\n"
        "  - the bridge is started by com.coplaydev.unity-mcp; check the Editor\n"
        "    log for 'StdioBridgeHost started on port' to see which port it chose\n"
        "  - a port that accepts the connection but never sends the handshake is a\n"
        "    STALE listener from a previous bridge, not a live one; it is skipped\n"
        "  - set POSUMO_UNITY_PORT to force a specific port"
    )


def call(command_type, params=None, timeout=300.0):
    """Send one command and return the decoded response dict."""
    payload = json.dumps({"type": command_type, "params": params or {}})
    port = resolve_port()
    try:
        conn = socket.create_connection((HOST, port), timeout=timeout)
    except OSError as exc:
        raise BridgeError(
            f"cannot reach the Unity bridge at {HOST}:{port} ({exc}). It answered a\n"
            "probe moments ago, so the Editor most likely just started a domain\n"
            "reload — retry, or check the Editor log for the new port."
        ) from exc

    with conn as sock:
        sock.settimeout(timeout)
        handshake = b""
        while not handshake.endswith(b"\n"):
            handshake += _read_exact(sock, 1)
            if len(handshake) > 256:
                raise BridgeError(f"handshake too long: {handshake!r}")
        body = payload.encode("utf-8")
        sock.sendall(struct.pack(">Q", len(body)) + body)
        length = struct.unpack(">Q", _read_exact(sock, 8))[0]
        raw = _read_exact(sock, length).decode("utf-8")

    try:
        return json.loads(raw)
    except json.JSONDecodeError:
        return {"status": "unparsed", "raw": raw}


def _unwrap(resp):
    """Return (ok, payload). The bridge nests a second success flag inside result."""
    if resp.get("status") != "success":
        return False, resp.get("error", resp)
    result = resp.get("result", resp)
    # A transport-level success can still carry a tool-level failure.
    if isinstance(result, dict) and result.get("success") is False:
        return False, result.get("error") or result
    return True, result


def report(label, resp, limit=4000):
    ok, payload = _unwrap(resp)
    print(f"[{'OK ' if ok else 'ERR'}] {label}")
    blob = payload if isinstance(payload, str) else json.dumps(payload, indent=2)
    if len(blob) > limit:
        blob = blob[:limit] + f"\n  ... (+{len(blob) - limit} chars)"
    for line in blob.splitlines():
        print("      " + line)
    return ok


def cmd_ping(_):
    resp = call("read_console", {"action": "get", "types": ["error"], "count": 1})
    ok, _payload = _unwrap(resp)
    print(f"bridge {HOST}:{resolve_port()} -> {'reachable' if ok else 'ERROR'}")
    return 0 if ok else 1


def cmd_scene(args):
    return 0 if report(
        f"load {args.name}",
        call("manage_scene", {"action": "load", "name": args.name, "path": args.path}),
    ) else 1


def cmd_editor(action):
    def run(_args):
        return 0 if report(action, call("manage_editor", {"action": action})) else 1
    return run


def cmd_errors(args):
    types = ["error", "warning"] if args.warnings else ["error"]
    resp = call("read_console", {"action": "get", "types": types,
                                 "count": args.count, "format": "detailed"})
    return 0 if report("console " + "+".join(types), resp) else 1


def cmd_exec(args):
    return 0 if report(
        "execute_code",
        # NOTE: the action parameter is mandatory; omitting it fails with a bare
        # "'action' parameter is required." that does not name the tool.
        call("execute_code", {"action": "execute", "code": args.code}),
    ) else 1


def cmd_shot(args):
    """Capture the Game view, INCLUDING UI Toolkit overlays.

    ScreenCapture.CaptureScreenshot is used rather than a Camera+RenderTexture
    render because every screen in this project is UI Toolkit drawn into a screen
    overlay panel, which a camera render does not see at all.

    It is also asynchronous — the file is written at end of frame — hence the wait
    below. And `--settle` exists because of a real mistake: capturing in the same
    frame as a panel's FadeIn (140 ms) photographs it at opacity ~0 and looks like
    a rendering bug rather than a timing one.
    """
    path = args.path
    full = path if os.path.isabs(path) else os.path.join(REPO_ROOT, path)
    if os.path.exists(full):
        os.remove(full)

    if args.settle > 0:
        time.sleep(args.settle)

    escaped = path.replace("\\", "\\\\").replace('"', '\\"')
    resp = call("execute_code", {
        "action": "execute",
        "code": f'UnityEngine.ScreenCapture.CaptureScreenshot("{escaped}", {args.scale}); return "queued";',
    })
    ok, payload = _unwrap(resp)
    if not ok:
        report("capture", resp)
        return 1

    deadline = time.time() + args.timeout
    while time.time() < deadline:
        # Wait for the size to stop changing, not merely for the file to appear —
        # it is created before the PNG has finished being written.
        if os.path.exists(full):
            size = os.path.getsize(full)
            time.sleep(0.4)
            if size > 0 and os.path.getsize(full) == size:
                print(f"[OK ] wrote {path} ({size} bytes)")
                return 0
        time.sleep(0.3)
    print(f"[ERR] {path} never appeared within {args.timeout}s")
    return 1


def cmd_raw(args):
    try:
        params = json.loads(args.params) if args.params else {}
    except json.JSONDecodeError as exc:
        print(f"[ERR] params is not valid JSON: {exc}")
        return 1
    return 0 if report(args.tool, call(args.tool, params)) else 1


def cmd_tools(_args):
    print(f"{len(TOOLS)} tools on the bridge:")
    for name in TOOLS:
        print("  " + name)
    print("\nInspect a tool's parameters by reading its source:")
    print("  Library/PackageCache/com.coplaydev.unity-mcp@*/Editor/Tools/")
    return 0


def main():
    parser = argparse.ArgumentParser(
        description="Control the Unity Editor over the CoplayDev bridge "
                    "(port auto-discovered; override with POSUMO_UNITY_PORT).")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("ping", help="check the bridge is reachable").set_defaults(fn=cmd_ping)
    sub.add_parser("tools", help="list available tools").set_defaults(fn=cmd_tools)

    p = sub.add_parser("scene", help="load a scene")
    p.add_argument("name")
    p.add_argument("--path", default="Assets/Scenes")
    p.set_defaults(fn=cmd_scene)

    for action in ("play", "stop", "pause"):
        sub.add_parser(action, help=f"{action} the editor").set_defaults(fn=cmd_editor(action))

    p = sub.add_parser("errors", help="read the console")
    p.add_argument("--warnings", action="store_true")
    p.add_argument("--count", type=int, default=25)
    p.set_defaults(fn=cmd_errors)

    p = sub.add_parser("exec", help="run a C# snippet (Roslyn)")
    p.add_argument("code")
    p.set_defaults(fn=cmd_exec)

    p = sub.add_parser("shot", help="capture the Game view to a PNG")
    p.add_argument("path", nargs="?", default="Temp/shot.png")
    p.add_argument("--scale", type=int, default=1)
    p.add_argument("--settle", type=float, default=0.5,
                   help="seconds to wait BEFORE capturing, for animations to finish")
    p.add_argument("--timeout", type=float, default=30.0)
    p.set_defaults(fn=cmd_shot)

    p = sub.add_parser("raw", help="call any tool with raw JSON params")
    p.add_argument("tool")
    p.add_argument("params", nargs="?", default="{}")
    p.set_defaults(fn=cmd_raw)

    args = parser.parse_args()
    try:
        return args.fn(args)
    except BridgeError as exc:
        print(f"[ERR] {exc}")
        return 2


if __name__ == "__main__":
    sys.exit(main())
