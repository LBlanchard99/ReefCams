using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using ReefCams.Core;

namespace ReefCams.Processor;

public sealed class EngineRunner
{
    private static readonly ConcurrentDictionary<string, bool> SupportsDisableBarMetadataByEnginePath = new(StringComparer.OrdinalIgnoreCase);

    public async Task RunProcessClipAsync(
        string engineExePath,
        string dbPath,
        string clipPath,
        ProcessingSettings processing,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        var supportsDisableBarMetadata = await SupportsDisableBarMetadataFlagAsync(engineExePath, cancellationToken).ConfigureAwait(false);
        if (!supportsDisableBarMetadata)
        {
            throw new InvalidOperationException("engine.exe does not support --disable-bar-metadata. Rebuild engine_dist from current engine_src.");
        }

        var args = BuildProcessArgs(dbPath, clipPath, processing, disableBarMetadata: true);
        await RunEngineAsync(engineExePath, args, statusProgress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRecord?> RunBenchmarkAsync(
        string engineExePath,
        string dbPath,
        string clipPath,
        ProcessingSettings processing,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        var supportsDisableBarMetadata = await SupportsDisableBarMetadataFlagAsync(engineExePath, cancellationToken).ConfigureAwait(false);
        if (!supportsDisableBarMetadata)
        {
            throw new InvalidOperationException("engine.exe does not support --disable-bar-metadata. Rebuild engine_dist from current engine_src.");
        }

        var args = BuildBenchmarkArgs(dbPath, clipPath, processing, disableBarMetadata: true);
        BenchmarkRecord? result = null;

        await RunEngineAsync(
            engineExePath,
            args,
            new Progress<string>(message =>
            {
                statusProgress?.Report(message);
                if (!message.StartsWith("{", StringComparison.Ordinal))
                {
                    return;
                }

                try
                {
                    using var doc = JsonDocument.Parse(message);
                    if (!doc.RootElement.TryGetProperty("type", out var typeElement) ||
                        !string.Equals(typeElement.GetString(), "benchmark_result", StringComparison.Ordinal))
                    {
                        return;
                    }

                    result = new BenchmarkRecord
                    {
                        RunAtUtc = ReefCamsRepository.ToEngineIsoFromDateTime(DateTimeOffset.UtcNow),
                        ProviderRequested = TryJoinProvider(doc.RootElement, "provider_requested"),
                        ProviderUsed = GetString(doc.RootElement, "provider_used"),
                        Fps = processing.Fps,
                        AvgInferMs = GetDouble(doc.RootElement, "avg_infer_ms"),
                        P95InferMs = GetDouble(doc.RootElement, "p95_infer_ms"),
                        TotalMs = GetDouble(doc.RootElement, "total_ms"),
                        EstimatePer10sSec = GetDouble(doc.RootElement, "estimate_per_10s_s")
                    };
                }
                catch
                {
                    // Keep progress robust even if one line cannot be parsed.
                }
            }),
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private static async Task RunEngineAsync(
        string engineExePath,
        string args,
        IProgress<string>? statusProgress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(engineExePath))
        {
            throw new FileNotFoundException($"engine.exe not found: {engineExePath}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = engineExePath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(engineExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();

        process.Start();
        using var cancellationRegistration = cancellationToken.Register(
            () =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore kill races.
                }
            });

        var outTask = Task.Run(
            async () =>
            {
                while (!process.StandardOutput.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        statusProgress?.Report(line.Trim());
                    }
                }
            },
            cancellationToken);

        var errTask = Task.Run(
            async () =>
            {
                while (!process.StandardError.EndOfStream)
                {
                    var line = await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        stderr.AppendLine(line);
                    }
                }
            },
            cancellationToken);

        try
        {
            await Task.WhenAll(outTask, errTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"engine.exe failed ({process.ExitCode}): {stderr}");
        }
    }

    private static string BuildProcessArgs(string dbPath, string clipPath, ProcessingSettings processing, bool disableBarMetadata)
    {
        var builder = new StringBuilder();
        builder.Append("process ");
        builder.Append("--clip ").Append(Quote(clipPath)).Append(' ');
        builder.Append("--fps ").Append(processing.Fps.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
        builder.Append("--db ").Append(Quote(dbPath)).Append(' ');
        builder.Append("--provider ").Append(Quote(processing.ProviderOrder)).Append(' ');
        if (disableBarMetadata)
        {
            builder.Append("--disable-bar-metadata ");
        }

        if (!string.IsNullOrWhiteSpace(processing.ModelPath))
        {
            builder.Append("--model ").Append(Quote(processing.ModelPath)).Append(' ');
        }

        return builder.ToString();
    }

    private static string BuildBenchmarkArgs(string dbPath, string clipPath, ProcessingSettings processing, bool disableBarMetadata)
    {
        var builder = new StringBuilder();
        builder.Append("benchmark ");
        builder.Append("--fps ").Append(processing.Fps.ToString("0.###", CultureInfo.InvariantCulture)).Append(' ');
        builder.Append("--clip ").Append(Quote(clipPath)).Append(' ');
        builder.Append("--db ").Append(Quote(dbPath)).Append(' ');
        builder.Append("--provider ").Append(Quote(processing.ProviderOrder)).Append(' ');
        if (disableBarMetadata)
        {
            builder.Append("--disable-bar-metadata ");
        }

        if (!string.IsNullOrWhiteSpace(processing.ModelPath))
        {
            builder.Append("--model ").Append(Quote(processing.ModelPath)).Append(' ');
        }

        return builder.ToString();
    }

    private static async Task<bool> SupportsDisableBarMetadataFlagAsync(string engineExePath, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(engineExePath);
        if (SupportsDisableBarMetadataByEnginePath.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = engineExePath,
                Arguments = "process --help",
                WorkingDirectory = Path.GetDirectoryName(engineExePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

            var helpText = $"{stdoutTask.Result}\n{stderrTask.Result}";
            var supported = helpText.Contains("--disable-bar-metadata", StringComparison.Ordinal);
            SupportsDisableBarMetadataByEnginePath.TryAdd(key, supported);
            return supported;
        }
        catch
        {
            SupportsDisableBarMetadataByEnginePath.TryAdd(key, false);
            return false;
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static double GetDouble(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : 0.0;
    }

    private static string TryJoinProvider(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var list = value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join(",", list);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return string.Empty;
    }
}
