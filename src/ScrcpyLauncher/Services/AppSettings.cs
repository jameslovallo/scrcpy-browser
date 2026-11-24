using System.Text.Json;

namespace ScrcpyLauncher.Services;

/// <summary>Settings stored in settings.json next to the executable, keeping the app portable.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

    /// <summary>Executable name (resolved via PATH) or absolute path.</summary>
    public string ScrcpyPath { get; set; } = "scrcpy";

    /// <summary>Executable name (resolved via PATH, then next to scrcpy) or absolute path.</summary>
    public string AdbPath { get; set; } = "adb";

    /// <summary>"System" (default, follows Windows), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>Additional arguments appended to every scrcpy session, e.g. "--max-fps=60".</summary>
    public List<string> ExtraScrcpyArgs { get; set; } = [];

    /// <summary>Packages pinned to the sidebar grid, in display order.</summary>
    public List<string> PinnedApps { get; set; } = ["com.android.chrome", "com.android.settings"];

    /// <summary>Packages launched with the mouse captured (scrcpy -M, UHID mouse); release it with the scrcpy shortcut.</summary>
    public List<string> CaptureMouseApps { get; set; } = [];

    /// <summary>Packages hidden from search and the pinned grid; restored from the settings view.</summary>
    public List<string> HiddenApps { get; set; } = [];

    /// <summary>When true, every system app is listed instead of only the allowlisted ones.</summary>
    public bool ShowAllSystemApps { get; set; }

    /// <summary>System packages that are still shown in the app list.</summary>
    public List<string> SystemAppAllowlist { get; set; } =
    [
        "com.android.chrome",
        "com.android.settings",
        "com.android.vending",
        "com.google.android.GoogleCamera",
        "com.android.camera",
        "com.android.camera2",
        "com.sec.android.app.camera",
        "com.google.android.apps.photos",
        "com.sec.android.gallery3d",
        "com.google.android.gm",
        "com.google.android.apps.maps",
        "com.google.android.youtube",
        "com.google.android.apps.youtube.music",
        "com.google.android.calculator",
        "com.android.calculator2",
        "com.sec.android.app.popupcalculator",
        "com.google.android.deskclock",
        "com.android.deskclock",
        "com.sec.android.app.clockpackage",
        "com.google.android.calendar",
        "com.samsung.android.calendar",
        "com.google.android.contacts",
        "com.android.contacts",
        "com.samsung.android.app.contacts",
        "com.google.android.dialer",
        "com.android.dialer",
        "com.samsung.android.dialer",
        "com.google.android.apps.messaging",
        "com.android.mms",
        "com.samsung.android.messaging",
        "com.google.android.documentsui",
        "com.android.documentsui",
        "com.google.android.apps.nbu.files",
        "com.sec.android.app.myfiles",
        "com.google.android.apps.docs",
        "com.google.android.keep",
        "com.google.android.googlequicksearchbox",
        "com.sec.android.app.sbrowser",
        "com.samsung.android.app.notes",
    ];

    public void Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read-only location: settings stay in memory for this run.
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
            }

            var defaults = new AppSettings();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(defaults, JsonOptions));
            return defaults;
        }
        catch (Exception)
        {
            // Read-only location or malformed file: fall back to defaults.
            return new AppSettings();
        }
    }
}
