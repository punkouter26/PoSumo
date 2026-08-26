#!/usr/bin/env bash
# ============================================================================
# require-tensorboard.sh — BLOCKING HOOK
#
# TensorBoard must be running whenever a trainer is. See .claude/rules/training.md
# for why: ELO is the only accept/reject signal for a self-play fight run, ELO
# exists only as a TensorBoard scalar, and mean reward — the number the console
# prints — is explicitly the wrong criterion. A run without TensorBoard burns
# hours of compute and leaves nothing readable behind.
#
# The wrappers (Start-Training.ps1, Start-StaminaExtension.ps1, and
# Run-GaitCampaign.ps1 which shells out to the second) all start TensorBoard
# themselves. The ONLY path with no TensorBoard in it is calling
# mlagents-learn.exe directly, so that is what this blocks — and only when
# nothing is already listening on 6006.
#
# Deliberately NOT a two-strike gate like bash-gate.sh: there is no case where
# "I acknowledge the consequences" is the right answer. Start TensorBoard, or
# use a wrapper. Both take one command.
# ============================================================================
# Trigger: PreToolUse on Bash
# Exit:    2 = block, 0 = allow
# ============================================================================

set -euo pipefail

INPUT=$(cat)
COMMAND=$(echo "$INPUT" | jq -r '.tool_input.command // empty')

if [ -z "$COMMAND" ]; then
    exit 0
fi

# Only a DIRECT trainer invocation. The wrappers are the sanctioned path and
# name the trainer internally, so exempt anything that goes through them.
if ! echo "$COMMAND" | grep -qEi 'mlagents-learn(\.exe)?|mlagents\.trainers\.learn'; then
    exit 0
fi
if echo "$COMMAND" | grep -qEi 'Start-Training\.ps1|Start-StaminaExtension\.ps1|Run-GaitCampaign\.ps1'; then
    exit 0
fi

# INSPECTION IS NOT LAUNCHING.
#
# The first version of this hook matched any command containing the trainer's
# name, and immediately blocked `Get-Process mlagents-learn` — i.e. the very
# command that answers "is a run in progress?". Asking whether training is
# running is the opposite of starting it without TensorBoard, and a guard that
# blocks its own diagnostic teaches people to work around it.
#
# Anchored on the verbs, because the trainer name will appear as an ARGUMENT to
# all of these rather than as the command being run.
#
# Both the process verbs AND the file verbs are needed, and the second group was
# added only after the hook blocked `ls Training/venv/Scripts/mlagents-learn.exe`
# — an existence check. Every time this list is too narrow the guard fires on
# someone trying to find out the state of the world, which is exactly the person
# it should be helping.
if echo "$COMMAND" | grep -qEi '(Get-Process|Stop-Process|Wait-Process|pgrep|pkill|taskkill|tasklist|ps -|grep|Where-Object|Select-String|which |command -v)'; then
    exit 0
fi
if echo "$COMMAND" | grep -qEi '(^|[|;&] *)(ls|ll|dir|stat|file|cat|head|tail|wc|test|\[|Test-Path|Get-Item|Get-ChildItem|Get-Content)( |$)'; then
    exit 0
fi
# Stop-Training.ps1 tears a run DOWN; it names the trainer to kill it.
if echo "$COMMAND" | grep -qEi 'Stop-Training\.ps1'; then
    exit 0
fi

# Already listening on 6006? Then the requirement is satisfied however it got
# there, and blocking would be noise. Checked with PowerShell because this runs
# on Windows under Git Bash, where ss/netstat may be absent.
TB_LIVE=""
if command -v powershell.exe >/dev/null 2>&1; then
    TB_LIVE=$(powershell.exe -NoProfile -Command \
        "@(Get-NetTCPConnection -State Listen -LocalPort 6006 -EA SilentlyContinue).Count" \
        2>/dev/null | tr -d '\r\n ' || true)
fi
if [ "${TB_LIVE:-0}" != "0" ] && [ -n "${TB_LIVE:-}" ]; then
    exit 0
fi

cat >&2 <<'MSG'
BLOCKED: starting a trainer with no TensorBoard.

Nothing is listening on port 6006, and this command calls mlagents-learn
directly rather than through a wrapper that would start it.

WHY THIS IS BLOCKED, not warned:
  A self-play fight run is accepted or rejected on the SHAPE OF THE ELO CURVE,
  and ELO exists only as a TensorBoard scalar. Mean reward — the number the
  trainer prints to the console — is explicitly NOT the criterion: it has been
  measured climbing to ~36 while ELO fell 1198 -> 1140, i.e. the policy learned
  to farm shaping instead of winning bouts. Without TensorBoard the run's only
  readable output is the one number you must not judge it by.

FIX — either of these:

  1. Use a wrapper (they start TensorBoard first, and skip if 6006 is taken):
       Training\Start-Training.ps1 ...
       Training\Start-StaminaExtension.ps1 -Fighters Standard,Matt -Phase Obs01 ...

  2. Or start TensorBoard yourself, then re-run this command:
       Training\venv\Scripts\python.exe -m tensorboard.main \
         --logdir Training/results --port 6006 --reload_interval 15

NOTE: if this run uses --force, stop TensorBoard FIRST and restart it after.
It holds Windows handles on the run directories, so a --force fired while it is
live leaves the old contents in place silently.

See .claude/rules/training.md
MSG
exit 2
