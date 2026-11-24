using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace ScrcpyLauncher.Models;

/// <summary>A pinned app in the sidebar grid, with its active (on screen) state.</summary>
public sealed class PinnedTile : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isRunning;

    public PinnedTile(AndroidApp app) => App = app;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AndroidApp App { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveVisibility)));
        }
    }

    public Visibility ActiveVisibility => _isActive ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
        }
    }

    public Visibility RunningVisibility => _isRunning ? Visibility.Visible : Visibility.Collapsed;

    public override string ToString() => App.Name;
}
