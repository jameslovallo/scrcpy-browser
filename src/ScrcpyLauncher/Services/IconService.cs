using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace ScrcpyLauncher.Services;

/// <summary>
/// Fetches application icons. Icons are rendered on the device by a small dex helper
/// (Assets\iconhelper.jar, run through app_process over adb) and pulled to a local cache.
/// Packages the helper cannot render fall back to the Play Store listing icon.
/// </summary>
public sealed partial class IconService : IDisposable
{
    private const string RemoteJar = "/data/local/tmp/scrcpylauncher-iconhelper.jar";
    private const string RemoteDir = "/data/local/tmp/scrcpylauncher-icons";
    private const int IconSize = 96;
    private const int BatchSize = 40;

    private readonly string? _adbPath;
    private readonly string _helperJar = BundledAssets.IconHelperJarPath;
    private readonly string _cacheRoot = ResolveCacheRoot();
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, byte> _failed = new();
    private readonly ConcurrentDictionary<string, byte> _helperPushed = new();

    public IconService(string? adbPath)
    {
        _adbPath = adbPath;
        // System proxy with the signed-in user's credentials (corporate networks).
        _http = new HttpClient(new HttpClientHandler { DefaultProxyCredentials = CredentialCache.DefaultCredentials })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) ScrcpyLauncher");
    }

    /// <summary>Clears the "no icon available" memory so the next load retries those packages.</summary>
    public void ResetFailures() => _failed.Clear();

    /// <summary>
    /// Resolves icons for <paramref name="packages"/>; <paramref name="onIcon"/> is invoked (on a worker thread)
    /// with the package name and local PNG path as each icon becomes available.
    /// </summary>
    public async Task LoadIconsAsync(string serial, IReadOnlyList<string> packages, Action<string, string> onIcon, CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(_cacheRoot, SanitizeFileName(serial));
        Directory.CreateDirectory(cacheDir);

        var missing = new List<string>();
        foreach (var pkg in packages)
        {
            var cached = Path.Combine(cacheDir, pkg + ".png");
            if (File.Exists(cached)) onIcon(pkg, cached);
            else if (!_failed.ContainsKey(Key(serial, pkg))) missing.Add(pkg);
        }
        if (missing.Count == 0) return;

        var unresolved = await LoadFromDeviceAsync(serial, missing, cacheDir, onIcon, cancellationToken);
        await LoadFromPlayStoreAsync(serial, unresolved, cacheDir, onIcon, cancellationToken);
    }

    private async Task<List<string>> LoadFromDeviceAsync(string serial, List<string> packages, string cacheDir, Action<string, string> onIcon, CancellationToken ct)
    {
        if (_adbPath is not { } adb || !File.Exists(_helperJar)) return packages;

        var helperLock = DeviceHelperLock.For(serial);
        await helperLock.WaitAsync(ct);
        try
        {
            return await LoadFromDeviceLockedAsync(adb, serial, packages, cacheDir, onIcon, ct);
        }
        finally
        {
            helperLock.Release();
        }
    }

    private async Task<List<string>> LoadFromDeviceLockedAsync(string adb, string serial, List<string> packages, string cacheDir, Action<string, string> onIcon, CancellationToken ct)
    {
        var unresolved = new List<string>();
        try
        {
            if (!_helperPushed.ContainsKey(serial))
            {
                var push = await ProcessRunner.RunAsync(adb, ["-s", serial, "push", _helperJar, RemoteJar], ct);
                if (push.ExitCode != 0) return packages;
                _helperPushed[serial] = 0;
            }

            for (var i = 0; i < packages.Count; i += BatchSize)
            {
                var batch = packages.Skip(i).Take(BatchSize).ToList();
                var remote = $"{RemoteDir}/{i / BatchSize}";
                var run = await ProcessRunner.RunAsync(adb,
                    ["-s", serial, "shell", $"rm -rf {remote}; CLASSPATH={RemoteJar} app_process / Main {remote} {IconSize} {string.Join(' ', batch)}"], ct);

                var ok = new HashSet<string>(
                    run.StandardOutput.Split('\n', StringSplitOptions.TrimEntries)
                        .Where(l => l.StartsWith("OK ", StringComparison.Ordinal))
                        .Select(l => l[3..]),
                    StringComparer.Ordinal);

                if (ok.Count > 0)
                {
                    var temp = Path.Combine(Path.GetTempPath(), "scrcpylauncher-icons-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await ProcessRunner.RunAsync(adb, ["-s", serial, "pull", remote, temp], ct);
                        foreach (var pkg in batch.Where(ok.Contains))
                        {
                            var src = Path.Combine(temp, pkg + ".png");
                            if (!File.Exists(src)) { ok.Remove(pkg); continue; }
                            var dest = Path.Combine(cacheDir, pkg + ".png");
                            File.Move(src, dest, overwrite: true);
                            onIcon(pkg, dest);
                        }
                    }
                    finally
                    {
                        try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }

                unresolved.AddRange(batch.Where(p => !ok.Contains(p)));
            }

            await ProcessRunner.RunAsync(adb, ["-s", serial, "shell", $"rm -rf {RemoteDir}"], ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // adb/helper unavailable: everything not yet resolved goes to the Play Store fallback.
            var resolved = new HashSet<string>(packages.Where(p => File.Exists(Path.Combine(cacheDir, p + ".png"))));
            return packages.Where(p => !resolved.Contains(p)).ToList();
        }
        return unresolved;
    }

    private async Task LoadFromPlayStoreAsync(string serial, List<string> packages, string cacheDir, Action<string, string> onIcon, CancellationToken ct)
    {
        if (packages.Count == 0) return;
        await Parallel.ForEachAsync(packages, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (pkg, token) =>
        {
            try
            {
                var page = await _http.GetStringAsync($"https://play.google.com/store/apps/details?id={Uri.EscapeDataString(pkg)}&hl=en", token);
                var match = OgImageRegex().Match(page);
                if (!match.Success) throw new InvalidDataException("No icon on listing.");

                var url = match.Groups["url"].Value;
                var eq = url.LastIndexOf('=');
                if (eq > url.IndexOf("googleusercontent.com/", StringComparison.Ordinal)) url = url[..eq];
                var bytes = await _http.GetByteArrayAsync(url + "=s" + IconSize, token);

                var dest = Path.Combine(cacheDir, pkg + ".png");
                await File.WriteAllBytesAsync(dest, bytes, token);
                onIcon(pkg, dest);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Not on the Play Store (or offline): keep the letter tile.
                _failed[Key(serial, pkg)] = 0;
            }
        });
    }

    private static string ResolveCacheRoot()
    {
        // Portable: keep the cache next to the executable when writable.
        var local = Path.Combine(AppContext.BaseDirectory, "IconCache");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return local;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScrcpyLauncher", "IconCache");
        }
    }

    private static string Key(string serial, string pkg) => serial + "|" + pkg;

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    public void Dispose() => _http.Dispose();

    [GeneratedRegex("""<meta[^>]+property="og:image"[^>]+content="(?<url>https://play-lh\.googleusercontent\.com/[^"]+)""")]
    private static partial Regex OgImageRegex();
}
