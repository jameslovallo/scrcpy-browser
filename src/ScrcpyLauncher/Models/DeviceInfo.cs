namespace ScrcpyLauncher.Models;

public sealed class DeviceInfo
{
    public DeviceInfo(string serial, string state, string? model)
    {
        Serial = serial;
        State = state;
        Model = model;
    }

    public string Serial { get; }
    public string State { get; }
    public string? Model { get; }
    public bool IsReady => State == "device";

    public string Display => IsReady
        ? $"{Name} ({Serial})"
        : $"{Serial} – {State}";

    /// <summary>Short name for the title bar: the model when connected, otherwise the serial.</summary>
    public string Name => IsReady ? (Model ?? "Android device").Replace('_', ' ') : Serial;
}
