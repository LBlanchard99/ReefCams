using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ReefCams.Core;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using Path = System.IO.Path;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace ReefCams.Processor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string RootGroupPrefix = "@group:";
    private const double BoundaryDragMinSpacingNorm = 0.003;
    private const double TimelineTextColumnExtraWidthPx = 5.0;
    private const double DetectionBoxPaddingPx = 4.0;
    private readonly ConfigService _configService = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly EngineRunner _engineRunner = new();
    private readonly Dictionary<string, List<ClipTimelineItem>> _hiddenGapGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rootDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rootPathById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _rootPresenceById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _clipPresenceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _scopePresenceByScopeKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Point> _boundaryPoints = [];
    private readonly List<Point> _draftBoundaryPoints = [];
    private readonly List<int> _draftBoundaryStrokeSizes = [];

    private ProjectPaths? _projectPaths;
    private ReefCamsRepository? _repository;
    private ClipIndexer? _indexer;
    private AppConfig _config = new();
    private List<ClipTimelineItem> _timelineAll = [];
    private IReadOnlyList<FrameMarker> _frames = [];
    private Dictionary<string, List<DetectionRecord>> _detectionsByFrame = new(StringComparer.Ordinal);
    private ClipTimelineItem? _selectedClip;
    private HierarchyNode? _selectedNode;
    private ScopeFilter? _selectedTreeScope;
    private string? _currentSessionScopeId;
    private bool _isSeekDragging;
    private bool _boundaryEditMode;
    private bool _isBoundaryDragActive;
    private int _currentBoundaryStrokePointCount;
    private bool _hasLastBoundaryDragPoint;
    private Point _lastBoundaryDragPoint;
    private bool _isProcessing;
    private bool _isBusy;
    private bool _isProgressIndeterminate;
    private bool _isViewerMode;
    private bool _isBulkCompletionMode;
    private bool _isPlayerPlaying;
    private bool _isRebuildingTimeline;
    private int _seekRenderVersion;
    private CancellationTokenSource? _operationCts;
    private readonly HashSet<string> _processingScopeKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completionScopeKeys = new(StringComparer.OrdinalIgnoreCase);
    private string _projectDirDisplay = "Project directory not selected";
    private string _statusText = string.Empty;
    private string _operationProgressText = "Idle";
    private string _selectedClipNameText = "Selected clip: none";
    private string _selectedClipInfoText = string.Empty;
    private string _selectedTreeScopeText = "Tree selection: none";
    private string _projectTreeHeaderText = "Project Tree (select node then Add Selected Scope)";
    private string _timelineScopeText = "Timeline scope: - - -";
    private string _scopeSelectionSummary = "Processing scope list: 0 checked";
    private string _overlayConfidenceText = "0.100";
    private double _minVisibleConfidence = 0.4;
    private string _minVisibleConfidenceText = "0.400000";
    private bool _hideBelowConfidence = true;
    private bool _hideCompletedClips = true;
    private bool _hideMissingClips = true;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _playbackTimer.Tick += PlaybackTimerOnTick;
        _playbackTimer.Start();
        SetPlayerPlaying(false);
        OverlayConfidenceText = ConfSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
        UpdateBoundaryEditControls();
        UpdateCompletionToggleButtonState();
        UpdateScopeSelectionSummary();
        ApplyViewMode(isViewerMode: false);
        UpdateTopActionButtonVisuals();
        Dispatcher.BeginInvoke(new Action(TryAutoLoadLastProject), DispatcherPriority.Background);
    }

    public ObservableCollection<HierarchyNode> HierarchyNodes { get; } = [];
    public ObservableCollection<TimelineDisplayRow> TimelineRows { get; } = [];
    public ObservableCollection<ProcessingScopeOption> ProcessingScopes { get; } = [];
    public ObservableCollection<ProcessingScopeOption> CompletionScopes { get; } = [];

    public string ProjectDirDisplay
    {
        get => _projectDirDisplay;
        private set => SetField(ref _projectDirDisplay, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string OperationProgressText
    {
        get => _operationProgressText;
        private set => SetField(ref _operationProgressText, value);
    }

    public string SelectedClipInfoText
    {
        get => _selectedClipInfoText;
        private set => SetField(ref _selectedClipInfoText, value);
    }

    public string SelectedClipNameText
    {
        get => _selectedClipNameText;
        private set => SetField(ref _selectedClipNameText, value);
    }

    public string SelectedTreeScopeText
    {
        get => _selectedTreeScopeText;
        private set => SetField(ref _selectedTreeScopeText, value);
    }

    public string ProjectTreeHeaderText
    {
        get => _projectTreeHeaderText;
        private set => SetField(ref _projectTreeHeaderText, value);
    }

    public string TimelineScopeText
    {
        get => _timelineScopeText;
        private set => SetField(ref _timelineScopeText, value);
    }

    public ObservableCollection<ProcessingScopeOption> ActiveScopeOptions =>
        _isBulkCompletionMode ? CompletionScopes : ProcessingScopes;

    public string ScopeSelectionSummary
    {
        get => _scopeSelectionSummary;
        private set => SetField(ref _scopeSelectionSummary, value);
    }

    public string OverlayConfidenceText
    {
        get => _overlayConfidenceText;
        private set => SetField(ref _overlayConfidenceText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetField(ref _isProgressIndeterminate, value);
    }

    public double MinVisibleConfidence
    {
        get => _minVisibleConfidence;
        private set => SetField(ref _minVisibleConfidence, value);
    }

    public string MinVisibleConfidenceText
    {
        get => _minVisibleConfidenceText;
        set => SetField(ref _minVisibleConfidenceText, value);
    }

    public bool HideBelowConfidence
    {
        get => _hideBelowConfidence;
        set => SetField(ref _hideBelowConfidence, value);
    }

    public bool HideCompletedClips
    {
        get => _hideCompletedClips;
        set => SetField(ref _hideCompletedClips, value);
    }

    public bool HideMissingClips
    {
        get => _hideMissingClips;
        set => SetField(ref _hideMissingClips, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected override void OnClosing(CancelEventArgs e)
    {
        _operationCts?.Cancel();
        _playbackTimer.Stop();
        PlayerElement.Stop();
        base.OnClosing(e);
    }

    private void ChooseProjectDir_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose ReefCams Project Directory",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        try
        {
            InitializeProject(dialog.SelectedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Failed to open project:\n{ex.Message}", "ReefCams");
            StatusText = "Failed to open project directory";
        }
    }

    private async void AddClipRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _indexer is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        if (IsBusy)
        {
            MessageBox.Show(this, "Another operation is currently running.", "ReefCams");
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose clip root directory (read-only source)",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var rootPath = Path.GetFullPath(dialog.SelectedPath);
        _repository.UpsertClipRoot(rootPath);

        var token = BeginOperation("indexing");
        IsProgressIndeterminate = true;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = 1;
        ProcessingProgressBar.Value = 0;
        OperationProgressText = "Indexing...";
        try
        {
            var rootName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var progress = new Progress<IndexProgress>(
                p =>
                {
                    StatusText = $"Indexing: {p.ProcessedCount} files ({p.CurrentPath})";
                    OperationProgressText = $"Indexing {rootName}: {p.ProcessedCount} files";
                });
            var total = await _indexer.IndexClipRootAsync(rootPath, progress, token);
            var merged = _repository.MergeDuplicateClipsByPath();

            StatusText = merged > 0
                ? $"Clip root added and indexed: {total} clips ({merged} duplicate clips merged)"
                : $"Clip root added and indexed: {total} clips";
            OperationProgressText = $"Indexed {total} clips";
            LoadHierarchy();
            if (_selectedNode is not null)
            {
                LoadTimelineForNode(_selectedNode);
            }
            UpdateScopeSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Add clip root/index canceled";
            OperationProgressText = "Canceled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Add clip root/index failed:\n{ex.Message}", "ReefCams");
            StatusText = "Add clip root/index failed";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void IndexRoots_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _indexer is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        if (IsBusy)
        {
            MessageBox.Show(this, "Another operation is currently running.", "ReefCams");
            return;
        }

        var roots = _repository.GetClipRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "No clip roots are configured.", "ReefCams");
            return;
        }

        var token = BeginOperation("indexing");
        IsProgressIndeterminate = true;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = 1;
        ProcessingProgressBar.Value = 0;
        OperationProgressText = "Indexing...";
        try
        {
            var total = 0;
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                var progress = new Progress<IndexProgress>(
                    p =>
                    {
                        StatusText = $"Indexing: {p.ProcessedCount} files ({p.CurrentPath})";
                        OperationProgressText = $"Indexing {root.DisplayName}: {p.ProcessedCount} files";
                    });
                total += await _indexer.IndexClipRootAsync(root.RootPath, progress, token);
            }

            var merged = _repository.MergeDuplicateClipsByPath();
            StatusText = merged > 0
                ? $"Index complete: {total} clips indexed ({merged} duplicate clips merged)"
                : $"Index complete: {total} clips indexed";
            OperationProgressText = $"Indexed {total} clips";
            LoadHierarchy();
            if (_selectedNode is not null)
            {
                LoadTimelineForNode(_selectedNode);
            }
            UpdateScopeSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Indexing canceled";
            OperationProgressText = "Canceled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Indexing failed:\n{ex.Message}", "ReefCams");
            StatusText = "Indexing failed";
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ProcessScope_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing || IsBusy)
        {
            return;
        }

        if (_repository is null || _projectPaths is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        var enginePath = ResolveEnginePath();
        if (enginePath is null)
        {
            MessageBox.Show(this, "engine.exe was not found under engine\\engine.exe next to ReefCams.Processor.exe.", "ReefCams");
            return;
        }

        var scopeOptions = ProcessingScopes.Where(x => x.IsChecked).ToList();
        if (scopeOptions.Count == 0)
        {
            MessageBox.Show(this, "No processing scopes are checked. Select a tree node and click 'Add Selected Scope'.", "ReefCams");
            return;
        }

        var candidateMap = new Dictionary<string, ClipTimelineItem>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ClipTimelineItem>();
        foreach (var option in scopeOptions)
        {
            foreach (var clip in _repository.GetClipsToProcess(option.Scope))
            {
                if (!IsClipCurrentlyPresent(clip))
                {
                    continue;
                }

                if (candidateMap.TryAdd(clip.ClipId, clip))
                {
                    candidates.Add(clip);
                }
            }
        }
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "No unprocessed clips in checked scopes.", "ReefCams");
            return;
        }

        var benchmark = _repository.GetLatestBenchmark();
        var estimatePerClipSec = benchmark?.EstimatePer10sSec ?? 0;
        var estimateTotalSec = estimatePerClipSec > 0 ? estimatePerClipSec * candidates.Count : 0;
        var estimateText = estimatePerClipSec > 0 ? FormatDelta(estimateTotalSec) : "unknown (run Benchmark first)";
        var confirm = MessageBox.Show(
            this,
            $"Process {candidates.Count} clips across {scopeOptions.Count} checked scope(s)?\nEstimated time: {estimateText}",
            "Confirm Processing",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var token = BeginOperation("processing");
        _isProcessing = true;
        IsProgressIndeterminate = false;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = candidates.Count;
        ProcessingProgressBar.Value = 0;
        OperationProgressText = $"0/{candidates.Count}";

        try
        {
            var index = 0;
            foreach (var clip in candidates)
            {
                token.ThrowIfCancellationRequested();
                index++;
                StatusText = $"Processing {index}/{candidates.Count}: {clip.ClipName}";
                var progress = new Progress<string>(line => HandleEngineProgressLine(line, index, candidates.Count));
                var resolvedClipPath = ResolveClipPathForCurrentProject(clip.ClipPath);
                await _engineRunner.RunProcessClipAsync(
                    enginePath,
                    _projectPaths.DbPath,
                    resolvedClipPath,
                    _config.Processing,
                    progress,
                    token);

                // Engine and indexer can disagree on clip_id for some mtimes; merge any path-level duplicates immediately.
                _repository.MergeDuplicateClipsByPath(resolvedClipPath);

                ProcessingProgressBar.Value = index;
                OperationProgressText = $"{index}/{candidates.Count}";

                // Keep project tree metrics current while processing is running.
                LoadHierarchy();
                if (_selectedNode is not null)
                {
                    LoadTimelineForNode(_selectedNode);
                }
                UpdateScopeSelectionSummary();
            }

            StatusText = $"Processing complete: {candidates.Count} clips";
            OperationProgressText = $"Completed {candidates.Count}/{candidates.Count}";
            LoadHierarchy();
            if (_selectedNode is not null)
            {
                LoadTimelineForNode(_selectedNode);
            }
            UpdateScopeSelectionSummary();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Processing canceled";
            OperationProgressText = "Canceled";
        }
        catch (Exception ex)
        {
            StatusText = "Processing failed";
            MessageBox.Show(this, $"Processing failed:\n{ex.Message}", "ReefCams");
        }
        finally
        {
            _isProcessing = false;
            EndOperation();
        }
    }

    private async void RunBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _projectPaths is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        if (IsBusy)
        {
            MessageBox.Show(this, "Another operation is currently running.", "ReefCams");
            return;
        }

        var enginePath = ResolveEnginePath();
        if (enginePath is null)
        {
            MessageBox.Show(this, "engine.exe was not found under engine\\engine.exe next to ReefCams.Processor.exe.", "ReefCams");
            return;
        }

        var clipPath = ResolveBenchmarkClipPath();
        if (string.IsNullOrWhiteSpace(clipPath))
        {
            MessageBox.Show(this, "No indexed clips found. Add a clip root and index clips first.", "ReefCams");
            return;
        }

        var token = BeginOperation("benchmark");
        IsProgressIndeterminate = true;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = 1;
        ProcessingProgressBar.Value = 0;
        OperationProgressText = "Benchmark running...";
        try
        {
            StatusText = "Running benchmark...";
            var progress = new Progress<string>(line => HandleEngineProgressLine(line, 0, 1));
            var result = await _engineRunner.RunBenchmarkAsync(
                enginePath,
                _projectPaths.DbPath,
                clipPath,
                _config.Processing,
                progress,
                token);
            if (result is null)
            {
                MessageBox.Show(this, "Benchmark completed but no benchmark_result payload was emitted.", "ReefCams");
                return;
            }

            StatusText = $"Benchmark done: avg {result.AvgInferMs:0.0} ms, p95 {result.P95InferMs:0.0} ms";
            OperationProgressText = "Benchmark complete";
            MessageBox.Show(
                this,
                $"Provider requested: {result.ProviderRequested}\n" +
                $"Provider used: {result.ProviderUsed}\n" +
                $"Avg infer ms: {result.AvgInferMs:0.0}\n" +
                $"P95 infer ms: {result.P95InferMs:0.0}\n" +
                $"Total ms: {result.TotalMs:0.0}\n" +
                $"Estimate sec per 10s clip: {result.EstimatePer10sSec:0.00}",
                "Benchmark Result");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Benchmark canceled";
            OperationProgressText = "Canceled";
        }
        catch (Exception ex)
        {
            StatusText = "Benchmark failed";
            MessageBox.Show(this, $"Benchmark failed:\n{ex.Message}", "ReefCams");
        }
        finally
        {
            EndOperation();
        }
    }

    private void ApplyCompletionScopes_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBulkCompletionMode || IsBusy)
        {
            return;
        }

        if (_repository is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        var options = CompletionScopes.Where(x => x.IsChecked).ToList();
        if (options.Count == 0)
        {
            MessageBox.Show(this, "No completion scopes are checked.", "ReefCams");
            return;
        }

        var clipIds = CollectCompletionTargetClipIds(options);
        if (clipIds.Count == 0)
        {
            MessageBox.Show(this, "No clips matched the checked completion scopes.", "ReefCams");
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Mark {clipIds.Count} clips as completed across {options.Count} checked completion scope(s)?",
            "Confirm Bulk Completion",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        BeginOperation("bulk completion");
        IsProgressIndeterminate = false;
        ProcessingProgressBar.Minimum = 0;
        ProcessingProgressBar.Maximum = clipIds.Count;
        ProcessingProgressBar.Value = 0;
        OperationProgressText = $"0/{clipIds.Count}";

        try
        {
            var updated = _repository.MarkClipsCompleted(clipIds, completed: true);
            ProcessingProgressBar.Value = clipIds.Count;
            StatusText = $"Bulk completion done: {updated} clips updated";
            OperationProgressText = $"{updated}/{clipIds.Count}";
            LoadHierarchy();
            if (_selectedNode is not null)
            {
                LoadTimelineForNode(_selectedNode);
            }
        }
        catch (Exception ex)
        {
            StatusText = "Bulk completion failed";
            MessageBox.Show(this, $"Bulk completion failed:\n{ex.Message}", "ReefCams");
        }
        finally
        {
            EndOperation();
        }
    }

    private async void ExportPackage_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _projectPaths is null || _selectedNode is null)
        {
            MessageBox.Show(this, "Choose a project directory and select a scope first.", "ReefCams");
            return;
        }

        if (IsBusy)
        {
            MessageBox.Show(this, "Another operation is currently running.", "ReefCams");
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose destination folder for exported package",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var destination = Path.Combine(dialog.SelectedPath, $"ReefCamsExport_{DateTime.Now:yyyyMMdd_HHmmss}");
        var minConf = MinVisibleConfidence;
        var request = new ExportRequest
        {
            Scope = _selectedNode.Scope,
            DestinationDirectory = destination,
            MinimumConfidence = minConf,
            IncludeViewerFiles = true
        };

        try
        {
            StatusText = "Building export package...";
            var exportBuilder = new ExportBuilder(_repository);
            var token = BeginOperation("export");
            IsProgressIndeterminate = false;
            ProcessingProgressBar.Minimum = 0;
            var totalToExport = Math.Max(1, _repository.GetExportCandidates(request.Scope, request.MinimumConfidence).Count);
            ProcessingProgressBar.Maximum = totalToExport;
            ProcessingProgressBar.Value = 0;
            OperationProgressText = $"0/{totalToExport}";
            var progress = new Progress<int>(
                count =>
                {
                    ProcessingProgressBar.Value = count;
                    OperationProgressText = $"{Math.Min(totalToExport, count)}/{totalToExport}";
                });

            var result = await Task.Run(
                () => exportBuilder.BuildExport(
                    _projectPaths,
                    _config,
                    request,
                    ResolveViewerDistributionDirectory(),
                    token,
                    progress),
                token);
            StatusText = $"Export done: {result.ExportedClipCount} clips";
            OperationProgressText = $"Completed {result.ExportedClipCount}/{totalToExport}";
            MessageBox.Show(this, $"Exported {result.ExportedClipCount} clips to:\n{result.ExportDirectory}", "ReefCams");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export canceled";
            OperationProgressText = "Canceled";
        }
        catch (Exception ex)
        {
            StatusText = "Export failed";
            MessageBox.Show(this, $"Export failed:\n{ex.Message}", "ReefCams");
        }
        finally
        {
            EndOperation();
        }
    }

    private void HandleEngineProgressLine(string line, int currentClip, int totalClips)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (!line.StartsWith("{", StringComparison.Ordinal))
        {
            StatusText = line;
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                StatusText = line;
                return;
            }

            var type = typeProp.GetString() ?? string.Empty;
            if (type.Equals("frame", StringComparison.Ordinal))
            {
                var tSec = doc.RootElement.TryGetProperty("t", out var tProp) && tProp.TryGetDouble(out var t) ? t : 0;
                var maxConf = doc.RootElement.TryGetProperty("max_conf_frame", out var cProp) && cProp.TryGetDouble(out var c) ? c : 0;
                StatusText = $"Clip {currentClip}/{totalClips}: frame {tSec:0.0}s max_conf {maxConf:0.000}";
                return;
            }

            if (type.Equals("done", StringComparison.Ordinal))
            {
                var maxConf = doc.RootElement.TryGetProperty("max_conf", out var cProp) && cProp.TryGetDouble(out var c) ? c : 0;
                StatusText = $"Clip {currentClip}/{totalClips} done max_conf {maxConf:0.000}";
                return;
            }

            if (type.Equals("benchmark_result", StringComparison.Ordinal))
            {
                StatusText = "Benchmark result received.";
                return;
            }

            if (type.Equals("error", StringComparison.Ordinal))
            {
                var message = doc.RootElement.TryGetProperty("message", out var mProp) ? mProp.GetString() : "Unknown engine error";
                StatusText = $"Engine error: {message}";
                return;
            }

            StatusText = line;
        }
        catch
        {
            StatusText = line;
        }
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not HierarchyNode node)
        {
            return;
        }

        _selectedNode = node;
        _selectedTreeScope = node.Scope;
        SelectedTreeScopeText = $"Tree selection: {BuildScopeLabel(node)}";
        TimelineScopeText = BuildTimelineScopeText(node.Scope);
        LoadTimelineForNode(node);
    }

    private void TimelineDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRebuildingTimeline)
        {
            return;
        }

        if (TimelineDataGrid.SelectedItem is not TimelineDisplayRow row || row.IsGapRow || row.Clip is null)
        {
            return;
        }

        LoadClip(row.Clip);
    }

    private void MinVisibleConfidenceTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        TryApplyMinVisibleConfidenceFromText(showError: true);
    }

    private void MinVisibleConfidenceTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
        {
            return;
        }

        if (TryApplyMinVisibleConfidenceFromText(showError: true))
        {
            e.Handled = true;
        }
    }

    private void HideBelowConfidence_Changed(object sender, RoutedEventArgs e)
    {
        RebuildTimelineRows();
    }

    private void HideCompletedClips_Changed(object sender, RoutedEventArgs e)
    {
        _config.Timeline.HideCompletedClips = HideCompletedClips;
        SaveConfig();
        LoadHierarchy();
        UpdateScopeSelectionSummary();
        RebuildTimelineRows();
    }

    private void HideMissingClips_Changed(object sender, RoutedEventArgs e)
    {
        _config.Timeline.HideMissingClips = HideMissingClips;
        SaveConfig();
        LoadHierarchy();
        UpdateScopeSelectionSummary();
        if (_selectedNode is not null)
        {
            LoadTimelineForNode(_selectedNode);
            return;
        }

        _timelineAll = [];
        _selectedClip = null;
        SetPlayerPlaying(false);
        UpdateSelectedClipInfo(null);
        RebuildTimelineRows();
    }

    private void StopOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCts is null || !IsBusy)
        {
            StatusText = "No operation is currently running.";
            OperationProgressText = "Idle";
            return;
        }

        _operationCts?.Cancel();
        StatusText = "Cancel requested...";
        OperationProgressText = "Cancel requested...";
    }

    private void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
        {
            return;
        }

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void ShowProcessingView_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewMode(isViewerMode: false);
    }

    private void ShowViewerView_Click(object sender, RoutedEventArgs e)
    {
        ApplyViewMode(isViewerMode: true);
    }

    private void ToggleBulkCompletionMode_Click(object sender, RoutedEventArgs e)
    {
        SetBulkCompletionMode(!_isBulkCompletionMode);
    }

    private void AddSelectedScope_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null)
        {
            MessageBox.Show(this, "Select a node from Project Tree first.", "ReefCams");
            return;
        }

        var scope = _selectedNode.Scope;
        if (!_isBulkCompletionMode)
        {
            var scopeKey = ScopeKey(scope);
            if (_processingScopeKeys.Contains(scopeKey))
            {
                StatusText = $"Scope already added: {BuildScopeLabel(_selectedNode)}";
                return;
            }

            ProcessingScopes.Add(
                new ProcessingScopeOption
                {
                    Scope = scope,
                    ScopeKey = scopeKey,
                    BaseLabel = BuildScopeLabel(_selectedNode),
                    DisplayLabel = BuildScopeLabel(_selectedNode),
                    IsChecked = true
                });
            _processingScopeKeys.Add(scopeKey);
            UpdateScopeSelectionSummary();
            StatusText = $"Added processing scope: {BuildScopeLabel(_selectedNode)}";
            return;
        }

        var completionScopeKey = ScopeKey(scope);
        if (_completionScopeKeys.Contains(completionScopeKey))
        {
            StatusText = $"Scope already added: {BuildScopeLabel(_selectedNode)}";
            return;
        }

        CompletionScopes.Add(
            new ProcessingScopeOption
            {
                Scope = scope,
                ScopeKey = completionScopeKey,
                BaseLabel = BuildScopeLabel(_selectedNode),
                DisplayLabel = BuildScopeLabel(_selectedNode),
                IsChecked = true
            });
        _completionScopeKeys.Add(completionScopeKey);
        UpdateScopeSelectionSummary();
        StatusText = $"Added completion scope: {BuildScopeLabel(_selectedNode)}";
    }

    private void AddUpToSelectedClipScope_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBulkCompletionMode)
        {
            return;
        }

        if (_selectedClip is null)
        {
            MessageBox.Show(this, "Select a clip in the timeline first.", "ReefCams");
            return;
        }

        var sessionScope = new ScopeFilter(
            TreeNodeType.Session,
            _selectedClip.RootId,
            _selectedClip.Site,
            _selectedClip.Dcim,
            _selectedClip.Session);
        var scopeKey = $"upto|{ScopeKey(sessionScope)}|{_selectedClip.ClipId}";
        if (_completionScopeKeys.Contains(scopeKey))
        {
            StatusText = $"Up-to scope already added: {sessionScope.Site}/{sessionScope.Dcim}/{sessionScope.Session} <= {_selectedClip.ClipName}";
            return;
        }

        CompletionScopes.Add(
            new ProcessingScopeOption
            {
                Scope = sessionScope,
                ScopeKey = scopeKey,
                BaseLabel = $"Session: {sessionScope.Site}/{sessionScope.Dcim}/{sessionScope.Session} (up to {_selectedClip.ClipName})",
                DisplayLabel = $"Session: {sessionScope.Site}/{sessionScope.Dcim}/{sessionScope.Session} (up to {_selectedClip.ClipName})",
                IsChecked = true,
                SelectionMode = ScopeSelectionMode.UpToClip,
                UpToClipId = _selectedClip.ClipId
            });
        _completionScopeKeys.Add(scopeKey);
        UpdateScopeSelectionSummary();
        StatusText = $"Added up-to completion scope for {_selectedClip.ClipName}";
    }

    private void ClearScopeList_Click(object sender, RoutedEventArgs e)
    {
        if (_isBulkCompletionMode)
        {
            CompletionScopes.Clear();
            _completionScopeKeys.Clear();
        }
        else
        {
            ProcessingScopes.Clear();
            _processingScopeKeys.Clear();
        }

        UpdateScopeSelectionSummary();
    }

    private void ProcessScopeCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateScopeSelectionSummary();
    }

    private void ExpandGap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string groupId)
        {
            return;
        }

        if (!_hiddenGapGroups.TryGetValue(groupId, out var hiddenRows))
        {
            return;
        }

        var idx = TimelineRows
            .Select((row, index) => new { row, index })
            .FirstOrDefault(x => x.row.IsGapRow && x.row.GapGroupId == groupId)?.index;
        if (idx is null)
        {
            return;
        }

        TimelineRows.RemoveAt(idx.Value);
        var insertOffset = 0;
        foreach (var hidden in hiddenRows)
        {
            TimelineRows.Insert(idx.Value + insertOffset, CreateTimelineRow(hidden, isGapRow: false, notes: "Expanded from hidden gap"));
            insertOffset++;
        }

        _hiddenGapGroups.Remove(groupId);
        QueueTimelineTextColumnWidthRefresh();
    }

    private void MoveTimelineSelection(int direction)
    {
        var clipRows = TimelineRows.Where(x => !x.IsGapRow && x.Clip is not null).ToList();
        if (clipRows.Count == 0)
        {
            return;
        }

        var currentRow = TimelineDataGrid.SelectedItem as TimelineDisplayRow;
        var currentIndex = currentRow is null
            ? clipRows.FindIndex(x => x.Clip?.ClipId.Equals(_selectedClip?.ClipId, StringComparison.OrdinalIgnoreCase) == true)
            : clipRows.FindIndex(x => x.Clip?.ClipId.Equals(currentRow.Clip?.ClipId, StringComparison.OrdinalIgnoreCase) == true);
        if (currentIndex < 0)
        {
            currentIndex = direction > 0 ? -1 : clipRows.Count;
        }

        var nextIndex = Math.Clamp(currentIndex + direction, 0, clipRows.Count - 1);
        if (nextIndex == currentIndex)
        {
            return;
        }

        var row = clipRows[nextIndex];
        TimelineDataGrid.SelectedItem = row;
        TimelineDataGrid.ScrollIntoView(row);
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        TogglePlayback();
    }

    private void JumpMaxFrame_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedClip?.MaxConfTimeSec is null)
        {
            return;
        }

        ShowFrameAt(_selectedClip.MaxConfTimeSec.Value);
    }

    private void PlayerElement_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (!PlayerElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        SeekSlider.Maximum = PlayerElement.NaturalDuration.TimeSpan.TotalSeconds;
        ShowFrameAt(SeekSlider.Value);
    }

    private void PlayerElement_MediaEnded(object sender, RoutedEventArgs e)
    {
        SetPlayerPlaying(false);
        ShowFrameAt(0);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.ComboBox)
        {
            return;
        }

        if (_selectedClip is not null && e.Key is (Key.Space or Key.Enter))
        {
            TogglePlayback();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            MoveTimelineSelection(+1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            MoveTimelineSelection(-1);
            e.Handled = true;
        }
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeekDragging || !PlayerElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _isSeekDragging = true;
    }

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isSeekDragging = false;
        ShowFrameAt(SeekSlider.Value);
    }

    private void OverlaySettings_Changed(object sender, RoutedEventArgs e)
    {
        OverlayConfidenceText = ConfSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
        RenderOverlay();
    }

    private void TogglePlayback()
    {
        if (_selectedClip is null)
        {
            return;
        }

        if (_isPlayerPlaying)
        {
            PlayerElement.Pause();
            SetPlayerPlaying(false);
            return;
        }

        PlayerElement.Play();
        SetPlayerPlaying(true);
    }

    private void SetPlayerPlaying(bool isPlaying)
    {
        _isPlayerPlaying = isPlaying;
        PlayPauseButton.Content = isPlaying ? "Pause" : "Play";
        PlayPauseButton.IsEnabled = _selectedClip is not null;
    }

    private void UpdateCompletionToggleButtonState()
    {
        if (_selectedClip is null)
        {
            MarkClipCompletedButton.Content = "Mark Clip Completed";
            MarkClipCompletedButton.IsEnabled = false;
            return;
        }

        MarkClipCompletedButton.Content = _selectedClip.Completed
            ? "Unmark Clip Completed"
            : "Mark Clip Completed";
        MarkClipCompletedButton.IsEnabled = true;
    }

    private void MarkClipCompleted_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _selectedClip is null)
        {
            return;
        }

        _repository.MarkClipCompleted(_selectedClip.ClipId, completed: !_selectedClip.Completed);
        RefreshAfterClipMutation(_selectedClip.ClipId);
    }

    private void ToggleBoundaryMode_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _selectedClip is null || string.IsNullOrWhiteSpace(_currentSessionScopeId))
        {
            MessageBox.Show(this, "Select a clip first.", "ReefCams");
            return;
        }

        if (!_boundaryEditMode)
        {
            _boundaryEditMode = true;
            _draftBoundaryPoints.Clear();
            _draftBoundaryPoints.AddRange(_boundaryPoints);
            _draftBoundaryStrokeSizes.Clear();
            ResetBoundaryDragState();
            UpdateBoundaryEditControls();
            StatusText = "Reef boundry edit mode active: click and drag to draw strokes.";
            RenderOverlay();
            return;
        }

        _boundaryEditMode = false;
        _draftBoundaryPoints.Clear();
        _draftBoundaryStrokeSizes.Clear();
        ResetBoundaryDragState();
        UpdateBoundaryEditControls();
        StatusText = "Reef boundry edit canceled.";
        RenderOverlay();
    }

    private void BoundaryRemoveLastPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!_boundaryEditMode)
        {
            return;
        }

        if (_isBoundaryDragActive)
        {
            CommitBoundaryStroke();
        }

        if (_draftBoundaryStrokeSizes.Count == 0)
        {
            return;
        }

        var removeCount = _draftBoundaryStrokeSizes[^1];
        _draftBoundaryStrokeSizes.RemoveAt(_draftBoundaryStrokeSizes.Count - 1);
        if (removeCount <= 0)
        {
            return;
        }

        var actualRemoveCount = Math.Min(removeCount, _draftBoundaryPoints.Count);
        _draftBoundaryPoints.RemoveRange(_draftBoundaryPoints.Count - actualRemoveCount, actualRemoveCount);
        RenderOverlay();
    }

    private void BoundaryClear_Click(object sender, RoutedEventArgs e)
    {
        if (!_boundaryEditMode)
        {
            return;
        }

        _draftBoundaryPoints.Clear();
        _draftBoundaryStrokeSizes.Clear();
        ResetBoundaryDragState();
        StatusText = "Reef boundry draft cleared.";
        RenderOverlay();
    }

    private void BoundarySave_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _selectedClip is null || string.IsNullOrWhiteSpace(_currentSessionScopeId))
        {
            return;
        }

        if (!_boundaryEditMode)
        {
            return;
        }

        if (_isBoundaryDragActive)
        {
            CommitBoundaryStroke();
        }

        if (_draftBoundaryPoints.Count == 0)
        {
            _repository.ClearSessionBoundary(_currentSessionScopeId);
            _boundaryPoints.Clear();
            _boundaryEditMode = false;
            _draftBoundaryPoints.Clear();
            _draftBoundaryStrokeSizes.Clear();
            ResetBoundaryDragState();
            UpdateBoundaryEditControls();
            StatusText = "Reef boundry cleared.";
            LoadHierarchy();
            RenderOverlay();
            return;
        }

        if (_draftBoundaryPoints.Count < 3)
        {
            MessageBox.Show(this, "Boundary requires at least 3 points, or clear all points to remove boundary.", "ReefCams");
            return;
        }

        var pointsJson = JsonSerializer.Serialize(_draftBoundaryPoints.Select(p => new[] { p.X, p.Y }));
        _repository.SaveSessionBoundary(_currentSessionScopeId, _selectedClip.ClipId, pointsJson);
        _boundaryPoints.Clear();
        _boundaryPoints.AddRange(_draftBoundaryPoints);
        _boundaryEditMode = false;
        _draftBoundaryPoints.Clear();
        _draftBoundaryStrokeSizes.Clear();
        ResetBoundaryDragState();
        UpdateBoundaryEditControls();
        StatusText = "Reef boundry saved.";
        LoadHierarchy();
        RenderOverlay();
    }

    private void OverlayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_boundaryEditMode)
        {
            return;
        }

        var point = e.GetPosition(OverlayCanvas);
        if (!TryNormalizePointFromOverlay(point, out var normalized))
        {
            return;
        }

        ResetBoundaryDragState();
        _isBoundaryDragActive = true;
        OverlayCanvas.CaptureMouse();
        AppendBoundaryDragPoint(normalized, force: true);
        RenderOverlay();
    }

    private void OverlayCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_boundaryEditMode || !_isBoundaryDragActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(OverlayCanvas);
        if (!TryNormalizePointFromOverlay(point, out var normalized))
        {
            return;
        }

        if (!AppendBoundaryDragPoint(normalized, force: false))
        {
            return;
        }

        RenderOverlay();
    }

    private void OverlayCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_boundaryEditMode || !_isBoundaryDragActive)
        {
            return;
        }

        CommitBoundaryStroke();
    }

    private void OverlayCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        BoundaryRemoveLastPoint_Click(sender, e);
    }

    private void InitializeProject(string projectDirectory)
    {
        _projectPaths = ProjectPaths.Create(projectDirectory);
        _projectPaths.EnsureExists();

        _repository = new ReefCamsRepository(_projectPaths.DbPath);
        _repository.InitializeSchema();
        var repaired = _repository.BackfillMissingRootIdsFromClipPath();
        var merged = _repository.MergeDuplicateClipsByPath();
        _indexer = new ClipIndexer(_repository);
        _config = _configService.LoadOrCreate(_projectPaths);
        MinVisibleConfidence = NormalizeMinVisibleConfidence(_config.Timeline.MinVisibleConfidence);
        MinVisibleConfidenceText = FormatConfidence(MinVisibleConfidence);
        HideCompletedClips = _config.Timeline.HideCompletedClips;
        HideMissingClips = _config.Timeline.HideMissingClips;

        ProjectDirDisplay = _projectPaths.RootDirectory;
        var notes = new List<string>();
        if (repaired > 0)
        {
            notes.Add($"{repaired} clips repaired");
        }

        if (merged > 0)
        {
            notes.Add($"{merged} duplicate clips merged");
        }

        StatusText = notes.Count > 0 ? $"Project loaded ({string.Join(", ", notes)})" : "Project loaded";

        ProcessingScopes.Clear();
        _processingScopeKeys.Clear();
        CompletionScopes.Clear();
        _completionScopeKeys.Clear();
        SetBulkCompletionMode(enabled: false, updateStatus: false);
        UpdateScopeSelectionSummary();
        _selectedNode = null;
        _selectedTreeScope = null;
        SelectedTreeScopeText = "Tree selection: none";
        TimelineScopeText = "Timeline scope: - - -";
        _selectedClip = null;
        SetPlayerPlaying(false);
        _boundaryEditMode = false;
        _draftBoundaryPoints.Clear();
        _draftBoundaryStrokeSizes.Clear();
        ResetBoundaryDragState();
        UpdateBoundaryEditControls();
        UpdateSelectedClipInfo(null);
        LoadHierarchy(preserveUiState: false);
        TimelineRows.Clear();
        SaveConfig();
        SaveUiState(_projectPaths.RootDirectory);
    }

    private void LoadHierarchy(bool preserveUiState = true)
    {
        if (_repository is null)
        {
            return;
        }

        RefreshRootDisplayNames();
        var expandedScopeKeys = preserveUiState
            ? CaptureExpandedScopeKeys(HierarchyNodes)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedScope = preserveUiState ? _selectedTreeScope : null;

        var roots = _repository.GetHierarchy(MinVisibleConfidence).ToList();
        if (HideMissingClips)
        {
            roots = FilterHierarchyToPresentClips(roots);
        }

        HierarchyNodes.Clear();
        foreach (var root in roots)
        {
            HierarchyNodes.Add(root);
        }

        if (expandedScopeKeys.Count > 0)
        {
            ApplyExpandedScopeKeys(HierarchyNodes, expandedScopeKeys);
        }

        if (selectedScope is not null)
        {
            TryRestoreSelectedNode(selectedScope);
        }
    }

    private void RefreshRootDisplayNames()
    {
        _rootDisplayNames.Clear();
        _rootPathById.Clear();
        _rootPresenceById.Clear();
        _clipPresenceCache.Clear();
        _scopePresenceByScopeKey.Clear();
        if (_repository is null)
        {
            return;
        }

        foreach (var root in _repository.GetClipRoots())
        {
            _rootDisplayNames[root.RootId] = root.DisplayName;
            _rootPathById[root.RootId] = root.RootPath;
            _rootPresenceById[root.RootId] = Directory.Exists(root.RootPath);
        }
    }

    private void LoadTimelineForNode(HierarchyNode node)
    {
        if (_repository is null)
        {
            return;
        }

        var selectedClipId = _selectedClip?.ClipId;
        if (_boundaryEditMode)
        {
            _boundaryEditMode = false;
            _draftBoundaryPoints.Clear();
            _draftBoundaryStrokeSizes.Clear();
            ResetBoundaryDragState();
            UpdateBoundaryEditControls();
        }

        if (node.Scope.Type == TreeNodeType.Root)
        {
            _timelineAll = [];
            _isRebuildingTimeline = true;
            try
            {
                TimelineRows.Clear();
                TimelineDataGrid.SelectedItem = null;
            }
            finally
            {
                _isRebuildingTimeline = false;
            }

            _selectedClip = null;
            SetPlayerPlaying(false);
            UpdateSelectedClipInfo(null);
            TimelineScopeText = "Timeline scope: select a site";
            StatusText = "Select a site to view timeline clips.";
            QueueTimelineTextColumnWidthRefresh();
            return;
        }

        TimelineScopeText = BuildTimelineScopeText(node.Scope);
        _timelineAll = ApplyPresenceFilter(_repository.GetTimeline(node.Scope));
        RebuildTimelineRows();
        if (!TrySelectTimelineClipById(selectedClipId))
        {
            _selectedClip = null;
            SetPlayerPlaying(false);
            UpdateSelectedClipInfo(null);
        }

        StatusText = $"{_timelineAll.Count} clips in selected scope";
    }

    private void RebuildTimelineRows()
    {
        var selectedClipId = _selectedClip?.ClipId;
        _isRebuildingTimeline = true;
        try
        {
            TimelineRows.Clear();
            _hiddenGapGroups.Clear();

            if (_timelineAll.Count == 0)
            {
                TimelineDataGrid.SelectedItem = null;
                QueueTimelineTextColumnWidthRefresh();
                return;
            }

            if (!HideBelowConfidence && !HideCompletedClips && !HideMissingClips)
            {
                foreach (var clip in _timelineAll)
                {
                    TimelineRows.Add(CreateTimelineRow(clip, isGapRow: false, notes: string.Empty));
                }

                QueueTimelineTextColumnWidthRefresh();
                return;
            }

            var hidden = new List<ClipTimelineItem>();
            foreach (var clip in _timelineAll)
            {
                if (ShouldHideTimelineClip(clip))
                {
                    hidden.Add(clip);
                    continue;
                }

                if (hidden.Count > 0)
                {
                    AddGapRow(hidden, clip);
                    hidden = [];
                }

                TimelineRows.Add(CreateTimelineRow(clip, isGapRow: false, notes: string.Empty));
            }

            if (hidden.Count > 0)
            {
                AddGapRow(hidden, nextVisibleClip: null);
            }

            QueueTimelineTextColumnWidthRefresh();
        }
        finally
        {
            _isRebuildingTimeline = false;
        }

        if (!TrySelectTimelineClipById(selectedClipId))
        {
            TimelineDataGrid.SelectedItem = null;
        }
    }

    private void QueueTimelineTextColumnWidthRefresh()
    {
        Dispatcher.BeginInvoke(new Action(ApplyTimelineTextColumnWidthPadding), DispatcherPriority.Loaded);
    }

    private void ApplyTimelineTextColumnWidthPadding()
    {
        var columns = new DataGridColumn?[] { ClipColumn, TimestampColumn, ConfColumn, DeltaColumn };
        foreach (var column in columns)
        {
            if (column is null)
            {
                continue;
            }

            column.Width = new DataGridLength(1, DataGridLengthUnitType.SizeToCells);
        }

        TimelineDataGrid.UpdateLayout();

        foreach (var column in columns)
        {
            if (column is null)
            {
                continue;
            }

            var measured = Math.Max(column.ActualWidth, column.MinWidth);
            column.Width = new DataGridLength(measured + TimelineTextColumnExtraWidthPx);
        }
    }

    private void AddGapRow(List<ClipTimelineItem> hidden, ClipTimelineItem? nextVisibleClip)
    {
        var groupId = Guid.NewGuid().ToString("N");
        _hiddenGapGroups[groupId] = new List<ClipTimelineItem>(hidden);

        double spanSec = 0;
        var validTimes = hidden
            .Where(x => x.CreatedAtUtc != DateTimeOffset.MinValue)
            .OrderBy(x => x.CreatedAtUtc)
            .ToList();
        if (validTimes.Count > 0 && nextVisibleClip is not null && nextVisibleClip.CreatedAtUtc != DateTimeOffset.MinValue)
        {
            spanSec = Math.Max(0, (nextVisibleClip.CreatedAtUtc - validTimes[0].CreatedAtUtc).TotalSeconds);
        }
        else if (validTimes.Count >= 2)
        {
            spanSec = Math.Max(0, (validTimes[^1].CreatedAtUtc - validTimes[0].CreatedAtUtc).TotalSeconds);
        }

        var summary = BuildHiddenGapSummary(hidden, spanSec);

        TimelineRows.Add(
            new TimelineDisplayRow
            {
                IsGapRow = true,
                GapGroupId = groupId,
                GapSummary = summary,
                ClipName = string.Empty,
                CreatedText = string.Empty,
                MaxConfText = "-",
                DeltaText = string.Empty,
                Notes = string.Empty
            });
    }

    private TimelineDisplayRow CreateTimelineRow(ClipTimelineItem clip, bool isGapRow, string notes)
    {
        return new TimelineDisplayRow
        {
            IsGapRow = isGapRow,
            Clip = clip,
            CreatedText = clip.CreatedAtUtc == DateTimeOffset.MinValue
                ? "-"
                : clip.CreatedAtUtc.UtcDateTime.ToString("MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture),
            GapSummary = string.Empty,
            ClipName = clip.ClipName,
            MaxConfText = clip.Processed ? clip.MaxConf.ToString("0.000", CultureInfo.InvariantCulture) : "-",
            Processed = clip.Processed,
            Completed = clip.Completed,
            DeltaText = clip.DeltaFromPreviousSec.HasValue ? FormatDelta(clip.DeltaFromPreviousSec.Value) : "-",
            Notes = notes
        };
    }

    private bool ShouldHideTimelineClip(ClipTimelineItem clip)
    {
        if (HideMissingClips && !IsClipCurrentlyPresent(clip))
        {
            return true;
        }

        if (HideCompletedClips && clip.Completed)
        {
            return true;
        }

        if (HideBelowConfidence && clip.Processed && clip.MaxConf < MinVisibleConfidence)
        {
            return true;
        }

        return false;
    }

    private string BuildHiddenGapSummary(IReadOnlyCollection<ClipTimelineItem> hidden, double spanSec)
    {
        var hiddenMissing = hidden.Count(x => !IsClipCurrentlyPresent(x));
        var hiddenCompleted = hidden.Count(x => x.Completed);
        var hiddenBelowConfidence = hidden.Count(x => x.Processed && x.MaxConf < MinVisibleConfidence);

        if (HideMissingClips && hiddenMissing > 0)
        {
            var extra = string.Empty;
            if (HideCompletedClips && hiddenCompleted > 0)
            {
                extra = $", {hiddenCompleted} completed";
            }
            else if (HideBelowConfidence && hiddenBelowConfidence > 0)
            {
                extra = $", {hiddenBelowConfidence} below {FormatConfidence(MinVisibleConfidence)}";
            }

            return $"{hiddenMissing} missing clips hidden{extra} - span {FormatDelta(spanSec)}";
        }

        if (HideBelowConfidence && HideCompletedClips)
        {
            if (hiddenCompleted > 0 && hiddenBelowConfidence > 0)
            {
                return $"{hidden.Count} hidden clips (below {FormatConfidence(MinVisibleConfidence)} confidence and/or completed) - span {FormatDelta(spanSec)}";
            }

            if (hiddenCompleted > 0)
            {
                return $"{hiddenCompleted} completed clips hidden - span {FormatDelta(spanSec)}";
            }

            return $"{hiddenBelowConfidence} clips below {FormatConfidence(MinVisibleConfidence)} confidence - span {FormatDelta(spanSec)}";
        }

        if (HideCompletedClips)
        {
            return $"{hiddenCompleted} completed clips hidden - span {FormatDelta(spanSec)}";
        }

        return $"{hiddenBelowConfidence} clips below {FormatConfidence(MinVisibleConfidence)} confidence - span {FormatDelta(spanSec)}";
    }

    private bool TrySelectTimelineClipById(string? clipId)
    {
        if (string.IsNullOrWhiteSpace(clipId))
        {
            return false;
        }

        var row = TimelineRows.FirstOrDefault(x => !x.IsGapRow && x.Clip?.ClipId.Equals(clipId, StringComparison.OrdinalIgnoreCase) == true);
        if (row is null)
        {
            return false;
        }

        TimelineDataGrid.SelectedItem = row;
        TimelineDataGrid.ScrollIntoView(row);
        return true;
    }

    private void LoadClip(ClipTimelineItem clip)
    {
        if (_repository is null)
        {
            return;
        }

        _selectedClip = clip;
        UpdateSelectedClipInfo(clip);
        _frames = _repository.GetFrames(clip.ClipId);
        _detectionsByFrame = _repository
            .GetDetections(clip.ClipId)
            .GroupBy(d => FrameKey(d.FrameTimeSec))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        _currentSessionScopeId = ScopeIds.Session(clip.RootId, clip.Site, clip.Dcim, clip.Session);
        _boundaryPoints.Clear();
        var boundary = _repository.GetSessionBoundary(_currentSessionScopeId);
        if (boundary is not null)
        {
            foreach (var point in ParseBoundaryPoints(boundary.PointsJson))
            {
                _boundaryPoints.Add(point);
            }
        }

        ConfSlider.Value = GetDefaultOverlayThreshold(clip);
        var resolvedClipPath = ResolveClipPathForCurrentProject(clip.ClipPath);
        if (!File.Exists(resolvedClipPath))
        {
            MessageBox.Show(this, $"Clip file not found:\n{resolvedClipPath}", "ReefCams");
            return;
        }

        PlayerElement.Stop();
        PlayerElement.Source = new Uri(resolvedClipPath);
        SeekSlider.Value = 0;
        // Force a first-frame render so clip selection does not appear black before play.
        PlayerElement.Play();
        Dispatcher.BeginInvoke(
            new Action(
                () =>
                {
                    PlayerElement.Pause();
                    SetPlayerPlaying(false);
                    ShowFrameAt(0);
                }),
            DispatcherPriority.Background);
    }

    private IEnumerable<Point> ParseBoundaryPoints(string pointsJson)
    {
        if (string.IsNullOrWhiteSpace(pointsJson))
        {
            yield break;
        }

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(pointsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                {
                    var x = item[0].GetDouble();
                    var y = item[1].GetDouble();
                    yield return new Point(x, y);
                    continue;
                }

                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("x", out var xProp) &&
                    item.TryGetProperty("y", out var yProp))
                {
                    yield return new Point(xProp.GetDouble(), yProp.GetDouble());
                }
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    private void PlaybackTimerOnTick(object? sender, EventArgs e)
    {
        if (_selectedClip is null || !PlayerElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        if (!_isSeekDragging)
        {
            SeekSlider.Value = PlayerElement.Position.TotalSeconds;
        }

        RenderOverlay();
    }

    private void RenderOverlay()
    {
        OverlayCanvas.Children.Clear();

        var width = OverlayCanvas.ActualWidth;
        var height = OverlayCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_selectedClip is null)
        {
            return;
        }

        var videoRect = GetVideoRenderRect(width, height);

        if (ShowDetectionsCheck.IsChecked == true)
        {
            var frame = GetFrameAtOrBefore(PlayerElement.Position.TotalSeconds);
            if (frame is not null && _detectionsByFrame.TryGetValue(FrameKey(frame.FrameTimeSec), out var detections))
            {
                var minConf = ConfSlider.Value;
                foreach (var det in detections)
                {
                    if (det.Conf < minConf)
                    {
                        continue;
                    }

                    var x = videoRect.X + (det.X * videoRect.Width) - DetectionBoxPaddingPx;
                    var y = videoRect.Y + (det.Y * videoRect.Height) - DetectionBoxPaddingPx;
                    var boxW = (det.W * videoRect.Width) + (DetectionBoxPaddingPx * 2);
                    var boxH = (det.H * videoRect.Height) + (DetectionBoxPaddingPx * 2);

                    var rect = new Rectangle
                    {
                        Width = Math.Max(2, boxW),
                        Height = Math.Max(2, boxH),
                        Stroke = Brushes.LimeGreen,
                        StrokeThickness = 2
                    };
                    Canvas.SetLeft(rect, Math.Max(videoRect.X, x));
                    Canvas.SetTop(rect, Math.Max(videoRect.Y, y));
                    OverlayCanvas.Children.Add(rect);

                    var label = new TextBlock
                    {
                        Text = $"{det.ClassLabel ?? det.ClassId.ToString(CultureInfo.InvariantCulture)} {det.Conf:0.00}",
                        Foreground = Brushes.Black,
                        Background = Brushes.LimeGreen,
                        FontSize = 12,
                        Padding = new Thickness(2, 0, 2, 0)
                    };
                    Canvas.SetLeft(label, Math.Max(videoRect.X, x));
                    Canvas.SetTop(label, Math.Max(videoRect.Y, y - 18));
                    OverlayCanvas.Children.Add(label);
                }
            }
        }

        if (ShowBoundaryCheck.IsChecked == true)
        {
            DrawBoundary(_boundaryPoints, Brushes.DeepSkyBlue, 2, videoRect);
        }

        if (_boundaryEditMode)
        {
            DrawBoundary(_draftBoundaryPoints, Brushes.Gold, 3, videoRect);
        }
    }

    private void DrawBoundary(IEnumerable<Point> points, Brush stroke, double thickness, Rect videoRect)
    {
        var pts = points.ToList();
        if (pts.Count < 2)
        {
            return;
        }

        var polyline = new Polyline
        {
            Stroke = stroke,
            StrokeThickness = thickness
        };
        foreach (var pt in pts)
        {
            polyline.Points.Add(new Point(videoRect.X + (pt.X * videoRect.Width), videoRect.Y + (pt.Y * videoRect.Height)));
        }

        if (pts.Count >= 3)
        {
            polyline.Points.Add(new Point(videoRect.X + (pts[0].X * videoRect.Width), videoRect.Y + (pts[0].Y * videoRect.Height)));
        }

        OverlayCanvas.Children.Add(polyline);
    }

    private FrameMarker? GetFrameAtOrBefore(double playbackSec)
    {
        if (_frames.Count == 0)
        {
            return null;
        }

        FrameMarker? result = null;
        foreach (var frame in _frames)
        {
            if (frame.FrameTimeSec <= playbackSec)
            {
                result = frame;
                continue;
            }

            break;
        }

        return result ?? _frames[0];
    }

    private void RefreshAfterClipMutation(string clipIdToReselect)
    {
        if (_selectedNode is null)
        {
            return;
        }

        LoadHierarchy();
        LoadTimelineForNode(_selectedNode);

        var row = TimelineRows.FirstOrDefault(x => !x.IsGapRow && x.Clip?.ClipId == clipIdToReselect);
        if (row is not null)
        {
            TimelineDataGrid.SelectedItem = row;
            return;
        }

        _selectedClip = null;
        SetPlayerPlaying(false);
        UpdateSelectedClipInfo(null);
    }

    private string? ResolveEnginePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "engine", "engine.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var fallback = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "engine_dist", "engine", "engine.exe"));
        return File.Exists(fallback) ? fallback : null;
    }

    private string? ResolveViewerDistributionDirectory()
    {
        var bundledViewerFolder = Path.Combine(AppContext.BaseDirectory, "viewer");
        if (Directory.Exists(bundledViewerFolder))
        {
            return bundledViewerFolder;
        }

        var sameFolderViewerExe = Path.Combine(AppContext.BaseDirectory, "ReefCams.Viewer.exe");
        if (File.Exists(sameFolderViewerExe))
        {
            return AppContext.BaseDirectory;
        }

        return null;
    }

    private void TryAutoLoadLastProject()
    {
        var state = LoadUiState();
        if (state is null || string.IsNullOrWhiteSpace(state.LastProjectDirectory))
        {
            return;
        }

        if (!Directory.Exists(state.LastProjectDirectory))
        {
            return;
        }

        try
        {
            InitializeProject(state.LastProjectDirectory);
            StatusText = $"Auto-loaded project: {state.LastProjectDirectory}";
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-load failed: {ex.Message}";
        }
    }

    private static string GetUiStatePath() => Path.Combine(AppContext.BaseDirectory, "processor_ui_state.json");

    private ProcessorUiState? LoadUiState()
    {
        var path = GetUiStatePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ProcessorUiState>(json);
        }
        catch
        {
            return null;
        }
    }

    private void SaveUiState(string projectDirectory)
    {
        try
        {
            var state = new ProcessorUiState { LastProjectDirectory = projectDirectory };
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetUiStatePath(), json);
        }
        catch
        {
            // Ignore non-critical UI state save errors.
        }
    }

    private Rect GetVideoRenderRect(double containerWidth, double containerHeight)
    {
        var naturalWidth = PlayerElement.NaturalVideoWidth;
        var naturalHeight = PlayerElement.NaturalVideoHeight;
        if (naturalWidth <= 0 || naturalHeight <= 0 || containerWidth <= 0 || containerHeight <= 0)
        {
            return new Rect(0, 0, Math.Max(0, containerWidth), Math.Max(0, containerHeight));
        }

        var scale = Math.Min(containerWidth / naturalWidth, containerHeight / naturalHeight);
        var renderWidth = naturalWidth * scale;
        var renderHeight = naturalHeight * scale;
        var offsetX = (containerWidth - renderWidth) / 2;
        var offsetY = (containerHeight - renderHeight) / 2;
        return new Rect(offsetX, offsetY, renderWidth, renderHeight);
    }

    private bool TryNormalizePointFromOverlay(Point overlayPoint, out Point normalized)
    {
        normalized = default;
        var videoRect = GetVideoRenderRect(OverlayCanvas.ActualWidth, OverlayCanvas.ActualHeight);
        if (videoRect.Width <= 0 || videoRect.Height <= 0)
        {
            return false;
        }

        var rx = (overlayPoint.X - videoRect.X) / videoRect.Width;
        var ry = (overlayPoint.Y - videoRect.Y) / videoRect.Height;
        if (rx < 0 || rx > 1 || ry < 0 || ry > 1)
        {
            return false;
        }

        normalized = new Point(Math.Clamp(rx, 0, 1), Math.Clamp(ry, 0, 1));
        return true;
    }

    private bool AppendBoundaryDragPoint(Point normalized, bool force)
    {
        if (!force && _hasLastBoundaryDragPoint)
        {
            var dx = normalized.X - _lastBoundaryDragPoint.X;
            var dy = normalized.Y - _lastBoundaryDragPoint.Y;
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < BoundaryDragMinSpacingNorm)
            {
                return false;
            }
        }

        _draftBoundaryPoints.Add(normalized);
        _currentBoundaryStrokePointCount++;
        _lastBoundaryDragPoint = normalized;
        _hasLastBoundaryDragPoint = true;
        return true;
    }

    private void CommitBoundaryStroke()
    {
        if (!_isBoundaryDragActive)
        {
            return;
        }

        if (_currentBoundaryStrokePointCount > 0)
        {
            _draftBoundaryStrokeSizes.Add(_currentBoundaryStrokePointCount);
        }

        OverlayCanvas.ReleaseMouseCapture();
        ResetBoundaryDragState();
    }

    private void ResetBoundaryDragState()
    {
        OverlayCanvas.ReleaseMouseCapture();
        _isBoundaryDragActive = false;
        _currentBoundaryStrokePointCount = 0;
        _hasLastBoundaryDragPoint = false;
        _lastBoundaryDragPoint = default;
    }

    private void UpdateBoundaryEditControls()
    {
        var visibility = _boundaryEditMode ? Visibility.Visible : Visibility.Collapsed;
        BoundaryRemoveLastPointButton.Visibility = visibility;
        BoundaryClearButton.Visibility = visibility;
        BoundarySaveButton.Visibility = visibility;
        DefineBoundaryButton.Content = _boundaryEditMode ? "Cancel Reef Boundry Edit" : "Edit Reef Boundry";
    }

    private void ShowFrameAt(double seconds)
    {
        if (!PlayerElement.NaturalDuration.HasTimeSpan)
        {
            return;
        }

        var max = PlayerElement.NaturalDuration.TimeSpan.TotalSeconds;
        var clamped = Math.Max(0, Math.Min(seconds, max));
        _seekRenderVersion++;
        var version = _seekRenderVersion;
        SeekSlider.Value = clamped;
        PlayerElement.Position = TimeSpan.FromSeconds(clamped);
        PlayerElement.Play();
        SetPlayerPlaying(false);

        Dispatcher.BeginInvoke(
            new Action(
                async () =>
                {
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    await Task.Delay(90);

                    if (version != _seekRenderVersion)
                    {
                        return;
                    }

                    PlayerElement.Pause();
                    SetPlayerPlaying(false);
                    PlayerElement.Position = TimeSpan.FromSeconds(clamped);
                    SeekSlider.Value = clamped;
                    RenderOverlay();
                }),
            DispatcherPriority.Background);
    }

    private CancellationToken BeginOperation(string operationName)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException("Another operation is already running.");
        }

        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = $"Running {operationName}...";
        UpdateTopActionButtonVisuals();
        return _operationCts.Token;
    }

    private void EndOperation()
    {
        _operationCts?.Dispose();
        _operationCts = null;
        IsBusy = false;
        IsProgressIndeterminate = false;
        if (OperationProgressText.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            OperationProgressText = "Idle";
        }

        UpdateTopActionButtonVisuals();
    }

    private void ApplyViewMode(bool isViewerMode)
    {
        _isViewerMode = isViewerMode;
        var collapsed = isViewerMode ? Visibility.Collapsed : Visibility.Visible;
        ProcessScopeButton.Visibility = (!_isBulkCompletionMode && !isViewerMode) ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompletionButton.Visibility = (_isBulkCompletionMode && !isViewerMode) ? Visibility.Visible : Visibility.Collapsed;
        BenchmarkButton.Visibility = collapsed;
        ExportButton.Visibility = collapsed;
        StopOperationButton.Visibility = collapsed;
        ScopeRow.Visibility = collapsed;
        ScopeListBorder.Visibility = collapsed;

        // Keep a compact sidebar so playback gets more horizontal space.
        SidebarColumnDef.Width = isViewerMode ? new GridLength(420) : new GridLength(460);
        if (isViewerMode)
        {
            ProjectTreeExpander.IsExpanded = false;
        }

        ProjectTreeHeaderText = isViewerMode ? "Project Tree" : "Project Tree (select node then Add Selected Scope)";
        UpdateTopActionButtonVisuals();
    }

    private void SetBulkCompletionMode(bool enabled, bool updateStatus = true)
    {
        _isBulkCompletionMode = enabled;
        AddUpToSelectedClipScopeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        AddSelectedScopeButton.Content = enabled ? "Add Selected Scope (All Clips)" : "Add Selected Scope";
        ClearScopeListButton.Content = enabled ? "Clear Completion List" : "Clear Scope List";
        ProcessScopeButton.Visibility = (!_isViewerMode && !enabled) ? Visibility.Visible : Visibility.Collapsed;
        ApplyCompletionButton.Visibility = (!_isViewerMode && enabled) ? Visibility.Visible : Visibility.Collapsed;
        UpdateScopeSelectionSummary();
        OnPropertyChanged(nameof(ActiveScopeOptions));
        UpdateTopActionButtonVisuals();
        if (updateStatus)
        {
            StatusText = enabled
                ? "Bulk completion mode enabled. Add scope entries and apply completion."
                : "Bulk completion mode disabled.";
        }
    }

    private void UpdateScopeSelectionSummary()
    {
        UpdateScopeOptionDisplayLabels();

        if (_isBulkCompletionMode)
        {
            var checkedCount = CompletionScopes.Count(x => x.IsChecked);
            ScopeSelectionSummary = $"Completion scope list: {checkedCount}/{CompletionScopes.Count} checked";
        }
        else
        {
            var checkedCount = ProcessingScopes.Count(x => x.IsChecked);
            ScopeSelectionSummary = $"Processing scope list: {checkedCount}/{ProcessingScopes.Count} checked";
        }

        OnPropertyChanged(nameof(ActiveScopeOptions));
        UpdateTopActionButtonVisuals();
    }

    private void UpdateScopeOptionDisplayLabels()
    {
        UpdateScopeOptionDisplayLabels(ProcessingScopes);
        UpdateScopeOptionDisplayLabels(CompletionScopes);
    }

    private void UpdateScopeOptionDisplayLabels(IEnumerable<ProcessingScopeOption> options)
    {
        if (_repository is null)
        {
            return;
        }

        foreach (var option in options)
        {
            var timeline = GetScopeTimelineForOption(option);
            var total = timeline.Count;
            var passing = timeline.Count(x => x.Processed && x.MaxConf >= MinVisibleConfidence);

            option.TotalClipCount = total;
            option.PassingClipCount = passing;
            option.DisplayLabel = $"{option.BaseLabel} ({total} total, {passing} >= {FormatConfidence(MinVisibleConfidence)})";
        }
    }

    private IReadOnlyList<ClipTimelineItem> GetScopeTimelineForOption(ProcessingScopeOption option)
    {
        if (_repository is null)
        {
            return [];
        }

        var timeline = ApplyPresenceFilter(_repository.GetTimeline(option.Scope));
        if (option.SelectionMode != ScopeSelectionMode.UpToClip || string.IsNullOrWhiteSpace(option.UpToClipId))
        {
            return timeline;
        }

        var upToClip = timeline.FirstOrDefault(x => x.ClipId.Equals(option.UpToClipId, StringComparison.OrdinalIgnoreCase));
        if (upToClip is null)
        {
            return [];
        }

        return timeline.Where(x => IsClipAtOrBefore(x, upToClip)).ToList();
    }

    private void UpdateTopActionButtonVisuals()
    {
        // View mode toggle buttons.
        ApplyButtonVisual(ShowProcessingViewButton, !_isViewerMode ? "#5A80AE" : "#365172", !_isViewerMode ? "#BFDFFF" : "#76A6D8", "#FFFFFF");
        ApplyButtonVisual(ShowViewerViewButton, _isViewerMode ? "#5A80AE" : "#365172", _isViewerMode ? "#BFDFFF" : "#76A6D8", "#FFFFFF");

        // Processing action button turns green when any processing scope is checked.
        var hasClipsSelected = ProcessingScopes.Any(x => x.IsChecked);
        ApplyButtonVisual(
            ProcessScopeButton,
            hasClipsSelected ? "#2F8A57" : "#365172",
            hasClipsSelected ? "#9AD9B7" : "#76A6D8",
            "#FFFFFF");

        // Completion action button turns green when any completion scope is checked.
        var hasCompletionSelected = CompletionScopes.Any(x => x.IsChecked);
        ApplyButtonVisual(
            ApplyCompletionButton,
            hasCompletionSelected ? "#2F8A57" : "#365172",
            hasCompletionSelected ? "#9AD9B7" : "#76A6D8",
            "#FFFFFF");

        // Stop button is red only while an operation is active.
        ApplyButtonVisual(
            StopOperationButton,
            IsBusy ? "#C24A4A" : "#365172",
            IsBusy ? "#F2A0A0" : "#76A6D8",
            "#FFFFFF");
    }

    private static void ApplyButtonVisual(Button button, string backgroundHex, string borderHex, string foregroundHex)
    {
        button.Background = (Brush)new BrushConverter().ConvertFromString(backgroundHex)!;
        button.BorderBrush = (Brush)new BrushConverter().ConvertFromString(borderHex)!;
        button.Foreground = (Brush)new BrushConverter().ConvertFromString(foregroundHex)!;
    }

    private void TryRestoreSelectedNode(ScopeFilter? previousScope)
    {
        ClearSelectionFlags(HierarchyNodes);

        if (previousScope is null)
        {
            _selectedNode = null;
            _selectedTreeScope = null;
            return;
        }

        var node = FindNode(HierarchyNodes, previousScope.Value);
        if (node is null)
        {
            _selectedNode = null;
            _selectedTreeScope = null;
            SelectedTreeScopeText = "Tree selection: none";
            TimelineScopeText = "Timeline scope: - - -";
            return;
        }

        node.IsSelected = true;
        _selectedNode = node;
        _selectedTreeScope = node.Scope;
        SelectedTreeScopeText = $"Tree selection: {BuildScopeLabel(node)}";
        TimelineScopeText = BuildTimelineScopeText(node.Scope);
    }

    private static HierarchyNode? FindNode(IEnumerable<HierarchyNode> nodes, ScopeFilter scope)
    {
        foreach (var node in nodes)
        {
            if (node.Scope.Equals(scope))
            {
                return node;
            }

            var child = FindNode(node.Children, scope);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static string ScopeKey(ScopeFilter scope) => $"{scope.Type}|{scope.RootId}|{scope.Site}|{scope.Dcim}|{scope.Session}";

    private static HashSet<string> CaptureExpandedScopeKeys(IEnumerable<HierarchyNode> nodes)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.IsExpanded)
            {
                expanded.Add(ScopeKey(node.Scope));
            }

            foreach (var childKey in CaptureExpandedScopeKeys(node.Children))
            {
                expanded.Add(childKey);
            }
        }

        return expanded;
    }

    private static void ApplyExpandedScopeKeys(IEnumerable<HierarchyNode> nodes, ISet<string> expandedScopeKeys)
    {
        foreach (var node in nodes)
        {
            node.IsExpanded = expandedScopeKeys.Contains(ScopeKey(node.Scope));
            ApplyExpandedScopeKeys(node.Children, expandedScopeKeys);
        }
    }

    private static void ClearSelectionFlags(IEnumerable<HierarchyNode> nodes)
    {
        foreach (var node in nodes)
        {
            node.IsSelected = false;
            ClearSelectionFlags(node.Children);
        }
    }

    private HashSet<string> CollectCompletionTargetClipIds(IEnumerable<ProcessingScopeOption> options)
    {
        var clipIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_repository is null)
        {
            return clipIds;
        }

        foreach (var option in options)
        {
            var timeline = _repository.GetTimeline(option.Scope).ToList();
            if (timeline.Count == 0)
            {
                continue;
            }

            if (option.SelectionMode == ScopeSelectionMode.UpToClip && !string.IsNullOrWhiteSpace(option.UpToClipId))
            {
                var upToClip = timeline.FirstOrDefault(x => x.ClipId.Equals(option.UpToClipId, StringComparison.OrdinalIgnoreCase));
                if (upToClip is null)
                {
                    continue;
                }

                foreach (var clip in timeline.Where(x => IsClipAtOrBefore(x, upToClip)))
                {
                    clipIds.Add(clip.ClipId);
                }

                continue;
            }

            foreach (var clip in timeline)
            {
                clipIds.Add(clip.ClipId);
            }
        }

        return clipIds;
    }

    private static bool IsClipAtOrBefore(ClipTimelineItem clip, ClipTimelineItem upToClip)
    {
        if (clip.CreatedAtUtc != DateTimeOffset.MinValue && upToClip.CreatedAtUtc != DateTimeOffset.MinValue)
        {
            var cmpCreated = clip.CreatedAtUtc.CompareTo(upToClip.CreatedAtUtc);
            if (cmpCreated < 0)
            {
                return true;
            }

            if (cmpCreated > 0)
            {
                return false;
            }
        }

        var cmpName = string.Compare(clip.ClipName, upToClip.ClipName, StringComparison.OrdinalIgnoreCase);
        if (cmpName < 0)
        {
            return true;
        }

        if (cmpName > 0)
        {
            return false;
        }

        return string.Compare(clip.ClipId, upToClip.ClipId, StringComparison.OrdinalIgnoreCase) <= 0;
    }

    private static string ScopeDisplay(ScopeFilter scope)
    {
        return scope.Type switch
        {
            TreeNodeType.Root => $"Root: {scope.RootId}",
            TreeNodeType.Site => $"Site: {scope.Site}",
            TreeNodeType.Dcim => $"Site/DCIM: {scope.Site}/{scope.Dcim}",
            TreeNodeType.Session => $"Session: {scope.Site}/{scope.Dcim}/{scope.Session}",
            _ => scope.RootId
        };
    }

    private static string BuildTimelineScopeText(ScopeFilter scope)
    {
        var site = string.IsNullOrWhiteSpace(scope.Site) ? "-" : scope.Site!;
        var folder = string.IsNullOrWhiteSpace(scope.Dcim) ? "-" : scope.Dcim!;
        var subfolder = string.IsNullOrWhiteSpace(scope.Session) ? "-" : scope.Session!;
        return $"Timeline scope: {site} - {folder} - {subfolder}";
    }

    private void UpdateSelectedClipInfo(ClipTimelineItem? clip)
    {
        if (clip is null)
        {
            SelectedClipNameText = "Selected clip: none";
            SelectedClipInfoText = string.Empty;
            UpdateCompletionToggleButtonState();
            return;
        }

        SelectedClipNameText = BuildQualifiedClipName(clip);
        var timestamp = clip.CreatedAtUtc == DateTimeOffset.MinValue
            ? "-"
            : clip.CreatedAtUtc.UtcDateTime.ToString("MM/dd/yy HH:mm:ss", CultureInfo.InvariantCulture);
        var maxConf = clip.Processed
            ? clip.MaxConf.ToString("0.000", CultureInfo.InvariantCulture)
            : "-";
        var delta = clip.DeltaFromPreviousSec.HasValue
            ? FormatDelta(clip.DeltaFromPreviousSec.Value)
            : "-";
        var processed = clip.Processed ? "Yes" : "No";
        var completed = clip.Completed ? "Yes" : "No";
        SelectedClipInfoText = $"{timestamp} | confidence {maxConf} | processed {processed} | completed {completed} | delta {delta}";
        UpdateCompletionToggleButtonState();
    }

    private string ResolveClipPathForCurrentProject(string clipPath)
    {
        if (Path.IsPathRooted(clipPath))
        {
            return Path.GetFullPath(clipPath);
        }

        if (_projectPaths is null)
        {
            return Path.GetFullPath(clipPath);
        }

        return Path.GetFullPath(Path.Combine(_projectPaths.RootDirectory, clipPath));
    }

    private List<HierarchyNode> FilterHierarchyToPresentClips(IEnumerable<HierarchyNode> roots)
    {
        var filtered = new List<HierarchyNode>();
        foreach (var root in roots)
        {
            if (!IsRootCurrentlyPresent(root.Scope.RootId))
            {
                continue;
            }

            if (PruneMissingHierarchyNode(root))
            {
                filtered.Add(root);
            }
        }

        return filtered;
    }

    private bool PruneMissingHierarchyNode(HierarchyNode node)
    {
        if (node.NodeType == TreeNodeType.Session)
        {
            return HasAnyPresentClipInScope(node.Scope);
        }

        for (var i = node.Children.Count - 1; i >= 0; i--)
        {
            if (PruneMissingHierarchyNode(node.Children[i]))
            {
                continue;
            }

            node.Children.RemoveAt(i);
        }

        return node.Children.Count > 0;
    }

    private bool HasAnyPresentClipInScope(ScopeFilter scope)
    {
        if (_repository is null)
        {
            return false;
        }

        var key = ScopeKey(scope);
        if (_scopePresenceByScopeKey.TryGetValue(key, out var cached))
        {
            return cached;
        }

        foreach (var clip in _repository.GetTimeline(scope))
        {
            if (!IsClipCurrentlyPresent(clip))
            {
                continue;
            }

            _scopePresenceByScopeKey[key] = true;
            return true;
        }

        _scopePresenceByScopeKey[key] = false;
        return false;
    }

    private bool IsRootCurrentlyPresent(string rootId)
    {
        if (rootId.StartsWith(RootGroupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var groupedName = rootId[RootGroupPrefix.Length..];
            foreach (var (actualRootId, displayName) in _rootDisplayNames)
            {
                if (!string.Equals(displayName.Trim(), groupedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsRootCurrentlyPresent(actualRootId))
                {
                    return true;
                }
            }

            return false;
        }

        if (_rootPresenceById.TryGetValue(rootId, out var present))
        {
            return present;
        }

        if (!_rootPathById.TryGetValue(rootId, out var rootPath))
        {
            return true;
        }

        present = Directory.Exists(rootPath);
        _rootPresenceById[rootId] = present;
        return present;
    }

    private bool IsClipCurrentlyPresent(ClipTimelineItem clip)
    {
        if (!IsRootCurrentlyPresent(clip.RootId))
        {
            return false;
        }

        var resolvedPath = ResolveClipPathForCurrentProject(clip.ClipPath);
        if (_clipPresenceCache.TryGetValue(resolvedPath, out var cached))
        {
            return cached;
        }

        var exists = File.Exists(resolvedPath);
        _clipPresenceCache[resolvedPath] = exists;
        return exists;
    }

    private List<ClipTimelineItem> ApplyPresenceFilter(IEnumerable<ClipTimelineItem> timeline)
    {
        var list = HideMissingClips
            ? timeline.Where(IsClipCurrentlyPresent).ToList()
            : timeline.ToList();

        ClipTimelineItem? previous = null;
        foreach (var clip in list)
        {
            if (previous is null ||
                clip.CreatedAtUtc == DateTimeOffset.MinValue ||
                previous.CreatedAtUtc == DateTimeOffset.MinValue)
            {
                clip.DeltaFromPreviousSec = null;
            }
            else
            {
                clip.DeltaFromPreviousSec = (clip.CreatedAtUtc - previous.CreatedAtUtc).TotalSeconds;
            }

            previous = clip;
        }

        return list;
    }

    private string BuildQualifiedClipName(ClipTimelineItem clip)
    {
        var root = _rootDisplayNames.TryGetValue(clip.RootId, out var rootName) && !string.IsNullOrWhiteSpace(rootName)
            ? rootName
            : clip.RootId;
        return $"{root}/{clip.Site}/{clip.Dcim}/{clip.Session}/{clip.ClipName}";
    }

    private string? ResolveBenchmarkClipPath()
    {
        if (!string.IsNullOrWhiteSpace(_selectedClip?.ClipPath))
        {
            var selected = ResolveClipPathForCurrentProject(_selectedClip.ClipPath);
            if (File.Exists(selected))
            {
                return selected;
            }
        }

        var timelineCandidate = _timelineAll.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.ClipPath))?.ClipPath;
        if (!string.IsNullOrWhiteSpace(timelineCandidate))
        {
            var candidate = ResolveClipPathForCurrentProject(timelineCandidate);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (_repository is null)
        {
            return null;
        }

        foreach (var root in _repository.GetClipRoots())
        {
            var scope = new ScopeFilter(TreeNodeType.Root, root.RootId);
            var rootClipPath = _repository.GetTimeline(scope)
                .Select(x => x.ClipPath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(ResolveClipPathForCurrentProject(path)));
            if (!string.IsNullOrWhiteSpace(rootClipPath))
            {
                return ResolveClipPathForCurrentProject(rootClipPath);
            }
        }

        return null;
    }

    private static string BuildScopeLabel(HierarchyNode node)
    {
        return node.NodeType switch
        {
            TreeNodeType.Root => $"Root: {node.Name}",
            TreeNodeType.Site => $"Site: {node.Name}",
            TreeNodeType.Dcim => $"DCIM: {node.Scope.Site}/{node.Name}",
            TreeNodeType.Session => $"Session: {node.Scope.Site}/{node.Scope.Dcim}/{node.Name}",
            _ => node.Name
        };
    }

    private static string FrameKey(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private bool TryApplyMinVisibleConfidenceFromText(bool showError)
    {
        if (!TryParseConfidence(MinVisibleConfidenceText, out var parsed))
        {
            if (showError)
            {
                MessageBox.Show(this, "Minimum confidence must be a number greater than or equal to 0 and less than 1.", "ReefCams");
            }

            MinVisibleConfidenceText = FormatConfidence(MinVisibleConfidence);
            return false;
        }

        var normalized = NormalizeMinVisibleConfidence(parsed);
        MinVisibleConfidence = normalized;
        MinVisibleConfidenceText = FormatConfidence(normalized);
        _config.Timeline.MinVisibleConfidence = normalized;
        SaveConfig();
        LoadHierarchy();
        UpdateScopeSelectionSummary();
        RebuildTimelineRows();
        return true;
    }

    private void SaveConfig()
    {
        if (_projectPaths is null)
        {
            return;
        }

        _configService.Save(_projectPaths, _config);
    }

    private static bool TryParseConfidence(string raw, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value < 1)
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static double NormalizeMinVisibleConfidence(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.4;
        }

        if (value < 0)
        {
            return 0;
        }

        if (value >= 1)
        {
            return 0.999999;
        }

        return value;
    }

    private static string FormatConfidence(double value)
    {
        return value.ToString("0.################", CultureInfo.InvariantCulture);
    }

    private double GetDefaultOverlayThreshold(ClipTimelineItem clip)
    {
        _ = clip;
        return NormalizeMinVisibleConfidence(_config.Timeline.MinVisibleConfidence);
    }

    private static string FormatDelta(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
        }

        if (ts.TotalMinutes >= 1)
        {
            return $"{ts.Minutes}m {ts.Seconds}s";
        }

        return $"{ts.Seconds}s";
    }

    private sealed class ProcessorUiState
    {
        public string LastProjectDirectory { get; set; } = string.Empty;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
