"""Summarise Systems_ActionLog output: what winners do differently, and whether lunging pays.

    python Tools/action_log_report.py                 # every session in Logs/ActionLogs
    python Tools/action_log_report.py <folder>        # e.g. a folder pulled off a phone

Standard library only. Reads actions_*.csv and bouts_*.csv, joined on `bout`.
"""
import csv
import glob
import os
import sys
from collections import defaultdict

FEATURES = [
    ("forward speed (m/s)", lambda r: float(r["vx"])),
    ("torso height (m)", lambda r: float(r["height"])),
    ("uprightness (1 = vertical)", lambda r: float(r["upright"])),
    ("both feet planted (share)", lambda r: 1.0 if r["footNear"] == "1" and r["footFar"] == "1" else 0.0),
    ("gap to opponent (m)", lambda r: float(r["gap"])),
    ("clay left behind (m)", lambda r: float(r["matLeft"])),
    ("stamina (1 = fresh)", lambda r: float(r["stamina"])),
    ("leg effort (mean |action|)", lambda r: sum(abs(float(r["a%d" % i])) for i in range(0, 6)) / 6),
    ("spine effort", lambda r: sum(abs(float(r["a%d" % i])) for i in range(6, 9)) / 3),
    ("arm effort", lambda r: sum(abs(float(r["a%d" % i])) for i in range(9, 13)) / 4),
    ("lunge intent (-1..1)", lambda r: float(r["lungeIntent"])),
]


def mean(values):
    values = list(values)
    return sum(values) / len(values) if values else float("nan")


def main():
    folder = sys.argv[1] if len(sys.argv) > 1 else os.path.join("Logs", "ActionLogs")
    bouts = {}
    for path in sorted(glob.glob(os.path.join(folder, "bouts_*.csv"))):
        with open(path, newline="", encoding="utf-8-sig") as handle:
            for row in csv.DictReader(handle):
                bouts[row["bout"]] = row
    if not bouts:
        print("no bouts found in", folder)
        return 1

    rows_by_bout = defaultdict(list)
    for path in sorted(glob.glob(os.path.join(folder, "actions_*.csv"))):
        with open(path, newline="", encoding="utf-8-sig") as handle:
            for row in csv.DictReader(handle):
                if row["bout"] in bouts:
                    rows_by_bout[row["bout"]].append(row)

    decided = [b for b in bouts.values() if b["winner"] != "draw"]
    seconds = sorted(float(b["seconds"]) for b in bouts.values())
    print("BOUTS %d (%d decided, %d drawn)   length: median %.1f s, shortest %.1f, longest %.1f"
          % (len(bouts), len(decided), len(bouts) - len(decided), seconds[len(seconds) // 2], seconds[0], seconds[-1]))
    outcomes = defaultdict(int)
    for b in bouts.values():
        outcomes[b["outcome"]] += 1
    print("OUTCOMES " + ", ".join("%s %d" % kv for kv in sorted(outcomes.items())))

    # ---- how bouts end ----------------------------------------------------
    def tally(title, key, rows):
        counts = defaultdict(int)
        for row in rows:
            counts[row.get(key) or "(none)"] += 1
        total = sum(counts.values())
        print(title)
        for name, n in sorted(counts.items(), key=lambda kv: -kv[1]):
            print("  %-24s %3d  %3.0f%%" % (name, n, 100.0 * n / total))

    print()
    tally("HOW BOUTS END (cause)", "cause", bouts.values())
    tally("TECHNIQUE CALLED", "technique", bouts.values())
    tally("LOSER'S FIRST BODY PART DOWN", "loserFirstPartDown", decided)
    forward = sum(1 for b in decided if b.get("loserPitchToward") and float(b["loserPitchToward"]) > 0)
    print("LOSER WENT DOWN  face-first toward the opponent %d, over backwards %d" % (forward, len(decided) - forward))
    near_rim = sum(1 for b in decided if b.get("loserMatLeft") and float(b["loserMatLeft"]) < 0.6)
    print("LOSER'S POSITION within 0.6 m of the rim behind him in %d of %d decided bouts" % (near_rim, len(decided)))

    print()
    print("HOW EACH FIGHTER LOSES")
    by_loser = defaultdict(lambda: defaultdict(int))
    for b in decided:
        if b["fighterA"] == b["fighterB"]:
            continue
        loser = b["fighterB"] if b["winner"] == b["fighterA"] else b["fighterA"]
        by_loser[loser][b.get("cause") or "(none)"] += 1
    for loser in sorted(by_loser):
        parts = sorted(by_loser[loser].items(), key=lambda kv: -kv[1])
        print("  %-8s %s" % (loser, ", ".join("%s %d" % kv for kv in parts)))
    print()

    wins = defaultdict(int)
    fought = defaultdict(int)
    for b in bouts.values():
        fought[b["fighterA"]] += 1
        fought[b["fighterB"]] += 1
        if b["winner"] != "draw":
            wins[b["winner"]] += 1
    print("RECORD   " + ", ".join("%s %d/%d" % (f, wins[f], fought[f]) for f in sorted(fought)))

    # ---- does lunging pay? -------------------------------------------------
    lunges = 0
    lunger_won = 0
    lunger_lost = 0
    ended_soon_won = 0
    ended_soon_lost = 0
    bouts_with_lunge = 0
    more_lunges_won = 0
    more_lunges_total = 0
    for bout_id, b in bouts.items():
        rows = rows_by_bout.get(bout_id, [])
        end_t = max((float(r["t"]) for r in rows), default=0.0)
        any_lunge = False
        for r in rows:
            n = int(r["lunged"])
            if n <= 0:
                continue
            any_lunge = True
            lunges += n
            if b["winner"] == "draw" or b["fighterA"] == b["fighterB"]:
                continue
            won = r["fighter"] == b["winner"]
            lunger_won += n if won else 0
            lunger_lost += 0 if won else n
            if end_t - float(r["t"]) <= 3.0:
                ended_soon_won += n if won else 0
                ended_soon_lost += 0 if won else n
        bouts_with_lunge += 1 if any_lunge else 0
        la, lb = int(b["lungesA"]), int(b["lungesB"])
        if la != lb and b["winner"] != "draw" and b["fighterA"] != b["fighterB"]:
            more_lunges_total += 1
            busier = b["fighterA"] if la > lb else b["fighterB"]
            more_lunges_won += 1 if busier == b["winner"] else 0
    print()
    print("LUNGES   %d thrown, %.1f per bout, in %d of %d bouts" % (lunges, lunges / len(bouts), bouts_with_lunge, len(bouts)))
    if lunger_won + lunger_lost:
        print("  thrown by the eventual winner: %d   by the eventual loser: %d   (%.0f%% by the winner)"
              % (lunger_won, lunger_lost, 100.0 * lunger_won / (lunger_won + lunger_lost)))
    if ended_soon_won + ended_soon_lost:
        print("  bout over within 3 s of a lunge: lunger WON %d, lunger LOST %d   (%.0f%% won)"
              % (ended_soon_won, ended_soon_lost, 100.0 * ended_soon_won / (ended_soon_won + ended_soon_lost)))
    if more_lunges_total:
        print("  the fighter who lunged MORE won %d of %d decided bouts" % (more_lunges_won, more_lunges_total))

    # ---- what winners do differently --------------------------------------
    print()
    print("%-30s %10s %10s %10s" % ("AVERAGE OVER THE BOUT", "winners", "losers", "difference"))
    for label, read in FEATURES:
        win_values, lose_values = [], []
        for bout_id, b in bouts.items():
            if b["winner"] == "draw" or b["fighterA"] == b["fighterB"]:
                continue
            per = defaultdict(list)
            for r in rows_by_bout.get(bout_id, []):
                per[r["fighter"] + r["side"]].append(read(r))
            for key, values in per.items():
                (win_values if key[:-1] == b["winner"] else lose_values).append(mean(values))
        w, l = mean(win_values), mean(lose_values)
        print("%-30s %10.3f %10.3f %+10.3f" % (label, w, l, w - l))
    print()
    print("Mirror bouts (a fighter against himself) cannot be split into winner and loser by name and are skipped above.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
