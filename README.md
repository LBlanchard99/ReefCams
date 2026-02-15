# ReefCams

ReefCams is a Windows desktop review suite for processing trail camera clips.

- `ReefCams.Processor` (WPF): indexing, processing, benchmark, export, and viewer functions.
- `ReefCams.Viewer` (WPF): viewer-only app for review packages.
- `engine.exe` (Python, PyInstaller): detector engine used by Processor.

## Prerequisites

- Windows
- .NET 8 SDK
- Python (with `py` launcher available)
- `ffprobe.exe` available at one of:
  - `engine_src/ffprobe.exe`
  - `engine_src/tools/ffprobe.exe`
  - repo root `ffprobe.exe`

## Build From Source

### 1) Fetch models (if needed)

Models are managed via release assets manifest.

```powershell
.\engine_src\fetch_models.ps1
```

This uses `engine_src/models/models.json`, verifies SHA256, and extracts model files into `engine_src/models`.

### 2) Build engine package

```powershell
.\engine_src\build_engine.ps1
```

Output:
- `engine_dist/engine/engine.exe`
- bundled model/benchmark files
- bundled `ffprobe.exe`

### 3) Run apps from source

Processor:
```powershell
dotnet run --project .\src\ReefCams.Processor\ReefCams.Processor.csproj
```

Viewer:
```powershell
dotnet run --project .\src\ReefCams.Viewer\ReefCams.Viewer.csproj
```

## Build Deployable Packages

Build both apps + engine and create package folders (and zips by default):

```powershell
.\build_apps.ps1 -Configuration Release -Runtime win-x64
```

Useful flags:
- `-SkipEngineBuild` to reuse existing `engine_dist/engine`
- `-NoZip` to skip zip creation
- `-NoRestore` to skip restore

Output:
- `artifacts/packages/ReefCams.Viewer/`
- `artifacts/packages/ReefCams.Processor/`
- `artifacts/packages/ReefCams.Viewer-win-x64.zip`
- `artifacts/packages/ReefCams.Processor-win-x64.zip`

Processor package includes:
- `ReefCams.Processor.exe`
- `engine/` bundle
- embedded copy of Viewer under `viewer/` for export workflows

## First Run Workflow

1. Launch Processor.
2. Choose a Project Directory.
3. Add one or more clip roots (can be from multiple drives).
4. Index roots.
5. Run benchmark (optional) and process selected scopes.

Notes:
- Source clips are treated as read-only.
- All writes go to project data/config/export paths.
- Roots with the same top-level project name are merged in the tree.

## Model Release Asset Flow (Option B)

- Model zip is attached to GitHub release (for example `v1.0`).
- Manifest file `engine_src/models/models.json` points to the release URL + SHA256.
- `fetch_models.ps1` bootstraps models for clean builds.
