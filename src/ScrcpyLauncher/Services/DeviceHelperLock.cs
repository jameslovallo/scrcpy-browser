using System.Collections.Concurrent;

namespace ScrcpyLauncher.Services;

internal static class DeviceHelperLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static SemaphoreSlim For(string serial) => Locks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
}
