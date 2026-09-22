#!/usr/bin/env python3
"""Static integrity sweep for PoSumo: broken scripts, unresolved GUIDs, static
game state without a reset, and event subscriptions with no unsubscribe.

Why this exists
---------------
Every fault class this tool hunts has already shipped here at least once:

* ``m_Script: {fileID: 0}``  — the genuine broken-script signature; a missing or
  failed script compile leaves this behind and nothing warns.
* unresolved GUIDs           — the 2026-09-05 lesson: a scan that walks only
  ``Assets/`` reports every ML-Agents component as broken because the ``file:``
  package lives in ``Training/ml-agents/``. That index is REUSED from
  ``ref_audit.py`` (three meta roots + the two built-in GUIDs), not duplicated.
* static state with no reset — Enter Play Mode domain reload is OFF. A static
  that holds game state and lacks a ``[RuntimeInitializeOnLoadMethod(
  SubsystemRegistration)]`` reset persists into the NEXT Play session; the bug
  only ever shows up on the second run, which is the worst possible time.
* unpaired subscriptions     — "subscribe in OnEnable, unsubscribe in OnDisable"
  is a convention. A ``+=`` with no ``-=`` anywhere in the project is the static
  leak shape; reported (gated only with ``--strict`` because a handler may be
  detached by lifetime rather than by a matching statement).

Usage
-----
    python Tools/integrity.py               # full sweep, gates
    python Tools/integrity.py --strict      # also gate on event findings
    python Tools/integrity.py --json        # machine-readable summary

Exit code 1 on any gated finding, so this is usable as a pre-flight gate next to
``ref_audit.py``.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ref_audit  # noqa: E402  -- reuses the meta index and GUID scan

REPO = ref_audit.REPO
SCRIPTS_ROOT = os.path.join(REPO, "Assets", "Scripts")
EDITOR_ROOT = os.path.join(REPO, "Assets", "Editor")

# Statics verified benign when this tool was written. Each entry needs a reason;
# anything added here is exempt from the reset gate. Resource caches are safe to
# carry across Play sessions BECAUSE domain reload is off: nothing destroys the
# cached object between sessions, so the cached reference stays valid.
STATIC_ALLOWLIST = {
    # Cached sprites/materials/shaders — created once from code or Resources and
    # reused by every instance; the reference remains valid across sessions.
    ("Assets/Scripts/Agent/Agent_BipedBody.cs", "_squareSprite"): "shared sprite cache; object outlives Play sessions",
    ("Assets/Scripts/Agent/Agent_BipedBody.cs", "_bodyMat"): "shared material cache; object outlives Play sessions",
    ("Assets/Scripts/Systems/Systems_ArenaAtmosphere.cs", "_shaftSprite"): "shared sprite cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_litSprite"): "shared sprite cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_unlitSprite"): "shared sprite cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_bodyShader"): "Shader.Find cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_normalMap"): "texture cache",
    ("Assets/Scripts/Systems/Systems_BlobShadow.cs", "_softDisc"): "shared sprite cache",
    ("Assets/Scripts/Systems/Systems_RingSqueezeCue.cs", "_edgeFade"): "shared texture cache",
    # Log-once / resolve-once latches — persisting them only suppresses repeat
    # warnings inside one Editor session; reflection lookups are deterministic.
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_bodyShaderMissingLogged"): "log-once latch",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_normalQualityField"): "reflection lookup cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_useNormalMapField"): "reflection lookup cache",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_normalReflectionResolved"): "resolve-once latch",
    ("Assets/Scripts/Systems/Systems_ArenaLighting.cs", "_normalReflectionWarned"): "warn-once latch",
    # Bot tuning knobs: declaration-initialized defaults with NO writer anywhere
    # in the project (verified by grep), so they behave as constants.
    ("Assets/Scripts/Agent/Agent_Bot.cs", "Gain"): "read-only default, no writers",
    ("Assets/Scripts/Agent/Agent_Bot.cs", "StandKp"): "read-only default, no writers",
    ("Assets/Scripts/Agent/Agent_Bot.cs", "StandKd"): "read-only default, no writers",
    ("Assets/Scripts/Agent/Agent_Bot.cs", "StandKv"): "read-only default, no writers",
    ("Assets/Scripts/Agent/Agent_Bot.cs", "StandKneeDeg"): "read-only default, no writers",
    ("Assets/Scripts/Agent/Agent_Bot.cs", "StanceSplitDeg"): "read-only default, no writers",
    # Self-healing singleton: nulled in OnDestroy and re-checked with Unity's ==
    # (which sees a destroyed object as null) before every reuse.
    ("Assets/Scripts/Systems/Systems_Telemetry.cs", "_instance"): "singleton, nulled in OnDestroy, Unity-null re-checked before reuse",
}

# A static FIELD declaration: access modifier + static + type + name, not
# readonly/const/event, not a method (no parenthesis before the terminator), and
# not an expression-bodied PROPERTY (the `=>` of `static bool X => true;` must
# not read as an initializer).
STATIC_FIELD_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"                       # optional attributes
    r"(?:public|private|internal|protected)\s+"
    r"static\s+"
    r"(?!readonly\b|const\b|event\b)"
    r"[\w<>,.\[\]\? ]+?\s+"                        # type
    r"(?P<name>\w+)\s*"                             # field name
    r"(?:\s*=(?!\s*>)[^=].*)?;\s*$",                # declaration end (init allowed, `=>` not)
    re.MULTILINE,
)

SUBSYSTEM_RESET_RE = re.compile(r"SubsystemRegistration")

# Field-like event declarations: `event SomeType Name;`
EVENT_DECL_RE = re.compile(
    r"\bevent\s+[\w<>,\.\[\]\? ]+?\s+(?P<name>\w+)\s*;"
)

CS_EXT = ".cs"


def cs_files(root: str) -> list[str]:
    found: list[str] = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d != ".git"]
        for name in filenames:
            if name.endswith(CS_EXT):
                found.append(os.path.join(dirpath, name))
    return sorted(found)


def read(path: str) -> str:
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        return handle.read()


def audit_runtime_statics(verbose: bool) -> list[dict]:
    """Every static field under Assets/Scripts must live in a file that carries
    a SubsystemRegistration reset."""
    findings: list[dict] = []
    for path in cs_files(SCRIPTS_ROOT):
        text = read(path)
        names = [m.group("name") for m in STATIC_FIELD_RE.finditer(text)]
        if not names:
            continue
        if SUBSYSTEM_RESET_RE.search(text):
            if verbose:
                rel = os.path.relpath(path, REPO)
                print(f"  ok  {rel}: {len(names)} static(s), reset present")
            continue
        rel = os.path.relpath(path, REPO).replace(os.sep, "/")
        for name in names:
            key = (rel, name)
            if key in STATIC_ALLOWLIST:
                continue
            findings.append({
                "file": rel,
                "field": name,
                "why": "static field with no SubsystemRegistration reset in this file; "
                       "with domain reload off its value persists into the next Play session",
            })
    return findings


def audit_event_pairing() -> list[dict]:
    """Every event declared under Assets/Scripts that is subscribed somewhere in
    the project must also be unsubscribed somewhere in the project."""
    declared: dict[str, list[str]] = {}
    for path in cs_files(SCRIPTS_ROOT):
        for match in EVENT_DECL_RE.finditer(read(path)):
            declared.setdefault(match.group("name"), []).append(
                os.path.relpath(path, REPO).replace(os.sep, "/"))

    corpus = cs_files(SCRIPTS_ROOT) + cs_files(EDITOR_ROOT)
    blobs = {path: read(path) for path in corpus}

    findings: list[dict] = []
    for name, decl_files in sorted(declared.items()):
        plus = minus = 0
        for blob in blobs.values():
            plus += len(re.findall(r"\b" + re.escape(name) + r"\s*\+=", blob))
            minus += len(re.findall(r"\b" + re.escape(name) + r"\s*-=", blob))
        if plus > 0 and minus == 0:
            findings.append({
                "event": name,
                "declared": decl_files,
                "subscriptions": plus,
                "unsubscriptions": 0,
                "why": "subscribed but never unsubscribed anywhere — if any subscriber "
                       "is a static event this is a cross-scene leak under domain-reload-off",
            })
    return findings


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verbose", action="store_true")
    parser.add_argument("--strict", action="store_true",
                        help="also gate (exit 1) on unpaired event subscriptions")
    parser.add_argument("--json", action="store_true")
    args = parser.parse_args()

    # --- reference + broken-script sweep, REUSING ref_audit's index ----------
    index = ref_audit.index_metas(args.verbose)
    files = ref_audit.scan_files()
    missing: dict[str, list[str]] = {}
    null_scripts: list[str] = []
    refs_checked = 0
    for path in files:
        try:
            with open(path, "rb") as handle:
                blob = handle.read()
        except OSError:
            continue
        rel = os.path.relpath(path, REPO).replace(os.sep, "/")
        if ref_audit.NULL_SCRIPT_RE.search(blob):
            null_scripts.append(rel)
        for raw in set(ref_audit.GUID_RE.findall(blob)):
            guid = raw.decode("ascii")
            refs_checked += 1
            if guid in ref_audit.BUILTIN_GUIDS or guid in index:
                continue
            missing.setdefault(guid, []).append(rel)

    static_findings = audit_runtime_statics(args.verbose)
    event_findings = audit_event_pairing()

    summary = {
        "filesScanned": len(files),
        "metasIndexed": len(index),
        "distinctRefsChecked": refs_checked,
        "missingGuids": {g: sorted(v) for g, v in sorted(missing.items())},
        "nullScriptFiles": sorted(null_scripts),
        "unresetStaticFields": static_findings,
        "unpairedEventSubscriptions": event_findings,
    }

    if args.json:
        print(json.dumps(summary, indent=2))
    else:
        print(f"scanned {len(files)} asset files against {len(index)} indexed GUIDs "
              f"({refs_checked} references)")
        print(f"null scripts (m_Script: {{fileID: 0}}): {len(null_scripts)}")
        print(f"unresolved GUIDs: {len(missing)}")
        print(f"runtime static fields without a reset: {len(static_findings)}")
        print(f"unpaired event subscriptions: {len(event_findings)}")

        if null_scripts:
            print("\nBROKEN SCRIPTS:")
            for rel in null_scripts:
                print(f"  {rel}")
        if missing:
            print("\nUNRESOLVED GUIDS:")
            for guid, users in sorted(missing.items()):
                print(f"  {guid}")
                for rel in sorted(users):
                    print(f"      {rel}")
        if static_findings:
            print("\nUNRESET STATIC STATE (Assets/Scripts):")
            for finding in static_findings:
                print(f"  {finding['file']} :: {finding['field']}")
                print(f"      {finding['why']}")
        if event_findings:
            print("\nUNPAIRED EVENT SUBSCRIPTIONS (report-only; gate with --strict):")
            for finding in event_findings:
                print(f"  {finding['event']} (+{finding['subscriptions']}/-{finding['unsubscriptions']}) "
                      f"declared in {', '.join(finding['declared'])}")

        if not (null_scripts or missing or static_findings or event_findings):
            print("\nall clear: references resolve, no broken scripts, static state "
                  "resets, event subscriptions pair")

    gated = bool(null_scripts or missing or static_findings
                 or (args.strict and event_findings))
    return 1 if gated else 0


if __name__ == "__main__":
    sys.exit(main())
