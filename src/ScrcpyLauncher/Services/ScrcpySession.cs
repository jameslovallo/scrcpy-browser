using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScrcpyLauncher.Interop;
using ScrcpyLauncher.Models;

namespace ScrcpyLauncher.Services;

/// <summary>
/// One scrcpy process mirroring a single app on its own flexible virtual display
/// (--new-display -x), with a borderless window that gets embedded in the launcher.
/// </summary>
public sealed partial class ScrcpySession : INotifyPropertyChanged
{
    private const int MaxLogLines = 50;

    private readonly ConcurrentQueue<string> _log = new();
    private Process? _process;
    private string _status = "Starting…";

    public ScrcpySession(AndroidApp app, string serial, bool captureMouse = false, int displayDpi = 0)
    {
        App = app;
        Serial = serial;
        CaptureMouse = captureMouse;
        DisplayDpi = displayDpi;
        WindowTitle = $"ScrcpyLauncher-{Guid.NewGuid():N}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on a background thread when the scrcpy process exits.</summary>
    public event EventHandler? Exited;

    public AndroidApp App { get; }
    public string Serial { get; }
    public string WindowTitle { get; }
    public bool CaptureMouse { get; }

    /// <summary>Density forwarded to the virtual display (Android dpi); 0 means scrcpy's default.</summary>
    public int DisplayDpi { get; }
    public string Name => App.Name;
    public string Initial => App.Initial;
    public IntPtr WindowHandle { get; private set; }

    /// <summary>Android display id of the session's virtual display, parsed from the scrcpy log.</summary>
    public int? DisplayId { get; private set; }

    public bool IsEmbedded => WindowHandle != IntPtr.Zero;
    public bool HasExited => _process?.HasExited ?? true;

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string RecentLog => string.Join(Environment.NewLine, _log);

    /// <summary>scrcpy command-line arguments (excluding user extras) for this session.</summary>
    public IReadOnlyList<string> BuildArguments() =>
    [
        .. CaptureMouse ? ["-M"] : Array.Empty<string>(),
        $"--serial={Serial}",
        // Density follows the host display scaling. No size: the flex display (-x) follows the window.
        DisplayDpi > 0 ? $"--new-display=/{DisplayDpi}" : "--new-display",
        "-x",
        "--no-vd-system-decorations",
        "--window-borderless",
        $"--window-title={WindowTitle}",
        $"--start-app={App.Package}",
        // Start off-screen so the window doesn't flash before it is embedded.
        "--window-x=-20000",
        "--window-y=-20000",
        // Keyboard: simulated physical keyboard (UHID).
        "-K",
        // Audio is never forwarded.
        "--no-audio",
        // Right click forwarded, middle = HOME, 4th (mouse back) = BACK, 5th = notifications;
        // with Shift: the scrcpy defaults.
        "--mouse-bind=+hbn:bhsn",
    ];

    public void Start(string scrcpyPath, IEnumerable<string> extraArgs)
    {
        var psi = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var arg in BuildArguments()) psi.ArgumentList.Add(arg);
        foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Waits for scrcpy to create its window. Returns <see cref="IntPtr.Zero"/> on timeout or exit.</summary>
    public async Task<IntPtr> WaitForWindowAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !HasExited)
        {
            var hwnd = await Task.Run(() => NativeMethods.FindTopLevelWindowByTitle(WindowTitle));
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(150);
        }
        return IntPtr.Zero;
    }

    public void MarkEmbedded(IntPtr hwnd)
    {
        WindowHandle = hwnd;
        Status = "Running";
        OnPropertyChanged(nameof(IsEmbedded));
    }

    /// <summary>Asks scrcpy to close gracefully (so it removes the virtual display), then kills it if needed.</summary>
    public async Task CloseAsync(TimeSpan gracePeriod)
    {
        var process = _process;
        if (process is null || process.HasExited) return;

        if (WindowHandle != IntPtr.Zero && NativeMethods.IsWindow(WindowHandle))
        {
            NativeMethods.PostMessage(WindowHandle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            using var cts = new CancellationTokenSource(gracePeriod);
            try { await process.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
        }

        // Don't kill the process tree: scrcpy may have spawned the shared adb server.
        try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
    }

    public override string ToString() => Name;

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (DisplayId is null && DisplayIdRegex().Match(line) is { Success: true } m) DisplayId = int.Parse(m.Groups[1].Value);
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines && _log.TryDequeue(out _)) { }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [GeneratedRegex(@"New display: \S+ \(id=(\d+)\)")]
    private static partial Regex DisplayIdRegex();
}
