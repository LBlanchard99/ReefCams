import sqlite3
import subprocess
import sys
from pathlib import Path

from util import cleanup_temp_dir, make_temp_dir


ENGINE_DIR = Path(__file__).resolve().parents[1]
ENGINE = ENGINE_DIR / "engine.py"
BENCH = ENGINE_DIR / "benchmark" / "benchmark_10s.mov"


def _run_process(db_path):
    return subprocess.run(
        [sys.executable, str(ENGINE), "process", "--clip", str(BENCH), "--db", str(db_path), "--fps", "1"],
        capture_output=True,
        text=True,
        check=False,
    )


def test_db_idempotent():
    tmp_path = make_temp_dir()
    try:
        db_path = Path(tmp_path) / "temp.db"
        first = _run_process(db_path)
        assert first.returncode == 0, first.stderr

        conn = sqlite3.connect(str(db_path))
        frames_before = conn.execute("SELECT COUNT(*) FROM frames").fetchone()[0]
        dets_before = conn.execute("SELECT COUNT(*) FROM detections").fetchone()[0]

        second = _run_process(db_path)
        assert second.returncode == 0, second.stderr

        frames_after = conn.execute("SELECT COUNT(*) FROM frames").fetchone()[0]
        dets_after = conn.execute("SELECT COUNT(*) FROM detections").fetchone()[0]
        assert frames_before == frames_after
        assert dets_before == dets_after
    finally:
        cleanup_temp_dir(tmp_path)
