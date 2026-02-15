using System.Text.Json;

namespace ReefCams.Core;

public sealed record ProjectPaths(
    string RootDirectory,
    string DataDirectory,
    string ConfigDirectory,
    string LogsDirectory,
    string ExportsDirectory)
{
    public string DbPath => Path.Combine(DataDirectory, "app.db");
    public string ConfigPath => Path.Combine(ConfigDirectory, "config.json");
    public string LogPath => Path.Combine(LogsDirectory, "app.log");

    public static ProjectPaths Create(string projectDirectory)
    {
        var root = Path.GetFullPath(projectDirectory);
        return new ProjectPaths(
            RootDirectory: root,
            DataDirectory: Path.Combine(root, "data"),
            ConfigDirectory: Path.Combine(root, "config"),
            LogsDirectory: Path.Combine(root, "logs"),
            ExportsDirectory: Path.Combine(root, "exports"));
    }

    public void EnsureExists()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }
}

public sealed class AppConfig
{
    public TimelineSettings Timeline { get; set; } = TimelineSettings.CreateDefault();
    public ProcessingSettings Processing { get; set; } = ProcessingSettings.CreateDefault();
}

public sealed class TimelineSettings
{
    public double MinVisibleConfidence { get; set; } = 0.4;
    public bool HideCompletedClips { get; set; } = true;
    public bool HideMissingClips { get; set; } = true;

    public static TimelineSettings CreateDefault() => new();
}

public sealed class ProcessingSettings
{
    public double Fps { get; set; } = 1.0;
    public string ProviderOrder { get; set; } = "DmlExecutionProvider,CPUExecutionProvider";
    public string ModelPath { get; set; } = @".\models\md_v1000_redwood_1280_static12.onnx";
    public double ConfThreshold { get; set; } = 0.001;
    public double MinAreaFrac { get; set; } = 0.0001;

    public static ProcessingSettings CreateDefault() => new();
}

public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppConfig LoadOrCreate(ProjectPaths paths)
    {
        paths.EnsureExists();
        if (!File.Exists(paths.ConfigPath))
        {
            var createdConfig = new AppConfig();
            Save(paths, createdConfig);
            return createdConfig;
        }

        var json = File.ReadAllText(paths.ConfigPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            var createdConfig = new AppConfig();
            Save(paths, createdConfig);
            return createdConfig;
        }

        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        config.Timeline ??= TimelineSettings.CreateDefault();
        config.Processing ??= ProcessingSettings.CreateDefault();

        if (double.IsNaN(config.Timeline.MinVisibleConfidence) ||
            double.IsInfinity(config.Timeline.MinVisibleConfidence) ||
            config.Timeline.MinVisibleConfidence < 0 ||
            config.Timeline.MinVisibleConfidence >= 1)
        {
            config.Timeline.MinVisibleConfidence = 0.4;
        }

        return config;
    }

    public void Save(ProjectPaths paths, AppConfig config)
    {
        paths.EnsureExists();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(paths.ConfigPath, json);
    }
}
