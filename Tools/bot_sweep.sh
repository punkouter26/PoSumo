#!/usr/bin/env bash
# BOT tuning sweep, v3.
#
# v1 was not trustworthy and its own output said so:
#   * the match ENDS after first-to-3 and then sits frozen on the rematch screen,
#     so the last six configurations were scored against a frozen body and all
#     returned 0 — half the sweep was measuring nothing;
#   * two IDENTICAL baseline rows scored 92.5 and 44.1, i.e. run-to-run variance
#     was as large as the differences between configurations, so a single trial
#     cannot rank anything.
#
# v2 restarts play mode for every trial (deterministic fresh match) and repeats
# each configuration REPEATS times, reporting the mean. Statics survive across
# play sessions because this project disables domain reload on entering play.
cd "c:/Users/punko/Downloads/PoSumo" || exit 1
U="python Tools/unity.py"
API="http://127.0.0.1:8090/skill"
REPEATS=3

playing () {
  timeout 25 curl -s -m 20 -X POST "$API/editor_get_state" -H "Content-Type: application/json" -d '{}' 2>/dev/null \
    | grep -oE '"isPlaying":[a-z]+' | cut -d: -f2
}

# True once a fighter's Rigidbody2D is actually simulating. This is the check the
# previous version lacked, and its absence invalidated 11 of 12 trials: it slept a
# fixed 5 s after pressing play and then measured whatever was there, which — if
# the restart had not taken, or the round was still in its countdown freeze — was a
# motionless body scoring zero. isPlaying is NOT sufficient: the referee freezes
# the fighters with simulated=false between rounds while play mode is perfectly
# live, so the only trustworthy signal is the body itself moving.
wait_live () {
  local tries=${1:-25}
  for _ in $(seq 1 "$tries"); do
    case "$(sample)" in
      *FRZ*|*none*|"") : ;;
      *) return 0 ;;
    esac
  done
  return 1
}

restart_play () {
  # editor_stop errors when not playing; that is fine and must not abort the run.
  if [ "$(playing)" = "true" ]; then
    timeout 40 curl -s -m 35 -X POST "$API/editor_stop" -H "Content-Type: application/json" -d '{}' >/dev/null 2>&1
    for _ in $(seq 1 15); do [ "$(playing)" = "false" ] && break; done
  fi
  timeout 40 curl -s -m 35 -X POST "$API/editor_play" -H "Content-Type: application/json" -d '{}' >/dev/null 2>&1
  for _ in $(seq 1 20); do [ "$(playing)" = "true" ] && break; done
  # and finally wait for physics on the bodies, not just for play mode
  wait_live 30
}

set_params () {
  timeout 30 $U exec "
var t = System.Type.GetType(\"PoSumo.Agent_Bot, PoSumo.Runtime\");
var f = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
t.GetField(\"StandKp\", f).SetValue(null, ${1}f);
t.GetField(\"StandKd\", f).SetValue(null, ${2}f);
t.GetField(\"StandKv\", f).SetValue(null, ${3}f);
t.GetField(\"StanceSplitDeg\", f).SetValue(null, ${4}f);
t.GetField(\"Gain\", f).SetValue(null, ${5}f);
t.GetField(\"StandKneeDeg\", f).SetValue(null, ${6}f);
return \"ok\";" >/dev/null 2>&1
}

sample () {
  timeout 25 $U exec '
var bodyT = System.Type.GetType("PoSumo.Agent_BipedBody, PoSumo.Runtime");
var bt = System.Type.GetType("PoSumo.Agent_Biped, PoSumo.Runtime");
var all = UnityEngine.Object.FindObjectsByType(bt, UnityEngine.FindObjectsSortMode.None);
if (all.Length == 0) return "none";
var sb = new System.Text.StringBuilder();
foreach (var o in all) {
  var mb = o as UnityEngine.MonoBehaviour;
  var body = mb.GetComponent(bodyT);
  var torso = (UnityEngine.Rigidbody2D)bodyT.GetField("Torso").GetValue(body);
  var chest = (UnityEngine.Rigidbody2D)bodyT.GetField("Chest").GetValue(body);
  if (!torso.simulated) { sb.Append("FRZ "); continue; }
  sb.Append(UnityEngine.Vector2.Dot(chest.transform.up, UnityEngine.Vector2.up).ToString("F2")).Append(",")
    .Append(torso.position.y.ToString("F2")).Append(",")
    .Append(torso.position.x.ToString("F2")).Append(" ");
}
return sb.ToString();' 2>&1 | grep -oE '"result": "[^"]*"' | sed 's/"result": "//; s/"$//'
}

trial () { # -> "upright gain"
  local UP=0 FIRST="" LAST="" LIVE=0
  for _ in $(seq 1 14); do
    S=$(sample)
    case "$S" in *none*|"") continue;; esac
    case "$S" in *FRZ*) continue;; esac
    LIVE=$((LIVE+1))
    for pair in $S; do
      U1=${pair%%,*}; rest=${pair#*,}; Y1=${rest%%,*}; X1=${rest#*,}
      ok=$(python -c "print(1 if $U1>0.7 and $Y1>0.80 else 0)" 2>/dev/null || echo 0)
      UP=$((UP+ok))
      [ -z "$FIRST" ] && FIRST=$X1
      LAST=$X1
    done
  done
  if [ -n "$FIRST" ] && [ -n "$LAST" ]; then
    G=$(python -c "print(round(abs($FIRST)-abs($LAST),2))" 2>/dev/null || echo 0)
  else G=0; fi
  # A trial that never saw a live frame measured NOTHING. Reporting it as 0
  # silently averages a dead instrument in with real data, which is exactly how
  # both earlier versions produced a confident ranking out of noise. Mark it
  # invalid so the caller drops it and says so.
  if [ "$LIVE" -lt 4 ]; then echo "INVALID 0 $LIVE"; else echo "$UP $G $LIVE"; fi
}

echo "config | meanUpright meanGainM meanScore  (n=$REPEATS, live-sample counts in brackets)"
BEST=-99999; BESTCFG=""
while read -r KP KD KV SPLIT GAIN KNEE; do
  [ -z "$KP" ] && continue
  SUMU=0; SUMG=0; LIVES=""; VALID=0; DEAD=0
  for r in $(seq 1 $REPEATS); do
    restart_play
    set_params "$KP" "$KD" "$KV" "$SPLIT" "$GAIN" "$KNEE"
    read -r U G L <<EOF2
$(trial)
EOF2
    if [ "$U" = "INVALID" ]; then
      DEAD=$((DEAD+1)); LIVES="${LIVES}x "
      continue
    fi
    VALID=$((VALID+1))
    SUMU=$(python -c "print($SUMU + $U)")
    SUMG=$(python -c "print(round($SUMG + $G,2))")
    LIVES="$LIVES$L "
  done
  if [ "$VALID" -eq 0 ]; then
    echo "$KP $KD $KV $SPLIT $GAIN $KNEE | NO VALID TRIALS ($DEAD dead) - NOT RANKED"
    continue
  fi
  MU=$(python -c "print(round($SUMU/$VALID,2))")
  MG=$(python -c "print(round($SUMG/$VALID,2))")
  SC=$(python -c "print(round($MU*10 + $MG*5,1))")
  echo "$KP $KD $KV $SPLIT $GAIN $KNEE | $MU $MG $SC  n=$VALID dead=$DEAD [$LIVES]"
  B=$(python -c "print(1 if $SC>$BEST else 0)")
  [ "$B" = "1" ] && { BEST=$SC; BESTCFG="$KP $KD $KV $SPLIT $GAIN $KNEE"; }
done <<'CFG'
22 4 26 20 2.5 7
22 4 26 20 2.5 7
14 3 26 20 2.5 7
34 6 26 20 2.5 7
22 4 40 20 2.5 7
22 4 26 26 2.5 7
CFG
echo "=== BEST: $BESTCFG score=$BEST ==="
