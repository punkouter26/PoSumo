<#
.SYNOPSIS
    Launch a PoSumo training run with TensorBoard, correct CLI flags, and a
    recorded session file.

.DESCRIPTION
    Wraps the mlagents-learn invocation so the three things that are easy to get
    wrong are not optional:

      * TensorBoard always runs alongside the trainer (project rule), and is
        started BEFORE it so the run appears the moment the first summary lands;
      * --base-port is always explicit, so two concurrent runs cannot collide on
        the worker sockets;
      * --num-envs is bounded to the 4-8 range the training workflow calls for and
        checked against the machine's actual core count.

    The session (trainer PID, TensorBoard PID, run id, port range) is written to
    Training/.session.json so Stop-Training.ps1 and any dashboard can find it.

.PARAMETER Config
    Path to the YAML under Training/configs/.

.PARAMETER RunId
    Run id. Must be <name>_<phase><nn> to pair 1:1 with the config.

.PARAMETER EnvName
    Env folder under Builds/, e.g. "MattEnv" for Builds/MattEnv/MattEnv.exe.

.PARAMETER NumEnvs
    Concurrent headless env players. 4-8; defaults to 6.

.PARAMETER BasePort
    First worker port. The trainer takes NumEnvs consecutive ports from here.

.PARAMETER Force
    Pass --force (cold restart, discards the existing run). TensorBoard is stopped
    first and restarted afterwards — see the warning below.

.PARAMETER Resume
    Pass --resume (warm restart, parameter-only tweaks).

.EXAMPLE
    ./Training/Start-Training.ps1 -Config Training/configs/MattUnified03.yaml `
        -RunId matt_unified03 -EnvName MattEnv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Config,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$EnvName,
    [ValidateRange(1, 16)][int]$NumEnvs = 6,
    [ValidateRange(1024, 65000)][int]$BasePort = 5005,
    [switch]$Force,
    [switch]$Resume
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$venvPython = Join-Path $repoRoot 'Training/venv/Scripts/python.exe'
$learnExe = Join-Path $repoRoot 'Training/venv/Scripts/mlagents-learn.exe'
$resultsDir = Join-Path $repoRoot 'Training/results'
$envExe = Join-Path $repoRoot "Builds/$EnvName/$EnvName.exe"
$sessionFile = Join-Path $repoRoot 'Training/.session.json'

if ($Force -and $Resume) {
    throw '--force and --resume are mutually exclusive: one discards the run, the other continues it.'
}
foreach ($required in @($venvPython, $learnExe, $envExe, $Config)) {
    if (-not (Test-Path $required)) {
        throw "missing: $required"
    }
}

# The trainer's own torch threads and TensorBoard both need cores. Over-subscribing
# does not fail, it just makes every env slower than the last, which reads as a
# hyperparameter problem rather than a scheduling one.
$cores = [Environment]::ProcessorCount
if ($NumEnvs -lt 4 -or $NumEnvs -gt 8) {
    Write-Warning "NumEnvs $NumEnvs is outside the 4-8 band the training workflow calls for."
}
if ($NumEnvs -gt ($cores - 4)) {
    Write-Warning "NumEnvs $NumEnvs on $cores logical cores leaves under 4 for the trainer and TensorBoard."
}

# A --force with TensorBoard live leaves the old run contents IN PLACE, silently:
# it holds handles on the run directories on Windows. The surviving checkpoints
# then outrank the new run's numerically for a long while, so
# DeployBrain.DeployLatestCheckpoint ships a brain from the run you deleted.
if ($Force) {
    Write-Host 'Force restart: stopping any live TensorBoard before the run directory is cleared.'
    & (Join-Path $PSScriptRoot 'Stop-Training.ps1') | Out-Null
}

if (-not (Test-Path $resultsDir)) {
    New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null
}

# TensorBoard first, so the run shows up as soon as the first summary is written.
Write-Host 'Starting TensorBoard on http://localhost:6006 ...'
$tb = Start-Process -FilePath $venvPython -PassThru -WindowStyle Minimized -ArgumentList @(
    '-m', 'tensorboard.main',
    '--logdir', 'Training/results',
    '--port', '6006',
    '--reload_interval', '15'
) -WorkingDirectory $repoRoot

$learnArgs = @(
    $Config,
    "--run-id=$RunId",
    "--results-dir=Training/results",
    "--env=Builds/$EnvName/$EnvName.exe",
    "--num-envs=$NumEnvs",
    "--base-port=$BasePort",
    '--no-graphics'
)
if ($Force) { $learnArgs += '--force' }
if ($Resume) { $learnArgs += '--resume' }

Write-Host "Starting trainer: $RunId"
Write-Host "  envs $NumEnvs on ports $BasePort..$($BasePort + $NumEnvs - 1)"
$trainer = Start-Process -FilePath $learnExe -PassThru -ArgumentList $learnArgs -WorkingDirectory $repoRoot

# Recorded so a dashboard, a later shell, or Stop-Training can find this session
# without guessing. Deliberately not the only way processes are found — Stop
# matches by name and path too, because a wrapper that crashes must not strand a
# run that nothing can then locate.
$session = [ordered]@{
    runId         = $RunId
    config        = $Config
    envName       = $EnvName
    numEnvs       = $NumEnvs
    basePort      = $BasePort
    trainerPid    = $trainer.Id
    tensorboardPid = $tb.Id
    tensorboardUrl = 'http://localhost:6006'
    telemetryUrl  = 'http://127.0.0.1:8787/metrics'
    startedUtc    = (Get-Date).ToUniversalTime().ToString('o')
}
$session | ConvertTo-Json -Depth 4 | Set-Content -Path $sessionFile -Encoding utf8

Write-Host ''
Write-Host "START RESULT: run-id=$RunId trainer=$($trainer.Id) tensorboard=$($tb.Id)"
Write-Host "  TensorBoard  http://localhost:6006"
Write-Host "  Live metrics http://127.0.0.1:8787/metrics  (each env walks upward from 8787)"
Write-Host "  Stop with    ./Training/Stop-Training.ps1"
