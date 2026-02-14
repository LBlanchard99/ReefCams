param(
    [switch]$StopRunningEngine
)

$ErrorActionPreference = "Stop"

function Remove-PathWithRetries {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Attempts = 5,
        [int]$DelaySeconds = 2
    )
    if (-not (Test-Path $Path)) { return }

    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            $msg = $_.Exception.Message
            if ($i -lt $Attempts) {
                Write-Warning "Failed to remove '$Path' (attempt $i/$Attempts): $msg"
                Start-Sleep -Seconds $DelaySeconds
                continue
            }
            throw "Failed to remove '$Path' after $Attempts attempts. A file is likely locked by another process. Close any running 'engine.exe' instances and windows open in '$Path', then retry."
        }
    }
}

function Stop-EngineUnderPath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath
    )
    $target = [System.IO.Path]::GetFullPath($RootPath)
    $procs = Get-Process -Name "engine" -ErrorAction SilentlyContinue
    foreach ($proc in $procs) {
        $procPath = $null
        try {
            $procPath = $proc.Path
        }
        catch {
            continue
        }
        if (-not $procPath) { continue }
        $fullProcPath = [System.IO.Path]::GetFullPath($procPath)
        if ($fullProcPath.StartsWith($target, [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Stopping running engine process PID $($proc.Id): $fullProcPath"
            Stop-Process -Id $proc.Id -Force -ErrorAction Stop
        }
    }
}

function Resolve-FfprobeSourcePath {
    param(
        [Parameter(Mandatory = $true)][string]$EngineRoot
    )
    $candidates = @(
        (Join-Path $EngineRoot "ffprobe.exe"),
        (Join-Path $EngineRoot "tools\\ffprobe.exe"),
        (Join-Path (Split-Path -Parent $EngineRoot) "ffprobe.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }
    return $null
}

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$DistRoot = Join-Path $Root "..\engine_dist"
$BuildRoot = Join-Path $Root "..\engine_build"
$PkgRoot = Join-Path $DistRoot "engine"
$VenvPath = Join-Path $Root ".venv"
$FfprobeSource = Resolve-FfprobeSourcePath -EngineRoot $Root

Write-Host "Engine source: $Root"
Write-Host "Build dir:     $BuildRoot"
Write-Host "Dist dir:      $DistRoot"
Write-Host "Package dir:   $PkgRoot"
if (-not $FfprobeSource) {
    throw "ffprobe.exe not found. Place it at '$Root\\ffprobe.exe' or '$Root\\tools\\ffprobe.exe' (or repo root) so it can be bundled."
}
Write-Host "ffprobe source: $FfprobeSource"

if (-not (Test-Path $VenvPath)) {
    Write-Host "Creating venv at $VenvPath"
    py -m venv $VenvPath
}

$Py = Join-Path $VenvPath "Scripts\python.exe"

Write-Host "Installing build requirements..."
& $Py -m pip install --upgrade pip | Out-Null
& $Py -m pip install --upgrade -r (Join-Path $Root "requirements_engine_build.txt")

if ($StopRunningEngine) {
    Stop-EngineUnderPath -RootPath $DistRoot
}

Write-Host "Cleaning old build outputs..."
Remove-PathWithRetries -Path $BuildRoot
Remove-PathWithRetries -Path $DistRoot

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
Copy-Item -Force -Path $FfprobeSource -Destination (Join-Path $PkgRoot "ffprobe.exe")

Write-Host "Done. Engine package is at $PkgRoot"
