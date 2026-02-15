using System.ComponentModel;
using System.Runtime.CompilerServices;
using ReefCams.Core;

namespace ReefCams.Processor;

public enum ScopeSelectionMode
{
    FullScope,
    UpToClip
}

public sealed class ProcessingScopeOption : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private string _displayLabel = string.Empty;

    public ScopeFilter Scope { get; init; }
    public string ScopeKey { get; init; } = string.Empty;
    public string BaseLabel { get; init; } = string.Empty;
    public ScopeSelectionMode SelectionMode { get; init; } = ScopeSelectionMode.FullScope;
    public string UpToClipId { get; init; } = string.Empty;
    public int TotalClipCount { get; set; }
    public int PassingClipCount { get; set; }

    public string DisplayLabel
    {
        get => _displayLabel;
        set
        {
            if (_displayLabel == value)
            {
                return;
            }

            _displayLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
