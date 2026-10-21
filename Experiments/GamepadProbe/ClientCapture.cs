using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZZZ.GamepadProbe;

internal sealed class ClientCapture(Process target, Action guard) : IClientCapture
{
    private Rectangle? initialBounds;

    public Bitmap Capture()
    {
        guard();
        target.Refresh();
        if (target.HasExited || target.MainWindowHandle == 0) throw new InvalidOperationException("游戏窗口已关闭。");
        var window = target.MainWindowHandle;
        if (!GetClientRect(window, out var rect)) throw new InvalidOperationException("无法获取游戏客户区。");
        var origin = new NativePoint();
        if (!ClientToScreen(window, ref origin)) throw new InvalidOperationException("无法获取游戏窗口位置。");
        var bounds = new Rectangle(origin.X, origin.Y, rect.Right, rect.Bottom);
        if (bounds.Width < 640 || bounds.Height < 360 || !SystemInformation.VirtualScreen.Contains(bounds))
            throw new InvalidOperationException("游戏客户区过小或未完整显示在屏幕内。");
        if (initialBounds is { } initial && initial != bounds)
            throw new InvalidOperationException("游戏窗口位置或分辨率变化，请停止后重新校准。");
        initialBounds ??= bounds;
        var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            guard();
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static void CaptureDiagnostic(string path)
    {
        var processes = Process.GetProcessesByName("ZenlessZoneZero");
        try
        {
            var candidates = processes.Where(p => p.MainWindowHandle != 0).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("未找到唯一绝区零窗口。");
            var process = candidates[0];
            using var image = new ClientCapture(process, () =>
            {
                if (Native.ForegroundProcessId() != process.Id) throw new InvalidOperationException("请将游戏置于前台后截图。");
            }).Capture();
            image.Save(path, ImageFormat.Png);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
