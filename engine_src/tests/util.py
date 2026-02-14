from __future__ import annotations

import shutil
from pathlib import Path
from uuid import uuid4


BASE_TMP = Path(__file__).resolve().parents[1] / "_test_tmp"
BASE_TMP.mkdir(exist_ok=True)


def make_temp_dir() -> Path:
    path = BASE_TMP / f"run_{uuid4().hex}"
    path.mkdir(parents=True, exist_ok=False)
    return path


def cleanup_temp_dir(path: Path) -> None:
    shutil.rmtree(path, ignore_errors=True)
