<#
.SYNOPSIS
    Second half of the stamina01 pass: waits for the 3M gate to finish, raises
    max_steps to 9M, and resumes all four runs in pairs.

.DESCRIPTION
    Run this AFTER (or alongside) Run-StaminaPass.ps1. It blocks until no
    mlagents-learn process has been alive for a settle window, so the 3M gate
    completes and writes its final checkpoints before anything is touched.

    WHY IT WAITS INSTEAD OF EDITING THE CONFIGS NOW. mlagents reads the YAML at
    launch. Pair 2 (nick, kim) has not started yet, so bumping max_steps early
    would send them straight to 9M cold while pair 1 got 3M-then-resume. Same
    end step count, different optimisation path, and the four brains would no
    longer be comparable. Every fighter gets the identical treatment.

    --resume, NEVER --force. --force deletes the run directory, which is the
    whole 3M trunk. It also silently no-ops while TensorBoard holds Windows
    handles on the run dirs (see Training/README.md), which is worse than
    failing: the old checkpoints survive with higher step numbers than the new
    run reaches for hours, and DeployLatestCheckpoint then ships a brain from
    the run you thought you deleted.

    THE LEARNING RATE STEPS BACK UP AT THE RESUME POINT, and that is expected.
    The schedule is linear over (1 - step/max_steps): at 3M of a 3M budget it
    has decayed to ~0, and re-reading it against a 9M budget puts it back at
    ~2e-4. That is the normal way to extend a run. It is only a problem if you
    forget it happened and read the jump as instability.

.PARAMETER TargetSteps
    New max_steps written into all four configs.

.PARAMETER NumEnvs
    Env players per run. Two runs are live at once, so double this in total.
#>
[CmdletBinding()]
param(
    [int]$TargetSteps = 9000000,
    [ValidateRange(1, 8)][int]$NumEnvs = 4,
    [int]$SettleSeconds = 60
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir  = Join-Path $repoRoot 'Training/logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "stamina-extend-$stamp.log"

function Say([string]$m) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

$learn = Join-Path $repoRoot 'Training/venv/Scripts/mlagents-learn.exe'
$runs = @(
    @{ Name='Matt';     Config='Training/configs/MattStamina01.yaml';     RunId='matt_stamina01';     Env='MattEnv';     Port=5005; Pair=1 }
    @{ Name='Standard'; Config='Training/configs/StandardStamina01.yaml'; RunId='standard_stamina01'; Env='StandardEnv'; Port=5105; Pair=1 }
    @{ Name='Nick';     Config='Training/configs/NickStamina01.yaml';     RunId='nick_stamina01';     Env='NickEnv';     Port=5205; Pair=2 }
    @{ Name='Kim';      Config='Training/configs/KimStamina01.yaml';      RunId='kim_stamina01';      Env='KimEnv';      Port=5305; Pair=2 }
)

Say "extend pass armed. target max_steps = $TargetSteps. log: $logFile"

# ------------------------------------------------- wait for the 3M gate
Say "waiting for the 3M gate to finish (no trainer alive for ${SettleSeconds}s)..."
$quietSince = $null
while ($true) {
    $alive = @(Get-Process mlagents-learn -ErrorAction SilentlyContinue)
    if ($alive.Count -gt 0) {
        if ($quietSince) { Say "trainer reappeared ($($alive.Count) alive) - resetting settle timer" }
        $quietSince = $null
    } else {
        if (-not $quietSince) { $quietSince = Get-Date; Say "no trainers alive; settling..." }
        elseif (((Get-Date) - $quietSince).TotalSeconds -ge $SettleSeconds) { break }
    }
    Start-Sleep -Seconds 15
}
Say "3M gate complete."

foreach ($r in $runs) {
    $dir = "Training/results/$($r.RunId)"
    if (-not (Test-Path $dir)) { throw "no run directory to resume: $dir" }
    # if/else, not the ?: ternary - that is PowerShell 7 only and this is
    # launched with `powershell -File`, which is Windows PowerShell 5.1.
    #
    # The checkpoint lives under <run>/<BehaviorName>/, NOT in the run root -
    # the first version looked in the root, reported MISSING for all four, and
    # was ignored because it only warns. Resolve it properly so a genuinely
    # absent checkpoint is loud.
    $ckPath = Join-Path $dir "$($r.Name)/checkpoint.pt"
    if (Test-Path $ckPath) { $ck = 'ok' } else { $ck = 'MISSING' }
    Say ("  {0,-20} checkpoint: {1}  ({2})" -f $r.RunId, $ck, $ckPath)
    if ($ck -eq 'MISSING') { throw "cannot resume $($r.RunId): no checkpoint at $ckPath" }
}

# ------------------------------------------------- raise max_steps
foreach ($r in $runs) {
    # -Encoding UTF8 is REQUIRED. Windows PowerShell 5.1's Get-Content defaults
    # to the ANSI code page, so it reads a BOM-less UTF-8 file byte-by-byte as
    # cp1252; writing that back out re-encodes each byte as UTF-8 and the file
    # is now double-encoded mojibake. That is what corrupted these configs on
    # the second attempt, and it is nastier than the BOM because the result is
    # still VALID UTF-8 - nothing complains until mlagents' loader, which opens
    # the YAML with the locale encoding and dies on any byte over 127.
    #
    # The configs are deliberately pure ASCII for that last reason: mlagents
    # does not pass an encoding to open(), so a single em-dash in a comment is
    # enough to make a config unloadable on a Windows box.
    $txt = Get-Content $r.Config -Raw -Encoding UTF8
    $new = $txt -replace 'max_steps:\s*\d+', "max_steps: $TargetSteps"
    # Verify the TARGET is present, do not verify that the text CHANGED. Those
    # are different checks and the second one is wrong: on a re-run the file
    # already says 9000000, the replace is a no-op, and a changed-text guard
    # then throws on the one case that is completely fine. This script must be
    # safe to re-run - the first attempt died on a BOM and had to be repeated.
    if ($new -notmatch "max_steps:\s*$TargetSteps") {
        throw "max_steps not set to $TargetSteps in $($r.Config)"
    }
    # NOT Set-Content -Encoding utf8. Under Windows PowerShell 5.1 that writes a
    # UTF-8 BOM, and mlagents' YAML loader rejects it outright with
    #   TrainerConfigError: There was an error decoding Config file ...
    #   Make sure your file is save using UTF-8
    # which is a confusing message for a file that IS UTF-8 - just not bare.
    # This cost the first resume attempt: all four launches died instantly.
    [System.IO.File]::WriteAllText(
        (Join-Path $repoRoot $r.Config), $new, (New-Object System.Text.UTF8Encoding($false)))
    Say "max_steps -> $TargetSteps in $($r.Config)"
}

# ------------------------------------------------- resume in pairs
function Start-Resume($r) {
    $argList = @(
        $r.Config
        "--run-id=$($r.RunId)"
        '--results-dir=Training/results'
        "--env=Builds/$($r.Env)/$($r.Env).exe"
        "--num-envs=$NumEnvs"
        '--no-graphics'
        "--base-port=$($r.Port)"
        '--resume'
    )
    $out = Join-Path $logDir "$($r.RunId)-extend-$stamp.out.log"
    Say "resume $($r.RunId) -> $out"
    Start-Process -FilePath $learn -ArgumentList $argList `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err" `
        -WindowStyle Minimized -PassThru
}

foreach ($pair in 1, 2) {
    $batch = $runs | Where-Object { $_.Pair -eq $pair }
    Say "---- resuming pair $pair : $(($batch | ForEach-Object { $_.RunId }) -join ', ') ----"
    $procs = @()
    foreach ($r in $batch) { $procs += Start-Resume $r; Start-Sleep -Seconds 20 }
    Say "pair $pair running (pids: $(($procs | ForEach-Object { $_.Id }) -join ', ')). waiting..."
    foreach ($p in $procs) { $p.WaitForExit(); Say "exited: pid $($p.Id) code $($p.ExitCode)" }
    Say "---- pair $pair complete ----"
}

Say "EXTEND PASS COMPLETE - all four runs at $TargetSteps steps"
Say "TensorBoard: http://localhost:6006/"
