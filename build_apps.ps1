param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot = "artifacts\packages",
    [switch]$SkipEngineBuild,
    [switch]$NoRestore,
    [switch]$NoZip,
    [switch]$StopRunningEngine
)

$ErrorActionPreference = "Stop"

function Remove-PathIfExists {
    param([Parameter(Mandatory = $true)][string]$PathToRemove)
    if (Test-Path $PathToRemove) {
        Remove-Item -LiteralPath $PathToRemove -Recurse -Force
    }
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$DirectoryPath)
    New-Item -ItemType Directory -Path $DirectoryPath -Force | Out-Null
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    & $FileName @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed ($LASTEXITCODE): $FileName $($Arguments -join ' ')"
    }
}

$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$EngineBuildScript = Join-Path $RepoRoot "engine_src\build_engine.ps1"
$ViewerProject = Join-Path $RepoRoot "src\ReefCams.Viewer\ReefCams.Viewer.csproj"
$ProcessorProject = Join-Path $RepoRoot "src\ReefCams.Processor\ReefCams.Processor.csproj"
$ProcessorLauncherProject = Join-Path $RepoRoot "src\ReefCams.Processor.Launcher\ReefCams.Processor.Launcher.csproj"
$EngineDist = Join-Path $RepoRoot "engine_dist\engine"

$OutputRootFull = Join-Path $RepoRoot $OutputRoot
$StagingRoot = Join-Path $OutputRootFull "_staging"
$ViewerPublish = Join-Path $StagingRoot "viewer_publish"
$ProcessorPublish = Join-Path $StagingRoot "processor_publish"
$ProcessorLauncherPublish = Join-Path $StagingRoot "processor_launcher_publish"
$ViewerPackage = Join-Path $OutputRootFull "ReefCams.Viewer"
$ProcessorPackage = Join-Path $OutputRootFull "ReefCams.Processor"
$ProcessorInternal = Join-Path $ProcessorPackage "_internal"

Write-Host "Repo root:       $RepoRoot"
Write-Host "Configuration:   $Configuration"
Write-Host "Runtime:         $Runtime"
Write-Host "Output root:     $OutputRootFull"

Ensure-Directory -DirectoryPath $OutputRootFull
Remove-PathIfExists -PathToRemove $StagingRoot
Remove-PathIfExists -PathToRemove $ViewerPackage
Remove-PathIfExists -PathToRemove $ProcessorPackage
Ensure-Directory -DirectoryPath $ViewerPublish
Ensure-Directory -DirectoryPath $ProcessorPublish
Ensure-Directory -DirectoryPath $ProcessorLauncherPublish

if (-not $SkipEngineBuild) {
    if (-not (Test-Path $EngineBuildScript)) {
        throw "Engine build script not found: $EngineBuildScript"
    }

    Write-Host "Building engine package..."
    & $EngineBuildScript -StopRunningEngine:$StopRunningEngine.IsPresent
    if ($LASTEXITCODE -ne 0) {
        throw "Engine build failed."
    }
}

if (-not (Test-Path $EngineDist)) {
    throw "Engine package folder not found: $EngineDist"
}

if (-not $NoRestore) {
    Write-Host "Restoring Viewer and Processor for runtime '$Runtime'..."
    Invoke-Checked -FileName "dotnet" -Arguments @("restore", $ViewerProject, "-r", $Runtime)
    Invoke-Checked -FileName "dotnet" -Arguments @("restore", $ProcessorProject, "-r", $Runtime)
    Invoke-Checked -FileName "dotnet" -Arguments @("restore", $ProcessorLauncherProject, "-r", $Runtime)
}

Write-Host "Publishing Viewer (self-contained single-file)..."
$viewerPublishArgs = @(
    "publish", $ViewerProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o", $ViewerPublish,
    "--no-restore"
)
Invoke-Checked -FileName "dotnet" -Arguments $viewerPublishArgs

Write-Host "Publishing Processor (self-contained multi-file)..."
$processorPublishArgs = @(
    "publish", $ProcessorProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=false",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o", $ProcessorPublish,
    "--no-restore"
)
Invoke-Checked -FileName "dotnet" -Arguments $processorPublishArgs

Write-Host "Publishing Processor launcher (self-contained single-file)..."
$processorLauncherPublishArgs = @(
    "publish", $ProcessorLauncherProject,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugSymbols=false",
    "-p:DebugType=None",
    "-o", $ProcessorLauncherPublish,
    "--no-restore"
)
Invoke-Checked -FileName "dotnet" -Arguments $processorLauncherPublishArgs

Write-Host "Assembling package folders..."
Ensure-Directory -DirectoryPath $ViewerPackage
Ensure-Directory -DirectoryPath $ProcessorPackage
Ensure-Directory -DirectoryPath $ProcessorInternal

Copy-Item -Path (Join-Path $ViewerPublish "*") -Destination $ViewerPackage -Recurse -Force
Copy-Item -Path (Join-Path $ProcessorPublish "*") -Destination $ProcessorInternal -Recurse -Force

$ProcessorLauncherExe = Join-Path $ProcessorLauncherPublish "ReefCams.Processor.exe"
if (-not (Test-Path $ProcessorLauncherExe)) {
    throw "Processor launcher executable was not found: $ProcessorLauncherExe"
}
Copy-Item -Path $ProcessorLauncherExe -Destination (Join-Path $ProcessorPackage "ReefCams.Processor.exe") -Force

$ProcessorInternalViewerDest = Join-Path $ProcessorInternal "viewer"
Remove-PathIfExists -PathToRemove $ProcessorInternalViewerDest
Ensure-Directory -DirectoryPath $ProcessorInternalViewerDest
Copy-Item -Path (Join-Path $ViewerPublish "*") -Destination $ProcessorInternalViewerDest -Recurse -Force

# Viewer package should not bundle ffprobe directly; processor/engine owns that dependency.
$ViewerFfprobe = Join-Path $ViewerPackage "ffprobe.exe"
if (Test-Path $ViewerFfprobe) {
    Remove-Item -LiteralPath $ViewerFfprobe -Force
}
$ProcessorViewerFfprobe = Join-Path $ProcessorInternalViewerDest "ffprobe.exe"
if (Test-Path $ProcessorViewerFfprobe) {
    Remove-Item -LiteralPath $ProcessorViewerFfprobe -Force
}

if (-not $NoZip) {
    $ViewerZip = Join-Path $OutputRootFull "ReefCams.Viewer-$Runtime.zip"
    $ProcessorZip = Join-Path $OutputRootFull "ReefCams.Processor-$Runtime.zip"
    Remove-PathIfExists -PathToRemove $ViewerZip
    Remove-PathIfExists -PathToRemove $ProcessorZip

    Write-Host "Creating zip archives..."
    Compress-Archive -Path (Join-Path $ViewerPackage "*") -DestinationPath $ViewerZip
    Compress-Archive -Path (Join-Path $ProcessorPackage "*") -DestinationPath $ProcessorZip
}

Write-Host ""
Write-Host "Done."
Write-Host "Viewer package:    $ViewerPackage"
Write-Host "Processor package: $ProcessorPackage"
if (-not $NoZip) {
    Write-Host "Zips:"
    Write-Host "  $(Join-Path $OutputRootFull "ReefCams.Viewer-$Runtime.zip")"
    Write-Host "  $(Join-Path $OutputRootFull "ReefCams.Processor-$Runtime.zip")"
}
