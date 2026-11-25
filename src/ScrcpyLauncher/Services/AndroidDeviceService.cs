using System.Text;
using System.Text.RegularExpressions;
using ScrcpyLauncher.Models;

namespace ScrcpyLauncher.Services;

/// <summary>Talks to adb/scrcpy to discover devices and installed apps.</summary>
public sealed partial class AndroidDeviceService
{
    private readonly AppSettings _settings;

    public AndroidDeviceService(AppSettings settings)
    {
        _settings = settings;
        ScrcpyPath = ToolLocator.Find(settings.ScrcpyPath);
        AdbPath = ToolLocator.Find(settings.AdbPath, ScrcpyPath is null ? null : Path.GetDirectoryName(ScrcpyPath));
    }

    public string? ScrcpyPath { get; }
    public string? AdbPath { get; }

    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var adb = AdbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        var result = await ProcessRunner.RunAsync(adb, ["devices", "-l"], cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"adb devices failed: {result.StandardError.Trim()}");
        return ParseDevices(result.StandardOutput);
    }

    /// <summary>Lists apps via "scrcpy --list-apps", which provides user-facing labels.</summary>
    public async Task<IReadOnlyList<AndroidApp>> GetAppsAsync(string serial, CancellationToken cancellationToken = default)
    {
        var apps = await GetAllAppsAsync(serial, cancellationToken);

        var allow = new HashSet<string>(_settings.SystemAppAllowlist, StringComparer.OrdinalIgnoreCase);
        return apps
            .Where(a => !a.IsSystem || _settings.ShowAllSystemApps || allow.Contains(a.Package))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<AndroidApp?> GetAppByPackageAsync(string serial, string package, CancellationToken cancellationToken = default)
    {
        var apps = await GetAllAppsAsync(serial, cancellationToken);
        return apps.FirstOrDefault(a => string.Equals(a.Package, package, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetDefaultBrowserPackageAsync(string serial, CancellationToken cancellationToken = default)
    {
        var adb = AdbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        var result = await ProcessRunner.RunAsync(adb, (string[])["-s", serial, "shell", "cmd", "role", "get-role-holders", "android.app.role.BROWSER"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not determine the default browser: {result.Combined.Trim()}");
        }

        var package = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => PackageRegex().IsMatch(line));
        return package ?? throw new InvalidOperationException("No default browser is configured on the Android device.");
    }

    public async Task OpenBrowserSearchAsync(string serial, string browserPackage, int displayId, string query, CancellationToken cancellationToken = default)
    {
        if (!PackageRegex().IsMatch(browserPackage)) throw new ArgumentException("Invalid Android package name.", nameof(browserPackage));
        if (displayId < 0) throw new ArgumentOutOfRangeException(nameof(displayId));

        var adb = AdbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        var searchUrl = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        var result = await ProcessRunner.RunAsync(adb,
        (string[])
        [
            "-s", serial, "shell", "am", "start", "--display", displayId.ToString(),
            "-a", "android.intent.action.VIEW", "-d", searchUrl, "-p", browserPackage,
        ], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not open the search in the default browser: {result.Combined.Trim()}");
        }
    }

    private async Task<IReadOnlyList<AndroidApp>> GetAllAppsAsync(string serial, CancellationToken cancellationToken)
    {
        var scrcpy = ScrcpyPath ?? throw new InvalidOperationException("scrcpy was not found on PATH.");
        var result = await ProcessRunner.RunAsync(scrcpy, [$"--serial={serial}", "--list-apps"], cancellationToken, Encoding.UTF8);
        var apps = ParseApps(result.Combined);
        if (apps.Count == 0 && result.ExitCode != 0)
        {
            throw new InvalidOperationException($"scrcpy --list-apps failed: {LastLines(result.StandardError, 3)}");
        }
        return apps;
    }

    /// <summary>
    /// Uninstalls an app. User apps use "adb uninstall"; system apps can't be removed from the system image,
    /// so they are uninstalled for the current user ("pm uninstall --user 0").
    /// </summary>
    public async Task UninstallAsync(string serial, AndroidApp app, CancellationToken cancellationToken = default)
    {
        var adb = AdbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        string[] args = app.IsSystem
            ? ["-s", serial, "shell", "pm", "uninstall", "--user", "0", app.Package]
            : ["-s", serial, "uninstall", app.Package];
        var result = await ProcessRunner.RunAsync(adb, args, cancellationToken);
        var output = result.Combined.Trim();
        if (result.ExitCode != 0 || !output.Contains("Success", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(output.Length == 0 ? $"adb exited with code {result.ExitCode}." : LastLines(output, 2));
        }
    }

    internal static IReadOnlyList<DeviceInfo> ParseDevices(string output)
    {
        var devices = new List<DeviceInfo>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var model = parts.Skip(2).FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?["model:".Length..];
            devices.Add(new DeviceInfo(parts[0], parts[1], model));
        }
        return devices;
    }

    internal static IReadOnlyList<AndroidApp> ParseApps(string output)
    {
        var apps = new List<AndroidApp>();
        var inList = false;
        (string Name, bool IsSystem)? pending = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inList)
            {
                inList = line.Contains("List of apps:", StringComparison.Ordinal);
                continue;
            }

            var full = FullEntryRegex().Match(line);
            if (full.Success)
            {
                apps.Add(new AndroidApp(full.Groups["name"].Value.Trim(), full.Groups["pkg"].Value, full.Groups["kind"].Value == "*"));
                pending = null;
                continue;
            }

            // Long labels wrap: the package name is printed alone on the following line.
            var packageOnly = PackageOnlyRegex().Match(line);
            if (packageOnly.Success && pending is { } p)
            {
                apps.Add(new AndroidApp(p.Name, packageOnly.Groups["pkg"].Value, p.IsSystem));
                pending = null;
                continue;
            }

            var nameOnly = NameOnlyRegex().Match(line);
            pending = nameOnly.Success ? (nameOnly.Groups["name"].Value.Trim(), nameOnly.Groups["kind"].Value == "*") : null;
        }

        return apps;
    }

    private static string LastLines(string text, int count) =>
        string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));

    [GeneratedRegex(@"^\s*(?<kind>[*-])\s+(?<name>.+?)\s{2,}(?<pkg>[A-Za-z][\w]*(?:\.[\w]+)+)\s*$")]
    private static partial Regex FullEntryRegex();

    [GeneratedRegex(@"^\s+(?<pkg>[A-Za-z][\w]*(?:\.[\w]+)+)\s*$")]
    private static partial Regex PackageOnlyRegex();

    [GeneratedRegex(@"^\s*(?<kind>[*-])\s+(?<name>.+?)\s*$")]
    private static partial Regex NameOnlyRegex();

    [GeneratedRegex(@"^[A-Za-z][\w]*(?:\.[\w]+)+$")]
    private static partial Regex PackageRegex();
}
