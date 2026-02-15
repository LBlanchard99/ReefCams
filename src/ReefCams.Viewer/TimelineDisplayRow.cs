using ReefCams.Core;

namespace ReefCams.Viewer;

public sealed class TimelineDisplayRow
{
    public bool IsGapRow { get; init; }
    public string GapGroupId { get; init; } = string.Empty;
    public string GapSummary { get; init; } = string.Empty;
    public string CreatedText { get; init; } = string.Empty;
    public string ClipName { get; init; } = string.Empty;
    public string MaxConfText { get; init; } = string.Empty;
    public bool Processed { get; init; }
    public bool Completed { get; init; }
    public string DeltaText { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public ClipTimelineItem? Clip { get; init; }
}
