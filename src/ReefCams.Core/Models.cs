using System.Globalization;

namespace ReefCams.Core;

public enum TreeNodeType
{
    Root,
    Site,
    Dcim,
    Session
}

public readonly record struct ScopeFilter(
    TreeNodeType Type,
    string RootId,
    string? Site = null,
    string? Dcim = null,
    string? Session = null);

public sealed class ClipRoot
{
    public string RootId { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public string DisplayName => Path.GetFileName(RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

public sealed class HierarchyNode
{
    public string NodeId { get; init; } = string.Empty;
    public TreeNodeType NodeType { get; init; }
    public string Name { get; init; } = string.Empty;
    public ScopeFilter Scope { get; init; }
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
    public bool HasBoundary { get; set; }
    public int TotalCount { get; set; }
    public int CompletedCount { get; set; }
    public int ProcessedCount { get; set; }
    public int RemainingToProcessCount { get; set; }
    public int PositiveCount { get; set; }
    public int NegativeCount { get; set; }
    public double ConfidenceThreshold { get; set; } = 0.4;
    public List<HierarchyNode> Children { get; } = [];
    public int RemainingCount => Math.Max(0, TotalCount - CompletedCount);
    private string RemainingToProcessLabel => RemainingToProcessCount > 0 ? $", {RemainingToProcessCount} to process" : string.Empty;
    public string Label =>
        $"{Name} ({CompletedCount}/{TotalCount} completed, {ProcessedCount}/{TotalCount} processed{RemainingToProcessLabel}, {PositiveCount} positive, {NegativeCount} negative @ conf>={ConfidenceThreshold.ToString("0.###", CultureInfo.InvariantCulture)}){(HasBoundary ? " [boundary]" : string.Empty)}";
}

public sealed class IndexedClip
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
}

public sealed class ClipTimelineItem
{
    public string ClipId { get; init; } = string.Empty;
    public string RootId { get; init; } = string.Empty;
    public string Site { get; init; } = string.Empty;
    public string Dcim { get; init; } = string.Empty;
    public string Session { get; init; } = string.Empty;
    public string ClipName { get; init; } = string.Empty;
    public string ClipPath { get; init; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; init; }
    public bool Processed { get; init; }
    public bool Completed { get; init; }
    public double MaxConf { get; init; }
    public double? MaxConfTimeSec { get; init; }
    public double? DeltaFromPreviousSec { get; set; }
}

public sealed class FrameMarker
{
    public double FrameTimeSec { get; init; }
    public double MaxConfFrame { get; init; }
}

public sealed class DetectionRecord
{
    public double FrameTimeSec { get; init; }
    public int ClassId { get; init; }
    public string? ClassLabel { get; init; }
    public double Conf { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public double AreaFrac { get; init; }
}

public sealed class ReefBoundary
{
    public string ScopeType { get; init; } = "session";
    public string ScopeId { get; init; } = string.Empty;
    public string ClipIdReference { get; init; } = string.Empty;
    public string PointsJson { get; init; } = "[]";
    public string UpdatedAtUtc { get; init; } = string.Empty;
}

public sealed class BenchmarkRecord
{
    public string RunAtUtc { get; init; } = string.Empty;
    public string ProviderRequested { get; init; } = string.Empty;
    public string ProviderUsed { get; init; } = string.Empty;
    public double Fps { get; init; }
    public double AvgInferMs { get; init; }
    public double P95InferMs { get; init; }
    public double TotalMs { get; init; }
    public double EstimatePer10sSec { get; init; }
}

public sealed class IndexProgress
{
    public int ProcessedCount { get; init; }
    public string CurrentPath { get; init; } = string.Empty;
}

public sealed class ExportRequest
{
    public ScopeFilter Scope { get; init; }
    public string DestinationDirectory { get; init; } = string.Empty;
    public double MinimumConfidence { get; init; }
    public bool IncludeViewerFiles { get; init; } = true;
}

public sealed class ExportResult
{
    public string ExportDirectory { get; init; } = string.Empty;
    public int ExportedClipCount { get; init; }
}

public static class ScopeIds
{
    public static string Session(string rootId, string site, string dcim, string session) => $"{rootId}|{site}|{dcim}|{session}";
}
