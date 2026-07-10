using System.Runtime.InteropServices;
namespace OptiHub.Helpers;

internal static class NativeWindowHelper
{
    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;
    private const int SC_SIZE = 0xF000;
    private const int GWLP_WNDPROC = -4;

    private static readonly object HookLock = new();
    private static IntPtr _oldWndProc = IntPtr.Zero;
    private static IntPtr _hookedWindow = IntPtr.Zero;
    private static WndProcDelegate? _newWndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void DisableMaximizeViaSystemMenu(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var style = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style.ToInt64() & ~WS_MAXIMIZEBOX));

        lock (HookLock)
        {
            if (_hookedWindow == hwnd) return;
            RestoreWindowProcedureCore();

            _newWndProc = WndProc;
            var oldWndProc = SetWindowLongPtr(
                hwnd,
                GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_newWndProc));

            if (oldWndProc == IntPtr.Zero)
            {
                _newWndProc = null;
                return;
            }

            _oldWndProc = oldWndProc;
            _hookedWindow = hwnd;
        }
    }

    public static void RestoreWindowProcedure(IntPtr hwnd)
    {
        lock (HookLock)
        {
            if (_hookedWindow != hwnd) return;
            RestoreWindowProcedureCore();
        }
    }

    private static void RestoreWindowProcedureCore()
    {
        if (_hookedWindow != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
            SetWindowLongPtr(_hookedWindow, GWLP_WNDPROC, _oldWndProc);

        _hookedWindow = IntPtr.Zero;
        _oldWndProc = IntPtr.Zero;
        _newWndProc = null;
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCLBUTTONDBLCLK)
            return IntPtr.Zero;

        if (msg == WM_SYSCOMMAND)
        {
            var cmd = wParam.ToInt32() & 0xFFF0;
            if (cmd is SC_MAXIMIZE or SC_SIZE)
                return IntPtr.Zero;
        }

        return _oldWndProc != IntPtr.Zero
            ? CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam)
            : DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : new IntPtr(GetWindowLong32(hWnd, nIndex));

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
