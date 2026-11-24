using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScrcpyLauncher.Interop;

/// <summary>
/// Ctrl+key shortcuts that also work while an embedded scrcpy window has keyboard focus
/// (XAML accelerators never see those keys). Uses a low-level keyboard hook that only reacts
/// while <paramref name="owner"/> is the foreground window, and swallows handled keys so they
/// aren't forwarded to the device.
/// </summary>
internal sealed class KeyboardShortcuts : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly IntPtr _owner;
    private readonly Dictionary<int, Action> _ctrlShortcuts;
    private readonly HookProc _proc;
    private readonly HashSet<int> _swallowedDown = [];
    private IntPtr _hook;

    public KeyboardShortcuts(IntPtr owner, Dictionary<int, Action> ctrlShortcuts)
    {
        _owner = owner;
        _ctrlShortcuts = ctrlShortcuts;
        _proc = HookCallback; // keep the delegate alive for the hook's lifetime
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module?.ModuleName), 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = (int)wParam;
            var vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode

            if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                // Swallow the release too, so the target never sees an unmatched key-up.
                if (_swallowedDown.Remove(vk)) return 1;
            }
            else if (msg is WM_KEYDOWN or WM_SYSKEYDOWN
                && _ctrlShortcuts.TryGetValue(vk, out var action)
                && GetForegroundWindow() == _owner
                && IsDown(VK_CONTROL) && !IsDown(VK_SHIFT) && !IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN))
            {
                // Ignore auto-repeat; still swallow it.
                if (_swallowedDown.Add(vk)) action();
                return 1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
