#!/usr/bin/env python3
"""Prove that every asset reference in this project actually resolves.

Why this exists
---------------
The obvious version of this check is wrong, and it is wrong in a way that
produces confident false alarms. A first pass over this repo reported
``a79441f348de89743a2939f4d699eac1`` as a MISSING script in all three shipped
scenes. It is ``UniversalAdditionalCameraData``, and it lives in
``Library/PackageCache/com.unity.render-pipelines.universal@.../``.

There are three separate places a ``.meta`` can be, and a scan that misses any
one of them invents broken references:

* ``Assets/``                — project assets.
* ``Library/PackageCache/``  — every registry package (URP, ML-Agents' deps, MCP).
* ``Training/ml-agents/``    — ``com.unity.ml-agents`` is referenced as
  ``file:../Training/ml-agents/com.unity.ml-agents``. A ``file:`` package is used
  IN PLACE: it is never copied into ``PackageCache`` and it is not under
  ``Packages/``. ``BehaviorParameters`` (5d1c4e0b...) lives only here.

Two GUIDs additionally have no ``.meta`` anywhere BY DESIGN — they are Unity's
built-in resources — and are whitelisted below.

The genuine broken-script signature is ``m_Script: {fileID: 0}``, which is
reported separately and is never a false positive.

Usage
-----
    python Tools/ref_audit.py              # scenes + assets under Assets/
    python Tools/ref_audit.py --verbose    # also list every root that was indexed
    python Tools/ref_audit.py --json       # machine-readable summary

Exit code is 1 if anything is unresolved, so this is usable as a gate.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# Roots that can contain a .meta. Order is not significant; all are indexed.
META_ROOTS = [
    os.path.join(REPO, "Assets"),
    os.path.join(REPO, "Library", "PackageCache"),
    os.path.join(REPO, "Training", "ml-agents"),
    os.path.join(REPO, "Packages"),
]

# Files whose references we check. Scenes first because they are what ships.
SCAN_EXTS = (".unity", ".asset", ".prefab", ".mat", ".controller", ".anim")

# Unity's built-in resources. They are referenced constantly, they have no .meta
# by design, and every naive audit flags them.
BUILTIN_GUIDS = {
    "0000000000000000e000000000000000",   # unity default resources
    "0000000000000000f000000000000000",   # unity builtin extra
}

GUID_RE = re.compile(rb"guid:\s*([0-9a-f]{32})")
META_GUID_RE = re.compile(r"^guid:\s*([0-9a-f]{32})\s*$", re.MULTILINE)
NULL_SCRIPT_RE = re.compile(rb"m_Script:\s*\{fileID:\s*0\}")


def index_metas(verbose: bool) -> dict[str, str]:
    """GUID -> the .meta file that declares it."""
    index: dict[str, str] = {}
    for root in META_ROOTS:
        if not os.path.isdir(root):
            if verbose:
                print(f"  (absent) {os.path.relpath(root, REPO)}")
            continue
        count = 0
        for dirpath, dirnames, filenames in os.walk(root):
            # Nothing under a Unity-generated cache inside Assets is a real asset.
            dirnames[:] = [d for d in dirnames if d not in (".git", "node_modules")]
            for name in filenames:
                if not name.endswith(".meta"):
                    continue
                path = os.path.join(dirpath, name)
                try:
                    with open(path, "r", encoding="utf-8", errors="replace") as handle:
                        head = handle.read(512)
                except OSError:
                    continue
                match = META_GUID_RE.search(head)
                if match:
                    index.setdefault(match.group(1), path)
                    count += 1
        if verbose:
            print(f"  {count:>6} metas  {os.path.relpath(root, REPO)}")
    return index


def scan_files() -> list[str]:
    found: list[str] = []
    assets = os.path.join(REPO, "Assets")
    for dirpath, dirnames, filenames in os.walk(assets):
        dirnames[:] = [d for d in dirnames if d != ".git"]
        for name in filenames:
            if name.endswith(SCAN_EXTS):
                found.append(os.path.join(dirpath, name))
    return sorted(found)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--verbose", action="store_true",
                        help="list the roots indexed and their meta counts")
    parser.add_argument("--json", action="store_true", help="machine-readable summary")
    args = parser.parse_args()

    if args.verbose:
        print("Indexing .meta roots:")
    index = index_metas(args.verbose)

    files = scan_files()
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
        if NULL_SCRIPT_RE.search(blob):
            null_scripts.append(rel)
        for raw in set(GUID_RE.findall(blob)):
            guid = raw.decode("ascii")
            refs_checked += 1
            if guid in BUILTIN_GUIDS or guid in index:
                continue
            missing.setdefault(guid, []).append(rel)

    summary = {
        "filesScanned": len(files),
        "metasIndexed": len(index),
        "distinctRefsChecked": refs_checked,
        "missingGuids": {g: sorted(v) for g, v in sorted(missing.items())},
        "nullScriptFiles": sorted(null_scripts),
    }

    if args.json:
        print(json.dumps(summary, indent=2))
    else:
        print(f"scanned {len(files)} files against {len(index)} indexed .meta GUIDs "
              f"({refs_checked} references)")
        if null_scripts:
            print(f"\nBROKEN SCRIPTS — m_Script: {{fileID: 0}} in {len(null_scripts)} file(s):")
            for rel in null_scripts:
                print(f"  {rel}")
        else:
            print("no m_Script: {fileID: 0} anywhere")
        if missing:
            print(f"\nUNRESOLVED GUIDS ({len(missing)}):")
            for guid, users in sorted(missing.items()):
                print(f"  {guid}")
                for rel in users:
                    print(f"      {rel}")
        else:
            print("every referenced GUID resolves")

    return 1 if (missing or null_scripts) else 0


if __name__ == "__main__":
    sys.exit(main())
