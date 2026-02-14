import sqlite3
import subprocess
import sys
from pathlib import Path

from util import cleanup_temp_dir, make_temp_dir


ENGINE_DIR = Path(__file__).resolve().parents[1]
ENGINE = ENGINE_DIR / "engine.py"
BENCH = ENGINE_DIR / "benchmark" / "benchmark_10s.mov"


def test_smoke():
    tmp_path = make_temp_dir()
    try:
        db_path = Path(tmp_path) / "temp.db"
        result = subprocess.run(
            [sys.executable, str(ENGINE), "process", "--clip", str(BENCH), "--db", str(db_path), "--fps", "1"],
            capture_output=True,
            text=True,
            check=False,
        )
        assert result.returncode == 0, result.stderr

        conn = sqlite3.connect(str(db_path))
        processed = conn.execute("SELECT processed FROM clips").fetchone()
        assert processed is not None and int(processed[0]) == 1

        frame_count = conn.execute("SELECT COUNT(*) FROM frames").fetchone()[0]
        assert 8 <= frame_count <= 13

        times = [row[0] for row in conn.execute("SELECT frame_time_sec FROM frames ORDER BY frame_time_sec").fetchall()]
        assert times == sorted(times)

        det_table = conn.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='detections'"
        ).fetchone()
        assert det_table is not None

        det_cols = {row[1] for row in conn.execute("PRAGMA table_info(detections)").fetchall()}
        assert "cls_label" in det_cols

        clip_cols = {row[1] for row in conn.execute("PRAGMA table_info(clips)").fetchall()}
        assert "max_conf_cls_id" in clip_cols
        assert "max_conf_label" in clip_cols

        det_count = conn.execute("SELECT COUNT(*) FROM detections").fetchone()[0]
        if det_count > 0:
            labels = [row[0] for row in conn.execute("SELECT DISTINCT cls_label FROM detections").fetchall()]
            assert all(isinstance(lbl, str) and len(lbl) > 0 for lbl in labels)

        max_conf, max_conf_label = conn.execute("SELECT max_conf, max_conf_label FROM clips").fetchone()
        if float(max_conf or 0.0) > 0.0:
            assert isinstance(max_conf_label, str) and len(max_conf_label) > 0
    finally:
        cleanup_temp_dir(tmp_path)
