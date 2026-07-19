using System.Drawing;
using System.Runtime.InteropServices;

namespace ZZZScannerNext.Interop;

internal static class NativeMethods
{
    public const int SwRestore = 9;
    public const uint MouseEventLeftDown = 0x0002;
    public const uint MouseEventLeftUp = 0x0004;
    public const uint MouseEventWheel = 0x0800;
    public const int WmHotKey = 0x0312;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint VkC = 0x43;
    private const int DwmwaExtendedFrameBounds = 9;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out NativeRect pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static void TryEnablePerMonitorDpiAwareness()
    {
        try
        {
            if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
            {
                return;
            }
        }
        catch
        {
        }

        try
        {
            SetProcessDPIAware();
        }
        catch
        {
        }
    }

    public static int TryGetDpiForWindow(IntPtr hWnd)
    {
        try
        {
            return (int)GetDpiForWindow(hWnd);
        }
        catch
        {
            return 96;
        }
    }

    public static Rectangle? TryGetWindowRect(IntPtr hWnd)
    {
        return GetWindowRect(hWnd, out var rect) ? rect : null;
    }

    public static Rectangle? TryGetExtendedFrameBounds(IntPtr hWnd)
    {
        try
        {
            var hr = DwmGetWindowAttribute(hWnd, DwmwaExtendedFrameBounds, out var rect, Marshal.SizeOf<NativeRect>());
            return hr == 0 ? rect : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        return TryIsProcessElevated(Environment.ProcessId, out var elevated) && elevated;
    }

    public static bool RequiresElevationForProcess(int processId)
    {
        if (IsCurrentProcessElevated())
        {
            return false;
        }

        return !TryIsProcessElevated(processId, out var elevated) || elevated;
    }

    private static bool TryIsProcessElevated(int processId, out bool elevated)
    {
        elevated = false;
        var processHandle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(processHandle, TokenQuery, out var tokenHandle))
            {
                return false;
            }

            try
            {
                if (!GetTokenInformation(tokenHandle, TokenElevation, out var tokenElevation, sizeof(int), out _))
                {
                    return false;
                }

                elevated = tokenElevation != 0;
                return true;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public static implicit operator Rectangle(NativeRect rect)
    {
        return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}
