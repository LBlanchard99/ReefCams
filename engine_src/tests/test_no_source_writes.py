import os
import shutil
import subprocess
import sys
from pathlib import Path

from util import cleanup_temp_dir, make_temp_dir


ENGINE_DIR = Path(__file__).resolve().parents[1]
ENGINE = ENGINE_DIR / "engine.py"
BENCH = ENGINE_DIR / "benchmark" / "benchmark_10s.mov"


def test_no_source_writes():
    tmp_path = make_temp_dir()
    try:
        clip_dir = tmp_path / "clips"
        clip_dir.mkdir(parents=True, exist_ok=True)
        clip_path = clip_dir / "benchmark_10s.mov"
        shutil.copy(BENCH, clip_path)

        before = sorted(os.listdir(clip_dir))
        db_path = tmp_path / "temp.db"

        result = subprocess.run(
            [sys.executable, str(ENGINE), "process", "--clip", str(clip_path), "--db", str(db_path), "--fps", "1"],
            capture_output=True,
            text=True,
            check=False,
        )
        assert result.returncode == 0, result.stderr

        after = sorted(os.listdir(clip_dir))
        assert before == after
    finally:
        cleanup_temp_dir(tmp_path)
