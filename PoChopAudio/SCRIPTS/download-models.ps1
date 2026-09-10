# Downloads ~4 MB u2netp background-removal model into the API project's Content\Models folder.
# Idempotent: skips if the file already exists. Re-run with -Force to redownload.
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ProjectRoot = Split-Path -Parent $PSScriptRoot
$TargetDir = Join-Path $ProjectRoot 'src\PoChopAudio.WinUI\Content\Models'
$Target = Join-Path $TargetDir 'u2netp.onnx'

if (-not $Force -and (Test-Path -LiteralPath $Target) -and (Get-Item $Target).Length -gt 1MB) {
    Write-Host "Model already present at $Target ($([math]::Round((Get-Item $Target).Length / 1MB, 2)) MB), skipping."
    Write-Host "Use -Force to redownload."
    return
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

# Source: BritishWerewolf/U-2-Netp on Hugging Face — Apache-2.0 licensed ONNX export of u2netp.
# ~4.4 MB. The DanielGeng/U-2-Net releases tab no longer hosts ONNX files; this is the canonical
# community mirror used by rembg and Hugging Face Transformers.js.
$Url = 'https://huggingface.co/BritishWerewolf/U-2-Netp/resolve/main/onnx/model.onnx'
$Temp = [IO.Path]::GetTempFileName()

Write-Host "Downloading u2netp.onnx from $Url"
try {
    Invoke-WebRequest -Uri $Url -OutFile $Temp -UseBasicParsing -TimeoutSec 120
    $expected = 4MB
    $actual = (Get-Item $Temp).Length
    if ($actual -lt $expected) {
        throw "Downloaded file is only $([math]::Round($actual / 1MB, 2)) MB; expected ~4.4 MB. URL may be stale."
    }
    Move-Item -LiteralPath $Temp -Destination $Target -Force
    Write-Host "Saved to $Target ($([math]::Round($actual / 1MB, 2)) MB)"
}
finally {
    if (Test-Path -LiteralPath $Temp) {
        Remove-Item -LiteralPath $Temp -Force -ErrorAction SilentlyContinue
    }
}
