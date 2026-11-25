using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ScrcpyLauncher.Services;

public sealed record MediaSnapshot(
    string Serial,
    string PackageName,
    string Title,
    string Artist,
    string Album,
    int PlaybackState,
    long Position,
    long Duration,
    long Actions,
    string MediaId,
    bool HasArtwork,
    string? ArtworkPath)
{
    public bool IsPlaying => PlaybackState == 3;
    public bool CanPrevious => (Actions & 16) != 0;
    public bool CanNext => (Actions & 32) != 0;
    public bool CanTogglePlayback => (Actions & 512) != 0 || (IsPlaying ? (Actions & 2) != 0 : (Actions & 4) != 0);
}

public sealed class MediaSessionService
{
    private const string RemoteJar = "/data/local/tmp/scrcpylauncher-iconhelper.jar";
    private const string RemoteArtwork = "/data/local/tmp/scrcpylauncher-media-art.png";
    private const int SnapshotFieldCount = 11;

    private readonly string? _adbPath;
    private readonly string _helperJar = BundledAssets.IconHelperJarPath;
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScrcpyLauncher",
        "MediaCache");
    private readonly ConcurrentDictionary<string, byte> _helperPushed = new();
    private readonly ConcurrentDictionary<string, string> _artworkByMedia = new();

    public MediaSessionService(string? adbPath) => _adbPath = adbPath;

    public async Task<MediaSnapshot?> GetSnapshotAsync(string serial, CancellationToken cancellationToken = default)
    {
        var adb = _adbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        if (!File.Exists(_helperJar)) throw new FileNotFoundException("The on-device helper JAR is missing.", _helperJar);

        var helperLock = DeviceHelperLock.For(serial);
        await helperLock.WaitAsync(cancellationToken);
        try
        {
            if (!_helperPushed.ContainsKey(serial))
            {
                var push = await ProcessRunner.RunAsync(adb, (string[])["-s", serial, "push", _helperJar, RemoteJar], cancellationToken);
                if (push.ExitCode != 0) throw new InvalidOperationException($"Could not install the media helper: {push.Combined.Trim()}");
                _helperPushed[serial] = 0;
            }

            var run = await ProcessRunner.RunAsync(adb,
                (string[])["-s", serial, "shell", $"CLASSPATH={RemoteJar} app_process / Main media snapshot {RemoteArtwork}"],
                cancellationToken);
            if (run.ExitCode != 0) throw new InvalidOperationException($"Could not read the active media session: {run.Combined.Trim()}");

            var row = run.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(line => line == "NONE" || line.StartsWith("MEDIA\t", StringComparison.Ordinal));
            if (row is null) throw new InvalidDataException($"The media helper returned an unexpected response: {run.Combined.Trim()}");
            if (row == "NONE") return null;

            var snapshot = ParseSnapshot(row, serial);
            if (!snapshot.HasArtwork) return snapshot;

            var mediaKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\0', serial, snapshot.PackageName, snapshot.MediaId, snapshot.Title, snapshot.Artist, snapshot.Album))));
            if (_artworkByMedia.TryGetValue(mediaKey, out var cachedArtwork) && File.Exists(cachedArtwork))
                return snapshot with { ArtworkPath = cachedArtwork };

            var artworkPath = await PullArtworkAsync(adb, serial, cancellationToken);
            _artworkByMedia[mediaKey] = artworkPath;
            return snapshot with { ArtworkPath = artworkPath };
        }
        finally
        {
            helperLock.Release();
        }
    }

    public async Task DispatchAsync(string serial, string command, CancellationToken cancellationToken = default)
    {
        if (command is not ("play" or "pause" or "play-pause" or "next" or "previous"))
            throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported media command.");

        var adb = _adbPath ?? throw new InvalidOperationException("adb was not found on PATH (or next to scrcpy).");
        var result = await ProcessRunner.RunAsync(adb,
            (string[])["-s", serial, "shell", "cmd", "media_session", "dispatch", command],
            cancellationToken);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Could not dispatch media command: {result.Combined.Trim()}");
    }

    internal static MediaSnapshot ParseSnapshot(string row, string serial)
    {
        var fields = row.Split('\t');
        if (fields.Length != SnapshotFieldCount || fields[0] != "MEDIA")
            throw new InvalidDataException("The media helper returned a malformed snapshot.");

        try
        {
            return new MediaSnapshot(
                serial,
                Decode(fields[1]),
                Decode(fields[2]),
                Decode(fields[3]),
                Decode(fields[4]),
                int.Parse(fields[5], CultureInfo.InvariantCulture),
                long.Parse(fields[6], CultureInfo.InvariantCulture),
                long.Parse(fields[7], CultureInfo.InvariantCulture),
                long.Parse(fields[8], CultureInfo.InvariantCulture),
                Decode(fields[9]),
                fields[10] switch
                {
                    "0" => false,
                    "1" => true,
                    _ => throw new FormatException("Invalid artwork flag."),
                },
                null);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidDataException("The media helper returned invalid snapshot data.", ex);
        }
    }

    private async Task<string> PullArtworkAsync(string adb, string serial, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"scrcpylauncher-media-{Guid.NewGuid():N}.png");
        try
        {
            var pull = await ProcessRunner.RunAsync(adb, (string[])["-s", serial, "pull", RemoteArtwork, tempPath], cancellationToken);
            if (pull.ExitCode != 0) throw new InvalidOperationException($"Could not retrieve media artwork: {pull.Combined.Trim()}");

            var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Directory.CreateDirectory(_cacheRoot);
            var cachedPath = Path.Combine(_cacheRoot, hash + ".png");
            if (!File.Exists(cachedPath)) await File.WriteAllBytesAsync(cachedPath, bytes, cancellationToken);
            return cachedPath;
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string Decode(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
