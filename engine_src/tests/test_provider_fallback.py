import json
import subprocess
import sys
from pathlib import Path

from util import cleanup_temp_dir, make_temp_dir


ENGINE_DIR = Path(__file__).resolve().parents[1]
ENGINE = ENGINE_DIR / "engine.py"
BENCH = ENGINE_DIR / "benchmark" / "benchmark_10s.mov"


def test_provider_fallback():
    tmp_path = make_temp_dir()
    try:
        db_path = Path(tmp_path) / "temp.db"
        result = subprocess.run(
            [
                sys.executable,
                str(ENGINE),
                "process",
                "--clip",
                str(BENCH),
                "--db",
                str(db_path),
                "--fps",
                "1",
                "--provider",
                "DmlExecutionProvider,CPUExecutionProvider",
            ],
            capture_output=True,
            text=True,
            check=False,
        )
        assert result.returncode == 0, result.stderr

        provider_used = ""
        for line in result.stdout.splitlines():
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if obj.get("type") == "done":
                provider_used = obj.get("provider_used", "")
                break

        assert provider_used in ("DmlExecutionProvider", "CPUExecutionProvider")
    finally:
        cleanup_temp_dir(tmp_path)
