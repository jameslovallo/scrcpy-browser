using static ScrcpyLauncher.Interop.NativeMethods;

namespace ScrcpyLauncher.Interop;

/// <summary>Positions scrcpy's borderless owned window over the host area without reparenting it.</summary>
internal static class WindowEmbedder
{
    public static void Embed(IntPtr child, IntPtr owner)
    {
        var style = GetWindowLongPtr(child, GWL_STYLE).ToInt64();
        style &= ~(WS_CHILD | WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_POPUP;
        SetWindowLongPtr(child, GWL_STYLE, new IntPtr(style));

        var exStyle = GetWindowLongPtr(child, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(child, GWL_EXSTYLE, new IntPtr(exStyle & ~WS_EX_APPWINDOW));

        SetWindowLongPtr(child, GWLP_HWNDPARENT, owner);
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        DwmRoundedCorners[child] = TryEnableDwmRoundedCorners(child);
    }

    /// <summary>Moves/sizes the owned window over the host-client rectangle.</summary>
    public static void Place(IntPtr child, IntPtr owner, int x, int y, int width, int height, int cornerRadius = 0)
    {
        if (!IsWindow(child)) return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (!DwmRoundedCorners.TryGetValue(child, out var useDwm) || !useDwm)
        {
            RoundCorners(child, width, height, cornerRadius);
        }

        var origin = new POINT { X = x, Y = y };
        if (!ClientToScreen(owner, ref origin)) return;

        if (IsWindowVisible(child) && GetWindowRect(child, out var r))
        {
            if (r.Left == origin.X && r.Top == origin.Y && r.Right - r.Left == width && r.Bottom - r.Top == height) return;
        }

        SetWindowPos(child, IntPtr.Zero, origin.X, origin.Y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private static readonly Dictionary<IntPtr, (int Width, int Height, int Radius)> AppliedRegion = [];
    private static readonly Dictionary<IntPtr, bool> DwmRoundedCorners = [];

    private static bool TryEnableDwmRoundedCorners(IntPtr child)
    {
        var preference = DWMWCP_ROUND;
        return DwmSetWindowAttribute(
            child,
            DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference,
            sizeof(int)) >= 0;
    }

    /// <summary>
    /// Falls back to a rounded window region when DWM rounded corners are unavailable.
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

    public static void Forget(IntPtr child)
    {
        AppliedRegion.Remove(child);
        DwmRoundedCorners.Remove(child);
    }

    public static void Hide(IntPtr child)
    {
        if (IsWindow(child) && IsWindowVisible(child)) ShowWindow(child, SW_HIDE);
    }

    public static bool Focus(IntPtr child)
    {
        return IsWindow(child) && SetForegroundWindow(child) && GetForegroundWindow() == child;
    }
}
