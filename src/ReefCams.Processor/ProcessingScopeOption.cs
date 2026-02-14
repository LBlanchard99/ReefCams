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

    public ScopeFilter Scope { get; init; }
    public string ScopeKey { get; init; } = string.Empty;
    public string DisplayLabel { get; init; } = string.Empty;
    public ScopeSelectionMode SelectionMode { get; init; } = ScopeSelectionMode.FullScope;
    public string UpToClipId { get; init; } = string.Empty;

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
