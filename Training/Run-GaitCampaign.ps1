<#
.SYNOPSIS
    Unattended multi-hour gait campaign: runs the four fighters through
    <Name>Gait01.yaml in sequential batches, so the whole roster is covered by one
    launch that nobody has to babysit.

.DESCRIPTION
    WHY BATCHES AND NOT ALL FOUR AT ONCE. Memory, not cores. Each headless env is a
    full Unity player; four fighters at four envs is 16 players plus four trainers,
    which measured out at roughly 17 GB against about 8 GB free on this box. Two at
    a time fits with headroom. Cores are not the constraint here — there are 24.

    Each batch is given a wall-clock slice rather than being left to reach
    max_steps. That is deliberate: max_steps is sized so a batch does NOT finish
    early and sit idle burning its slice, and the curriculum in the YAML ramps
    against `progress` (elapsed/max_steps), so a batch stopped at its deadline
    simply ends partway up the ramp with every checkpoint intact and resumable.

    STOPPING IS GRACEFUL AND THAT MATTERS. Stop-Training.ps1 closes the trainer
    rather than killing it and waits 60 s, because the final-checkpoint write on a
    512x3 trunk is not instant and killing through it truncates the .pt. Even so,
    do not count on the top-level <Behavior>.onnx existing afterwards — the trainer
    only writes it on a clean exit at max_steps. Deploy from the newest NUMBERED
    checkpoint (DeployBrain.DeployLatestCheckpoint) after a deadline stop.

    THIS SCRIPT MUST OWN THE MACHINE. Two runs today were destroyed by Editor work
    landing mid-run — entering Play mode alongside eight env players took one run
    from 4.2M steps/hour to 69 steps in 80 minutes. Do not use the Unity Editor
    while this is running.

.PARAMETER Hours
    Total wall-clock budget, split evenly across the batches.

.EXAMPLE
    ./Training/Run-GaitCampaign.ps1 -Hours 8
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 48)][double]$Hours = 8,
    [ValidateRange(1, 8)][int]$NumEnvs = 4,
    [string[]]$Batch1 = @('Standard', 'Matt'),
    [string[]]$Batch2 = @('Nick', 'Kim')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$launcher = Join-Path $PSScriptRoot 'Start-StaminaExtension.ps1'
$stopper = Join-Path $PSScriptRoot 'Stop-Training.ps1'
$log = Join-Path $repoRoot 'Training/.gait-campaign.log'

function Write-Stamp([string]$message) {
    $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $message
    Write-Host $line
    Add-Content -Path $log -Value $line
}

$batches = @($Batch1, $Batch2) | Where-Object { $_ -and $_.Count -gt 0 }
$sliceMin = [int]([Math]::Floor(($Hours * 60) / $batches.Count))

Set-Content -Path $log -Value ""
Write-Stamp "GAIT CAMPAIGN start — $Hours h total, $($batches.Count) batches, $sliceMin min each"

for ($index = 0; $index -lt $batches.Count; $index++) {
    $fighters = $batches[$index]
    Write-Stamp "BATCH $($index + 1)/$($batches.Count): $($fighters -join ', ')"

    # Any survivor from the previous batch competes for memory with this one, and
    # an orphaned env player is invisible until you count processes — Stop-Training
    # found eight still alive after a stop that reported success.
    & $stopper *>&1 | Out-Null
    Start-Sleep -Seconds 10
    Get-Process -Name '*Env' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 5

    $freeGB = [Math]::Round((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue / 1024, 1)
    Write-Stamp "  memory before launch: $freeGB GB"

    try {
        # -Minutes 0: this script owns the deadline, so the launcher must not also
        # arm a watchdog. Two independent stoppers on one run is how a batch gets
        # killed at someone else's deadline.
        & $launcher -Fighters $fighters -NumEnvs $NumEnvs -Minutes 0 `
            -Phase 'Gait01' -InitializeFromPhase 'stamina01' `
            -MinFreeGB 6 *>&1 | ForEach-Object { Write-Stamp "  $_" }
    }
    catch {
        Write-Stamp "  LAUNCH FAILED: $($_.Exception.Message)"
        continue
    }

    $deadline = (Get-Date).AddMinutes($sliceMin)
    Write-Stamp "  running until $($deadline.ToString('HH:mm'))"

    # Poll rather than one long sleep, so a batch that dies on its own (or reaches
    # max_steps) releases the machine to the next batch instead of holding an empty
    # slice. 5 minutes is well under the 100k-step checkpoint interval.
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 300
        $alive = @(Get-Process -Name 'mlagents-learn' -ErrorAction SilentlyContinue).Count
        if ($alive -eq 0) {
            Write-Stamp "  trainers exited early (reached max_steps or died) — moving on"
            break
        }
    }

    Write-Stamp "  batch deadline reached; stopping gracefully"
    & $stopper *>&1 | Out-Null
    Start-Sleep -Seconds 20

    foreach ($fighter in $fighters) {
        $runId = "$($fighter.ToLowerInvariant())_gait01"
        $dir = Join-Path $repoRoot "Training/results/$runId/$fighter"
        $steps = $null
        if (Test-Path $dir) {
            $steps = Get-ChildItem $dir -Filter '*.pt' -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '-(\d+)\.pt$' } |
                ForEach-Object { [int]($_.Name -replace '.*-(\d+)\.pt$', '$1') } |
                Sort-Object -Descending | Select-Object -First 1
        }
        Write-Stamp "  RESULT $runId : $steps steps"
    }
}

Write-Stamp "GAIT CAMPAIGN complete"
Write-Stamp "Deploy from NUMBERED checkpoints (DeployLatestCheckpoint), not the top-level onnx — a deadline stop does not write a final export."
