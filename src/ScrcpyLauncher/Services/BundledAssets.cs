using System.Security.Cryptography;

namespace ScrcpyLauncher.Services;

internal static class BundledAssets
{
    private static readonly object Gate = new();
    private static readonly string AssetRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ScrcpyLauncher",
        "BundledAssets");

    public static string AppIconPath => Extract("ScrcpyLauncher.Assets.AppIcon.ico", "AppIcon.ico");
    public static string IconHelperJarPath => Extract("ScrcpyLauncher.Assets.iconhelper.jar", "iconhelper.jar");

    private static string Extract(string resourceName, string fileName)
    {
        lock (Gate)
        {
            using var resource = typeof(BundledAssets).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new FileNotFoundException($"Bundled asset resource '{resourceName}' was not found.");
            var version = Convert.ToHexString(SHA256.HashData(resource));
            resource.Position = 0;

            var directory = Path.Combine(AssetRoot, version);
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path)) return path;

            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                resource.CopyTo(output);
            }

            File.Move(temporaryPath, path, overwrite: true);
            return path;
        }
    }
}
