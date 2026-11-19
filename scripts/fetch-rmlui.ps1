# 拉取/锁定 RmlUi 源码到 third_party/rmlui（MIT）。
# 已存在则跳过。版本通过 -Tag 控制，默认 6.3。
param(
    [string]$Tag = "6.3",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "third_party\rmlui"

if ((Test-Path (Join-Path $dest ".git")) -and -not $Force) {
    Write-Host "RmlUi already present at $dest (use -Force to re-fetch)."
    exit 0
}
if ($Force -and (Test-Path $dest)) {
    Remove-Item -Recurse -Force $dest
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
Write-Host "Cloning RmlUi $Tag -> $dest"
git clone --depth 1 --branch $Tag --recurse-submodules --shallow-submodules `
    https://github.com/mikke89/RmlUi.git $dest
Write-Host "Done."
