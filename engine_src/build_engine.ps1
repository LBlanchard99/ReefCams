$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistRoot = Join-Path $Root "..\engine_dist"
$BuildRoot = Join-Path $Root "..\engine_build"
$PkgRoot = Join-Path $DistRoot "engine"
$VenvPath = Join-Path $Root ".venv"

Write-Host "Engine source: $Root"
Write-Host "Build dir:     $BuildRoot"
Write-Host "Dist dir:      $DistRoot"
Write-Host "Package dir:   $PkgRoot"

if (-not (Test-Path $VenvPath)) {
    Write-Host "Creating venv at $VenvPath"
    py -m venv $VenvPath
}

$Py = Join-Path $VenvPath "Scripts\python.exe"

Write-Host "Installing build requirements..."
& $Py -m pip install --upgrade pip | Out-Null
& $Py -m pip install --upgrade -r (Join-Path $Root "requirements_engine_build.txt")

Write-Host "Cleaning old build outputs..."
if (Test-Path $BuildRoot) { Remove-Item -Recurse -Force $BuildRoot }
if (Test-Path $DistRoot) { Remove-Item -Recurse -Force $DistRoot }

Write-Host "Running PyInstaller..."
Push-Location $Root
& $Py -m PyInstaller (Join-Path $Root "engine.spec") --clean --noconfirm --distpath $DistRoot --workpath $BuildRoot | Out-Host
Pop-Location

Write-Host "Copying models/benchmark/docs..."
New-Item -ItemType Directory -Force -Path (Join-Path $PkgRoot "models") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $PkgRoot "benchmark") | Out-Null

Copy-Item -Force -Path (Join-Path $Root "models\*") -Destination (Join-Path $PkgRoot "models")
Copy-Item -Force -Path (Join-Path $Root "benchmark\*") -Destination (Join-Path $PkgRoot "benchmark")
Copy-Item -Force -Path (Join-Path $Root "README_ENGINE.md") -Destination $PkgRoot

Write-Host "Done. Engine package is at $PkgRoot"
