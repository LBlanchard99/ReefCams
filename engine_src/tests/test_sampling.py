import sys
from pathlib import Path

ENGINE_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ENGINE_DIR))

import video  # noqa: E402

BENCH = ENGINE_DIR / "benchmark" / "benchmark_10s.mov"


def test_sampling():
    meta = video.probe_clip(BENCH)
    duration = meta["duration_sec"]
    samples = list(video.sample_frames_with_times(BENCH, fps=1))
    times = [t for t, _ in samples]
    assert times

    for t in times:
        assert 0.0 <= t <= duration + 0.75

    for prev, cur in zip(times, times[1:]):
        assert cur >= prev

    deltas = [b - a for a, b in zip(times, times[1:])]
    for dt in deltas:
        assert 0.4 <= dt <= 1.6
