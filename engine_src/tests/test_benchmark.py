import json
import subprocess
import sys
from pathlib import Path


ENGINE_DIR = Path(__file__).resolve().parents[1]
ENGINE = ENGINE_DIR / "engine.py"


def test_benchmark():
    result = subprocess.run(
        [sys.executable, str(ENGINE), "benchmark", "--fps", "1"],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stderr

    payload = None
    for line in result.stdout.splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("type") == "benchmark_result":
            payload = obj
            break

    assert payload is not None
    assert payload.get("provider_used")
    assert payload.get("avg_infer_ms") is not None
    assert payload.get("p95_infer_ms") is not None
    assert payload.get("estimate_per_10s_s", 0) > 0
