namespace ReefCams.Core;

public sealed class ClipIndexer
{
    private readonly ReefCamsRepository _repository;
    private readonly FfprobeCreationTimeReader _creationTimeReader;

    public ClipIndexer(ReefCamsRepository repository)
    {
        _repository = repository;
        _creationTimeReader = new FfprobeCreationTimeReader();
    }

    public Task<int> IndexClipRootAsync(string rootPath, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                if (!Directory.Exists(rootPath))
                {
                    throw new DirectoryNotFoundException($"Clip root not found: {rootPath}");
                }

                var normalizedRoot = Path.GetFullPath(rootPath);
                var rootId = _repository.UpsertClipRoot(normalizedRoot);

                var processed = 0;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false
                };

                foreach (var file in Directory.EnumerateFiles(normalizedRoot, "*.*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!file.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var clip = BuildIndexedClip(file, normalizedRoot, rootId, cancellationToken);
                    _repository.UpsertIndexedClip(clip);

                    processed++;
                    progress?.Report(new IndexProgress { ProcessedCount = processed, CurrentPath = file });
                }

                return processed;
            },
            cancellationToken);
    }

    private IndexedClip BuildIndexedClip(string filePath, string rootPath, string rootId, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(filePath);
        var rel = Path.GetRelativePath(rootPath, fullPath);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var site = parts.Length >= 2 ? parts[0] : string.Empty;
        var dcim = parts.Length >= 3 ? parts[1] : string.Empty;
        var session = parts.Length >= 4 ? parts[2] : string.Empty;

        var info = new FileInfo(fullPath);
        var fallbackCreatedUtc = info.CreationTimeUtc == DateTime.MinValue ? info.LastWriteTimeUtc : info.CreationTimeUtc;
        var ffprobeCreatedUtc = _creationTimeReader.TryReadCreationTimeUtc(fullPath, cancellationToken);
        var createdUtc = ffprobeCreatedUtc ?? new DateTimeOffset(fallbackCreatedUtc, TimeSpan.Zero);
        var createdText = ReefCamsRepository.ToEngineIsoFromDateTime(createdUtc);
        var mtimeText = ReefCamsRepository.ToEngineIsoFromDateTime(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        var clipId = ReefCamsRepository.ComputeClipId(fullPath, info.Length, mtimeText);

        return new IndexedClip
        {
            ClipId = clipId,
            RootId = rootId,
            Site = site,
            Dcim = dcim,
            Session = session,
            ClipName = info.Name,
            ClipPath = fullPath,
            CreatedTimeUtc = createdText,
            FileMtimeUtc = mtimeText,
            FileSize = info.Length
        };
    }
}
