import math
import os
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Tuple

import cv2


def _file_mtime_utc(path: Path) -> str:
    ts = os.path.getmtime(path)
    return datetime.fromtimestamp(ts, timezone.utc).isoformat()


def probe_clip(path: str | Path) -> dict:
    clip_path = Path(path)
    cap = cv2.VideoCapture(str(clip_path))
    if not cap.isOpened():
        raise RuntimeError(f"Failed to open clip: {clip_path}")

    video_fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
    frame_count = float(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0)
    width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
    duration_sec = 0.0
    if video_fps > 0.0 and frame_count > 0.0:
        duration_sec = frame_count / video_fps
    if not math.isfinite(duration_sec) or duration_sec <= 0.0:
        try:
            cap.set(cv2.CAP_PROP_POS_AVI_RATIO, 1.0)
            _ = cap.read()
            pos_msec = float(cap.get(cv2.CAP_PROP_POS_MSEC) or 0.0)
            if pos_msec > 0.0:
                duration_sec = pos_msec / 1000.0
        except Exception:
            duration_sec = 0.0
    if not math.isfinite(duration_sec):
        duration_sec = 0.0

    cap.release()
    return {
        "clip_path": str(clip_path),
        "file_size": int(os.path.getsize(clip_path)),
        "file_mtime_utc": _file_mtime_utc(clip_path),
        "duration_sec": float(duration_sec),
        "video_fps": float(video_fps),
        "width": int(width),
        "height": int(height),
    }


def sample_frames_with_times(path: str | Path, fps: float) -> Iterable[Tuple[float, "cv2.Mat"]]:
    clip_path = Path(path)
    cap = cv2.VideoCapture(str(clip_path))
    if not cap.isOpened():
        return []

    src_fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
    frame_count = float(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0)
    duration_sec = 0.0
    if src_fps > 0.0 and frame_count > 0.0:
        duration_sec = frame_count / src_fps

    step = 1.0 / max(fps, 0.001)
    t = 0.0
    prev_t_actual = -1.0
    while True:
        if duration_sec > 0.0 and t >= duration_sec:
            break
        cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
        ret, frame = cap.read()
        if not ret:
            break
        t_actual = float(cap.get(cv2.CAP_PROP_POS_MSEC) or (t * 1000.0)) / 1000.0
        if t_actual < prev_t_actual:
            t_actual = prev_t_actual
        prev_t_actual = t_actual
        yield t_actual, frame
        t += step

    cap.release()


def sample_frames_with_times_debug(
    path: str | Path, fps: float
) -> Iterable[Tuple[float, "cv2.Mat", dict]]:
    clip_path = Path(path)
    cap = cv2.VideoCapture(str(clip_path))
    if not cap.isOpened():
        return []

    src_fps = float(cap.get(cv2.CAP_PROP_FPS) or 0.0)
    frame_count = float(cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0.0)
    duration_sec = 0.0
    if src_fps > 0.0 and frame_count > 0.0:
        duration_sec = frame_count / src_fps

    step = 1.0 / max(fps, 0.001)
    t = 0.0
    prev_t_actual = -1.0
    while True:
        if duration_sec > 0.0 and t >= duration_sec:
            break
        t_set0 = cv2.getTickCount()
        cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
        t_set1 = cv2.getTickCount()
        t_read0 = cv2.getTickCount()
        ret, frame = cap.read()
        t_read1 = cv2.getTickCount()
        if not ret:
            break
        t_actual = float(cap.get(cv2.CAP_PROP_POS_MSEC) or (t * 1000.0)) / 1000.0
        if t_actual < prev_t_actual:
            t_actual = prev_t_actual
        prev_t_actual = t_actual
        freq = cv2.getTickFrequency()
        seek_ms = (t_set1 - t_set0) * 1000.0 / freq
        read_ms = (t_read1 - t_read0) * 1000.0 / freq
        yield t_actual, frame, {"seek_ms": seek_ms, "read_ms": read_ms}
        t += step

    cap.release()
