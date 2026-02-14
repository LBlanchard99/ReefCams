using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ReefCams.Core;

internal sealed class FfprobeCreationTimeReader
{
    private readonly string? _ffprobePath;

    public FfprobeCreationTimeReader(string? ffprobePath = null)
    {
        _ffprobePath = ResolveFfprobePath(ffprobePath);
    }

    public DateTimeOffset? TryReadCreationTimeUtc(string clipPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clipPath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(_ffprobePath) || !File.Exists(_ffprobePath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffprobePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("quiet");
            startInfo.ArgumentList.Add("-print_format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("format_tags=creation_time:stream_tags=creation_time");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(clipPath);

            using var process = new Process
            {
                StartInfo = startInfo
            };

            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            var stdout = stdoutTask.GetAwaiter().GetResult();
            stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            return ParseCreationTime(stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseCreationTime(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (TryReadCreationTimeFromNode(root, out var created))
        {
            return created;
        }

        if (root.TryGetProperty("format", out var format) && TryReadCreationTimeFromNode(format, out created))
        {
            return created;
        }

        if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var stream in streams.EnumerateArray())
        {
            if (TryReadCreationTimeFromNode(stream, out created))
            {
                return created;
            }
        }

        return null;
    }

    private static bool TryReadCreationTimeFromNode(JsonElement node, out DateTimeOffset createdUtc)
    {
        createdUtc = default;

        if (!node.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!tags.TryGetProperty("creation_time", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        createdUtc = parsed;
        return true;
    }

    private static string? ResolveFfprobePath(string? explicitPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        var envPath = Environment.GetEnvironmentVariable("REEFCAMS_FFPROBE_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            candidates.Add(envPath);
        }

        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDir, "ffprobe.exe"));
        candidates.Add(Path.Combine(baseDir, "engine", "ffprobe.exe"));

        var parent = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && parent is not null; i++)
        {
            candidates.Add(Path.Combine(parent.FullName, "engine_dist", "engine", "ffprobe.exe"));
            candidates.Add(Path.Combine(parent.FullName, "engine_src", "ffprobe.exe"));
            parent = parent.Parent;
        }

        foreach (var candidate in candidates
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
