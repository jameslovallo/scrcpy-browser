using System.Text;
using ScrcpyLauncher.Services;

var fields = new[]
{
    "MEDIA",
    Encode("com.example.player"),
    Encode("夜の曲"),
    Encode("Artist"),
    Encode("Album"),
    "3",
    "1532",
    "240000",
    "566",
    Encode("track-01"),
    "1",
};
var snapshot = MediaSessionService.ParseSnapshot(string.Join('\t', fields), "test-device");
if (snapshot.Serial != "test-device" || snapshot.PackageName != "com.example.player" ||
    snapshot.Title != "夜の曲" || snapshot.Artist != "Artist" || snapshot.Album != "Album" ||
    snapshot.PlaybackState != 3 || snapshot.Position != 1532 || snapshot.Duration != 240000 ||
    snapshot.MediaId != "track-01" || !snapshot.HasArtwork || snapshot.ArtworkPath is not null ||
    !snapshot.IsPlaying || !snapshot.CanPrevious || !snapshot.CanNext || !snapshot.CanTogglePlayback)
{
    throw new Exception("Media snapshot fields were not decoded correctly.");
}

fields[10] = "0";
var withoutArtwork = MediaSessionService.ParseSnapshot(string.Join('\t', fields), "test-device");
if (withoutArtwork.HasArtwork) throw new Exception("Snapshot without artwork was marked as having artwork.");

try
{
    MediaSessionService.ParseSnapshot("MEDIA\tbad", "test-device");
    throw new Exception("Malformed snapshot was accepted.");
}
catch (InvalidDataException)
{
}

Console.WriteLine("Media snapshot parsing passed.");

static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
