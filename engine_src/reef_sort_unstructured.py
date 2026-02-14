from __future__ import annotations

import argparse
import csv
import shutil
from pathlib import Path
from typing import Iterable, List, Tuple

from reef_bar_metadata import BarMetadata, build_templates_from_calibration, extract_metadata


def iter_clips(folder: Path) -> List[Path]:
    return sorted([*folder.glob("*.MOV"), *folder.glob("*.MP4"), *folder.glob("*.mp4")])


def parse_clip_number(name: str) -> int:
    stem = Path(name).stem
    digits = "".join(ch for ch in stem if ch.isdigit())
    if len(digits) >= 4:
        return int(digits[-4:])
    if digits:
        return int(digits)
    return 0


def build_session_name(
    clip_number: int,
    base: int,
    suffix: str,
    roll: int,
    fixed: str | None,
) -> str:
    if fixed:
        return fixed
    idx = clip_number // max(1, roll)
    return f"{base + idx:03d}{suffix}"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Sort unstructured ReefCams clips into folder hierarchy.")
    script_dir = Path(__file__).resolve().parent
    default_input = script_dir.parent / "TestClipsUnstructured"
    default_output = default_input / "Sorted"
    parser.add_argument(
        "--input",
        type=Path,
        default=default_input,
        help="Folder with unstructured clips.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=default_output,
        help="Destination root for sorted clips.",
    )
    parser.add_argument("--season", type=str, default="Winter 2026", help="Top-level season folder name.")
    parser.add_argument("--dcim", type=str, default="DCIM1", help="DCIM folder name.")
    parser.add_argument("--session-base", type=int, default=100, help="Base session number (e.g., 100).")
    parser.add_argument("--session-suffix", type=str, default="EK113", help="Session suffix (e.g., EK113).")
    parser.add_argument("--session-roll", type=int, default=1000, help="Roll session when clip number passes this.")
    parser.add_argument("--session-name", type=str, default=None, help="Fixed session name (overrides roll).")
    parser.add_argument("--dry-run", action="store_true", help="Do not copy, only print plan.")
    parser.add_argument("--log", type=Path, default=Path("sorted_clips.csv"), help="CSV log path.")
    parser.add_argument(
        "--overrides",
        type=Path,
        default=None,
        help="CSV with manual metadata overrides: clip,site,temp_f,temp_c,date,time",
    )

    # Calibration inputs for OCR templates
    parser.add_argument(
        "--calibration-clip",
        type=Path,
        default=default_input / "01090421.MOV",
    )
    parser.add_argument("--calibration-site", type=str, default="F32")
    parser.add_argument("--calibration-temp-f", type=str, default="82F")
    parser.add_argument("--calibration-temp-c", type=str, default="27C")
    parser.add_argument("--calibration-date", type=str, default="01-09-2026")
    parser.add_argument("--calibration-time", type=str, default="16:01:29")
    parser.add_argument("--debug-dir", type=Path, default=None, help="Optional debug image output folder.")

    return parser.parse_args()


def _parse_temp(value: str, suffix: str) -> int:
    v = value.strip()
    if v.upper().endswith(suffix):
        v = v[:-1]
    return int(v)


def load_overrides(path: Path) -> dict[str, BarMetadata]:
    overrides: dict[str, BarMetadata] = {}
    if not path.exists():
        return overrides
    with path.open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f)
        rows = list(reader)
    if not rows:
        return overrides
    # Detect header
    header = [c.strip().lower() for c in rows[0]]
    start_idx = 1 if "clip" in header else 0
    for row in rows[start_idx:]:
        if not row or len(row) < 6:
            continue
        clip_name = row[0].strip()
        if not clip_name:
            continue
        site = row[1].strip().upper()
        temp_f = _parse_temp(row[2], "F")
        temp_c = _parse_temp(row[3], "C")
        date = row[4].strip()
        time = row[5].strip()
        overrides[clip_name] = BarMetadata(site=site, temp_f=temp_f, temp_c=temp_c, date=date, time=time)
    return overrides


def main() -> None:
    args = parse_args()
    expected_tokens = [
        args.calibration_site,
        args.calibration_temp_f,
        args.calibration_temp_c,
        args.calibration_date,
        args.calibration_time,
    ]
    bank = build_templates_from_calibration(args.calibration_clip, expected_tokens, debug_dir=args.debug_dir)
    overrides = load_overrides(args.overrides) if args.overrides else {}

    clips = iter_clips(args.input)
    if not clips:
        print(f"No clips found in {args.input}")
        return

    log_path = args.log
    if not log_path.is_absolute():
        log_path = args.output / log_path
    log_path.parent.mkdir(parents=True, exist_ok=True)

    with log_path.open("w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(
            [
                "source",
                "dest",
                "site",
                "temp_f",
                "temp_c",
                "date",
                "time",
                "session",
            ]
        )
        for clip in clips:
            meta = None
            if overrides and clip.name in overrides:
                meta = overrides[clip.name]
            else:
                try:
                    meta = extract_metadata(clip, bank, debug_dir=args.debug_dir)
                except Exception as e:
                    print(f"SKIP {clip.name}: {e}")
                    continue

            clip_num = parse_clip_number(clip.name)
            session = build_session_name(
                clip_num,
                base=args.session_base,
                suffix=args.session_suffix,
                roll=args.session_roll,
                fixed=args.session_name,
            )

            dest_dir = args.output / args.season / meta.site / args.dcim / session
            dest_path = dest_dir / clip.name
            if args.dry_run:
                print(f"DRYRUN {clip.name} -> {dest_path}")
            else:
                dest_dir.mkdir(parents=True, exist_ok=True)
                if not dest_path.exists():
                    shutil.copy2(clip, dest_path)
            writer.writerow([str(clip), str(dest_path), meta.site, meta.temp_f, meta.temp_c, meta.date, meta.time, session])

    print(f"Done. Log: {log_path}")


if __name__ == "__main__":
    main()
