from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

import cv2
import numpy as np


@dataclass
class BarMetadata:
    site: str
    temp_f: int
    temp_c: int
    date: str  # MM-DD-YYYY
    time: str  # HH:MM:SS (24h)

    def as_dict(self) -> Dict[str, object]:
        return {
            "site": self.site,
            "temp_f": self.temp_f,
            "temp_c": self.temp_c,
            "date": self.date,
            "time": self.time,
        }


def read_first_frame(clip_path: Path) -> np.ndarray:
    cap = cv2.VideoCapture(str(clip_path))
    if not cap.isOpened():
        raise RuntimeError(f"Failed to open clip: {clip_path}")
    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
    ok, frame = cap.read()
    cap.release()
    if not ok or frame is None:
        raise RuntimeError(f"Failed to read first frame: {clip_path}")
    return frame


def parse_mmdd_from_filename(name: str) -> Optional[Tuple[int, int]]:
    digits = "".join(ch for ch in Path(name).stem if ch.isdigit())
    if len(digits) < 4:
        return None
    try:
        mm = int(digits[0:2])
        dd = int(digits[2:4])
    except ValueError:
        return None
    if not (1 <= mm <= 12 and 1 <= dd <= 31):
        return None
    return mm, dd


def resolve_bundled_ffprobe() -> Optional[Path]:
    if getattr(sys, "frozen", False):
        base = Path(sys.executable).resolve().parent
        candidates = [
            base / "ffprobe.exe",
            base / "_internal" / "ffprobe.exe",
        ]
    else:
        script_dir = Path(__file__).resolve().parent
        repo_root = script_dir.parent
        candidates = [
            script_dir / "ffprobe.exe",
            script_dir / "tools" / "ffprobe.exe",
            repo_root / "ffprobe.exe",
        ]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return None


def get_creation_time_utc(clip_path: Path) -> Optional[datetime]:
    # Use the bundled ffprobe.exe only.
    ffprobe = resolve_bundled_ffprobe()
    if ffprobe is None:
        return None
    try:
        result = subprocess.run(
            [
                str(ffprobe),
                "-v",
                "error",
                "-show_entries",
                "format_tags=creation_time",
                "-of",
                "default=nw=1:nk=1",
                str(clip_path),
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            return None
        lines = [ln.strip() for ln in result.stdout.splitlines() if ln.strip()]
        if not lines:
            return None
        raw = lines[0]
        # Example: 2025-12-25T16:41:35.000000Z
        if raw.endswith("Z"):
            raw = raw.replace("Z", "+00:00")
        dt = datetime.fromisoformat(raw)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def extract_bottom_bar(frame_bgr: np.ndarray) -> Tuple[np.ndarray, Tuple[int, int]]:
    h = frame_bgr.shape[0]
    # Bar is fixed at the very bottom and fixed size.
    # 1080p clips show a ~56px bar; scale proportionally to height.
    bar_h = max(32, int(round(h * (56 / 1080))))
    start = h - bar_h
    end = h - 1
    bar = frame_bgr[start : end + 1, :]
    return bar, (start, end)


def binarize_bar(bar_bgr: np.ndarray) -> np.ndarray:
    gray = cv2.cvtColor(bar_bgr, cv2.COLOR_BGR2GRAY)
    blur = cv2.GaussianBlur(gray, (3, 3), 0)
    _, bw = cv2.threshold(blur, 0, 255, cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
    kernel = np.ones((2, 2), np.uint8)
    bw = cv2.morphologyEx(bw, cv2.MORPH_OPEN, kernel, iterations=1)
    return bw


def column_segments(bw: np.ndarray, min_col_frac: float = 0.05) -> List[Tuple[int, int]]:
    h, w = bw.shape
    col_sum = (bw > 0).sum(axis=0)
    min_pixels = max(2, int(h * min_col_frac))
    mask = col_sum > min_pixels
    segments: List[Tuple[int, int]] = []
    start = None
    for i, val in enumerate(mask):
        if val and start is None:
            start = i
        elif not val and start is not None:
            if i - start > 1:
                segments.append((start, i))
            start = None
    if start is not None:
        segments.append((start, w))
    return segments


def split_tokens(segments: List[Tuple[int, int]], gap_factor: float = 2.0) -> List[List[Tuple[int, int]]]:
    if not segments:
        return []
    if len(segments) == 1:
        return [segments[:]]
    gaps = [segments[i][0] - segments[i - 1][1] for i in range(1, len(segments))]
    med_gap = float(np.median(gaps)) if gaps else 8.0
    gap_thresh = max(12.0, med_gap * gap_factor)
    tokens: List[List[Tuple[int, int]]] = []
    current = [segments[0]]
    for i in range(1, len(segments)):
        gap = segments[i][0] - segments[i - 1][1]
        if gap > gap_thresh:
            tokens.append(current)
            current = [segments[i]]
        else:
            current.append(segments[i])
    if current:
        tokens.append(current)
    return tokens


def normalize_glyph(img: np.ndarray, size: int = 32) -> np.ndarray:
    ys, xs = np.where(img > 0)
    if ys.size == 0 or xs.size == 0:
        return np.zeros((size, size), dtype=np.uint8)
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    x0, x1 = int(xs.min()), int(xs.max()) + 1
    crop = img[y0:y1, x0:x1]
    h, w = crop.shape
    pad = 2
    side = max(h, w) + pad * 2
    canvas = np.zeros((side, side), dtype=np.uint8)
    yoff = (side - h) // 2
    xoff = (side - w) // 2
    canvas[yoff : yoff + h, xoff : xoff + w] = crop
    resized = cv2.resize(canvas, (size, size), interpolation=cv2.INTER_AREA)
    _, resized = cv2.threshold(resized, 0, 255, cv2.THRESH_BINARY)
    return resized


def extract_glyph(bw: np.ndarray, seg: Tuple[int, int]) -> np.ndarray:
    x0, x1 = seg
    sub = bw[:, x0:x1]
    ys = np.where(sub > 0)[0]
    if ys.size == 0:
        return np.zeros((32, 32), dtype=np.uint8)
    y0, y1 = int(ys.min()), int(ys.max()) + 1
    crop = sub[y0:y1, :]
    return normalize_glyph(crop, size=32)


def count_glyph_holes(glyph: np.ndarray) -> int:
    # glyph is binary (0/255) with foreground white
    bin_img = (glyph > 0).astype(np.uint8)
    # Light closing to reduce spurious tiny holes
    kernel = np.ones((2, 2), np.uint8)
    bin_img = cv2.morphologyEx(bin_img, cv2.MORPH_CLOSE, kernel, iterations=1)

    inv = 1 - bin_img
    h, w = inv.shape
    mask = np.zeros((h + 2, w + 2), np.uint8)
    inv2 = inv.copy()
    cv2.floodFill(inv2, mask, (0, 0), 0)
    holes = (inv2 > 0).astype(np.uint8)
    num, labels = cv2.connectedComponents(holes)
    if num <= 1:
        return 0
    area_thresh = int(0.01 * h * w)
    count = 0
    for label in range(1, num):
        area = int((labels == label).sum())
        if area >= area_thresh:
            count += 1
    return count


def build_synthetic_templates(target_h: int, chars: Iterable[str]) -> Dict[str, List[np.ndarray]]:
    templates: Dict[str, List[np.ndarray]] = {}
    fonts = [
        cv2.FONT_HERSHEY_SIMPLEX,
        cv2.FONT_HERSHEY_PLAIN,
        cv2.FONT_HERSHEY_DUPLEX,
        cv2.FONT_HERSHEY_TRIPLEX,
        cv2.FONT_HERSHEY_COMPLEX,
        cv2.FONT_HERSHEY_COMPLEX_SMALL,
    ]
    base_thickness = max(1, int(round(target_h / 15)))
    thicknesses = sorted({base_thickness, max(1, base_thickness - 1), base_thickness + 1})
    for ch in chars:
        for font in fonts:
            for thickness in thicknesses:
                scale = 1.0
                (_, th), _ = cv2.getTextSize(ch, font, scale, thickness)
                if th > 0:
                    scale = (target_h * 0.9) / th
                canvas = np.zeros((target_h * 3, target_h * 3), dtype=np.uint8)
                (tw, th), _ = cv2.getTextSize(ch, font, scale, thickness)
                x = (canvas.shape[1] - tw) // 2
                y = (canvas.shape[0] + th) // 2
                cv2.putText(canvas, ch, (x, y), font, scale, 255, thickness, cv2.LINE_AA)
                _, bw = cv2.threshold(canvas, 0, 255, cv2.THRESH_BINARY)
                glyph = normalize_glyph(bw, size=32)
                templates.setdefault(ch, []).append(glyph)
    return templates


class TemplateBank:
    def __init__(self, templates: Dict[str, List[np.ndarray]], synth_templates: Dict[str, List[np.ndarray]]):
        self.templates = templates
        self.synth_templates = synth_templates

    def score(self, glyph: np.ndarray, tmpl: np.ndarray) -> float:
        g = glyph.astype(np.float32)
        t = tmpl.astype(np.float32)
        g = g - g.mean()
        t = t - t.mean()
        denom = (np.linalg.norm(g) * np.linalg.norm(t)) + 1e-6
        return float(np.dot(g.flatten(), t.flatten()) / denom)

    def best_match(self, glyph: np.ndarray, allowed: Optional[Sequence[str]] = None) -> Tuple[str, float]:
        chars = allowed if allowed is not None else list(self.templates.keys() | self.synth_templates.keys())
        best_ch = "?"
        best_score = -1.0
        for ch in chars:
            candidates = []
            if ch in self.templates:
                candidates.extend(self.templates[ch])
            if ch in self.synth_templates:
                candidates.extend(self.synth_templates[ch])
            if not candidates:
                continue
            score = max(self.score(glyph, t) for t in candidates)
            if score > best_score:
                best_score = score
                best_ch = ch
        return best_ch, best_score


def rank_char_candidates(
    glyph: np.ndarray,
    bank: TemplateBank,
    allowed: Optional[Sequence[str]],
    top_k: int = 3,
) -> List[Tuple[str, float]]:
    if allowed is None:
        allowed = list(bank.templates.keys() | bank.synth_templates.keys())
    scores: List[Tuple[str, float]] = []
    holes = None
    if all(ch.isdigit() for ch in allowed):
        holes = count_glyph_holes(glyph)
    hole_pref = {0: set("123457"), 1: set("069"), 2: set("8")}
    for ch in allowed:
        candidates = []
        if ch in bank.templates:
            candidates.extend(bank.templates[ch])
        if ch in bank.synth_templates:
            candidates.extend(bank.synth_templates[ch])
        if not candidates:
            continue
        score = max(bank.score(glyph, t) for t in candidates)
        if holes is not None:
            preferred = hole_pref.get(holes, set())
            if ch in preferred:
                score += 0.03  # slight preference if hole count matches
            elif preferred:
                score -= 0.03  # slight penalty if hole count mismatches
        scores.append((ch, score))
    scores.sort(key=lambda x: x[1], reverse=True)
    return scores[: max(1, top_k)]


def estimate_char_height(bw: np.ndarray, segments: List[Tuple[int, int]]) -> int:
    heights = []
    for seg in segments:
        x0, x1 = seg
        sub = bw[:, x0:x1]
        ys = np.where(sub > 0)[0]
        if ys.size == 0:
            continue
        heights.append(int(ys.max() - ys.min() + 1))
    if not heights:
        return 24
    return int(np.median(heights))


def build_templates_from_calibration(
    clip_path: Path,
    expected_tokens: Sequence[str],
    debug_dir: Optional[Path] = None,
) -> TemplateBank:
    frame = read_first_frame(clip_path)
    bar, _ = extract_bottom_bar(frame)
    bw = binarize_bar(bar)
    segments = column_segments(bw)
    tokens = split_tokens(segments)

    if debug_dir:
        debug_dir.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(debug_dir / "bar.png"), bar)
        cv2.imwrite(str(debug_dir / "bar_bw.png"), bw)

    # Map tokens in order to expected tokens by length.
    selected: List[List[Tuple[int, int]]] = []
    idx = 0
    for token_str in expected_tokens:
        target_len = len(token_str)
        while idx < len(tokens) and len(tokens[idx]) != target_len:
            idx += 1
        if idx >= len(tokens):
            raise RuntimeError(
                f"Calibration failed: could not find token length {target_len} in {clip_path.name}"
            )
        selected.append(tokens[idx])
        idx += 1

    templates: Dict[str, List[np.ndarray]] = {}
    for token_segs, token_str in zip(selected, expected_tokens):
        if len(token_segs) != len(token_str):
            raise RuntimeError(
                f"Calibration token mismatch: segs={len(token_segs)} str={len(token_str)}"
            )
        for seg, ch in zip(token_segs, token_str):
            glyph = extract_glyph(bw, seg)
            templates.setdefault(ch, []).append(glyph)

    target_h = estimate_char_height(bw, segments)
    synth_chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ:-"
    synth_templates = build_synthetic_templates(target_h, synth_chars)
    return TemplateBank(templates, synth_templates)


def build_template_bank(
    calibration_clip: Optional[Path],
    expected_tokens: Sequence[str],
    fallback_clip: Optional[Path] = None,
    debug_dir: Optional[Path] = None,
) -> TemplateBank:
    if calibration_clip and calibration_clip.exists():
        return build_templates_from_calibration(calibration_clip, expected_tokens, debug_dir=debug_dir)
    if fallback_clip and fallback_clip.exists():
        frame = read_first_frame(fallback_clip)
        bar, _ = extract_bottom_bar(frame)
        bw = binarize_bar(bar)
        segments = column_segments(bw)
        target_h = estimate_char_height(bw, segments)
        synth_chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ:-"
        synth_templates = build_synthetic_templates(target_h, synth_chars)
        return TemplateBank({}, synth_templates)
    # Fallback to a generic synthetic bank
    synth_templates = build_synthetic_templates(24, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ:-")
    return TemplateBank({}, synth_templates)


def ocr_token(
    bw: np.ndarray,
    token_segs: List[Tuple[int, int]],
    bank: TemplateBank,
    kind: str,
) -> str:
    out = []
    for i, seg in enumerate(token_segs):
        glyph = extract_glyph(bw, seg)
        if kind == "site":
            allowed = list("ABCDEFGHIJKLMNOPQRSTUVWXYZ") if i == 0 else list("0123456789")
        elif kind == "temp_f":
            allowed = list("0123456789") if i < len(token_segs) - 1 else ["F"]
        elif kind == "temp_c":
            allowed = list("0123456789") if i < len(token_segs) - 1 else ["C"]
        elif kind == "date":
            allowed = ["-"] if i in (2, 5) else list("0123456789")
        elif kind == "time":
            allowed = [":"] if i in (2, 5) else list("0123456789")
        else:
            allowed = None
        ch, _ = rank_char_candidates(glyph, bank, allowed=allowed, top_k=1)[0]
        out.append(ch)
    return "".join(out)


def best_numeric_value(
    digit_candidates: List[List[Tuple[str, float]]],
    min_val: int | None = None,
    max_val: int | None = None,
) -> Optional[Tuple[int, float]]:
    if not digit_candidates:
        return None
    best = None
    best_score = -1.0
    for combo in np.array(np.meshgrid(*[range(len(c)) for c in digit_candidates])).T.reshape(-1, len(digit_candidates)):
        digits = [digit_candidates[i][idx][0] for i, idx in enumerate(combo)]
        score = sum(digit_candidates[i][idx][1] for i, idx in enumerate(combo))
        val = int("".join(digits))
        if min_val is not None and val < min_val:
            continue
        if max_val is not None and val > max_val:
            continue
        if score > best_score:
            best_score = score
            best = val
    if best is None:
        return None
    return best, best_score


def decode_temp_token(
    bw: np.ndarray,
    token_segs: List[Tuple[int, int]],
    bank: TemplateBank,
    suffix: str,
    top_k: int = 3,
) -> Tuple[Optional[int], List[Tuple[int, float]]]:
    digit_segs = token_segs[:-1]
    digit_candidates: List[List[Tuple[str, float]]] = []
    for seg in digit_segs:
        glyph = extract_glyph(bw, seg)
        digit_candidates.append(rank_char_candidates(glyph, bank, allowed=list("0123456789"), top_k=top_k))
    # Build candidate values list
    values: List[Tuple[int, float]] = []
    if digit_candidates:
        for combo in np.array(np.meshgrid(*[range(len(c)) for c in digit_candidates])).T.reshape(-1, len(digit_candidates)):
            digits = [digit_candidates[i][idx][0] for i, idx in enumerate(combo)]
            score = sum(digit_candidates[i][idx][1] for i, idx in enumerate(combo))
            val = int("".join(digits))
            values.append((val, score))
    # Sort by score
    values.sort(key=lambda x: x[1], reverse=True)
    # Range constraints
    if suffix.upper() == "F":
        values = [(v, s) for v, s in values if 0 <= v <= 200]
    else:
        values = [(v, s) for v, s in values if -20 <= v <= 100]
    best_val = values[0][0] if values else None
    return best_val, values


def decode_date_token(
    bw: np.ndarray,
    token_segs: List[Tuple[int, int]],
    bank: TemplateBank,
    top_k: int = 3,
    month_day_hint: Optional[Tuple[int, int]] = None,
    year_hint: Optional[int] = None,
) -> Optional[Tuple[str, float]]:
    if len(token_segs) != 10:
        return None
    digit_positions = [0, 1, 3, 4, 6, 7, 8, 9]
    digit_candidates: List[List[Tuple[str, float]]] = []
    for pos in digit_positions:
        glyph = extract_glyph(bw, token_segs[pos])
        digit_candidates.append(rank_char_candidates(glyph, bank, allowed=list("0123456789"), top_k=top_k))
    best = None
    best_score = -1.0
    all_candidates: List[Tuple[int, int, int, float, str]] = []
    for combo in np.array(np.meshgrid(*[range(len(c)) for c in digit_candidates])).T.reshape(-1, len(digit_candidates)):
        digits = [digit_candidates[i][idx][0] for i, idx in enumerate(combo)]
        score = sum(digit_candidates[i][idx][1] for i, idx in enumerate(combo))
        month = int("".join(digits[0:2]))
        day = int("".join(digits[2:4]))
        year = int("".join(digits[4:8]))
        if not (1 <= month <= 12):
            continue
        if not (2000 <= year <= 2099):
            continue
        # days in month
        if month == 2:
            leap = (year % 4 == 0) and (year % 100 != 0 or year % 400 == 0)
            max_day = 29 if leap else 28
        elif month in (1, 3, 5, 7, 8, 10, 12):
            max_day = 31
        else:
            max_day = 30
        if not (1 <= day <= max_day):
            continue
        text = f"{month:02d}-{day:02d}-{year:04d}"
        all_candidates.append((month, day, year, score, text))
        if score > best_score:
            best_score = score
            best = text
    if best is None:
        return None
    # Apply hints if available
    def filter_candidates(cands: List[Tuple[int, int, int, float, str]]) -> List[Tuple[int, int, int, float, str]]:
        filtered = cands
        if month_day_hint:
            filtered = [c for c in filtered if (c[0], c[1]) == month_day_hint]
            if filtered:
                cands = filtered
        if year_hint:
            filtered = [c for c in cands if c[2] == year_hint]
            if filtered:
                cands = filtered
        return cands

    candidates = filter_candidates(all_candidates)
    if not candidates:
        candidates = all_candidates
    candidates.sort(key=lambda x: x[3], reverse=True)
    return candidates[0][4], candidates[0][3]


def decode_time_token(
    bw: np.ndarray,
    token_segs: List[Tuple[int, int]],
    bank: TemplateBank,
    top_k: int = 3,
    time_hint: Optional[Tuple[int, int, int]] = None,
) -> Optional[Tuple[str, float]]:
    if len(token_segs) != 8:
        return None
    digit_positions = [0, 1, 3, 4, 6, 7]
    digit_candidates: List[List[Tuple[str, float]]] = []
    for pos in digit_positions:
        glyph = extract_glyph(bw, token_segs[pos])
        digit_candidates.append(rank_char_candidates(glyph, bank, allowed=list("0123456789"), top_k=top_k))
    best = None
    best_score = -1.0
    all_candidates: List[Tuple[int, int, int, float, str]] = []
    for combo in np.array(np.meshgrid(*[range(len(c)) for c in digit_candidates])).T.reshape(-1, len(digit_candidates)):
        digits = [digit_candidates[i][idx][0] for i, idx in enumerate(combo)]
        score = sum(digit_candidates[i][idx][1] for i, idx in enumerate(combo))
        hour = int("".join(digits[0:2]))
        minute = int("".join(digits[2:4]))
        second = int("".join(digits[4:6]))
        if not (0 <= hour <= 23):
            continue
        if not (0 <= minute <= 59):
            continue
        if not (0 <= second <= 59):
            continue
        text = f"{hour:02d}:{minute:02d}:{second:02d}"
        all_candidates.append((hour, minute, second, score, text))
        if score > best_score:
            best_score = score
            best = text
    if best is None:
        return None
    if time_hint:
        exact = [c for c in all_candidates if (c[0], c[1], c[2]) == time_hint]
        if exact:
            exact.sort(key=lambda x: x[3], reverse=True)
            return exact[0][4], exact[0][3]
    all_candidates.sort(key=lambda x: x[3], reverse=True)
    return all_candidates[0][4], all_candidates[0][3]


def extract_metadata(
    clip_path: Path,
    bank: TemplateBank,
    debug_dir: Optional[Path] = None,
) -> BarMetadata:
    frame = read_first_frame(clip_path)
    bar, _ = extract_bottom_bar(frame)
    bw = binarize_bar(bar)
    segments = column_segments(bw)
    tokens = split_tokens(segments)

    if debug_dir:
        debug_dir.mkdir(parents=True, exist_ok=True)
        cv2.imwrite(str(debug_dir / f"{clip_path.stem}_bar.png"), bar)
        cv2.imwrite(str(debug_dir / f"{clip_path.stem}_bar_bw.png"), bw)

    def find_site(start_idx: int) -> Tuple[str, int]:
        for i in range(start_idx, len(tokens)):
            text = ocr_token(bw, tokens[i], bank, "site")
            if re.fullmatch(r"[A-Z][0-9]{2}", text):
                return text, i
        raise RuntimeError(f"Tokenization failed: missing token kind site in {clip_path.name}")

    # First, try to detect a fused temp token like 76F24C.
    fused_temp = None
    for i in range(0, len(tokens)):
        if len(tokens[i]) < 4:
            continue
        text = ocr_token(bw, tokens[i], bank, "generic")
        fused = re.search(r"(\d{1,3})F(\d{1,3})C", text)
        if fused:
            fused_temp = (int(fused.group(1)), int(fused.group(2)), i)
            break

    def find_temp(start_idx: int, suffix: str) -> Tuple[int, List[Tuple[int, float]], int]:
        if fused_temp is not None:
            f_val, c_val, idx = fused_temp
            return (f_val if suffix == "F" else c_val), [], idx
        pattern = r"\d{1,3}" + re.escape(suffix)
        for i in range(start_idx, len(tokens)):
            if len(tokens[i]) < 2:
                continue
            text = ocr_token(bw, tokens[i], bank, "temp_f" if suffix == "F" else "temp_c")
            if not re.fullmatch(pattern, text):
                continue
            val, candidates = decode_temp_token(bw, tokens[i], bank, suffix=suffix)
            if val is None:
                continue
            return val, candidates, i
        raise RuntimeError(f"Tokenization failed: missing token kind temp_{suffix} in {clip_path.name}")

    mmdd_hint = parse_mmdd_from_filename(clip_path.name)
    created_dt = get_creation_time_utc(clip_path)
    year_hint = created_dt.year if created_dt else None
    time_hint = (created_dt.hour, created_dt.minute, created_dt.second) if created_dt else None

    def find_date(start_idx: int) -> Tuple[str, int]:
        for i in range(start_idx, len(tokens)):
            decoded = decode_date_token(
                bw,
                tokens[i],
                bank,
                month_day_hint=mmdd_hint,
                year_hint=year_hint,
            )
            if decoded is None:
                continue
            text, _ = decoded
            if re.fullmatch(r"\d{2}-\d{2}-\d{4}", text):
                return text, i
        raise RuntimeError(f"Tokenization failed: missing token kind date in {clip_path.name}")

    def find_time(start_idx: int) -> Tuple[str, int]:
        for i in range(start_idx, len(tokens)):
            decoded = decode_time_token(bw, tokens[i], bank, time_hint=time_hint)
            if decoded is None:
                continue
            text, _ = decoded
            if re.fullmatch(r"\d{2}:\d{2}:\d{2}", text):
                return text, i
        raise RuntimeError(f"Tokenization failed: missing token kind time in {clip_path.name}")

    site, idx = find_site(0)
    temp_f_val, temp_f_candidates, idx = find_temp(idx + 1, "F")
    temp_c_val, temp_c_candidates, idx = find_temp(idx + 1, "C")
    date, idx = find_date(idx + 1)
    time, idx = find_time(idx + 1)

    # Refine temps using F/C consistency if needed.
    def f_to_c(f: int) -> float:
        return (f - 32) * 5.0 / 9.0

    if temp_f_candidates and temp_c_candidates:
        # Only override OCR with F/C consistency if mismatch is large.
        base_err = abs(temp_c_val - f_to_c(temp_f_val))
        if base_err > 2.0:
            best_pair = None
            best_err = None
            best_score = -1.0
            for f_val, f_score in temp_f_candidates:
                target_c = f_to_c(f_val)
                for c_val, c_score in temp_c_candidates:
                    err = abs(c_val - target_c)
                    score = f_score + c_score
                    if best_err is None or err < best_err or (abs(err - best_err) < 1e-6 and score > best_score):
                        best_err = err
                        best_score = score
                        best_pair = (f_val, c_val)
            if best_pair:
                temp_f_val, temp_c_val = best_pair

    temp_f = f"{temp_f_val}F"
    temp_c = f"{temp_c_val}C"

    # Validate & coerce types
    if not re.fullmatch(r"[A-Z][0-9]{2}", site):
        raise RuntimeError(f"OCR failed site parse: '{site}' in {clip_path.name}")
    if not re.fullmatch(r"\d{2}-\d{2}-\d{4}", date):
        raise RuntimeError(f"OCR failed date parse: '{date}' in {clip_path.name}")
    if not re.fullmatch(r"\d{2}:\d{2}:\d{2}", time):
        raise RuntimeError(f"OCR failed time parse: '{time}' in {clip_path.name}")
    if not re.fullmatch(r"\d{1,3}F", temp_f):
        raise RuntimeError(f"OCR failed temp_f parse: '{temp_f}' in {clip_path.name}")
    if not re.fullmatch(r"\d{1,3}C", temp_c):
        raise RuntimeError(f"OCR failed temp_c parse: '{temp_c}' in {clip_path.name}")

    return BarMetadata(
        site=site,
        temp_f=int(temp_f[:-1]),
        temp_c=int(temp_c[:-1]),
        date=date,
        time=time,
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Extract metadata from bottom bar of ReefCams clips.")
    script_dir = Path(__file__).resolve().parent
    default_clip = script_dir.parent / "TestClipsUnstructured" / "01090421.MOV"
    parser.add_argument("--clip", type=Path, required=True, help="Clip to read.")
    parser.add_argument(
        "--calibration-clip",
        type=Path,
        default=default_clip,
        help="Clip used to build OCR templates.",
    )
    parser.add_argument("--calibration-site", type=str, default="F32")
    parser.add_argument("--calibration-temp-f", type=str, default="82F")
    parser.add_argument("--calibration-temp-c", type=str, default="27C")
    parser.add_argument("--calibration-date", type=str, default="01-09-2026")
    parser.add_argument("--calibration-time", type=str, default="16:01:29")
    parser.add_argument("--debug-dir", type=Path, default=None, help="Optional debug image output folder.")
    return parser.parse_args()


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
    meta = extract_metadata(args.clip, bank, debug_dir=args.debug_dir)
    print(json.dumps(meta.as_dict(), indent=2))


if __name__ == "__main__":
    main()
