#requires -Version 7.0
<#
.SYNOPSIS
    Restores, builds, tests and (optionally) runs the PoChopAudio desktop app.
.DESCRIPTION
    PoChopAudio is a WinUI 3 desktop app. There is no server and no web client: audio splitting
    and image cutout both run in-process through PoChopAudio.Services.
.EXAMPLE
    ./SCRIPTS/setup.ps1 -Run
#>
[CmdletBinding()]
param(
    [switch]$Run,

    # The app is unpackaged and self-contained, so it must be built for a concrete architecture.
    # Defaults to whatever this machine is.
    #
    # Read from RuntimeInformation, not $env:PROCESSOR_ARCHITECTURE: that variable reports the
    # architecture of the *process*, so it reads AMD64 inside an emulated x64 shell on an ARM64
    # machine and the script would silently build x64 and then look for the exe under bin/ARM64.
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = $(
        switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
            'Arm64' { 'ARM64' }
            'X86'   { 'x86' }
            default { 'x64' }
        }
    )
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

try {
    $sdk = (dotnet --version)
    if ([version]($sdk -split '-')[0] -lt [version]'10.0.0') {
        throw "PoChopAudio needs the .NET 10 SDK; found $sdk."
    }

    $rid = "win-$($Platform.ToLowerInvariant())"

    Write-Host "Restoring…" -ForegroundColor Cyan
    dotnet restore PoChopAudio.slnx

    Write-Host "Building ($Platform)…" -ForegroundColor Cyan
    dotnet build PoChopAudio.slnx --no-restore -c Release

    Write-Host "Testing…" -ForegroundColor Cyan
    dotnet test PoChopAudio.slnx --no-build -c Release

    if ($Run) {
        # The WinUI project is built separately from the solution pass: it needs an explicit
        # platform and RID, which the solution-wide build does not supply.
        Write-Host "Building the desktop app ($rid)…" -ForegroundColor Cyan
        dotnet build src/PoChopAudio.WinUI/PoChopAudio.WinUI.csproj -c Release -p:Platform=$Platform -r $rid

        # Run from bin, not publish. `dotnet publish` drops PoChopAudio.WinUI.pri — the app's own
        # resource index — and without it the app dies at startup with a stowed exception
        # (0xC000027B) inside Microsoft.UI.Xaml.dll. The build output has the .pri and every
        # self-contained Windows App SDK file, so it is the supported way to launch.
        $exe = Join-Path $root "src/PoChopAudio.WinUI/bin/$Platform/Release/net10.0-windows10.0.22621.0/$rid/PoChopAudio.WinUI.exe"
        if (-not (Test-Path $exe)) {
            throw "Built app not found at $exe."
        }

        Write-Host "Starting $exe" -ForegroundColor Green
        & $exe
    }
}
finally {
    Pop-Location
}
