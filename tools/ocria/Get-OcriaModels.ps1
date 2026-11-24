<#
.SYNOPSIS
Downloads the OCRia-nano-1 ONNX models the "ocria" provider runs, and checks every file's size and SHA-256.

.DESCRIPTION
The models are published by LlamaIndex under a modified Open RAIL-M license: free for research, personal use and
organizations under USD 2M in annual revenue or funding; anything larger, or a product that competes with LlamaParse,
needs a commercial license from LlamaIndex. Read it before using the models:
https://huggingface.co/llamaindex/OCRia-nano-1/blob/v1.0.0/LICENSE

Files go to the same folder LiteParse uses, so `lit models pull ocria-nano` and this script share one copy:
$env:OCRIA_MODEL_DIR, else $env:LITEPARSE_MODEL_DIR\ocria-nano, else %LOCALAPPDATA%\liteparse\models\ocria-nano
(~/.cache/liteparse/models/ocria-nano elsewhere). Files that are already there and match their pin are skipped.

.PARAMETER Scripts
Which recognizers to fetch besides the always-installed Latin one: cjk, rtl, world.

.PARAMETER AcceptLicense
Accept the model license without a prompt (for CI).

.EXAMPLE
pwsh tools/ocria/Get-OcriaModels.ps1
pwsh tools/ocria/Get-OcriaModels.ps1 -Scripts cjk,rtl,world -AcceptLicense
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [string[]] $Scripts = @(),
    [switch] $AcceptLicense
)

$ErrorActionPreference = 'Stop'
# `-File` hands "cjk,rtl" over as one string, so split here rather than relying on array binding.
$Scripts = @($Scripts | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
foreach ($s in $Scripts) {
    if ($s -notin 'cjk', 'rtl', 'world') { throw "Unknown script '$s'; expected cjk, rtl or world." }
}
$manifest = Get-Content (Join-Path $PSScriptRoot 'models.json') -Raw | ConvertFrom-Json

if (-not $Destination) {
    $Destination =
        if ($env:OCRIA_MODEL_DIR) { $env:OCRIA_MODEL_DIR }
        elseif ($env:LITEPARSE_MODEL_DIR) { Join-Path $env:LITEPARSE_MODEL_DIR 'ocria-nano' }
        elseif ($IsWindows -or $env:OS -eq 'Windows_NT') { Join-Path $env:LOCALAPPDATA 'liteparse\models\ocria-nano' }
        else { Join-Path $HOME '.cache/liteparse/models/ocria-nano' }
}

Write-Host "OCRia-nano-1 $($manifest.version) is licensed by LlamaIndex under a modified Open RAIL-M license."
Write-Host "Free for research, personal use and organizations under USD 2M revenue/funding; others need a commercial license."
Write-Host "License: $($manifest.license)"
if (-not $AcceptLicense) {
    $answer = Read-Host 'Do you accept the license? [y/N]'
    if ($answer -notmatch '^(y|yes)$') { throw 'License not accepted; nothing downloaded.' }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$wanted = $manifest.files | Where-Object { $_.script -eq 'core' -or $Scripts -contains $_.script }

function Test-Pin([string] $path, $file) {
    if (-not (Test-Path $path)) { return $false }
    if ((Get-Item $path).Length -ne $file.size) { return $false }
    return (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $file.sha256
}

foreach ($file in $wanted) {
    $target = Join-Path $Destination $file.name
    if (Test-Pin $target $file) {
        Write-Host "  ok        $($file.name)"
        continue
    }
    $partial = "$target.partial"
    Write-Host "  download  $($file.name) ($([math]::Round($file.size / 1KB)) KB)"
    Invoke-WebRequest -Uri "$($manifest.baseUrl)/$($file.name)" -OutFile $partial -UseBasicParsing
    if (-not (Test-Pin $partial $file)) {
        Remove-Item $partial -Force
        throw "$($file.name) does not match its pinned size/SHA-256; refusing to install it."
    }
    Move-Item $partial $target -Force
}

Write-Host "Installed to $Destination"
