<#
.SYNOPSIS
    Runs the four *_tall01 cold retrains back to back inside a wall-clock budget.

.DESCRIPTION
    One sequential pass over Matt, Standard, Nick and Kim. Each run is started
    through Start-Training.ps1 (so it gets TensorBoard, an explicit --base-port
    and a .session.json entry like any other run) and is then given at most
    -PerRunMinutes of wall clock before it is torn down and the next one starts.

    WHY A WALL-CLOCK CAP AT ALL. These are COLD retrains — staminaObservation
    took the vector 45 -> 46, so no *_fatigue01 checkpoint can be loaded and
    --initialize-from is unavailable. max_steps is 6M against the 12-45M the
    shipped trunks took, and four of them have to fit one evening. The cap is
    what keeps a slow run from eating the budget of the three behind it.

    A run that reaches max_steps exits on its own and is detected immediately —
    the cap is a backstop, not the normal path. That distinction matters: only a
    trainer that shuts down cleanly writes the unnumbered <Behavior>.onnx that
    DeployBrain reads. A capped run leaves only numbered checkpoints, which is
    why the configs use checkpoint_interval 100000 / keep_checkpoints 20 and why
    the summary below reports which ending each run got.

    Teardown is Stop-Training.ps1, which closes the trainer window rather than
    killing it and waits for the final checkpoint write. Killing through that
    write truncates the .pt.

.PARAMETER PerRunMinutes
    Wall-clock cap per fighter. Four runs at the default 120 is eight hours.

.PARAMETER NumEnvs
    Concurrent env players per run. Recorded in each config header, because
    ML-Agents batches experience differently per env count — changing this makes
    a different run, not a faster one.

.EXAMPLE
    ./Training/Run-TallSweep.ps1 -PerRunMinutes 120
#>
param(
    [ValidateRange(5, 600)][int]$PerRunMinutes = 120,
    [ValidateRange(1, 16)][int]$NumEnvs = 6,
    [string[]]$Only
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $PSScriptRoot 'tall-sweep.log'

function Write-Sweep([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $message
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

# Base ports are spaced by more than NumEnvs even though the runs are strictly
# sequential. A worker that outlives its trainer still holds its socket, and a
# reused base port then hangs the next run on a handshake the corpse answers.
$runs = @(
    [ordered]@{ Name = 'Matt';     Config = 'Training/configs/MattTall01.yaml';     RunId = 'matt_tall01';     EnvName = 'MattEnv';     BasePort = 5005 }
    [ordered]@{ Name = 'Standard'; Config = 'Training/configs/StandardTall01.yaml'; RunId = 'standard_tall01'; EnvName = 'StandardEnv'; BasePort = 5025 }
    [ordered]@{ Name = 'Nick';     Config = 'Training/configs/NickTall01.yaml';     RunId = 'nick_tall01';     EnvName = 'NickEnv';     BasePort = 5045 }
    [ordered]@{ Name = 'Kim';      Config = 'Training/configs/KimTall01.yaml';      RunId = 'kim_tall01';      EnvName = 'KimEnv';      BasePort = 5065 }
)

if ($Only) {
    $runs = $runs | Where-Object { $Only -contains $_.Name }
}

Write-Sweep "SWEEP START — $($runs.Count) run(s), cap ${PerRunMinutes}min each, num-envs $NumEnvs"

$results = @()

foreach ($run in $runs) {
    $envExe = Join-Path $repoRoot "Builds/$($run.EnvName)/$($run.EnvName).exe"
    if (-not (Test-Path $envExe)) {
        Write-Sweep "SKIP $($run.Name): env missing at $envExe"
        $results += [pscustomobject]@{ Name = $run.Name; Ending = 'skipped-no-env'; Minutes = 0 }
        continue
    }

    # Clean slate: a previous trainer or a TensorBoard holding handles on the
    # results dir would otherwise still be live when the next run starts.
    & (Join-Path $PSScriptRoot 'Stop-Training.ps1') *> $null

    Write-Sweep "START $($run.Name) -> $($run.RunId) on port $($run.BasePort)"
    & (Join-Path $PSScriptRoot 'Start-Training.ps1') `
        -Config $run.Config -RunId $run.RunId -EnvName $run.EnvName `
        -NumEnvs $NumEnvs -BasePort $run.BasePort *> $null

    $started = Get-Date
    $deadline = $started.AddMinutes($PerRunMinutes)
    Start-Sleep -Seconds 45   # let the trainer process appear before watching for its absence

    $ending = 'capped'
    while ((Get-Date) -lt $deadline) {
        $alive = @(Get-Process -Name 'mlagents-learn' -ErrorAction SilentlyContinue)
        if ($alive.Count -eq 0) {
            $ending = 'completed'
            break
        }
        Start-Sleep -Seconds 30
    }

    $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    Write-Sweep "END   $($run.Name): $ending after ${minutes}min"

    & (Join-Path $PSScriptRoot 'Stop-Training.ps1') *> $null

    $steps = -1
    $ckptDir = Join-Path $repoRoot "Training/results/$($run.RunId)/$($run.Name)"
    if (Test-Path $ckptDir) {
        $numbered = Get-ChildItem -Path $ckptDir -Filter '*.onnx' -ErrorAction SilentlyContinue |
            ForEach-Object { if ($_.BaseName -match '-(\d+)$') { [int]$Matches[1] } }
        if ($numbered) { $steps = ($numbered | Measure-Object -Maximum).Maximum }
    }
    $finalOnnx = Join-Path $ckptDir "$($run.Name).onnx"
    Write-Sweep "      $($run.Name): maxCheckpoint=$steps finalOnnx=$(Test-Path $finalOnnx)"

    $results += [pscustomobject]@{
        Name      = $run.Name
        Ending    = $ending
        Minutes   = $minutes
        MaxStep   = $steps
        FinalOnnx = (Test-Path $finalOnnx)
    }
}

Write-Sweep 'SWEEP RESULT:'
foreach ($r in $results) {
    Write-Sweep ("  {0,-9} {1,-10} {2,6}min  maxStep={3}  finalOnnx={4}" -f $r.Name, $r.Ending, $r.Minutes, $r.MaxStep, $r.FinalOnnx)
}
Write-Sweep 'SWEEP DONE'
