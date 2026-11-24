using System.Text;
using ScrcpyLauncher.Services;

if (args.Contains("--list-apps"))
{
    var stdout = "List of apps:\n - Calculator                     com.example.calculator\n" +
                 " - Google\u00a0Wallet                  com.google.android.apps.walletnfcrel\n" +
                 " - Caf\u00e9 \u6771\u4eac\n   com.example.cafe\n";
    var stderr = "List of apps:\n - Espa\u00f1a                    com.example.espana\n";
    await Console.OpenStandardOutput().WriteAsync(Encoding.UTF8.GetBytes(stdout));
    await Console.OpenStandardError().WriteAsync(Encoding.UTF8.GetBytes(stderr));
    return;
}

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.GetEncoding(437);
var settings = new AppSettings { ScrcpyPath = Environment.ProcessPath!, ShowAllSystemApps = true };
var apps = await new AndroidDeviceService(settings).GetAppsAsync("test-device");
var names = apps.ToDictionary(app => app.Package, app => app.Name);
var expected = new Dictionary<string, string>
{
    ["com.example.calculator"] = "Calculator",
    ["com.google.android.apps.walletnfcrel"] = "Google\u00a0Wallet",
    ["com.example.cafe"] = "Caf\u00e9 \u6771\u4eac",
    ["com.example.espana"] = "Espa\u00f1a",
};
foreach (var (package, name) in expected)
{
    if (!names.TryGetValue(package, out var actual) || actual != name)
        throw new Exception($"Incorrect label for {package}: expected {CodePoints(name)}, got {(actual is null ? "<missing>" : CodePoints(actual))}");
}
Console.WriteLine("App list UTF-8 decoding passed.");

static string CodePoints(string text) => string.Join(" ", text.Select(ch => $"U+{(int)ch:X4}"));

namespace ScrcpyLauncher.Models
{
    public sealed record AndroidApp(string Name, string Package, bool IsSystem);
}
