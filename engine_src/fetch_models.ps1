param(
    [string]$ManifestPath = "",
    [string]$DestinationDir = "",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Test-ExpectedFilesPresent {
    param(
        [Parameter(Mandatory = $true)][object]$FileEntry,
        [Parameter(Mandatory = $true)][string]$DestinationDir
    )

    if ($null -eq $FileEntry.expectedFiles -or $FileEntry.expectedFiles.Count -eq 0) {
        return $false
    }

    foreach ($expectedFile in $FileEntry.expectedFiles) {
        if ([string]::IsNullOrWhiteSpace($expectedFile)) {
            continue
        }

        $expectedPath = Join-Path $DestinationDir $expectedFile
        if (-not (Test-Path -LiteralPath $expectedPath)) {
            return $false
        }
    }

    return $true
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $scriptDir "models\models.json"
}
if ([string]::IsNullOrWhiteSpace($DestinationDir)) {
    $DestinationDir = Join-Path $scriptDir "models"
}

$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$DestinationDir = [System.IO.Path]::GetFullPath($DestinationDir)

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "Model manifest not found: $ManifestPath"
}

Ensure-Directory -Path $DestinationDir

$manifest = Get-Content -Raw -LiteralPath $ManifestPath | ConvertFrom-Json
if ($null -eq $manifest.files -or $manifest.files.Count -eq 0) {
    throw "Manifest has no files: $ManifestPath"
}

foreach ($file in $manifest.files) {
    if ([string]::IsNullOrWhiteSpace($file.name) -or [string]::IsNullOrWhiteSpace($file.url) -or [string]::IsNullOrWhiteSpace($file.sha256)) {
        throw "Manifest entry is missing name/url/sha256."
    }

    if (-not $Force.IsPresent -and (Test-ExpectedFilesPresent -FileEntry $file -DestinationDir $DestinationDir)) {
        Write-Host "Expected model files already present, skipping download: $($file.name)"
        continue
    }

    $assetPath = Join-Path $DestinationDir $file.name
    $targetHash = $file.sha256.ToUpperInvariant()
    $needsDownload = $Force.IsPresent -or -not (Test-Path -LiteralPath $assetPath)

    if (-not $needsDownload) {
        $currentHash = Get-Sha256Hex -Path $assetPath
        if ($currentHash -ne $targetHash) {
            Write-Host "Checksum mismatch for existing file, re-downloading: $($file.name)"
            $needsDownload = $true
        }
    }

    if ($needsDownload) {
        Write-Host "Downloading $($file.name)..."
        Invoke-WebRequest -Uri $file.url -OutFile $assetPath
    }

    $downloadedHash = Get-Sha256Hex -Path $assetPath
    if ($downloadedHash -ne $targetHash) {
        throw "SHA256 mismatch for $($file.name). Expected $targetHash, got $downloadedHash."
    }

    Write-Host "Extracting $($file.name)..."
    Expand-Archive -LiteralPath $assetPath -DestinationPath $DestinationDir -Force

    if ($null -ne $file.expectedFiles) {
        foreach ($expectedFile in $file.expectedFiles) {
            if ([string]::IsNullOrWhiteSpace($expectedFile)) {
                continue
            }

            $expectedPath = Join-Path $DestinationDir $expectedFile
            if (-not (Test-Path -LiteralPath $expectedPath)) {
                throw "Expected extracted model file missing: $expectedPath"
            }
        }
    }
}

Write-Host "Model fetch complete."
