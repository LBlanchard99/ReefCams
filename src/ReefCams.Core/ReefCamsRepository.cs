using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ReefCams.Core;

public sealed class ReefCamsRepository
{
    private readonly string _dbPath;

    public ReefCamsRepository(string dbPath)
    {
        _dbPath = dbPath;
    }

    public void InitializeSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");

        using var conn = OpenConnection();

        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS clip_roots(
                root_id TEXT PRIMARY KEY,
                root_path TEXT NOT NULL UNIQUE
            );
            """);

        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS clips(
                clip_id TEXT PRIMARY KEY,
                root_id TEXT,
                site TEXT,
                dcim TEXT,
                session TEXT,
                clip_name TEXT,
                clip_path TEXT NOT NULL,
                created_time_utc TEXT,
                file_mtime_utc TEXT,
                file_size INTEGER,
                duration_sec REAL,
                width INTEGER,
                height INTEGER,
                video_fps REAL,
                temp_f INTEGER,
                temp_c INTEGER,
                bar_date TEXT,
                bar_time TEXT,
                processed INTEGER NOT NULL DEFAULT 0,
                processed_fps REAL,
                max_conf REAL,
                max_conf_time_sec REAL,
                max_conf_cls_id INTEGER,
                max_conf_label TEXT,
                completed INTEGER NOT NULL DEFAULT 0,
                completed_at_utc TEXT
            );
            """);

        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS frames(
                clip_id TEXT NOT NULL,
                frame_time_sec REAL NOT NULL,
                max_conf_frame REAL NOT NULL,
                PRIMARY KEY(clip_id, frame_time_sec)
            );
            """);

        conn.Execute(
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
            );
            """);

        conn.Execute(
            """
            CREATE TABLE IF NOT EXISTS reef_boundaries(
                scope_type TEXT NOT NULL,
                scope_id TEXT NOT NULL,
                clip_id_reference TEXT,
                points_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY(scope_type, scope_id)
            );
            """);

        conn.Execute(
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
            );
            """);

        conn.Execute("CREATE INDEX IF NOT EXISTS idx_clips_scope_time ON clips(root_id, site, dcim, session, created_time_utc);");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_clips_status ON clips(processed, completed);");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_frames_clip_time ON frames(clip_id, frame_time_sec);");
        conn.Execute("CREATE INDEX IF NOT EXISTS idx_det_clip_time ON detections(clip_id, frame_time_sec);");

        EnsureColumn(conn, "clips", "root_id", "TEXT");
        EnsureColumn(conn, "clips", "dcim", "TEXT");
        EnsureColumn(conn, "clips", "session", "TEXT");
        EnsureColumn(conn, "clips", "clip_name", "TEXT");
        EnsureColumn(conn, "clips", "created_time_utc", "TEXT");
        EnsureColumn(conn, "clips", "completed", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(conn, "clips", "completed_at_utc", "TEXT");
        EnsureColumn(conn, "benchmarks", "load_ms", "REAL");

        conn.Execute("PRAGMA foreign_keys=ON;");
    }

    public string UpsertClipRoot(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var rootId = Sha1(fullPath.Trim().ToLowerInvariant());

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO clip_roots(root_id, root_path)
            VALUES($rootId, $rootPath)
            ON CONFLICT(root_id) DO UPDATE SET root_path = excluded.root_path;
            """;
        cmd.Parameters.AddWithValue("$rootId", rootId);
        cmd.Parameters.AddWithValue("$rootPath", fullPath);
        cmd.ExecuteNonQuery();
        return rootId;
    }

    public IReadOnlyList<ClipRoot> GetClipRoots()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT root_id, root_path FROM clip_roots ORDER BY root_path;";
        using var reader = cmd.ExecuteReader();
        var roots = new List<ClipRoot>();
        while (reader.Read())
        {
            roots.Add(
                new ClipRoot
                {
                    RootId = reader.GetString(0),
                    RootPath = reader.GetString(1)
                });
        }

        return roots;
    }

    public void UpsertIndexedClip(IndexedClip clip)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var updateByPath = conn.CreateCommand();
        updateByPath.Transaction = tx;
        updateByPath.CommandText =
            """
            UPDATE clips
            SET root_id = $rootId,
                site = $site,
                dcim = $dcim,
                session = $session,
                clip_name = $clipName,
                created_time_utc = $createdTimeUtc,
                file_mtime_utc = $fileMtimeUtc,
                file_size = $fileSize
            WHERE clip_path = $clipPath;
            """;
        updateByPath.Parameters.AddWithValue("$rootId", clip.RootId);
        updateByPath.Parameters.AddWithValue("$site", clip.Site);
        updateByPath.Parameters.AddWithValue("$dcim", clip.Dcim);
        updateByPath.Parameters.AddWithValue("$session", clip.Session);
        updateByPath.Parameters.AddWithValue("$clipName", clip.ClipName);
        updateByPath.Parameters.AddWithValue("$createdTimeUtc", clip.CreatedTimeUtc);
        updateByPath.Parameters.AddWithValue("$fileMtimeUtc", clip.FileMtimeUtc);
        updateByPath.Parameters.AddWithValue("$fileSize", clip.FileSize);
        updateByPath.Parameters.AddWithValue("$clipPath", clip.ClipPath);
        var updated = updateByPath.ExecuteNonQuery();
        if (updated > 0)
        {
            tx.Commit();
            return;
        }

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            """
            INSERT INTO clips(
                clip_id, root_id, site, dcim, session, clip_name, clip_path,
                created_time_utc, file_mtime_utc, file_size,
                processed, completed
            )
            VALUES(
                $clipId, $rootId, $site, $dcim, $session, $clipName, $clipPath,
                $createdTimeUtc, $fileMtimeUtc, $fileSize,
                0, 0
            )
            ON CONFLICT(clip_id) DO UPDATE SET
                root_id = excluded.root_id,
                site = excluded.site,
                dcim = excluded.dcim,
                session = excluded.session,
                clip_name = excluded.clip_name,
                clip_path = excluded.clip_path,
                created_time_utc = excluded.created_time_utc,
                file_mtime_utc = excluded.file_mtime_utc,
                file_size = excluded.file_size;
            """;
        insert.Parameters.AddWithValue("$clipId", clip.ClipId);
        insert.Parameters.AddWithValue("$rootId", clip.RootId);
        insert.Parameters.AddWithValue("$site", clip.Site);
        insert.Parameters.AddWithValue("$dcim", clip.Dcim);
        insert.Parameters.AddWithValue("$session", clip.Session);
        insert.Parameters.AddWithValue("$clipName", clip.ClipName);
        insert.Parameters.AddWithValue("$clipPath", clip.ClipPath);
        insert.Parameters.AddWithValue("$createdTimeUtc", clip.CreatedTimeUtc);
        insert.Parameters.AddWithValue("$fileMtimeUtc", clip.FileMtimeUtc);
        insert.Parameters.AddWithValue("$fileSize", clip.FileSize);
        insert.ExecuteNonQuery();

        tx.Commit();
    }

    public IReadOnlyList<HierarchyNode> GetHierarchy()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                c.root_id,
                IFNULL(r.root_path, ''),
                IFNULL(c.site, ''),
                IFNULL(c.dcim, ''),
                IFNULL(c.session, ''),
                IFNULL(c.processed, 0),
                IFNULL(c.completed, 0)
            FROM clips c
            LEFT JOIN clip_roots r ON r.root_id = c.root_id;
            """;

        using var reader = cmd.ExecuteReader();
        var roots = new Dictionary<string, HierarchyNode>(StringComparer.OrdinalIgnoreCase);
        var boundaries = GetBoundarySessionScopes(conn);

        while (reader.Read())
        {
            var rootId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(rootId))
            {
                continue;
            }

            var rootPath = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var site = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var dcim = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var session = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var processed = reader.GetInt32(5) == 1;
            var completed = reader.GetInt32(6) == 1;

            if (!roots.TryGetValue(rootId, out var rootNode))
            {
                var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(rootName))
                {
                    rootName = string.IsNullOrWhiteSpace(rootPath) ? "(root)" : rootPath;
                }

                rootNode = new HierarchyNode
                {
                    NodeId = $"root:{rootId}",
                    NodeType = TreeNodeType.Root,
                    Name = rootName,
                    Scope = new ScopeFilter(TreeNodeType.Root, rootId)
                };
                roots[rootId] = rootNode;
            }

            var siteNode = EnsureChild(rootNode, TreeNodeType.Site, site, new ScopeFilter(TreeNodeType.Site, rootId, site));
            var dcimNode = EnsureChild(siteNode, TreeNodeType.Dcim, dcim, new ScopeFilter(TreeNodeType.Dcim, rootId, site, dcim));
            var sessionNode = EnsureChild(dcimNode, TreeNodeType.Session, session, new ScopeFilter(TreeNodeType.Session, rootId, site, dcim, session));

            var sessionScopeId = ScopeIds.Session(rootId, site, dcim, session);
            sessionNode.HasBoundary = boundaries.Contains(sessionScopeId);

            IncrementMetrics(rootNode, processed, completed);
            IncrementMetrics(siteNode, processed, completed);
            IncrementMetrics(dcimNode, processed, completed);
            IncrementMetrics(sessionNode, processed, completed);
        }

        foreach (var root in roots.Values)
        {
            SortChildrenRecursively(root);
        }

        return roots.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public int BackfillMissingRootIdsFromClipPath()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var roots = GetClipRoots()
            .Select(x => new
            {
                x.RootId,
                FullPath = Path.GetFullPath(x.RootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            })
            .OrderByDescending(x => x.FullPath.Length)
            .ToList();

        using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText =
            """
            SELECT clip_id, clip_path
            FROM clips
            WHERE root_id IS NULL OR trim(root_id) = '';
            """;

        using var reader = select.ExecuteReader();
        var pending = new List<(string ClipId, string ClipPath)>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            pending.Add((reader.GetString(0), reader.GetString(1)));
        }

        var updated = 0;
        foreach (var row in pending)
        {
            var clipPath = Path.GetFullPath(row.ClipPath);
            var match = roots.FirstOrDefault(root =>
                clipPath.StartsWith(root.FullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                clipPath.Equals(root.FullPath, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                continue;
            }

            var rel = Path.GetRelativePath(match.FullPath, clipPath);
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var site = parts.Length >= 2 ? parts[0] : string.Empty;
            var dcim = parts.Length >= 3 ? parts[1] : string.Empty;
            var session = parts.Length >= 4 ? parts[2] : string.Empty;
            var clipName = Path.GetFileName(clipPath);

            using var update = conn.CreateCommand();
            update.Transaction = tx;
            update.CommandText =
                """
                UPDATE clips
                SET root_id = $rootId,
                    site = CASE WHEN IFNULL(site, '') = '' THEN $site ELSE site END,
                    dcim = CASE WHEN IFNULL(dcim, '') = '' THEN $dcim ELSE dcim END,
                    session = CASE WHEN IFNULL(session, '') = '' THEN $session ELSE session END,
                    clip_name = CASE WHEN IFNULL(clip_name, '') = '' THEN $clipName ELSE clip_name END
                WHERE clip_id = $clipId;
                """;
            update.Parameters.AddWithValue("$rootId", match.RootId);
            update.Parameters.AddWithValue("$site", site);
            update.Parameters.AddWithValue("$dcim", dcim);
            update.Parameters.AddWithValue("$session", session);
            update.Parameters.AddWithValue("$clipName", clipName);
            update.Parameters.AddWithValue("$clipId", row.ClipId);
            updated += update.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    public int MergeDuplicateClipsByPath()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var listCmd = conn.CreateCommand();
        listCmd.Transaction = tx;
        listCmd.CommandText =
            """
            SELECT clip_path
            FROM clips
            GROUP BY clip_path
            HAVING COUNT(*) > 1;
            """;

        var duplicatePaths = new List<string>();
        using (var reader = listCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    duplicatePaths.Add(reader.GetString(0));
                }
            }
        }

        var totalMerged = 0;
        foreach (var path in duplicatePaths)
        {
            totalMerged += MergeDuplicateClipsByPathInternal(conn, tx, path);
        }

        tx.Commit();
        return totalMerged;
    }

    public int MergeDuplicateClipsByPath(string clipPath)
    {
        if (string.IsNullOrWhiteSpace(clipPath))
        {
            return 0;
        }

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        var merged = MergeDuplicateClipsByPathInternal(conn, tx, Path.GetFullPath(clipPath));
        tx.Commit();
        return merged;
    }

    public IReadOnlyList<ClipTimelineItem> GetTimeline(ScopeFilter scope, RankThresholds thresholds)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        var where = BuildScopeWhere(scope, cmd);
        cmd.CommandText =
            $"""
            SELECT
                clip_id, IFNULL(root_id, ''), IFNULL(site, ''), IFNULL(dcim, ''), IFNULL(session, ''),
                IFNULL(clip_name, ''), clip_path,
                IFNULL(NULLIF(created_time_utc, ''), IFNULL(file_mtime_utc, '')),
                IFNULL(processed, 0), IFNULL(completed, 0),
                IFNULL(max_conf, 0), max_conf_time_sec
            FROM clips
            {where}
            ORDER BY
                datetime(IFNULL(NULLIF(created_time_utc, ''), IFNULL(file_mtime_utc, ''))),
                clip_name,
                clip_id;
            """;

        var rows = new List<ClipTimelineItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var createdRaw = reader.GetString(7);
            var created = TryParseUtc(createdRaw);
            var maxConf = reader.GetDouble(10);
            var rank = Ranker.Classify(maxConf, thresholds);
            rows.Add(
                new ClipTimelineItem
                {
                    ClipId = reader.GetString(0),
                    RootId = reader.GetString(1),
                    Site = reader.GetString(2),
                    Dcim = reader.GetString(3),
                    Session = reader.GetString(4),
                    ClipName = reader.GetString(5),
                    ClipPath = reader.GetString(6),
                    CreatedAtUtc = created,
                    Processed = reader.GetInt32(8) == 1,
                    Completed = reader.GetInt32(9) == 1,
                    MaxConf = maxConf,
                    MaxConfTimeSec = reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    Rank = rank
                });
        }

        ClipTimelineItem? previous = null;
        foreach (var row in rows)
        {
            if (previous is not null)
            {
                if (row.CreatedAtUtc != DateTimeOffset.MinValue && previous.CreatedAtUtc != DateTimeOffset.MinValue)
                {
                    row.DeltaFromPreviousSec = (row.CreatedAtUtc - previous.CreatedAtUtc).TotalSeconds;
                }
            }

            previous = row;
        }

        return rows;
    }

    public IReadOnlyList<ClipTimelineItem> GetClipsToProcess(ScopeFilter scope, RankThresholds thresholds)
    {
        return GetTimeline(scope, thresholds)
            .Where(x => !x.Processed && !x.Completed)
            .ToList();
    }

    public BenchmarkRecord? GetLatestBenchmark()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT
                IFNULL(run_at_utc, ''),
                IFNULL(provider_requested, ''),
                IFNULL(provider_used, ''),
                IFNULL(fps, 0),
                IFNULL(avg_infer_ms, 0),
                IFNULL(p95_infer_ms, 0),
                IFNULL(total_ms, 0),
                IFNULL(estimate_per_10s_s, 0)
            FROM benchmarks
            ORDER BY datetime(run_at_utc) DESC
            LIMIT 1;
            """;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new BenchmarkRecord
        {
            RunAtUtc = reader.GetString(0),
            ProviderRequested = reader.GetString(1),
            ProviderUsed = reader.GetString(2),
            Fps = reader.GetDouble(3),
            AvgInferMs = reader.GetDouble(4),
            P95InferMs = reader.GetDouble(5),
            TotalMs = reader.GetDouble(6),
            EstimatePer10sSec = reader.GetDouble(7)
        };
    }

    public void MarkClipCompleted(string clipId, bool completed)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE clips
            SET completed = $completed,
                completed_at_utc = CASE WHEN $completed = 1 THEN $nowUtc ELSE NULL END
            WHERE clip_id = $clipId;
            """;
        cmd.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("$nowUtc", ToEngineIsoFromDateTime(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$clipId", clipId);
        cmd.ExecuteNonQuery();
    }

    public int MarkClipsCompleted(IEnumerable<string> clipIds, bool completed)
    {
        var ids = clipIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0)
        {
            return 0;
        }

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            """
            UPDATE clips
            SET completed = $completed,
                completed_at_utc = CASE WHEN $completed = 1 THEN $nowUtc ELSE NULL END
            WHERE clip_id = $clipId;
            """;
        cmd.Parameters.Add("$completed", SqliteType.Integer);
        cmd.Parameters.Add("$nowUtc", SqliteType.Text);
        cmd.Parameters.Add("$clipId", SqliteType.Text);

        var updated = 0;
        foreach (var clipId in ids)
        {
            cmd.Parameters["$completed"].Value = completed ? 1 : 0;
            cmd.Parameters["$nowUtc"].Value = ToEngineIsoFromDateTime(DateTimeOffset.UtcNow);
            cmd.Parameters["$clipId"].Value = clipId;
            updated += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return updated;
    }

    public void MarkUpToClipCompleted(string clipId)
    {
        using var conn = OpenConnection();
        using var lookup = conn.CreateCommand();
        lookup.CommandText =
            """
            SELECT root_id, site, dcim, session, created_time_utc, clip_name
            FROM clips
            WHERE clip_id = $clipId;
            """;
        lookup.Parameters.AddWithValue("$clipId", clipId);
        using var reader = lookup.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        var rootId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
        var site = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
        var dcim = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
        var session = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var created = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var clipName = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
        reader.Close();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            UPDATE clips
            SET completed = 1,
                completed_at_utc = $nowUtc
            WHERE IFNULL(root_id, '') = $rootId
              AND IFNULL(site, '') = $site
              AND IFNULL(dcim, '') = $dcim
              AND IFNULL(session, '') = $session
              AND (
                datetime(IFNULL(created_time_utc, '')) < datetime($created)
                OR (
                    datetime(IFNULL(created_time_utc, '')) = datetime($created)
                    AND IFNULL(clip_name, '') <= $clipName
                )
              );
            """;
        cmd.Parameters.AddWithValue("$rootId", rootId);
        cmd.Parameters.AddWithValue("$site", site);
        cmd.Parameters.AddWithValue("$dcim", dcim);
        cmd.Parameters.AddWithValue("$session", session);
        cmd.Parameters.AddWithValue("$created", created);
        cmd.Parameters.AddWithValue("$clipName", clipName);
        cmd.Parameters.AddWithValue("$nowUtc", ToEngineIsoFromDateTime(DateTimeOffset.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<FrameMarker> GetFrames(string clipId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT frame_time_sec, max_conf_frame
            FROM frames
            WHERE clip_id = $clipId
            ORDER BY frame_time_sec;
            """;
        cmd.Parameters.AddWithValue("$clipId", clipId);

        var rows = new List<FrameMarker>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(
                new FrameMarker
                {
                    FrameTimeSec = reader.GetDouble(0),
                    MaxConfFrame = reader.GetDouble(1)
                });
        }

        return rows;
    }

    public IReadOnlyList<DetectionRecord> GetDetections(string clipId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT frame_time_sec, cls_id, cls_label, conf, x, y, w, h, IFNULL(area_frac, 0)
            FROM detections
            WHERE clip_id = $clipId
            ORDER BY frame_time_sec, conf DESC;
            """;
        cmd.Parameters.AddWithValue("$clipId", clipId);

        var rows = new List<DetectionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(
                new DetectionRecord
                {
                    FrameTimeSec = reader.GetDouble(0),
                    ClassId = reader.GetInt32(1),
                    ClassLabel = reader.IsDBNull(2) ? null : reader.GetString(2),
                    Conf = reader.GetDouble(3),
                    X = reader.GetDouble(4),
                    Y = reader.GetDouble(5),
                    W = reader.GetDouble(6),
                    H = reader.GetDouble(7),
                    AreaFrac = reader.GetDouble(8)
                });
        }

        return rows;
    }

    public ReefBoundary? GetSessionBoundary(string scopeId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT scope_type, scope_id, IFNULL(clip_id_reference, ''), points_json, updated_at_utc
            FROM reef_boundaries
            WHERE scope_type = 'session' AND scope_id = $scopeId;
            """;
        cmd.Parameters.AddWithValue("$scopeId", scopeId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ReefBoundary
        {
            ScopeType = reader.GetString(0),
            ScopeId = reader.GetString(1),
            ClipIdReference = reader.GetString(2),
            PointsJson = reader.GetString(3),
            UpdatedAtUtc = reader.GetString(4)
        };
    }

    public void SaveSessionBoundary(string scopeId, string clipIdReference, string pointsJson)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO reef_boundaries(scope_type, scope_id, clip_id_reference, points_json, updated_at_utc)
            VALUES('session', $scopeId, $clipIdReference, $pointsJson, $updatedAtUtc)
            ON CONFLICT(scope_type, scope_id) DO UPDATE SET
                clip_id_reference = excluded.clip_id_reference,
                points_json = excluded.points_json,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$scopeId", scopeId);
        cmd.Parameters.AddWithValue("$clipIdReference", clipIdReference);
        cmd.Parameters.AddWithValue("$pointsJson", pointsJson);
        cmd.Parameters.AddWithValue("$updatedAtUtc", ToEngineIsoFromDateTime(DateTimeOffset.UtcNow));
        cmd.ExecuteNonQuery();
    }

    public void ClearSessionBoundary(string scopeId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            DELETE FROM reef_boundaries
            WHERE scope_type = 'session' AND scope_id = $scopeId;
            """;
        cmd.Parameters.AddWithValue("$scopeId", scopeId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<ClipTimelineItem> GetExportCandidates(ScopeFilter scope, RankThresholds thresholds, double minConfidence)
    {
        return GetTimeline(scope, thresholds)
            .Where(x => x.Processed && !x.Completed && x.MaxConf >= minConfidence)
            .ToList();
    }

    public void TrimDatabaseToClipIds(string destinationDbPath, IReadOnlyCollection<string> clipIds)
    {
        if (clipIds.Count == 0)
        {
            return;
        }

        using var conn = OpenConnection(destinationDbPath);
        using var tx = conn.BeginTransaction();

        using var tempTable = conn.CreateCommand();
        tempTable.Transaction = tx;
        tempTable.CommandText = "CREATE TEMP TABLE keep_clips(clip_id TEXT PRIMARY KEY);";
        tempTable.ExecuteNonQuery();

        foreach (var clipId in clipIds)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO keep_clips(clip_id) VALUES($clipId);";
            insert.Parameters.AddWithValue("$clipId", clipId);
            insert.ExecuteNonQuery();
        }

        conn.Execute("DELETE FROM frames WHERE clip_id NOT IN (SELECT clip_id FROM keep_clips);", tx);
        conn.Execute("DELETE FROM detections WHERE clip_id NOT IN (SELECT clip_id FROM keep_clips);", tx);
        conn.Execute("DELETE FROM clips WHERE clip_id NOT IN (SELECT clip_id FROM keep_clips);", tx);
        conn.Execute("DELETE FROM reef_boundaries WHERE scope_type='session' AND scope_id NOT IN (SELECT DISTINCT root_id || '|' || site || '|' || dcim || '|' || session FROM clips);", tx);
        conn.Execute("DELETE FROM clip_roots WHERE root_id NOT IN (SELECT DISTINCT root_id FROM clips WHERE root_id IS NOT NULL AND root_id <> '');", tx);

        tx.Commit();
    }

    public static string ComputeClipId(string clipPath, long fileSize, string fileMtimeUtc)
    {
        var normalizedPath = Path.GetFullPath(clipPath).Trim().ToLowerInvariant();
        var payload = $"{normalizedPath}|{fileSize}|{fileMtimeUtc}";
        return Sha1(payload);
    }

    public static string ToEngineIsoFromDateTime(DateTimeOffset utcDateTime)
    {
        var utc = utcDateTime.ToUniversalTime();
        var micros = (utc.Ticks % TimeSpan.TicksPerSecond) / 10;
        var baseText = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        if (micros == 0)
        {
            return $"{baseText}+00:00";
        }

        return $"{baseText}.{micros:D6}+00:00";
    }

    private static DateTimeOffset TryParseUtc(string raw)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return DateTimeOffset.MinValue;
    }

    private static HashSet<string> GetBoundarySessionScopes(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT scope_id FROM reef_boundaries WHERE scope_type='session';";
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }

        return set;
    }

    private static void IncrementMetrics(HierarchyNode node, bool processed, bool completed)
    {
        node.TotalCount++;
        if (processed)
        {
            node.ProcessedCount++;
        }

        if (completed)
        {
            node.CompletedCount++;
        }

        if (!completed && !processed)
        {
            node.RemainingToProcessCount++;
        }
    }

    private static HierarchyNode EnsureChild(HierarchyNode parent, TreeNodeType type, string name, ScopeFilter scope)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? "(unknown)" : name;
        var existing = parent.Children.FirstOrDefault(x => x.NodeType == type && x.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var child = new HierarchyNode
        {
            NodeId = $"{type}:{scope.RootId}:{scope.Site}:{scope.Dcim}:{scope.Session}",
            NodeType = type,
            Name = displayName,
            Scope = scope
        };
        parent.Children.Add(child);
        return child;
    }

    private static void SortChildrenRecursively(HierarchyNode node)
    {
        node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var child in node.Children)
        {
            SortChildrenRecursively(child);
        }
    }

    private static string BuildScopeWhere(ScopeFilter scope, SqliteCommand cmd)
    {
        var clauses = new List<string> { "IFNULL(root_id, '') = $rootId" };
        cmd.Parameters.AddWithValue("$rootId", scope.RootId);

        if (scope.Type is TreeNodeType.Site or TreeNodeType.Dcim or TreeNodeType.Session)
        {
            clauses.Add("IFNULL(site, '') = $site");
            cmd.Parameters.AddWithValue("$site", scope.Site ?? string.Empty);
        }

        if (scope.Type is TreeNodeType.Dcim or TreeNodeType.Session)
        {
            clauses.Add("IFNULL(dcim, '') = $dcim");
            cmd.Parameters.AddWithValue("$dcim", scope.Dcim ?? string.Empty);
        }

        if (scope.Type is TreeNodeType.Session)
        {
            clauses.Add("IFNULL(session, '') = $session");
            cmd.Parameters.AddWithValue("$session", scope.Session ?? string.Empty);
        }

        return "WHERE " + string.Join(" AND ", clauses);
    }

    private SqliteConnection OpenConnection(string? dbPath = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath ?? _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }

    private static string Sha1(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string columnName, string sqlType)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        conn.Execute($"ALTER TABLE {table} ADD COLUMN {columnName} {sqlType};");
    }

    private static int MergeDuplicateClipsByPathInternal(SqliteConnection conn, SqliteTransaction tx, string clipPath)
    {
        using var select = conn.CreateCommand();
        select.Transaction = tx;
        select.CommandText =
            """
            SELECT
                clip_id, IFNULL(root_id, ''), IFNULL(site, ''), IFNULL(dcim, ''), IFNULL(session, ''),
                IFNULL(clip_name, ''), clip_path, IFNULL(created_time_utc, ''), IFNULL(file_mtime_utc, ''),
                IFNULL(file_size, 0), duration_sec, width, height, video_fps,
                temp_f, temp_c, IFNULL(bar_date, ''), IFNULL(bar_time, ''),
                IFNULL(processed, 0), processed_fps, max_conf, max_conf_time_sec, max_conf_cls_id, max_conf_label,
                IFNULL(completed, 0), IFNULL(completed_at_utc, '')
            FROM clips
            WHERE clip_path = $clipPath
            ORDER BY clip_id;
            """;
        select.Parameters.AddWithValue("$clipPath", clipPath);

        var rows = new List<ClipMergeRow>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(
                    new ClipMergeRow
                    {
                        ClipId = reader.GetString(0),
                        RootId = reader.GetString(1),
                        Site = reader.GetString(2),
                        Dcim = reader.GetString(3),
                        Session = reader.GetString(4),
                        ClipName = reader.GetString(5),
                        ClipPath = reader.GetString(6),
                        CreatedTimeUtc = reader.GetString(7),
                        FileMtimeUtc = reader.GetString(8),
                        FileSize = reader.GetInt64(9),
                        DurationSec = reader.IsDBNull(10) ? null : reader.GetDouble(10),
                        Width = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                        Height = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                        VideoFps = reader.IsDBNull(13) ? null : reader.GetDouble(13),
                        TempF = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                        TempC = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                        BarDate = reader.GetString(16),
                        BarTime = reader.GetString(17),
                        Processed = reader.GetInt32(18) == 1,
                        ProcessedFps = reader.IsDBNull(19) ? null : reader.GetDouble(19),
                        MaxConf = reader.IsDBNull(20) ? null : reader.GetDouble(20),
                        MaxConfTimeSec = reader.IsDBNull(21) ? null : reader.GetDouble(21),
                        MaxConfClsId = reader.IsDBNull(22) ? null : reader.GetInt32(22),
                        MaxConfLabel = reader.IsDBNull(23) ? null : reader.GetString(23),
                        Completed = reader.GetInt32(24) == 1,
                        CompletedAtUtc = reader.GetString(25)
                    });
            }
        }

        if (rows.Count <= 1)
        {
            return 0;
        }

        var keep = rows
            .OrderByDescending(ClipMergeScore)
            .ThenByDescending(x => x.Processed ? 1 : 0)
            .ThenByDescending(x => x.Completed ? 1 : 0)
            .ThenBy(x => x.ClipId, StringComparer.Ordinal)
            .First();

        var maxConfSource = rows
            .Where(x => x.MaxConf.HasValue)
            .OrderByDescending(x => x.MaxConf!.Value)
            .FirstOrDefault();

        var merged = new ClipMergeRow
        {
            ClipId = keep.ClipId,
            RootId = FirstNonEmpty(keep.RootId, rows.Select(x => x.RootId)),
            Site = FirstNonEmpty(keep.Site, rows.Select(x => x.Site)),
            Dcim = FirstNonEmpty(keep.Dcim, rows.Select(x => x.Dcim)),
            Session = FirstNonEmpty(keep.Session, rows.Select(x => x.Session)),
            ClipName = FirstNonEmpty(keep.ClipName, rows.Select(x => x.ClipName)),
            ClipPath = keep.ClipPath,
            CreatedTimeUtc = FirstNonEmpty(keep.CreatedTimeUtc, rows.Select(x => x.CreatedTimeUtc)),
            FileMtimeUtc = FirstNonEmpty(keep.FileMtimeUtc, rows.Select(x => x.FileMtimeUtc)),
            FileSize = keep.FileSize > 0 ? keep.FileSize : rows.Max(x => x.FileSize),
            DurationSec = keep.DurationSec ?? rows.Select(x => x.DurationSec).FirstOrDefault(x => x.HasValue),
            Width = keep.Width ?? rows.Select(x => x.Width).FirstOrDefault(x => x.HasValue),
            Height = keep.Height ?? rows.Select(x => x.Height).FirstOrDefault(x => x.HasValue),
            VideoFps = keep.VideoFps ?? rows.Select(x => x.VideoFps).FirstOrDefault(x => x.HasValue),
            TempF = keep.TempF ?? rows.Select(x => x.TempF).FirstOrDefault(x => x.HasValue),
            TempC = keep.TempC ?? rows.Select(x => x.TempC).FirstOrDefault(x => x.HasValue),
            BarDate = FirstNonEmpty(keep.BarDate, rows.Select(x => x.BarDate)),
            BarTime = FirstNonEmpty(keep.BarTime, rows.Select(x => x.BarTime)),
            Processed = rows.Any(x => x.Processed),
            ProcessedFps = keep.ProcessedFps ?? rows.Select(x => x.ProcessedFps).FirstOrDefault(x => x.HasValue),
            MaxConf = maxConfSource?.MaxConf,
            MaxConfTimeSec = maxConfSource?.MaxConfTimeSec,
            MaxConfClsId = maxConfSource?.MaxConfClsId,
            MaxConfLabel = maxConfSource?.MaxConfLabel,
            Completed = rows.Any(x => x.Completed),
            CompletedAtUtc = FirstNonEmpty(keep.CompletedAtUtc, rows.Select(x => x.CompletedAtUtc))
        };

        var removed = 0;
        foreach (var row in rows.Where(x => !x.ClipId.Equals(keep.ClipId, StringComparison.OrdinalIgnoreCase)))
        {
            using (var moveFrames = conn.CreateCommand())
            {
                moveFrames.Transaction = tx;
                moveFrames.CommandText = "UPDATE OR IGNORE frames SET clip_id = $keepId WHERE clip_id = $dupId;";
                moveFrames.Parameters.AddWithValue("$keepId", keep.ClipId);
                moveFrames.Parameters.AddWithValue("$dupId", row.ClipId);
                moveFrames.ExecuteNonQuery();
            }

            using (var moveDetections = conn.CreateCommand())
            {
                moveDetections.Transaction = tx;
                moveDetections.CommandText = "UPDATE OR IGNORE detections SET clip_id = $keepId WHERE clip_id = $dupId;";
                moveDetections.Parameters.AddWithValue("$keepId", keep.ClipId);
                moveDetections.Parameters.AddWithValue("$dupId", row.ClipId);
                moveDetections.ExecuteNonQuery();
            }

            using (var updateBoundary = conn.CreateCommand())
            {
                updateBoundary.Transaction = tx;
                updateBoundary.CommandText = "UPDATE reef_boundaries SET clip_id_reference = $keepId WHERE clip_id_reference = $dupId;";
                updateBoundary.Parameters.AddWithValue("$keepId", keep.ClipId);
                updateBoundary.Parameters.AddWithValue("$dupId", row.ClipId);
                updateBoundary.ExecuteNonQuery();
            }

            using (var deleteFrames = conn.CreateCommand())
            {
                deleteFrames.Transaction = tx;
                deleteFrames.CommandText = "DELETE FROM frames WHERE clip_id = $dupId;";
                deleteFrames.Parameters.AddWithValue("$dupId", row.ClipId);
                deleteFrames.ExecuteNonQuery();
            }

            using (var deleteDetections = conn.CreateCommand())
            {
                deleteDetections.Transaction = tx;
                deleteDetections.CommandText = "DELETE FROM detections WHERE clip_id = $dupId;";
                deleteDetections.Parameters.AddWithValue("$dupId", row.ClipId);
                deleteDetections.ExecuteNonQuery();
            }

            using (var deleteClip = conn.CreateCommand())
            {
                deleteClip.Transaction = tx;
                deleteClip.CommandText = "DELETE FROM clips WHERE clip_id = $dupId;";
                deleteClip.Parameters.AddWithValue("$dupId", row.ClipId);
                removed += deleteClip.ExecuteNonQuery();
            }
        }

        using (var updateKeep = conn.CreateCommand())
        {
            updateKeep.Transaction = tx;
            updateKeep.CommandText =
                """
                UPDATE clips
                SET root_id = $rootId,
                    site = $site,
                    dcim = $dcim,
                    session = $session,
                    clip_name = $clipName,
                    clip_path = $clipPath,
                    created_time_utc = $createdTimeUtc,
                    file_mtime_utc = $fileMtimeUtc,
                    file_size = $fileSize,
                    duration_sec = $durationSec,
                    width = $width,
                    height = $height,
                    video_fps = $videoFps,
                    temp_f = $tempF,
                    temp_c = $tempC,
                    bar_date = $barDate,
                    bar_time = $barTime,
                    processed = $processed,
                    processed_fps = $processedFps,
                    max_conf = $maxConf,
                    max_conf_time_sec = $maxConfTimeSec,
                    max_conf_cls_id = $maxConfClsId,
                    max_conf_label = $maxConfLabel,
                    completed = $completed,
                    completed_at_utc = $completedAtUtc
                WHERE clip_id = $clipId;
                """;
            updateKeep.Parameters.AddWithValue("$rootId", merged.RootId);
            updateKeep.Parameters.AddWithValue("$site", merged.Site);
            updateKeep.Parameters.AddWithValue("$dcim", merged.Dcim);
            updateKeep.Parameters.AddWithValue("$session", merged.Session);
            updateKeep.Parameters.AddWithValue("$clipName", merged.ClipName);
            updateKeep.Parameters.AddWithValue("$clipPath", merged.ClipPath);
            updateKeep.Parameters.AddWithValue("$createdTimeUtc", merged.CreatedTimeUtc);
            updateKeep.Parameters.AddWithValue("$fileMtimeUtc", merged.FileMtimeUtc);
            updateKeep.Parameters.AddWithValue("$fileSize", merged.FileSize);
            updateKeep.Parameters.AddWithValue("$durationSec", (object?)merged.DurationSec ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$width", (object?)merged.Width ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$height", (object?)merged.Height ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$videoFps", (object?)merged.VideoFps ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$tempF", (object?)merged.TempF ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$tempC", (object?)merged.TempC ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$barDate", merged.BarDate);
            updateKeep.Parameters.AddWithValue("$barTime", merged.BarTime);
            updateKeep.Parameters.AddWithValue("$processed", merged.Processed ? 1 : 0);
            updateKeep.Parameters.AddWithValue("$processedFps", (object?)merged.ProcessedFps ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$maxConf", (object?)merged.MaxConf ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$maxConfTimeSec", (object?)merged.MaxConfTimeSec ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$maxConfClsId", (object?)merged.MaxConfClsId ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$maxConfLabel", (object?)merged.MaxConfLabel ?? DBNull.Value);
            updateKeep.Parameters.AddWithValue("$completed", merged.Completed ? 1 : 0);
            updateKeep.Parameters.AddWithValue("$completedAtUtc", merged.Completed ? merged.CompletedAtUtc : string.Empty);
            updateKeep.Parameters.AddWithValue("$clipId", merged.ClipId);
            updateKeep.ExecuteNonQuery();
        }

        return removed;
    }

    private static int ClipMergeScore(ClipMergeRow row)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(row.CreatedTimeUtc))
        {
            score += 16;
        }

        if (!string.IsNullOrWhiteSpace(row.RootId))
        {
            score += 8;
        }

        if (!string.IsNullOrWhiteSpace(row.Site))
        {
            score += 4;
        }

        if (!string.IsNullOrWhiteSpace(row.Dcim))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(row.Session))
        {
            score += 2;
        }

        if (!string.IsNullOrWhiteSpace(row.ClipName))
        {
            score += 2;
        }

        return score;
    }

    private static string FirstNonEmpty(string preferred, IEnumerable<string> values)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred;
        }

        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
    }

    private sealed class ClipMergeRow
    {
        public string ClipId { get; init; } = string.Empty;
        public string RootId { get; init; } = string.Empty;
        public string Site { get; init; } = string.Empty;
        public string Dcim { get; init; } = string.Empty;
        public string Session { get; init; } = string.Empty;
        public string ClipName { get; init; } = string.Empty;
        public string ClipPath { get; init; } = string.Empty;
        public string CreatedTimeUtc { get; init; } = string.Empty;
        public string FileMtimeUtc { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public double? DurationSec { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public double? VideoFps { get; init; }
        public int? TempF { get; init; }
        public int? TempC { get; init; }
        public string BarDate { get; init; } = string.Empty;
        public string BarTime { get; init; } = string.Empty;
        public bool Processed { get; init; }
        public double? ProcessedFps { get; init; }
        public double? MaxConf { get; init; }
        public double? MaxConfTimeSec { get; init; }
        public int? MaxConfClsId { get; init; }
        public string? MaxConfLabel { get; init; }
        public bool Completed { get; init; }
        public string CompletedAtUtc { get; init; } = string.Empty;
    }
}

internal static class SqliteExtensions
{
    public static void Execute(this SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
