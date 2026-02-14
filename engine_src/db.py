import hashlib
import sqlite3
from pathlib import Path
from typing import Iterable, Sequence


def open_db(db_path: str | Path) -> sqlite3.Connection:
    conn = sqlite3.connect(str(db_path))
    conn.execute("PRAGMA foreign_keys=ON;")
    return conn


def ensure_schema(conn: sqlite3.Connection) -> None:
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS clips(
            clip_id TEXT PRIMARY KEY,
            clip_path TEXT NOT NULL,
            file_size INTEGER,
            file_mtime_utc TEXT,
            duration_sec REAL,
            video_fps REAL,
            width INTEGER,
            height INTEGER,
            site TEXT,
            temp_f INTEGER,
            temp_c INTEGER,
            bar_date TEXT,
            bar_time TEXT,
            processed INTEGER NOT NULL DEFAULT 0,
            processed_fps REAL,
            max_conf REAL,
            max_conf_time_sec REAL,
            max_conf_cls_id INTEGER,
            max_conf_label TEXT
        )
        """
    )
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS frames(
            clip_id TEXT NOT NULL,
            frame_time_sec REAL NOT NULL,
            max_conf_frame REAL NOT NULL,
            PRIMARY KEY (clip_id, frame_time_sec)
        )
        """
    )
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS detections(
            clip_id TEXT NOT NULL,
            frame_time_sec REAL NOT NULL,
            cls_id INTEGER NOT NULL,
            cls_label TEXT,
            conf REAL NOT NULL,
            x REAL NOT NULL,
            y REAL NOT NULL,
            w REAL NOT NULL,
            h REAL NOT NULL,
            area_frac REAL,
            PRIMARY KEY (clip_id, frame_time_sec, cls_id, conf, x, y, w, h)
        )
        """
    )
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS benchmarks(
            run_at_utc TEXT,
            provider_requested TEXT,
            provider_used TEXT,
            fps REAL,
            load_ms REAL,
            avg_infer_ms REAL,
            p95_infer_ms REAL,
            total_ms REAL,
            estimate_per_10s_s REAL
        )
        """
    )
    conn.execute("CREATE INDEX IF NOT EXISTS idx_frames_clip ON frames(clip_id)")
    conn.execute("CREATE INDEX IF NOT EXISTS idx_det_clip_time ON detections(clip_id, frame_time_sec)")
    _ensure_columns(
        conn,
        "clips",
        {
            "site": "TEXT",
            "temp_f": "INTEGER",
            "temp_c": "INTEGER",
            "bar_date": "TEXT",
            "bar_time": "TEXT",
            "max_conf_cls_id": "INTEGER",
            "max_conf_label": "TEXT",
        },
    )
    _ensure_columns(
        conn,
        "detections",
        {
            "cls_label": "TEXT",
        },
    )
    conn.commit()


def _ensure_columns(conn: sqlite3.Connection, table: str, columns: dict[str, str]) -> None:
    existing = {row[1] for row in conn.execute(f"PRAGMA table_info({table})").fetchall()}
    for name, col_type in columns.items():
        if name in existing:
            continue
        conn.execute(f"ALTER TABLE {table} ADD COLUMN {name} {col_type}")


def compute_clip_id(path: str | Path, file_size: int, file_mtime_utc: str) -> str:
    norm = Path(path).resolve()
    norm_str = str(norm).lower()
    payload = f"{norm_str}|{file_size}|{file_mtime_utc}"
    return hashlib.sha1(payload.encode("utf-8")).hexdigest()


def get_clip_state(conn: sqlite3.Connection, clip_id: str) -> dict | None:
    row = conn.execute(
        "SELECT processed, processed_fps, max_conf, max_conf_time_sec, max_conf_cls_id, max_conf_label "
        "FROM clips WHERE clip_id=?",
        (clip_id,),
    ).fetchone()
    if not row:
        return None
    return {
        "processed": int(row[0]),
        "processed_fps": row[1],
        "max_conf": row[2],
        "max_conf_time_sec": row[3],
        "max_conf_cls_id": row[4],
        "max_conf_label": row[5],
    }


def get_clip_id_by_path(conn: sqlite3.Connection, clip_path: str) -> str | None:
    row = conn.execute(
        "SELECT clip_id FROM clips WHERE clip_path=? LIMIT 1",
        (clip_path,),
    ).fetchone()
    if not row:
        return None
    return str(row[0])


def upsert_clip_metadata(
    conn: sqlite3.Connection,
    clip_id: str,
    clip_path: str,
    file_size: int,
    file_mtime_utc: str,
    duration_sec: float,
    video_fps: float,
    width: int,
    height: int,
) -> None:
    conn.execute(
        """
        INSERT INTO clips(
            clip_id, clip_path, file_size, file_mtime_utc,
            duration_sec, video_fps, width, height,
            site, temp_f, temp_c, bar_date, bar_time,
            processed, processed_fps, max_conf, max_conf_time_sec, max_conf_cls_id, max_conf_label
        )
        VALUES(?,?,?,?,?,?,?,?,NULL,NULL,NULL,NULL,NULL,0,NULL,NULL,NULL,NULL,NULL)
        ON CONFLICT(clip_id) DO UPDATE SET
            clip_path=excluded.clip_path,
            file_size=excluded.file_size,
            file_mtime_utc=excluded.file_mtime_utc,
            duration_sec=excluded.duration_sec,
            video_fps=excluded.video_fps,
            width=excluded.width,
            height=excluded.height
        """,
        (clip_id, clip_path, file_size, file_mtime_utc, duration_sec, video_fps, width, height),
    )
    conn.commit()


def update_clip_bar_metadata(
    conn: sqlite3.Connection,
    clip_id: str,
    site: str,
    temp_f: int,
    temp_c: int,
    bar_date: str,
    bar_time: str,
) -> None:
    conn.execute(
        """
        UPDATE clips
        SET site=?, temp_f=?, temp_c=?, bar_date=?, bar_time=?
        WHERE clip_id=?
        """,
        (site, temp_f, temp_c, bar_date, bar_time, clip_id),
    )
    conn.commit()


def write_clip_results(
    conn: sqlite3.Connection,
    clip_id: str,
    processed_fps: float,
    frames: Iterable[Sequence],
    detections: Iterable[Sequence],
    max_conf: float,
    max_conf_time_sec: float,
    max_conf_cls_id: int | None,
    max_conf_label: str | None,
) -> None:
    cur = conn.cursor()
    cur.execute("BEGIN")
    cur.execute("DELETE FROM frames WHERE clip_id=?", (clip_id,))
    cur.execute("DELETE FROM detections WHERE clip_id=?", (clip_id,))

    frame_rows = list(frames)
    det_rows = list(detections)
    if frame_rows:
        cur.executemany(
            "INSERT INTO frames(clip_id, frame_time_sec, max_conf_frame) VALUES(?,?,?)",
            frame_rows,
        )
    if det_rows:
        cur.executemany(
            "INSERT INTO detections(clip_id, frame_time_sec, cls_id, cls_label, conf, x, y, w, h, area_frac) "
            "VALUES(?,?,?,?,?,?,?,?,?,?)",
            det_rows,
        )
    cur.execute(
        "UPDATE clips SET processed=1, processed_fps=?, max_conf=?, max_conf_time_sec=?, "
        "max_conf_cls_id=?, max_conf_label=? WHERE clip_id=?",
        (processed_fps, max_conf, max_conf_time_sec, max_conf_cls_id, max_conf_label, clip_id),
    )
    conn.commit()


def insert_benchmark(
    conn: sqlite3.Connection,
    run_at_utc: str,
    provider_requested: str,
    provider_used: str,
    fps: float,
    load_ms: float,
    avg_infer_ms: float,
    p95_infer_ms: float,
    total_ms: float,
    estimate_per_10s_s: float,
) -> None:
    conn.execute(
        """
        INSERT INTO benchmarks(
            run_at_utc, provider_requested, provider_used, fps,
            load_ms, avg_infer_ms, p95_infer_ms, total_ms, estimate_per_10s_s
        )
        VALUES(?,?,?,?,?,?,?,?,?)
        """,
        (
            run_at_utc,
            provider_requested,
            provider_used,
            fps,
            load_ms,
            avg_infer_ms,
            p95_infer_ms,
            total_ms,
            estimate_per_10s_s,
        ),
    )
    conn.commit()
