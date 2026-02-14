using System.Text.Json;

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
        var clips = _repository.GetExportCandidates(request.Scope, config.RankThresholds, request.MinimumConfidence);
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
        foreach (var clip in clips)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.Combine(clip.Site, clip.Dcim, clip.Session, clip.ClipName);
            var targetPath = Path.Combine(clipsDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? clipsDir);
            File.Copy(clip.ClipPath, targetPath, overwrite: true);
            copied++;
            progress?.Report(copied);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var exportedDb = Path.Combine(dataDir, "exported.db");
        File.Copy(projectPaths.DbPath, exportedDb, overwrite: true);
        _repository.TrimDatabaseToClipIds(exportedDb, clips.Select(x => x.ClipId).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var exportedConfig = new AppConfig
        {
            RankThresholds = config.RankThresholds,
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
            CopyDirectory(viewerSourceDirectory, destination, cancellationToken);
        }

        return new ExportResult
        {
            ExportDirectory = destination,
            ExportedClipCount = clips.Count
        };
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        var src = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dst = Path.GetFullPath(destinationDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, dir);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }
}
