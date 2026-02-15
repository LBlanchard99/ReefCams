using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ReefCams.Core;

public sealed class ExportBuilder
{
    private readonly ReefCamsRepository _repository;

    public ExportBuilder(ReefCamsRepository repository)
    {
        _repository = repository;
    }

    public ExportResult BuildExport(ProjectPaths projectPaths, AppConfig config, ExportRequest request, string? viewerSourceDirectory)
    {
        return BuildExport(projectPaths, config, request, viewerSourceDirectory, cancellationToken: CancellationToken.None, progress: null);
    }

    public ExportResult BuildExport(
        ProjectPaths projectPaths,
        AppConfig config,
        ExportRequest request,
        string? viewerSourceDirectory,
        CancellationToken cancellationToken,
        IProgress<int>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clips = _repository.GetExportCandidates(request.Scope, request.MinimumConfidence);
        if (clips.Count == 0)
        {
            throw new InvalidOperationException("No clips matched the export criteria.");
        }

        var destination = Path.GetFullPath(request.DestinationDirectory);
        var clipsDir = Path.Combine(destination, "clips");
        var dataDir = Path.Combine(destination, "data");
        var configDir = Path.Combine(destination, "config");

        Directory.CreateDirectory(destination);
        Directory.CreateDirectory(clipsDir);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(configDir);

        var copied = 0;
        var clipPathById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.Combine("clips", clip.Site, clip.Dcim, clip.Session, clip.ClipName);
            var targetPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? clipsDir);
            var sourcePath = ResolveClipSourcePath(projectPaths.RootDirectory, clip.ClipPath);
            File.Copy(sourcePath, targetPath, overwrite: true);
            clipPathById[clip.ClipId] = relative;
            copied++;
            progress?.Report(copied);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var exportedDb = Path.Combine(dataDir, "app.db");
        File.Copy(projectPaths.DbPath, exportedDb, overwrite: true);
        var exportedClipIds = clips.Select(x => x.ClipId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _repository.TrimDatabaseToClipIds(exportedDb, exportedClipIds);
        RewriteClipPaths(exportedDb, clipPathById);

        var exportedConfig = new AppConfig
        {
            Timeline = config.Timeline,
            Processing = config.Processing
        };
        var configJson = JsonSerializer.Serialize(exportedConfig, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(Path.Combine(configDir, "config.json"), configJson);

        if (request.IncludeViewerFiles && !string.IsNullOrWhiteSpace(viewerSourceDirectory) && Directory.Exists(viewerSourceDirectory))
        {
            CopyDirectory(
                viewerSourceDirectory,
                destination,
                cancellationToken,
                excludeTopLevelNames: ["data", "config", "clips", "exports", "logs"]);
        }

        return new ExportResult
        {
            ExportDirectory = destination,
            ExportedClipCount = clips.Count
        };
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? excludeTopLevelNames = null)
    {
        var src = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dst = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var excludes = new HashSet<string>(
            excludeTopLevelNames ?? [],
            StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, dir);
            if (IsExcludedTopLevelPath(relative, excludes))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, file);
            if (IsExcludedTopLevelPath(relative, excludes))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static bool IsExcludedTopLevelPath(string relativePath, ISet<string> excludes)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || excludes.Count == 0)
        {
            return false;
        }

        var firstSegment = relativePath
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return firstSegment is not null && excludes.Contains(firstSegment);
    }

    private static string ResolveClipSourcePath(string projectRootDirectory, string clipPath)
    {
        if (Path.IsPathRooted(clipPath))
        {
            return clipPath;
        }

        return Path.GetFullPath(Path.Combine(projectRootDirectory, clipPath));
    }

    private static void RewriteClipPaths(string dbPath, IReadOnlyDictionary<string, string> clipPathById)
    {
        if (clipPathById.Count == 0)
        {
            return;
        }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true
        }.ToString());
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE clips SET clip_path = $clipPath WHERE clip_id = $clipId;";
        var clipPathParam = cmd.CreateParameter();
        clipPathParam.ParameterName = "$clipPath";
        cmd.Parameters.Add(clipPathParam);
        var clipIdParam = cmd.CreateParameter();
        clipIdParam.ParameterName = "$clipId";
        cmd.Parameters.Add(clipIdParam);

        foreach (var entry in clipPathById)
        {
            clipIdParam.Value = entry.Key;
            clipPathParam.Value = entry.Value.Replace('\\', '/');
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
