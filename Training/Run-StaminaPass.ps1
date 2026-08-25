<#
.SYNOPSIS
    Unattended launcher for the *_stamina01 pass: all four fighters, two at a
    time, with TensorBoard up and a guard against launching into no memory.

.DESCRIPTION
    Wraps Start-Training.ps1 for the specific job of running the four
    stamina01 configs to completion without supervision. It exists because the
    thing that actually kills an overnight run here is not a bad
    hyperparameter, it is launching four trainers onto a box that then swaps.

    RUN IDs ARE NOT TIMESTAMPED, DELIBERATELY. The project rule is that a
    run-id pairs 1:1 with its config as <name>_<phase><nn> - Training/README.md
    maps every config to the run it produced, and DeployBrain.cs pins each
    fighter's menu item to a named run. A timestamped id would break both and
    make the brain that ships untraceable to the recipe that made it. The
    timestamp goes in the LOG name instead, which is where it is useful.

    PAIRS, NOT ALL FOUR. Two trainers plus their env players is about 8 GB
    here. Four at once was measured (Training/README.md) taking a 16 GB box to
    0.7 GB free, which swaps, which destabilises every run at the same time.
    CPU is not the constraint - this box has 24 logical cores - memory is.

.PARAMETER NumEnvs
    Concurrent headless env players PER RUN. Two runs are live at once, so the
    real player count is double this. 4 gives 8 players across 2 trainers.

.PARAMETER MinFreeGB
    Refuse to launch below this much available memory. The whole point of the
    script; do not lower it to "just get it started".

.EXAMPLE
    ./Training/Run-StaminaPass.ps1
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 8)][int]$NumEnvs = 4,
    [ValidateRange(1, 64)][int]$MinFreeGB = 8,
    [switch]$SkipMemoryCheck
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDir  = Join-Path $repoRoot 'Training/logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$logFile = Join-Path $logDir "stamina-pass-$stamp.log"

function Say([string]$m) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $logFile -Value $line
}

# The four runs, in launch order. Pair 1 goes first, then pair 2.
$runs = @(
    @{ Name = 'Matt';     Config = 'Training/configs/MattStamina01.yaml';     RunId = 'matt_stamina01';     Env = 'MattEnv';     Port = 5005; Pair = 1 }
    @{ Name = 'Standard'; Config = 'Training/configs/StandardStamina01.yaml'; RunId = 'standard_stamina01'; Env = 'StandardEnv'; Port = 5105; Pair = 1 }
    @{ Name = 'Nick';     Config = 'Training/configs/NickStamina01.yaml';     RunId = 'nick_stamina01';     Env = 'NickEnv';     Port = 5205; Pair = 2 }
    @{ Name = 'Kim';      Config = 'Training/configs/KimStamina01.yaml';      RunId = 'kim_stamina01';      Env = 'KimEnv';      Port = 5305; Pair = 2 }
)

Say "stamina01 pass starting. log: $logFile"
Say "num-envs $NumEnvs per run, 2 runs concurrent -> $($NumEnvs * 2) env players"

# ---------------------------------------------------------------- preflight
$venvPython = Join-Path $repoRoot 'Training/venv/Scripts/python.exe'
$learn      = Join-Path $repoRoot 'Training/venv/Scripts/mlagents-learn.exe'
foreach ($p in @($venvPython, $learn)) {
    if (-not (Test-Path $p)) { throw "missing: $p - the venv is not built" }
}

foreach ($r in $runs) {
    if (-not (Test-Path $r.Config)) { throw "missing config: $($r.Config)" }
    $exe = "Builds/$($r.Env)/$($r.Env).exe"
    if (-not (Test-Path $exe)) {
        throw "missing env: $exe - build it with the PoSumo menu item 'Build $($r.Name) Training Env' first"
    }
}
Say "preflight ok: venv, 4 configs, 4 env binaries all present"

# Memory guard. Available MBytes counts reclaimable standby memory, so it is
# the honest number; FreePhysicalMemory is not.
if (-not $SkipMemoryCheck) {
    $availGB = [math]::Round((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue / 1024, 1)
    Say "available memory: $availGB GB (minimum $MinFreeGB GB)"
    if ($availGB -lt $MinFreeGB) {
        Say "ABORT: not enough memory. Two trainers plus $($NumEnvs * 2) env players need roughly 8 GB."
        Say "Close whatever is holding memory and re-run. Largest consumers:"
        Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 5 |
            ForEach-Object { Say ("    {0,-22} {1,6:N2} GB  pid {2}" -f $_.ProcessName, ($_.WorkingSet64/1GB), $_.Id) }
        throw "insufficient memory: $availGB GB < $MinFreeGB GB"
    }
}

# ------------------------------------------------------------- tensorboard
# Project rule: TensorBoard runs alongside training, and is started BEFORE the
# trainer so the run appears the moment the first summary lands.
$tb = Get-CimInstance Win32_Process -Filter "Name='python.exe'" |
      Where-Object { $_.CommandLine -like '*tensorboard*' } | Select-Object -First 1
if ($tb) {
    Say "tensorboard already running (pid $($tb.ProcessId))"
} else {
    Start-Process -FilePath $venvPython `
        -ArgumentList '-m', 'tensorboard.main', '--logdir', 'Training/results', '--port', '6006', '--reload_interval', '15' `
        -WindowStyle Minimized
    Start-Sleep -Seconds 5
    Say "tensorboard started"
}
Say "TENSORBOARD URL: http://localhost:6006/"

# -------------------------------------------------------------------- run
function Start-Run($r) {
    # NOT $args - that is a PowerShell automatic variable inside a function and
    # assigning to it is legal but shadows the caller's argument array.
    $argList = @(
        $r.Config
        "--run-id=$($r.RunId)"
        '--results-dir=Training/results'
        "--env=Builds/$($r.Env)/$($r.Env).exe"
        "--num-envs=$NumEnvs"
        '--no-graphics'
        "--base-port=$($r.Port)"
        '--force'
    )
    $out = Join-Path $logDir "$($r.RunId)-$stamp.out.log"
    Say "launch $($r.RunId)  env=$($r.Env)  base-port=$($r.Port)  -> $out"
    Start-Process -FilePath $learn -ArgumentList $argList `
        -RedirectStandardOutput $out -RedirectStandardError "$out.err" `
        -WindowStyle Minimized -PassThru
}

foreach ($pair in 1, 2) {
    $batch = $runs | Where-Object { $_.Pair -eq $pair }
    Say "---- pair $pair : $(($batch | ForEach-Object { $_.RunId }) -join ', ') ----"

    $procs = @()
    foreach ($r in $batch) {
        $procs += Start-Run $r
        Start-Sleep -Seconds 20   # stagger, so two env builds do not race the same GPU/driver init
    }

    Say "pair $pair running (pids: $(($procs | ForEach-Object { $_.Id }) -join ', ')). waiting..."
    foreach ($p in $procs) {
        $p.WaitForExit()
        Say "exited: pid $($p.Id) code $($p.ExitCode)"
    }
    Say "---- pair $pair complete ----"
}

Say "ALL FOUR RUNS COMPLETE"
Say "TensorBoard: http://localhost:6006/"
Say "Next: judge each run on ELO SHAPE (not level), confirm stamina varies, then"
Say "      MatchTestHarness.Run(10) head-to-head before deploying anything."
