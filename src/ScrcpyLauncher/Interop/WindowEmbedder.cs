using static ScrcpyLauncher.Interop.NativeMethods;

namespace ScrcpyLauncher.Interop;

/// <summary>Re-parents a foreign top-level window (scrcpy's SDL window) into our window as a child.</summary>
internal static class WindowEmbedder
{
    public static void Embed(IntPtr child, IntPtr parent)
    {
        // Per SetParent docs: switch WS_POPUP -> WS_CHILD before re-parenting.
        var style = GetWindowLongPtr(child, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD | WS_CLIPSIBLINGS;
        SetWindowLongPtr(child, GWL_STYLE, new IntPtr(style));

        var exStyle = GetWindowLongPtr(child, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(child, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_APPWINDOW));

        SetParent(child, parent);
        SetWindowPos(child, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Moves/sizes the child to the given parent-client rectangle; no-op if already there (avoids resize churn with -x).</summary>
    public static void Place(IntPtr child, int x, int y, int width, int height, int cornerRadius = 0)
    {
        if (!IsWindow(child)) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        RoundCorners(child, width, height, cornerRadius);

        if (IsWindowVisible(child) && GetWindowRect(child, out var r))
        {
            MapWindowPoints(IntPtr.Zero, GetParent(child), ref r, 2);
            if (r.Left == x && r.Top == y && r.Right - r.Left == width && r.Bottom - r.Top == height) return;
        }

        SetWindowPos(child, HWND_TOP, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static readonly Dictionary<IntPtr, (int Width, int Height, int Radius)> AppliedRegion = [];

    /// <summary>
    /// Clips the child to a rounded rectangle matching the host card. Window regions are applied by the window
    /// manager, so this costs nothing per frame; the region is only rebuilt when the size or radius changes.
    /// </summary>
    private static void RoundCorners(IntPtr child, int width, int height, int radius)
    {
        var key = (width, height, radius);
        if (AppliedRegion.TryGetValue(child, out var applied) && applied == key) return;
        // GDI excludes the right/bottom edge of a region, hence the +1.
        var region = radius > 0 ? CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2) : IntPtr.Zero;
        if (SetWindowRgn(child, region, true) != 0)
        {
            AppliedRegion[child] = key; // the system now owns the region
        }
        else if (region != IntPtr.Zero)
        {
            DeleteObject(region);
        }
    }

    public static void Forget(IntPtr child) => AppliedRegion.Remove(child);

    public static void Hide(IntPtr child)
    {
        if (IsWindow(child) && IsWindowVisible(child)) ShowWindow(child, SW_HIDE);
    }

    public static bool Focus(IntPtr child)
    {
        if (!IsWindow(child)) return false;

        var currentThread = GetCurrentThreadId();
        var childThread = GetWindowThreadProcessId(child, out _);
        if (childThread == 0) return false;

        var attached = currentThread != childThread;
        if (attached && !AttachThreadInput(currentThread, childThread, true)) return false;
        try
        {
            SetFocus(child);
            return GetFocus() == child;
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, childThread, false);
        }
    }
}
