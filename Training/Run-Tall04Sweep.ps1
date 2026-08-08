<#
.SYNOPSIS
    Runs the Standard / Nick / Kim *_tall04 warm starts back to back.

.DESCRIPTION
    Matt already has his (matt_tall04). This brings the other three up to a
    comparable amount of training so the ROSTER stays balanced — the career
    ladder is zero-sum, so leaving three fighters on older brains while Matt has
    taken ~7M extra steps would skew the whole banzuke.

    It is NOT expected to change their gait. Four attempts on Matt failed to
    raise the walk height; see the walk-shaping note in CLAUDE.md for why the
    fall terminal dominates the shaping.

    Each run warm-starts from that fighter's *_fatigue01 trunk, so a run that is
    interrupted still leaves the shipped brain untouched on disk — nothing is
    deployed by this script.
#>
param(
    [ValidateRange(5, 600)][int]$CapMinutes = 75,
    [ValidateRange(1, 16)][int]$NumEnvs = 6
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$logPath = Join-Path $PSScriptRoot 'tall04-sweep.log'

function Write-Sweep([string]$m) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

# Ports spaced well past NumEnvs: a worker that outlives its trainer keeps its
# socket, and a reused base port then hangs the next run on a handshake the
# corpse answers.
$runs = @(
    [ordered]@{ Name='Standard'; Config='Training/configs/StandardTall04.yaml'; RunId='standard_tall04'; EnvName='StandardEnv'; Port=5025; From='standard_fatigue01' }
    [ordered]@{ Name='Nick';     Config='Training/configs/NickTall04.yaml';     RunId='nick_tall04';     EnvName='NickEnv';     Port=5045; From='nick_fatigue01' }
    [ordered]@{ Name='Kim';      Config='Training/configs/KimTall04.yaml';      RunId='kim_tall04';      EnvName='KimEnv';      Port=5065; From='kim_fatigue01' }
)

Write-Sweep "TALL04 SWEEP START — $($runs.Count) runs, cap ${CapMinutes}min each, num-envs $NumEnvs"

foreach ($r in $runs) {
    & (Join-Path $PSScriptRoot 'Stop-Training.ps1') *> $null

    Write-Sweep "START $($r.Name) -> $($r.RunId) from $($r.From) on port $($r.Port)"
    & (Join-Path $PSScriptRoot 'Start-Training.ps1') `
        -Config $r.Config -RunId $r.RunId -EnvName $r.EnvName `
        -NumEnvs $NumEnvs -BasePort $r.Port -InitializeFrom $r.From *> $null

    $started = Get-Date
    $deadline = $started.AddMinutes($CapMinutes)
    Start-Sleep -Seconds 45

    $ending = 'capped'
    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Name 'mlagents-learn' -ErrorAction SilentlyContinue).Count -eq 0) {
            $ending = 'completed'; break
        }
        Start-Sleep -Seconds 30
    }

    $mins = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    # The final unnumbered <Behavior>.onnx lands at the RUN ROOT, not inside the
    # behavior subfolder. Getting that path wrong is why an earlier sweep
    # reported finalOnnx=False on a run that had in fact written one.
    $final = Join-Path $repoRoot "Training/results/$($r.RunId)/$($r.Name).onnx"
    Write-Sweep "END   $($r.Name): $ending after ${mins}min  finalOnnx=$(Test-Path $final)"

    & (Join-Path $PSScriptRoot 'Stop-Training.ps1') *> $null
}

Write-Sweep 'TALL04 SWEEP DONE'
