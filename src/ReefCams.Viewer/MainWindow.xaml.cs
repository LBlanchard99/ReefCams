using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

namespace ReefCams.Viewer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const string RootGroupPrefix = "@group:";
    private const double BoundaryDragMinSpacingNorm = 0.003;
    private const double TimelineTextColumnExtraWidthPx = 5.0;
    private const double DetectionBoxPaddingPx = 4.0;
    private readonly ConfigService _configService = new();
    private readonly DispatcherTimer _playbackTimer;
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
    private string? _currentSessionScopeId;
    private bool _isSeekDragging;
    private bool _boundaryEditMode;
    private bool _isBoundaryDragActive;
    private int _currentBoundaryStrokePointCount;
    private bool _hasLastBoundaryDragPoint;
    private Point _lastBoundaryDragPoint;
    private bool _isPlayerPlaying;
    private bool _isRebuildingTimeline;
    private int _seekRenderVersion;
    private string _projectDirDisplay = "Project directory not selected";
    private string _statusText = string.Empty;
    private string _selectedClipNameText = "Selected clip: none";
    private string _selectedClipInfoText = string.Empty;
    private string _overlayConfidenceText = "0.100";
    private string _timelineScopeText = "Timeline scope: - - -";
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
        Dispatcher.BeginInvoke(new Action(TryAutoLoadPackagedProject), DispatcherPriority.Background);
    }

    public ObservableCollection<HierarchyNode> HierarchyNodes { get; } = [];
    public ObservableCollection<TimelineDisplayRow> TimelineRows { get; } = [];

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

    public string SelectedClipNameText
    {
        get => _selectedClipNameText;
        private set => SetField(ref _selectedClipNameText, value);
    }

    public string SelectedClipInfoText
    {
        get => _selectedClipInfoText;
        private set => SetField(ref _selectedClipInfoText, value);
    }

    public string OverlayConfidenceText
    {
        get => _overlayConfidenceText;
        private set => SetField(ref _overlayConfidenceText, value);
    }

    public string TimelineScopeText
    {
        get => _timelineScopeText;
        private set => SetField(ref _timelineScopeText, value);
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

        InitializeProject(dialog.SelectedPath);
    }

    private void AddClipRoot_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
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

        _repository.UpsertClipRoot(dialog.SelectedPath);
        LoadHierarchy();
        StatusText = $"Clip root added: {dialog.SelectedPath}";
    }

    private async void IndexRoots_Click(object sender, RoutedEventArgs e)
    {
        if (_repository is null || _indexer is null)
        {
            MessageBox.Show(this, "Choose a project directory first.", "ReefCams");
            return;
        }

        var roots = _repository.GetClipRoots();
        if (roots.Count == 0)
        {
            MessageBox.Show(this, "No clip roots are configured.", "ReefCams");
            return;
        }

        try
        {
            var total = 0;
            foreach (var root in roots)
            {
                var progress = new Progress<IndexProgress>(p => StatusText = $"Indexing: {p.ProcessedCount} files ({p.CurrentPath})");
                total += await _indexer.IndexClipRootAsync(root.RootPath, progress);
            }

            var merged = _repository.MergeDuplicateClipsByPath();
            StatusText = merged > 0
                ? $"Index complete: {total} clips indexed ({merged} duplicate clips merged)"
                : $"Index complete: {total} clips indexed";
            LoadHierarchy();
            if (_selectedNode is not null)
            {
                LoadTimelineForNode(_selectedNode);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Indexing failed:\n{ex.Message}", "ReefCams");
            StatusText = "Indexing failed";
        }
    }

    private void HierarchyTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not HierarchyNode node)
        {
            return;
        }

        _selectedNode = node;
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
        RebuildTimelineRows();
    }

    private void HideMissingClips_Changed(object sender, RoutedEventArgs e)
    {
        _config.Timeline.HideMissingClips = HideMissingClips;
        SaveConfig();
        LoadHierarchy();
        if (_selectedNode is not null)
        {
            LoadTimelineForNode(_selectedNode);
            return;
        }

        _timelineAll = [];
        _selectedClip = null;
        SetPlayerPlaying(false);
        UpdateSelectedClipInfo(null);
        UpdateCompletionToggleButtonState();
        RebuildTimelineRows();
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
        ShowFrameAt(0);
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
        TimelineScopeText = "Timeline scope: - - -";
        _selectedClip = null;
        SetPlayerPlaying(false);
        _boundaryEditMode = false;
        _draftBoundaryPoints.Clear();
        _draftBoundaryStrokeSizes.Clear();
        ResetBoundaryDragState();
        UpdateBoundaryEditControls();
        UpdateSelectedClipInfo(null);
        UpdateCompletionToggleButtonState();

        LoadHierarchy();
        TimelineRows.Clear();
        SaveConfig();
    }

    private void LoadHierarchy()
    {
        if (_repository is null)
        {
            return;
        }

        RefreshRootDisplayNames();
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
            UpdateCompletionToggleButtonState();
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
            UpdateCompletionToggleButtonState();
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
                    AddGapRow(hidden, nextVisibleClip: clip);
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
        UpdateCompletionToggleButtonState();
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
        var resolvedClipPath = ResolveClipPathForPlayback(clip.ClipPath);
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
        UpdateCompletionToggleButtonState();
    }

    private static string FrameKey(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

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

    private void TryAutoLoadPackagedProject()
    {
        var baseDir = AppContext.BaseDirectory;
        var dbPath = Path.Combine(baseDir, "data", "app.db");
        if (!File.Exists(dbPath))
        {
            return;
        }

        try
        {
            InitializeProject(baseDir);
            StatusText = $"Auto-loaded packaged project: {baseDir}";
        }
        catch (Exception ex)
        {
            StatusText = $"Auto-load failed: {ex.Message}";
        }
    }

    private string ResolveClipPathForPlayback(string clipPath)
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

        var resolvedPath = ResolveClipPathForPlayback(clip.ClipPath);
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

    private static string BuildTimelineScopeText(ScopeFilter scope)
    {
        var site = string.IsNullOrWhiteSpace(scope.Site) ? "-" : scope.Site;
        var folder = string.IsNullOrWhiteSpace(scope.Dcim) ? "-" : scope.Dcim;
        var subfolder = string.IsNullOrWhiteSpace(scope.Session) ? "-" : scope.Session;
        return $"Timeline scope: {site} - {folder} - {subfolder}";
    }

    private static string ScopeKey(ScopeFilter scope) => $"{scope.Type}|{scope.RootId}|{scope.Site}|{scope.Dcim}|{scope.Session}";

    private void UpdateSelectedClipInfo(ClipTimelineItem? clip)
    {
        if (clip is null)
        {
            SelectedClipNameText = "Selected clip: none";
            SelectedClipInfoText = string.Empty;
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
    }

    private string BuildQualifiedClipName(ClipTimelineItem clip)
    {
        var root = _rootDisplayNames.TryGetValue(clip.RootId, out var rootName) && !string.IsNullOrWhiteSpace(rootName)
            ? rootName
            : clip.RootId;
        return $"{root}/{clip.Site}/{clip.Dcim}/{clip.Session}/{clip.ClipName}";
    }

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
