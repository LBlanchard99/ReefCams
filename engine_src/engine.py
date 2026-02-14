import argparse
import json
import math
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

import db
import md
import models
import video
import reef_bar_metadata as bar_meta


if getattr(sys, "frozen", False):
    BASE_DIR = Path(sys.executable).resolve().parent
else:
    BASE_DIR = Path(__file__).resolve().parent


def emit(payload: dict) -> None:
    print(json.dumps(payload, separators=(",", ":"), ensure_ascii=True), flush=True)


def parse_providers(provider_str: str | None) -> list[str]:
    raw = provider_str if provider_str else models.DEFAULT_PROVIDER
    return [p.strip() for p in raw.split(",") if p.strip()]


def resolve_path(path_str: str, base_dir: Path) -> Path:
    candidate = Path(path_str)
    if candidate.is_absolute():
        return candidate
    cwd_candidate = (Path.cwd() / candidate).resolve()
    if cwd_candidate.exists():
        return cwd_candidate
    base_candidate = (base_dir / candidate).resolve()
    if base_candidate.exists():
        return base_candidate
    return cwd_candidate


def try_extract_bar_metadata(clip_path: Path, args: argparse.Namespace) -> tuple[bool, float, bar_meta.BarMetadata | None]:
    t0 = time.perf_counter()
    try:
        calib_clip_arg = getattr(args, "meta_calibration_clip", None)
        calib_clip = resolve_path(calib_clip_arg, BASE_DIR) if calib_clip_arg else None
        bank = bar_meta.build_template_bank(
            calibration_clip=calib_clip,
            expected_tokens=[
                getattr(args, "meta_calibration_site", "F32"),
                getattr(args, "meta_calibration_temp_f", "82F"),
                getattr(args, "meta_calibration_temp_c", "27C"),
                getattr(args, "meta_calibration_date", "01-09-2026"),
                getattr(args, "meta_calibration_time", "16:01:29"),
            ],
            fallback_clip=clip_path,
        )
        bar = bar_meta.extract_metadata(clip_path, bank)
        return True, (time.perf_counter() - t0) * 1000.0, bar
    except Exception:
        return False, (time.perf_counter() - t0) * 1000.0, None


def process_clip(args: argparse.Namespace) -> int:
    t0 = time.perf_counter()
    clip_path = Path(args.clip)
    db_path = Path(args.db)
    fps = float(args.fps)
    provider_list = parse_providers(args.provider)
    model_path = resolve_path(args.model or models.DEFAULT_MODEL_REL, BASE_DIR)

    file_stat = clip_path.stat()
    file_size = int(file_stat.st_size)
    file_mtime_utc = datetime.fromtimestamp(file_stat.st_mtime, timezone.utc).isoformat()

    conn = db.open_db(db_path)
    db.ensure_schema(conn)
    clip_path_text = str(clip_path.resolve())
    existing_clip_id = db.get_clip_id_by_path(conn, clip_path_text)
    clip_id = existing_clip_id if existing_clip_id else db.compute_clip_id(clip_path, file_size, file_mtime_utc)
    state = db.get_clip_state(conn, clip_id)
    if state and state["processed"] == 1 and state["processed_fps"] == fps and not args.force:
        emit(
            {
                "type": "done",
                "clip": str(clip_path),
                "frames": 0,
                "max_conf": float(state["max_conf"] or 0.0),
                "max_t": float(state["max_conf_time_sec"] or 0.0),
                "max_conf_cls_id": state.get("max_conf_cls_id"),
                "max_conf_label": state.get("max_conf_label"),
                "total_ms": (time.perf_counter() - t0) * 1000.0,
                "provider_used": "",
                "skipped": True,
            }
        )
        conn.close()
        return 0

    emit(
        {
            "type": "start",
            "clip": str(clip_path),
            "fps": fps,
            "provider_requested": provider_list,
            "model": str(model_path),
        }
    )

    meta = video.probe_clip(clip_path)
    db.upsert_clip_metadata(
        conn,
        clip_id=clip_id,
        clip_path=clip_path_text,
        file_size=meta["file_size"],
        file_mtime_utc=meta["file_mtime_utc"],
        duration_sec=meta["duration_sec"],
        video_fps=meta["video_fps"],
        width=meta["width"],
        height=meta["height"],
    )
    # Extract bottom-bar metadata (best effort).
    bar_ok, bar_ms, bar = try_extract_bar_metadata(clip_path, args)
    if bar_ok and bar is not None:
        db.update_clip_bar_metadata(
            conn,
            clip_id=clip_id,
            site=bar.site,
            temp_f=int(bar.temp_f),
            temp_c=int(bar.temp_c),
            bar_date=bar.date,
            bar_time=bar.time,
        )

    detector = md.MegaDetector(model_path, providers=provider_list)
    provider_used = detector.provider_used[0] if detector.provider_used else ""

    frames_rows = []
    detections_rows = []
    max_conf = 0.0
    max_t = 0.0
    max_conf_cls_id: int | None = None
    max_conf_label: str | None = None
    frame_count = 0

    for t_actual, frame in video.sample_frames_with_times(clip_path, fps):
        dets = detector.infer(
            frame,
            conf_thresh=models.DEFAULT_CONF_THRESH,
            min_area_frac=models.DEFAULT_MIN_AREA_FRAC,
        )
        max_det = max(dets, key=lambda d: d[2], default=None)
        max_conf_frame = float(max_det[2]) if max_det is not None else 0.0
        frames_rows.append((clip_id, float(t_actual), float(max_conf_frame)))
        if max_det is not None and max_conf_frame >= max_conf:
            max_conf = float(max_conf_frame)
            max_t = float(t_actual)
            max_conf_cls_id = int(max_det[0])
            max_conf_label = str(max_det[1])
        for det in dets:
            cls_id, cls_label, conf, x, y, w, h, area_frac = det
            detections_rows.append(
                (
                    clip_id,
                    float(t_actual),
                    int(cls_id),
                    str(cls_label),
                    float(conf),
                    float(x),
                    float(y),
                    float(w),
                    float(h),
                    float(area_frac),
                )
            )
        frame_count += 1
        emit(
            {
                "type": "frame",
                "clip": str(clip_path),
                "t": float(t_actual),
                "max_conf_frame": float(max_conf_frame),
                "max_conf_frame_cls_id": (int(max_det[0]) if max_det is not None else None),
                "max_conf_frame_label": (str(max_det[1]) if max_det is not None else None),
                "det_count": len(dets),
            }
        )

    db.write_clip_results(
        conn,
        clip_id=clip_id,
        processed_fps=fps,
        frames=frames_rows,
        detections=detections_rows,
        max_conf=max_conf,
        max_conf_time_sec=max_t,
        max_conf_cls_id=max_conf_cls_id,
        max_conf_label=max_conf_label,
    )
    conn.close()

    emit(
        {
            "type": "done",
            "clip": str(clip_path),
            "frames": frame_count,
            "max_conf": max_conf,
            "max_t": max_t,
            "max_conf_cls_id": max_conf_cls_id,
            "max_conf_label": max_conf_label,
            "total_ms": (time.perf_counter() - t0) * 1000.0,
            "provider_used": provider_used,
            "meta_ms": float(bar_ms),
            "meta_ok": bool(bar_ok),
        }
    )
    return 0


def _p95(values: list[float]) -> float:
    if not values:
        return 0.0
    vals = sorted(values)
    idx = max(0, int(math.ceil(0.95 * len(vals))) - 1)
    return float(vals[idx])


def run_benchmark(args: argparse.Namespace) -> int:
    clip_path = resolve_path(args.clip or models.DEFAULT_BENCHMARK_REL, BASE_DIR)
    model_path = resolve_path(args.model or models.DEFAULT_MODEL_REL, BASE_DIR)
    provider_list = parse_providers(args.provider)
    fps = float(args.fps)

    available_providers = []
    dml_available = False
    try:
        import onnxruntime as ort  # local import to keep CLI startup light

        available_providers = ort.get_available_providers()
        dml_available = "DmlExecutionProvider" in available_providers
    except Exception:
        available_providers = []
        dml_available = False

    bench_t0 = time.perf_counter()
    emit({"type": "benchmark_stage", "stage": "start", "elapsed_ms": 0.0})
    emit(
        {
            "type": "benchmark_env",
            "dml_available": bool(dml_available),
            "providers_available": available_providers,
            "provider_requested": provider_list,
        }
    )
    emit({"type": "benchmark_stage", "stage": "model_load_start", "elapsed_ms": 0.0})
    detector = md.MegaDetector(model_path, providers=provider_list)
    emit(
        {
            "type": "benchmark_stage",
            "stage": "model_load_done",
            "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0,
            "load_ms": float(detector.load_ms),
        }
    )
    provider_used = detector.provider_used[0] if detector.provider_used else ""

    meta_ok, meta_ms, _ = try_extract_bar_metadata(clip_path, args)

    if args.warmup:
        emit(
            {
                "type": "benchmark_stage",
                "stage": "warmup_start",
                "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0,
            }
        )
        warmup_ms = 0.0
        warmup_frame_found = False
        for _, frame, _ in video.sample_frames_with_times_debug(clip_path, fps):
            warmup_frame_found = True
            t0 = time.perf_counter()
            _ = detector.infer(
                frame,
                conf_thresh=models.DEFAULT_CONF_THRESH,
                min_area_frac=models.DEFAULT_MIN_AREA_FRAC,
            )
            warmup_ms = (time.perf_counter() - t0) * 1000.0
            break
        emit(
            {
                "type": "benchmark_stage",
                "stage": "warmup_done",
                "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0,
                "warmup_infer_ms": float(warmup_ms),
                "warmup_frame_found": bool(warmup_frame_found),
            }
        )

    infer_times: list[float] = []
    loop_t0 = time.perf_counter()
    frame_count = 0
    first_frame_logged = False
    emit({"type": "benchmark_stage", "stage": "video_iter_start", "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0})
    for idx, (t_actual, frame, io_t) in enumerate(video.sample_frames_with_times_debug(clip_path, fps)):
        if not first_frame_logged:
            emit(
                {
                    "type": "benchmark_stage",
                    "stage": "first_frame",
                    "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0,
                    "t": float(t_actual),
                }
            )
            first_frame_logged = True
        t0 = time.perf_counter()
        _ = detector.infer(
            frame,
            conf_thresh=models.DEFAULT_CONF_THRESH,
            min_area_frac=models.DEFAULT_MIN_AREA_FRAC,
        )
        infer_ms = (time.perf_counter() - t0) * 1000.0
        infer_times.append(infer_ms)
        frame_count += 1
        emit(
            {
                "type": "benchmark_frame",
                "idx": int(idx),
                "t": float(t_actual),
                "seek_ms": float(io_t.get("seek_ms", 0.0)),
                "read_ms": float(io_t.get("read_ms", 0.0)),
                "infer_ms": float(infer_ms),
                "elapsed_ms": (time.perf_counter() - bench_t0) * 1000.0,
            }
        )

    total_ms = (time.perf_counter() - loop_t0) * 1000.0
    avg_infer_ms = float(sum(infer_times) / max(len(infer_times), 1))
    p95_infer_ms = _p95(infer_times)
    per_frame_ms = total_ms / max(frame_count, 1)
    estimate_per_10s_s = (per_frame_ms * 10.0) / 1000.0

    result = {
        "type": "benchmark_result",
        "provider_requested": provider_list,
        "provider_used": provider_used,
        "load_ms": float(detector.load_ms),
        "avg_infer_ms": avg_infer_ms,
        "p95_infer_ms": p95_infer_ms,
        "total_ms": float(total_ms),
        "estimate_per_10s_s": float(estimate_per_10s_s),
        "meta_ms": float(meta_ms),
        "meta_ok": bool(meta_ok),
    }
    emit(result)

    if args.db:
        run_at_utc = datetime.now(timezone.utc).isoformat()
        conn = db.open_db(Path(args.db))
        db.ensure_schema(conn)
        db.insert_benchmark(
            conn,
            run_at_utc=run_at_utc,
            provider_requested=",".join(provider_list),
            provider_used=provider_used,
            fps=fps,
            load_ms=float(detector.load_ms),
            avg_infer_ms=avg_infer_ms,
            p95_infer_ms=p95_infer_ms,
            total_ms=float(total_ms),
            estimate_per_10s_s=float(estimate_per_10s_s),
        )
        conn.close()

    if getattr(args, "summary", False):
        print("")
        print("Benchmark Summary")
        print(f"Provider requested: {', '.join(provider_list) if provider_list else 'N/A'}")
        print(f"Provider used:      {provider_used or 'N/A'}")
        print(f"Model load:         {detector.load_ms:.1f} ms")
        if args.warmup:
            print("Warmup:             enabled")
        print(f"Avg inference:      {avg_infer_ms:.1f} ms")
        print(f"P95 inference:      {p95_infer_ms:.1f} ms")
        print(f"Total loop:         {total_ms:.1f} ms")
        print(f"Estimate / 10s:     {estimate_per_10s_s:.2f} s")
        print(f"Metadata OCR:       {meta_ms:.1f} ms ({'ok' if meta_ok else 'failed'})")
    return 0


def run_probe(args: argparse.Namespace) -> int:
    clip_path = Path(args.clip)
    meta = video.probe_clip(clip_path)
    meta["type"] = "probe_result"
    emit(meta)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="ReefCams detector engine")
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_process = sub.add_parser("process", help="Process a clip into SQLite")
    p_process.add_argument("--clip", required=True)
    p_process.add_argument("--fps", type=float, default=models.DEFAULT_FPS)
    p_process.add_argument("--db", required=True)
    p_process.add_argument("--model")
    p_process.add_argument("--provider")
    p_process.add_argument("--force", action="store_true")
    p_process.add_argument("--meta-calibration-clip", default=None, help="Optional calibration clip for OCR templates.")
    p_process.add_argument("--meta-calibration-site", default="F32")
    p_process.add_argument("--meta-calibration-temp-f", default="82F")
    p_process.add_argument("--meta-calibration-temp-c", default="27C")
    p_process.add_argument("--meta-calibration-date", default="01-09-2026")
    p_process.add_argument("--meta-calibration-time", default="16:01:29")

    p_bench = sub.add_parser("benchmark", help="Run a benchmark clip")
    p_bench.add_argument("--fps", type=float, default=models.DEFAULT_FPS)
    p_bench.add_argument("--provider")
    p_bench.add_argument("--model")
    p_bench.add_argument("--clip")
    p_bench.add_argument("--db")
    p_bench.add_argument("--warmup", action="store_true", help="Run a single warmup inference before timing.")
    p_bench.add_argument("--summary", action="store_true", help="Print a human-readable summary after benchmark.")
    p_bench.add_argument("--meta-calibration-clip", default=None, help="Optional calibration clip for OCR templates.")
    p_bench.add_argument("--meta-calibration-site", default="F32")
    p_bench.add_argument("--meta-calibration-temp-f", default="82F")
    p_bench.add_argument("--meta-calibration-temp-c", default="27C")
    p_bench.add_argument("--meta-calibration-date", default="01-09-2026")
    p_bench.add_argument("--meta-calibration-time", default="16:01:29")

    p_probe = sub.add_parser("probe", help="Probe clip metadata")
    p_probe.add_argument("--clip", required=True)

    return parser


def main() -> int:
    if len(sys.argv) == 1:
        args = argparse.Namespace(
            cmd="benchmark",
            fps=models.DEFAULT_FPS,
            provider=None,
            model=None,
            clip=None,
            db=None,
            warmup=True,
            summary=True,
            pause_on_exit=True,
            meta_calibration_clip=None,
            meta_calibration_site="F32",
            meta_calibration_temp_f="82F",
            meta_calibration_temp_c="27C",
            meta_calibration_date="01-09-2026",
            meta_calibration_time="16:01:29",
        )
    else:
        parser = build_parser()
        args = parser.parse_args()
    try:
        if args.cmd == "process":
            return process_clip(args)
        if args.cmd == "benchmark":
            result = run_benchmark(args)
            if getattr(args, "pause_on_exit", False) and sys.stdin.isatty():
                print("")
                try:
                    input("Press Enter to exit...")
                except EOFError:
                    pass
            return result
        if args.cmd == "probe":
            return run_probe(args)
        raise RuntimeError(f"Unknown command: {args.cmd}")
    except Exception as exc:
        emit({"type": "error", "clip": getattr(args, "clip", None), "message": str(exc), "exception": repr(exc)})
        return 1


if __name__ == "__main__":
    sys.exit(main())
