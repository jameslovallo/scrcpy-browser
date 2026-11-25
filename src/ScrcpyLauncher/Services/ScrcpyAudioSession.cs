using System.Collections.Concurrent;
using System.Diagnostics;

namespace ScrcpyLauncher.Services;

public sealed class ScrcpyAudioSession(string serial)
{
    private const int MaxLogLines = 50;

    private readonly ConcurrentQueue<string> _log = new();
    private Process? _process;

    public event EventHandler? Exited;

    public string Serial { get; } = serial;
    public bool IsRunning => _process is { HasExited: false };
    public int? ExitCode => _process is { HasExited: true } process ? process.ExitCode : null;
    public string RecentLog => string.Join(Environment.NewLine, _log);

    public void Start(string scrcpyPath, IEnumerable<string> extraArgs)
    {
        if (_process is not null) throw new InvalidOperationException("The audio session has already been started.");

        var psi = new ProcessStartInfo(scrcpyPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add($"--serial={Serial}");
        psi.ArgumentList.Add("--no-video");
        psi.ArgumentList.Add("--no-window");
        psi.ArgumentList.Add("--no-control");
        foreach (var arg in extraArgs) psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        process.Exited += (_, _) => Exited?.Invoke(this, EventArgs.Empty);
        _process = process;

        var started = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Failed to start scrcpy audio capture.");
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
            }
            _process = null;
            process.Dispose();
            throw;
        }
    }

    public async Task CloseAsync()
    {
        var process = _process;
        if (process is null) return;

        if (!process.HasExited)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
        }
        await process.WaitForExitAsync();
        process.Dispose();
        if (ReferenceEquals(_process, process)) _process = null;
    }

    private void AppendLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        _log.Enqueue(line);
        while (_log.Count > MaxLogLines && _log.TryDequeue(out _)) { }
    }
}
