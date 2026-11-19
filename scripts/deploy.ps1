# 部署 CuRmlGUI 到游戏 BepInEx\plugins\CuRmlGUI\。
# 用法:
#   powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -Build
#   powershell -ExecutionPolicy Bypass -File scripts\deploy.ps1 -GameDir "D:\Steam\...\Casualties Unknown"
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Casualties Unknown",
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release",
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$bin  = Join-Path $root "bin\$Config"
$nativeOut = Join-Path $root "native\out\$Config\WebGUINative.dll"

if ($Build) {
    Write-Host "== Build native =="
    & (Join-Path $PSScriptRoot "build-native.ps1") -Config $Config
    Write-Host "== Build managed =="
    & dotnet build (Join-Path $root "CuRmlGUI.csproj") -c $Config
}

$managed = Join-Path $bin "CuRmlGUI.dll"
if (-not (Test-Path $managed))   { throw "Missing $managed. Build first (-Build)." }
if (-not (Test-Path $nativeOut)) { throw "Missing $nativeOut. Build native first (-Build)." }

$pluginDir = Join-Path $GameDir "BepInEx\plugins\CuRmlGUI"
if (-not (Test-Path (Join-Path $GameDir "BepInEx"))) {
    throw "BepInEx not found under '$GameDir'."
}

# 关闭游戏，避免 DLL 被占用。
$procs = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -like "Casualties*" }
if ($procs) { $procs | Stop-Process -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 300 }

New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

Write-Host "== Copy to $pluginDir =="

# 托管插件
Copy-Item $managed (Join-Path $pluginDir "CuRmlGUI.dll") -Force
$pdb = Join-Path $bin "CuRmlGUI.pdb"
if (Test-Path $pdb) { Copy-Item $pdb (Join-Path $pluginDir "CuRmlGUI.pdb") -Force }

# 原生运行时（静态 CRT，无额外依赖）
Copy-Item $nativeOut (Join-Path $pluginDir "WebGUINative.dll") -Force

# SkiaSharp 托管 + 原生（1.68.3 的 libSkiaSharp.dll 直接落在 bin 根目录，
# 复制到插件根目录，供 NativeLoader 用绝对路径预加载）
Copy-Item (Join-Path $bin "SkiaSharp.dll") (Join-Path $pluginDir "SkiaSharp.dll") -Force
$skiaNative = Join-Path $bin "libSkiaSharp.dll"
if (-not (Test-Path $skiaNative)) { throw "Missing $skiaNative" }
Copy-Item $skiaNative (Join-Path $pluginDir "libSkiaSharp.dll") -Force

# SkiaSharp 在 net472 下的托管依赖
foreach ($dep in @("System.Buffers.dll", "System.Memory.dll", "System.Numerics.Vectors.dll", "System.Runtime.CompilerServices.Unsafe.dll")) {
    $p = Join-Path $bin $dep
    if (Test-Path $p) { Copy-Item $p (Join-Path $pluginDir $dep) -Force }
}

# 清理旧版本遗留（SkiaSharp 2.x 时代需要、1.68.3 不再使用）
$stale = Join-Path $pluginDir "System.Runtime.InteropServices.RuntimeInformation.dll"
if (Test-Path $stale) { Remove-Item $stale -Force }

# 发行字体（Latin 主字体 + CJK 子集回退字体）
$fontsSrc = Join-Path $root "assets\fonts"
if (Test-Path $fontsSrc) {
    $fontsDst = Join-Path $pluginDir "assets\fonts"
    New-Item -ItemType Directory -Force -Path $fontsDst | Out-Null
    Copy-Item (Join-Path $fontsSrc "*") $fontsDst -Force
}

Write-Host "== Deployed files =="
Get-ChildItem $pluginDir | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "OK"
