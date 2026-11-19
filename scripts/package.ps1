# Assemble a distributable CuRmlGUI plugin folder and zip it.
#   powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Config Release]
# Expects bin\<Config>\CuRmlGUI.dll and native\out\<Config>\WebGUINative.dll to exist.
param(
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release",
    [string]$OutDir = "publish"
)

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent $PSScriptRoot
$bin   = Join-Path $root "bin\$Config"
$native = Join-Path $root "native\out\$Config\WebGUINative.dll"

$managed = Join-Path $bin "CuRmlGUI.dll"
if (-not (Test-Path $managed)) { throw "Missing $managed. Build managed first." }
if (-not (Test-Path $native))  { throw "Missing $native. Build native first." }

$stage = Join-Path $root "$OutDir\CuRmlGUI"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Managed plugin + deps
Copy-Item $managed (Join-Path $stage "CuRmlGUI.dll") -Force
foreach ($dep in @("SkiaSharp.dll", "libSkiaSharp.dll",
                   "System.Buffers.dll", "System.Memory.dll",
                   "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll")) {
    $p = Join-Path $bin $dep
    if (Test-Path $p) { Copy-Item $p (Join-Path $stage $dep) -Force }
}

# Native runtime
Copy-Item $native (Join-Path $stage "WebGUINative.dll") -Force

# Fonts
$fontsSrc = Join-Path $root "assets\fonts"
$fontsDst = Join-Path $stage "assets\fonts"
New-Item -ItemType Directory -Force -Path $fontsDst | Out-Null
Copy-Item (Join-Path $fontsSrc "*") $fontsDst -Force

# Docs
foreach ($doc in @("README.md", "LICENSE")) {
    $dp = Join-Path $root $doc
    if (Test-Path $dp) { Copy-Item $dp (Join-Path $stage $doc) -Force }
}

# Zip
$zip = Join-Path $root "$OutDir\CuRmlGUI-0.1.0.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

Write-Host "== Packaged =="
Get-ChildItem $stage -Recurse -File | ForEach-Object {
    "{0,12}  {1}" -f $_.Length, $_.FullName.Substring($stage.Length + 1)
}
Write-Host "== Zip =="
Write-Host $zip
