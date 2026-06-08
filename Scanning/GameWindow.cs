using System.Diagnostics;
using System.Drawing;
using ZZZScannerNext.Interop;

namespace ZZZScannerNext.Scanning;

public sealed class GameWindow
{
    private readonly IntPtr _handle;
    private Rectangle _clientScreenRect;
    private float _coordinateScale = 1f;

    private GameWindow(IntPtr handle, ClientMetrics metrics)
    {
        _handle = handle;
        _clientScreenRect = metrics.ScreenRect;
        _coordinateScale = metrics.Scale;
        Dpi = NativeMethods.TryGetDpiForWindow(handle);
    }

    public Rectangle ClientScreenRect => _clientScreenRect;
    public int Dpi { get; }
    public float CoordinateScale => _coordinateScale;

    public static GameWindow Find(string processName)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();

        var process = Process.GetProcesses()
            .FirstOrDefault(p =>
            {
                try
                {
                    return string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)
                        && p.MainWindowHandle != IntPtr.Zero;
                }
                catch
                {
                    return false;
                }
            });

        if (process is null)
        {
            throw new InvalidOperationException($"未找到游戏窗口进程：{processName}");
        }

        using (process)
        {
            return new GameWindow(process.MainWindowHandle, GetClientMetrics(process.MainWindowHandle));
        }
    }

    public void BringToFront()
    {
        NativeMethods.ShowWindow(_handle, NativeMethods.SwRestore);
        NativeMethods.SetForegroundWindow(_handle);
        var metrics = GetClientMetrics(_handle);
        _clientScreenRect = metrics.ScreenRect;
        _coordinateScale = metrics.Scale;
    }

    public void LeftClick(Point point, int durationMs = 0)
    {
        NativeMethods.SetCursorPos(point.X, point.Y);
        LeftClickCurrent(durationMs);
    }

    public void LeftClickCurrent(int durationMs = 0)
    {
        NativeMethods.mouse_event(NativeMethods.MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        if (durationMs > 0)
        {
            Thread.Sleep(durationMs);
        }

        NativeMethods.mouse_event(NativeMethods.MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public void MouseWheel(int delta)
    {
        NativeMethods.mouse_event(NativeMethods.MouseEventWheel, 0, 0, delta, UIntPtr.Zero);
    }

    public void LeftDrag(Point start, Point end, int durationMs = 120)
    {
        NativeMethods.SetCursorPos(start.X, start.Y);
        NativeMethods.mouse_event(NativeMethods.MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        var steps = Math.Max(4, durationMs / 16);
        for (var i = 1; i <= steps; i++)
        {
            var x = start.X + (end.X - start.X) * i / steps;
            var y = start.Y + (end.Y - start.Y) * i / steps;
            NativeMethods.SetCursorPos(x, y);
            Thread.Sleep(Math.Max(1, durationMs / steps));
        }

        NativeMethods.mouse_event(NativeMethods.MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    public void MoveCursor(Point point)
    {
        NativeMethods.SetCursorPos(point.X, point.Y);
    }

    public Bitmap Capture(Rectangle screenRect)
    {
        var image = new Bitmap(screenRect.Width, screenRect.Height);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(screenRect.Location, Point.Empty, screenRect.Size);
        return image;
    }

    public Color GetPixel(Point point)
    {
        using var image = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(image);
        graphics.CopyFromScreen(point, Point.Empty, image.Size);
        return image.GetPixel(0, 0);
    }

    public Point ToScreenPoint(PointF normalized, bool clientToScreen = true)
    {
        var x = (int)Math.Round(normalized.X * _clientScreenRect.Width);
        var y = (int)Math.Round(normalized.Y * _clientScreenRect.Height);
        return clientToScreen ? new Point(_clientScreenRect.X + x, _clientScreenRect.Y + y) : new Point(x, y);
    }

    public Rectangle ToScreenRectangle(RectangleF normalized)
    {
        var x = (int)Math.Round(normalized.X * _clientScreenRect.Width);
        var y = (int)Math.Round(normalized.Y * _clientScreenRect.Height);
        var width = Math.Max(1, (int)Math.Round(normalized.Width * _clientScreenRect.Width));
        var height = Math.Max(1, (int)Math.Round(normalized.Height * _clientScreenRect.Height));
        return new Rectangle(_clientScreenRect.X + x, _clientScreenRect.Y + y, width, height);
    }

    public Size ToClientSize(SizeF normalized)
    {
        return new Size(
            Math.Max(1, (int)Math.Round(normalized.Width * _clientScreenRect.Width)),
            Math.Max(1, (int)Math.Round(normalized.Height * _clientScreenRect.Height)));
    }

    private static ClientMetrics GetClientMetrics(IntPtr handle)
    {
        if (!NativeMethods.GetClientRect(handle, out var nativeRect))
        {
            throw new InvalidOperationException("无法读取游戏客户区。");
        }

        Rectangle client = nativeRect;
        var point = new NativePoint { X = client.X, Y = client.Y };
        if (!NativeMethods.ClientToScreen(handle, ref point))
        {
            throw new InvalidOperationException("无法换算游戏窗口坐标。");
        }

        var logicalClientScreen = new Rectangle(point.X, point.Y, client.Width, client.Height);
        // CopyFromScreen and SetCursorPos operate in the same coordinate space that
        // ClientToScreen returns for this process. Converting it again by DPI makes
        // high-DPI secondary monitors overshoot the lower rows.
        return new ClientMetrics(logicalClientScreen, 1f);
    }

    private readonly record struct ClientMetrics(Rectangle ScreenRect, float Scale);
}
