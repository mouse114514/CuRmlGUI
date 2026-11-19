# 构建原生运行时 WebGUINative.dll（x64, 静态 CRT）。
# 依赖: Visual Studio 2026 (MSVC 14.51) + 其自带 CMake。无需 vcvarsall（走 VS 生成器）。
param(
    [ValidateSet("Debug", "Release")]
    [string]$Config = "Release",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$root  = Split-Path -Parent $PSScriptRoot
$vs    = "C:\Program Files\Microsoft Visual Studio\18\Community"
$cmake = Join-Path $vs "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$src   = Join-Path $root "native"
$build = Join-Path $src "build"

if (-not (Test-Path (Join-Path $root "third_party\rmlui\CMakeLists.txt"))) {
    throw "RmlUi source missing. Run scripts/fetch-rmlui.ps1 first."
}

if ($Clean -and (Test-Path $build)) {
    Remove-Item -Recurse -Force $build
}

Write-Host "== Configure =="
& $cmake -S $src -B $build -G "Visual Studio 18 2026" -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)" }

Write-Host "== Build ($Config) =="
& $cmake --build $build --config $Config
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)" }

$out = Join-Path $src "out\$Config\WebGUINative.dll"
if (Test-Path $out) {
    Write-Host "OK -> $out"
} else {
    throw "Build finished but WebGUINative.dll not found at $out"
}
