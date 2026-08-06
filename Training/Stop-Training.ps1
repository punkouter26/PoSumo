<#
.SYNOPSIS
    Terminate a PoSumo training session and everything it leaked.

.DESCRIPTION
    Kills the trainer, then any orphaned headless env players, then TensorBoard,
    then prunes the empty run directories that staging leaves behind in the
    TensorBoard logdir.

    ORDER MATTERS. The trainer auto-respawns its env workers, so killing the
    workers first accomplishes nothing — mlagents-learn.exe has to go first, and
    on any disconnect it writes a final checkpoint before exiting, which is why
    this waits for it rather than hard-killing everything at once.

    TensorBoard is killed AFTER the trainer but the reason to kill it at all is
    what happens NEXT: it holds open handles on the run directories on Windows, and
    an `mlagents-learn --force` fired while it is live leaves the old run contents
    in place, silently and with no error. The surviving checkpoints then outrank
    the new run's numerically, so DeployLatestCheckpoint ships a brain from the run
    you thought you had deleted.

.PARAMETER KeepTensorBoard
    Leave TensorBoard running — for reading the curves of the run just stopped.
    Do NOT use this if the next thing you do is relaunch with --force.

.PARAMETER Prune
    Also delete zero-byte / event-less run directories from the logdir.

.EXAMPLE
    ./Training/Stop-Training.ps1
    ./Training/Stop-Training.ps1 -KeepTensorBoard
    ./Training/Stop-Training.ps1 -Prune
#>
[CmdletBinding()]
param(
    [switch]$KeepTensorBoard,
    [switch]$Prune
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$resultsDir = Join-Path $repoRoot 'Training/results'
$buildsDir = Join-Path $repoRoot 'Builds'

function Stop-TrackedProcess {
    param(
        [Parameter(Mandatory)][System.Diagnostics.Process[]]$Process,
        [Parameter(Mandatory)][string]$Label,
        [int]$GraceSeconds = 15
    )

    foreach ($proc in $Process) {
        Write-Host "  stopping $Label (PID $($proc.Id))"
        try {
            # CloseMainWindow first where there is one: the trainer treats it as a
            # disconnect and saves a final checkpoint. A bare Kill loses whatever
            # has been learned since the last numbered checkpoint.
            if (-not $proc.CloseMainWindow()) {
                $proc.Kill()
            }
        } catch {
            # Already gone between the enumerate and the stop — that is a success.
            continue
        }
    }

    foreach ($proc in $Process) {
        try {
            if (-not $proc.WaitForExit($GraceSeconds * 1000)) {
                Write-Warning "  $Label (PID $($proc.Id)) ignored the close request — killing"
                $proc.Kill()
                $null = $proc.WaitForExit(5000)
            }
        } catch {
            continue
        }
    }
}

# --- 1. the trainer -------------------------------------------------------
# Matched on name, not on a recorded PID: a session started by hand, by a
# previous shell, or by a crashed wrapper still has to be findable.
$trainers = @(Get-Process -Name 'mlagents-learn' -ErrorAction SilentlyContinue)
if ($trainers.Count -gt 0) {
    Write-Host "Trainer: $($trainers.Count) process(es)"
    # 60 s, not 15: the final-checkpoint write on a large trunk is not instant, and
    # killing through it is how a run ends up with a truncated .pt.
    Stop-TrackedProcess -Process $trainers -Label 'mlagents-learn' -GraceSeconds 60
} else {
    Write-Host 'Trainer: none running'
}

# --- 2. orphaned env players ---------------------------------------------
# Only meaningful once the trainer is down; before that they respawn. Matched by
# path under Builds/ so this cannot reach an unrelated Unity player the developer
# has open.
$envProcs = @()
foreach ($proc in (Get-Process -ErrorAction SilentlyContinue)) {
    try {
        $path = $proc.Path
    } catch {
        continue   # access denied on a system process — not ours
    }
    if ($path -and $path.StartsWith($buildsDir, [StringComparison]::OrdinalIgnoreCase)) {
        $envProcs += $proc
    }
}

if ($envProcs.Count -gt 0) {
    Write-Host "Orphaned env players: $($envProcs.Count)"
    Stop-TrackedProcess -Process $envProcs -Label 'env player' -GraceSeconds 10
} else {
    Write-Host 'Orphaned env players: none'
}

# --- 3. TensorBoard -------------------------------------------------------
if ($KeepTensorBoard) {
    Write-Host 'TensorBoard: left running (-KeepTensorBoard). Do NOT relaunch with --force until it is down.'
} else {
    # TensorBoard runs as `python -m tensorboard.main`, so the process name is
    # python.exe and only the command line distinguishes it from anything else the
    # venv is doing. Get-CimInstance is the only way to read that on Windows.
    $tbProcs = @()
    $cimProcs = Get-CimInstance Win32_Process -Filter "Name = 'python.exe'" -ErrorAction SilentlyContinue
    foreach ($cim in $cimProcs) {
        if ($cim.CommandLine -and $cim.CommandLine -match 'tensorboard') {
            $proc = Get-Process -Id $cim.ProcessId -ErrorAction SilentlyContinue
            if ($proc) { $tbProcs += $proc }
        }
    }

    if ($tbProcs.Count -gt 0) {
        Write-Host "TensorBoard: $($tbProcs.Count) process(es)"
        Stop-TrackedProcess -Process $tbProcs -Label 'tensorboard' -GraceSeconds 10
    } else {
        Write-Host 'TensorBoard: none running'
    }
}

# --- 4. prune empty runs from the logdir ---------------------------------
# Training/results IS the TensorBoard logdir, so it is a curated list rather than
# a dumping ground. --initialize-from resolves relative to --results-dir, so
# staging directories must sit INSIDE it at launch but carry no history; they show
# up forever afterwards as empty runs.
if ($Prune) {
    if (-not (Test-Path $resultsDir)) {
        Write-Host "Prune: $resultsDir does not exist (gitignored, and absent in a fresh clone)"
    } else {
        $removed = 0
        foreach ($dir in (Get-ChildItem -Path $resultsDir -Directory -ErrorAction SilentlyContinue)) {
            $events = Get-ChildItem -Path $dir.FullName -Recurse -File -Filter '*tfevents*' -ErrorAction SilentlyContinue
            if (-not $events -or $events.Count -eq 0) {
                Write-Host "  pruning event-less run: $($dir.Name)"
                Remove-Item -Path $dir.FullName -Recurse -Force
                $removed++
            }
        }
        Write-Host "Prune: removed $removed event-less run director$(if ($removed -eq 1) { 'y' } else { 'ies' })"
    }
} else {
    Write-Host 'Prune: skipped (pass -Prune to remove event-less runs)'
}

Write-Host ''
Write-Host 'STOP RESULT: training session terminated'
