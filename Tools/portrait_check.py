#!/usr/bin/env python3
"""Portrait layout + end-to-end flow check, driven through the Unity bridge.

This exists because the audit it automates was done by hand once (2026-08-25) and
found two real defects that only a LIVE, correctly-sized run could show:

  * the tournament ScrollView was COMPRESSING its children to fit the viewport
    instead of scrolling them, so the roster palette overlapped the QUARTERFINALS
    header and the banzuke rows printed on top of each other;
  * the Editor was parked on a training scene at 960x2658 -- neither the game's
    entry scene nor any real device aspect -- so neither defect was visible.

Both are invisible from the code and from a screenshot at the wrong size, which is
the whole argument for a script.

Usage
-----
    python Tools/portrait_check.py                       # 3 default aspects, full flow
    python Tools/portrait_check.py --sizes 1080x2400     # one aspect
    python Tools/portrait_check.py --no-play             # bracket layout audit only
    python Tools/portrait_check.py --out Temp/portrait   # where the PNGs land

What it does per size
---------------------
  1. registers/selects a Game view size (they persist in the Editor afterwards)
  2. opens SCN_TOURNAMENT -- ALWAYS the entry scene, never SCN_SUMO, or there is
     no bracket, no Systems_TournamentState and no title awarded
  3. captures the bracket and runs an OVERFLOW AUDIT on the live visual tree
  4. optionally starts the tournament and captures the arena phases

The overflow audit is the generalised form of the bug above: it reports any
element whose children lay out past its own resolved height. That is exactly the
signature of a flex-shrink compression and it cannot be seen in a screenshot until
something happens to overlap something else legible.

Standard library only, same as Tools/unity.py, which it reuses for transport.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import unity  # noqa: E402  -- Tools/unity.py, the bridge client


# Real device aspects, not round numbers. 9:16 is the panel reference, 9:20 is an
# ordinary modern phone, and 3:4 is a tablet in portrait -- the case the HUD's
# DOCK_MAX_PERCENT floor exists for, and the one nobody ever looks at.
DEFAULT_SIZES = ["1080x1920", "1080x2400", "1200x1600"]


def _exec(code: str, timeout: float = 120.0) -> str:
    """Run C# in the Editor and return its return value as a string.

    `unity._unwrap` yields (ok, payload) and the bridge nests the script's own
    return under payload["result"], so this unwraps both layers. A failure is
    returned as text rather than raised: one size erroring should not abandon the
    rest of the matrix.
    """
    resp = unity.call("execute_code", {"action": "execute", "code": code}, timeout=timeout)
    ok, payload = unity._unwrap(resp)
    if not ok:
        return f"ERROR: {payload}"
    if isinstance(payload, dict):
        # execute_code nests the script's return under data.result.
        data = payload.get("data")
        if isinstance(data, dict) and "result" in data:
            return str(data["result"])
        return str(payload.get("result", payload))
    return str(payload)


# --- Game view sizing ------------------------------------------------------
#
# There is no public API for this. GameViewSizes is an internal ScriptableSingleton
# and `GameView.selectedSizeIndex` is a non-public property, so both are reached by
# reflection. A size added here PERSISTS in the Editor's size list, which is why the
# entries are named -- an anonymous 1080x1920 is indistinguishable from a user's own.
_SET_SIZE = r'''
var asm = typeof(UnityEditor.EditorWindow).Assembly;
var sizesType = asm.GetType("UnityEditor.GameViewSizes");
var singleton = typeof(UnityEditor.ScriptableSingleton<>).MakeGenericType(sizesType);
var instance = singleton.GetProperty("instance").GetValue(null);
var group = sizesType.GetProperty("currentGroup").GetValue(instance);
var groupType = group.GetType();
var sizeType = asm.GetType("UnityEditor.GameViewSize");
var sizeTypeEnum = asm.GetType("UnityEditor.GameViewSizeType");
var ctor = sizeType.GetConstructor(new System.Type[]{ sizeTypeEnum, typeof(int), typeof(int), typeof(string) });
string label = "PoSumo %(w)dx%(h)d";
var getTexts = groupType.GetMethod("GetDisplayTexts");
var texts = (string[])getTexts.Invoke(group, null);
int found = -1;
for (int i = 0; i < texts.Length; i++) { if (texts[i].Contains(label)) { found = i; break; } }
if (found < 0) {
    var made = ctor.Invoke(new object[]{ System.Enum.ToObject(sizeTypeEnum, 1), %(w)d, %(h)d, label });
    groupType.GetMethod("AddCustomSize").Invoke(group, new object[]{ made });
    texts = (string[])getTexts.Invoke(group, null);
    for (int i = 0; i < texts.Length; i++) { if (texts[i].Contains(label)) { found = i; break; } }
}
var gvType = asm.GetType("UnityEditor.GameView");
var win = UnityEditor.EditorWindow.GetWindow(gvType, false, "Game", false);
var prop = gvType.GetProperty("selectedSizeIndex",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
    | System.Reflection.BindingFlags.Public);
prop.SetValue(win, found);
win.Repaint();
return label + " idx=" + found;
'''


def set_game_view(width: int, height: int) -> str:
    return _exec(_SET_SIZE % {"w": width, "h": height})


# --- Overflow audit --------------------------------------------------------
#
# Walks every UIDocument's visual tree and reports elements whose children lay out
# past the bottom of the element itself. `flex-shrink` defaults to 1, so a column
# taller than its parent is silently COMPRESSED rather than clipped or scrolled --
# no error, no warning, and the only visible symptom is two unrelated things
# overlapping somewhere further down the screen.
_AUDIT = r'''
var docs = UnityEngine.Object.FindObjectsByType<UnityEngine.UIElements.UIDocument>(
    UnityEngine.FindObjectsSortMode.None);
var sb = new System.Text.StringBuilder();
int flagged = 0;
System.Action<UnityEngine.UIElements.VisualElement, string> walk = null;
walk = (e, path) => {
    var r = e.resolvedStyle;
    if (r.display == UnityEngine.UIElements.DisplayStyle.None) return;
    // A ScrollView and its content container are SUPPOSED to hold more than they
    // show -- that is the entire point of one, and flagging them would mean the
    // audit reports a finding precisely when scrolling has been fixed. Everything
    // INSIDE them is still walked; only these two frames are exempt.
    bool scroller = e is UnityEngine.UIElements.ScrollView
                 || (e.parent != null && e.parent is UnityEngine.UIElements.ScrollView)
                 || e.ClassListContains("unity-scroll-view__content-container")
                 || e.ClassListContains("unity-scroll-view__content-viewport");
    if (!scroller && e.childCount > 0 && r.height > 1f) {
        float lowest = 0f;
        for (int i = 0; i < e.childCount; i++) {
            var c = e[i].resolvedStyle;
            if (c.display == UnityEngine.UIElements.DisplayStyle.None) continue;
            if (e[i].resolvedStyle.position == UnityEngine.UIElements.Position.Absolute) continue;
            float bottom = c.top + c.height;
            if (bottom > lowest) lowest = bottom;
        }
        // 1.5pt of slack: sub-pixel rounding is normal and is not a finding.
        if (lowest > r.height + 1.5f) {
            flagged++;
            sb.Append("  OVERFLOW " + path + " " + e.GetType().Name
                + " height=" + r.height.ToString("F0")
                + " content=" + lowest.ToString("F0")
                + " over=" + (lowest - r.height).ToString("F0")
                + " children=" + e.childCount
                + " flexShrink=" + r.flexShrink.ToString("F0") + "\n");
        }
    }
    for (int i = 0; i < e.childCount; i++) walk(e[i], path + "/" + i);
};
foreach (var d in docs) walk(d.rootVisualElement, d.gameObject.name);
sb.Insert(0, "documents=" + docs.Length + " overflowing=" + flagged + "\n");
return sb.ToString();
'''


def audit_overflow() -> str:
    return _exec(_AUDIT)


def shot(path: str, settle: float = 2.0) -> None:
    """Capture the Game view. Mirrors Tools/unity.py's own settle discipline --
    ScreenCapture.CaptureScreenshot is asynchronous AND a panel captured in the
    same frame it starts its fade is photographed at opacity ~0."""
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    args = argparse.Namespace(path=path, scale=1, settle=settle, timeout=60.0)
    unity.cmd_shot(args)


def scene_state() -> str:
    return _exec(
        'return "scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name'
        ' + " playing=" + UnityEditor.EditorApplication.isPlaying'
        ' + " screen=" + UnityEngine.Screen.width + "x" + UnityEngine.Screen.height'
        ' + " orient=" + UnityEngine.Screen.orientation;'
    )


def run_size(width: int, height: int, out_dir: str, play: bool) -> dict:
    tag = f"{width}x{height}"
    print(f"\n=== {tag} " + "=" * (60 - len(tag)))

    print("  " + str(set_game_view(width, height)))
    unity.call("manage_scene", {"action": "load", "name": "SCN_TOURNAMENT",
                                "path": "Assets/Scenes"}, timeout=120)

    _exec("UnityEditor.EditorApplication.isPlaying = true; return \"play\";")
    time.sleep(6.0)
    print("  " + str(scene_state()))

    shot(os.path.join(out_dir, f"bracket_{tag}.png"), settle=2.5)
    bracket_audit = audit_overflow()
    print("  BRACKET AUDIT:")
    print("".join("    " + line + "\n" for line in bracket_audit.strip().splitlines()))

    arena_audit = ""
    if play:
        _exec('var b = UnityEngine.Object.FindAnyObjectByType<PoSumo.Systems_TournamentBracket>();'
              ' if (b != null) b.PressAction(); return "started";')
        # Long enough to clear the intro countdown and the walk-in, so the capture
        # lands on a live round rather than on a ceremony beat.
        time.sleep(14.0)
        shot(os.path.join(out_dir, f"arena_{tag}.png"), settle=2.5)
        arena_audit = audit_overflow()
        print("  ARENA AUDIT:")
        print("".join("    " + line + "\n" for line in arena_audit.strip().splitlines()))

    _exec("UnityEditor.EditorApplication.isPlaying = false; return \"stop\";")
    time.sleep(2.5)
    return {"size": tag, "bracket": bracket_audit, "arena": arena_audit}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--sizes", default=",".join(DEFAULT_SIZES),
                    help="comma-separated WxH list (default: %(default)s)")
    ap.add_argument("--out", default="Temp/portrait", help="directory for the PNGs")
    ap.add_argument("--no-play", action="store_true",
                    help="audit the bracket layout only; do not run a match")
    args = ap.parse_args()

    sizes = []
    for token in args.sizes.split(","):
        token = token.strip().lower()
        if not token:
            continue
        w, _, h = token.partition("x")
        sizes.append((int(w), int(h)))

    results = []
    for w, h in sizes:
        results.append(run_size(w, h, args.out, play=not args.no_play))

    print("\n" + "=" * 66)
    bad = 0
    for r in results:
        for label in ("bracket", "arena"):
            text = r[label] or ""
            for line in text.splitlines():
                if "OVERFLOW" in line:
                    bad += 1
                    print(f"{r['size']:>10} {label:<8} {line.strip()}")
    print(f"\n{bad} overflowing element(s) across {len(results)} size(s).")
    print(f"PNGs in {args.out}/")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
