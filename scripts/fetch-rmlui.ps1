# Fetch RmlUi (MIT) into third_party/rmlui and apply the local patch set.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\fetch-rmlui.ps1 [-Tag 6.3] [-Force]

param(
    [string]$Tag = "6.3",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\rmlui"
$patch = Join-Path $PSScriptRoot "rmlui-rgba-alpha.patch"

function Invoke-Patch {
    if (-not (Test-Path $patch)) { return }

    git -C $dest apply --reverse --check $patch 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "RmlUi patch already applied."
        return
    }

    Write-Host "Applying rmlui-rgba-alpha.patch"
    git -C $dest apply --whitespace=nowarn $patch
    if ($LASTEXITCODE -ne 0) { throw "Failed to apply $patch" }
}

if ((Test-Path (Join-Path $dest ".git")) -and -not $Force) {
    Write-Host "RmlUi already present at $dest (use -Force to re-fetch)."
    Invoke-Patch
    exit 0
}

if ($Force -and (Test-Path $dest)) {
    Remove-Item -Recurse -Force $dest
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
Write-Host "Cloning RmlUi $Tag -> $dest"
git clone --depth 1 --branch $Tag --recurse-submodules --shallow-submodules `
    https://github.com/mikke89/RmlUi.git $dest
Invoke-Patch
Write-Host "Done."
