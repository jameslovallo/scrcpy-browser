using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ScrcpyLauncher.Models;

public sealed class AndroidApp : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _isHidden;

    public AndroidApp(string name, string package, bool isSystem)
    {
        Name = name;
        Package = package;
        IsSystem = isSystem;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public string Package { get; }
    public bool IsSystem { get; }
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : char.ToUpperInvariant(Name[0]).ToString();
    public string HiddenActionText => _isHidden ? "Show" : "Hide";

    public bool IsHidden
    {
        get => _isHidden;
        set
        {
            if (_isHidden == value) return;
            _isHidden = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHidden)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HiddenActionText)));
        }
    }

    /// <summary>App icon; null until loaded (the UI shows <see cref="Initial"/> meanwhile).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InitialVisibility)));
        }
    }

    public Visibility InitialVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Must be called on the UI thread.</summary>
    public void SetIconFile(string path) =>
        Icon = new BitmapImage { DecodePixelWidth = 64, UriSource = new Uri(path) };

    public override string ToString() => Name;
}
