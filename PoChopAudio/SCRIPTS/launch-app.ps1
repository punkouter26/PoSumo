$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\src\PoChopAudio.WinUI\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\PoChopAudio.WinUI.exe'
if (-not (Test-Path $exe)) { throw "Not built: $exe" }
Start-Process -FilePath $exe
Start-Sleep -Seconds 6
Get-Process PoChopAudio.WinUI -ErrorAction SilentlyContinue |
    Format-Table Id, MainWindowTitle, Responding -AutoSize