<#
.SYNOPSIS
    Resume the `*_stamina01` campaign toward its 15M budget, unattended, for a
    bounded wall-clock window.

.DESCRIPTION
    All four stamina01 runs completed their 9M gate on 2026-08-16. The config
    headers state the follow-on explicitly: raise max_steps to 15000000 and
    relaunch with --resume (never --force, which deletes the run). This script is
    that relaunch for one or more fighters at once.

    Why a wrapper instead of calling Start-Training.ps1 four times: that script
    starts its own TensorBoard and writes a single Training/.session.json, so a
    second call would fail to bind port 6006 and the third would overwrite the
    session record of the first two. Here TensorBoard is started once and the
    session file lists every run.

    THREE THINGS THAT ARE NOT OPTIONAL, and are enforced rather than documented:

      * max_steps must already be above the resumed step count. A --resume into a
        config whose max_steps equals the checkpoint's step count exits
        immediately having trained nothing, and looks exactly like a successful
        short run. Checked per fighter before launch.
      * --base-port must be explicit and spaced. The trainer takes NumEnvs
        consecutive ports from it, so two runs on the default 5005 collide on the
        worker sockets and the second hangs waiting for a handshake the first
        already answered.
      * --num-envs stays at 4, matching what the original stamina01 runs used
        (Training/results/<run>/run_logs/Player-0..3.log). ML-Agents batches
        experience per env, so changing it mid-campaign makes the continuation a
        different run rather than a longer one.

    MEMORY IS THE REAL LIMIT HERE, not cores. Each headless env is a full Unity
    player. Four fighters at 4 envs is 16 players plus 4 trainers, and on a 31 GB
    box that only fits if nothing large is already resident. The guard below
    refuses to launch rather than sending the machine into paging, where every
    env runs slower than the last and it reads as a hyperparameter problem.

.PARAMETER Fighters
    Which fighters to resume. Each maps to Training/configs/<Name>Stamina01.yaml,
    run id <name>_stamina01, and Builds/<Name>Env/<Name>Env.exe.

.PARAMETER Minutes
    Wall-clock budget. At the deadline Stop-Training.ps1 is called, which closes
    the trainers rather than killing them so the final checkpoint write lands.
    0 leaves the runs going until they reach max_steps on their own.

.EXAMPLE
    ./Training/Start-StaminaExtension.ps1 -Fighters Standard,Matt -Minutes 60
#>
[CmdletBinding()]
param(
    [ValidateSet('Standard', 'Matt', 'Nick', 'Kim')]
    [string[]]$Fighters = @('Standard', 'Matt'),
    [ValidateRange(1, 8)][int]$NumEnvs = 4,
    [ValidateRange(0, 1440)][int]$Minutes = 60,
    [ValidateRange(1024, 65000)][int]$BasePort = 5005,
    [int]$MinFreeGB = 12,
    [switch]$SkipMemoryCheck,

    # Campaign selector. 'Stamina01' resumes the 15M extension; 'Gait01' starts the
    # crawling-gait fine-tune, which is a NEW run id and therefore cannot resume —
    # it warm-starts from the stamina trunk instead. Adding a campaign here is a
    # PascalCase config stem plus its lowercase run suffix; nothing else changes.
    # 'Obs01' is the corrective run for the world-absolute observation 0 fix, and
    # carries the upright walk/stance shaping raised on 2026-08-25. New run id, so
    # it warm-starts from the tall04 trunks rather than resuming.
    [ValidateSet('Stamina01', 'Gait01', 'Obs01')]
    [string]$Phase = 'Stamina01',

    # Source run for --initialize-from, e.g. 'stamina01'. Resolves BY BEHAVIOR NAME
    # and RELATIVE TO --results-dir, so the source must sit in Training/results and
    # carry a behavior of the same name. network_settings must match the source
    # trunk exactly (512 x 3 here) or the load fails outright.
    [string]$InitializeFromPhase
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$venvPython = Join-Path $repoRoot 'Training/venv/Scripts/python.exe'
$learnExe = Join-Path $repoRoot 'Training/venv/Scripts/mlagents-learn.exe'
$sessionFile = Join-Path $repoRoot 'Training/.session.json'

foreach ($required in @($venvPython, $learnExe)) {
    if (-not (Test-Path $required)) { throw "missing: $required" }
}

# Roughly 0.7 GB per headless env player plus ~1.5 GB per trainer, measured
# against the stamina01 runs. Deliberately a floor with headroom rather than a
# tight fit: the cost of guessing low is an hour of thrashing that produces a
# worse policy than not running at all.
$needGB = [Math]::Ceiling(($Fighters.Count * $NumEnvs * 0.7) + ($Fighters.Count * 1.5))
$freeGB = [Math]::Round((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue / 1024, 1)
Write-Host "Memory: ${freeGB} GB available, estimate ${needGB} GB needed for $($Fighters.Count) run(s) x $NumEnvs envs."
if (-not $SkipMemoryCheck -and $freeGB -lt [Math]::Max($needGB, $MinFreeGB)) {
    throw ("only ${freeGB} GB available; need about ${needGB} GB. " +
           'Close whatever is resident (check: Get-Process | Sort WorkingSet64 -Desc | Select -First 5) ' +
           'or lower -Fighters / -NumEnvs. Override with -SkipMemoryCheck.')
}

$cores = [Environment]::ProcessorCount
$totalEnvs = $Fighters.Count * $NumEnvs
if ($totalEnvs -gt ($cores - 4)) {
    Write-Warning "$totalEnvs env players on $cores logical cores leaves under 4 for the trainers and TensorBoard."
}

# TensorBoard first, so a run appears the moment its first summary lands. Only
# one, and only if nothing already holds 6006 — a second bind fails silently
# enough that you notice an hour later with no graphs.
$tbPid = $null
$tbLive = @(Get-NetTCPConnection -State Listen -LocalPort 6006 -ErrorAction SilentlyContinue)
if ($tbLive.Count -gt 0) {
    Write-Host 'TensorBoard already listening on 6006; leaving it alone.'
}
else {
    Write-Host 'Starting TensorBoard on http://localhost:6006 ...'
    $tb = Start-Process -FilePath $venvPython -PassThru -WindowStyle Minimized -ArgumentList @(
        '-m', 'tensorboard.main',
        '--logdir', 'Training/results',
        '--port', '6006',
        '--reload_interval', '15'
    ) -WorkingDirectory $repoRoot
    $tbPid = $tb.Id
}

$launched = @()
$port = $BasePort

$phaseSuffix = $Phase.ToLowerInvariant()

foreach ($fighter in $Fighters) {
    $runId = "$($fighter.ToLowerInvariant())_$phaseSuffix"
    $config = Join-Path $repoRoot "Training/configs/${fighter}${Phase}.yaml"
    $envExe = Join-Path $repoRoot "Builds/${fighter}Env/${fighter}Env.exe"
    $runDir = Join-Path $repoRoot "Training/results/$runId"

    foreach ($required in @($config, $envExe)) {
        if (-not (Test-Path $required)) { throw "missing for ${fighter}: $required" }
    }

    $maxSteps = [int]((Select-String -Path $config -Pattern '^\s+max_steps:\s*(\d+)').Matches[0].Groups[1].Value)
    $reached = $null
    if (Test-Path (Join-Path $runDir $fighter)) {
        $reached = (Get-ChildItem (Join-Path $runDir $fighter) -Filter '*.pt' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '-(\d+)\.pt$' } |
            ForEach-Object { [int]($_.Name -replace '.*-(\d+)\.pt$', '$1') } |
            Sort-Object -Descending | Select-Object -First 1)
    }

    $learnArgs = @(
        $config,
        "--run-id=$runId",
        '--results-dir=Training/results',
        "--env=Builds/${fighter}Env/${fighter}Env.exe",
        "--num-envs=$NumEnvs",
        "--base-port=$port",
        '--no-graphics'
    )

    if ($InitializeFromPhase) {
        # A fresh run id: --resume would be meaningless and --force would be a lie.
        $source = "$($fighter.ToLowerInvariant())_$($InitializeFromPhase.ToLowerInvariant())"
        $sourceDir = Join-Path $repoRoot "Training/results/$source"
        if (-not (Test-Path $sourceDir)) { throw "InitializeFrom source not found: $sourceDir" }
        if (Test-Path $runDir) {
            throw "$runId already exists. A warm start must begin from an empty run id, or the trainer resumes it instead of loading $source. Delete it or pick a new phase."
        }
        $learnArgs += "--initialize-from=$source"
        Write-Host "$runId : warm start from $source toward $maxSteps"
    }
    else {
        # The guard that cost a wasted launch: a resume already AT max_steps trains
        # nothing, exits clean, and looks exactly like a successful short run.
        if ($null -ne $reached -and $maxSteps -le $reached) {
            throw "$runId is already at $reached steps and max_steps is $maxSteps — raise max_steps in $config or this resume trains nothing."
        }
        $learnArgs += '--resume'
        Write-Host "$runId : resuming from $reached toward $maxSteps"
    }
    $proc = Start-Process -FilePath $learnExe -PassThru -ArgumentList $learnArgs -WorkingDirectory $repoRoot
    Write-Host "  trainer pid $($proc.Id) on ports $port..$($port + $NumEnvs - 1)"

    $launched += [ordered]@{
        runId      = $runId
        fighter    = $fighter
        config     = "Training/configs/${fighter}Stamina01.yaml"
        envName    = "${fighter}Env"
        numEnvs    = $NumEnvs
        basePort   = $port
        trainerPid = $proc.Id
        resumedAt  = $reached
        maxSteps   = $maxSteps
    }

    # Spaced by 10 so a run can grow to 8 envs without overlapping its neighbour.
    $port += 10
}

$session = [ordered]@{
    campaign       = 'stamina01 extension to 15M'
    runs           = $launched
    tensorboardPid = $tbPid
    tensorboardUrl = 'http://localhost:6006'
    telemetryUrl   = 'http://127.0.0.1:8787/metrics'
    startedUtc     = (Get-Date).ToUniversalTime().ToString('o')
    stopAfterMin   = $Minutes
}
$session | ConvertTo-Json -Depth 5 | Set-Content -Path $sessionFile -Encoding utf8

Write-Host ''
Write-Host "START RESULT: $($launched.Count) run(s) launched — $($launched.runId -join ', ')"
Write-Host '  TensorBoard  http://localhost:6006'
Write-Host '  Live metrics http://127.0.0.1:8787/metrics  (each env walks upward from 8787)'
Write-Host "  Session      Training/.session.json"

if ($Minutes -gt 0) {
    # Detached, so this shell can close without taking the deadline with it.
    # Stop-Training.ps1 closes the trainers rather than killing them and waits 60 s,
    # because the final-checkpoint write on a 512x3 trunk is not instant and
    # killing through it truncates the .pt.
    $stopScript = Join-Path $PSScriptRoot 'Stop-Training.ps1'
    $watchdog = "Start-Sleep -Seconds $($Minutes * 60); & '$stopScript'"
    Start-Process -FilePath 'pwsh' -WindowStyle Minimized -ArgumentList @(
        '-NoProfile', '-Command', $watchdog
    ) | Out-Null
    Write-Host "  Auto-stop    in $Minutes min (graceful; final checkpoints are written)"
}
Write-Host "  Stop early   ./Training/Stop-Training.ps1"
