# ReefCams Engine (v1)

## Commands (only these)
- `engine.exe process --clip "<path>" --fps 1 --db "<dbpath>" [--model "<modelpath>"] [--provider "DmlExecutionProvider,CPUExecutionProvider"] [--force]`
- `engine.exe benchmark --fps 1 [--provider "..."] [--model "<modelpath>"] [--clip "<benchmarkclip>"] [--db "<dbpath>"] [--warmup] [--summary]`
- `engine.exe probe --clip "<path>"`

## No-arg behavior
If you run `engine.exe` with no arguments (e.g., double-click), it runs:
- `benchmark` with `--warmup` and `--fps 1`
- prints a human-readable summary after JSONL output
- waits for Enter before exiting (keeps the console window open)

## Outputs (only these)
- Writes to SQLite at `--db`
- JSON Lines (JSONL) progress to stdout

## JSONL types
- `benchmark_env`: DML availability + available providers
- `benchmark_stage`: start, model_load_start, model_load_done, video_iter_start, first_frame, warmup_start, warmup_done
- `benchmark_frame`: per-frame timings (seek/read/infer)
- `benchmark_result`: summary timings and provider_used
- `start` / `frame` / `done` / `error` for processing
  - `frame` includes `max_conf_frame_label` (`animal`/`person`/`vehicle` when present)
  - `done` includes clip-level `max_conf_label` and `max_conf_cls_id`

## Guardrails
- Never writes to source clip folders.
- No review images, tracking, processed logs, or sidecar files.

## Utilities (dev/test only)
- `reef_bar_metadata.py`: Extracts bottom-bar metadata from the first frame of a clip using template OCR.
- `reef_sort_unstructured.py`: Sorts unstructured clips into the Winter 2026 folder hierarchy by calling the metadata utility.

### Example usage
```powershell
# Validate OCR on the known test clip
py reef_bar_metadata.py --clip .\ReefCamsReviewer\TestClipsUnstructured\01090421.MOV

# Dry-run sorting (no copies)
py reef_sort_unstructured.py --dry-run
```

## Troubleshooting
- If DirectML is unavailable, the engine falls back to CPU.
- If you see `type:"error"` on stdout, check that the model and clip paths are valid.

## Packaging (repeatable build)
From `engine_src/`:
```powershell
.\build_engine.ps1
```
`ffprobe.exe` must exist at `engine_src\ffprobe.exe` (or `engine_src\tools\ffprobe.exe`, or repo root) so it can be bundled into `engine_dist\engine\ffprobe.exe`.

This produces:
```
engine_dist/engine/
  engine.exe
  ffprobe.exe
  models/
  benchmark/
  README_ENGINE.md
```
