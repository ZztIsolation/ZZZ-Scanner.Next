using System.Diagnostics;
using System.Drawing;
using ZZZScannerNext.Interop;

namespace ZZZScannerNext.Scanning;

public sealed class GameWindow : IDisposable
{
    private readonly IntPtr _handle;
    private IWindowCaptureSource _captureSource = new GdiCaptureSource();
    private Action<string>? _captureLog;
    private Rectangle _clientScreenRect;
    private float _coordinateScale = 1f;
    private bool _disposed;

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
    public string ActiveCaptureMode => _captureSource.Name;
    public string ActiveFrameBackend => _captureSource.FrameBackendName;

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
            if (NativeMethods.RequiresElevationForProcess(process.Id))
            {
                throw new ScannerElevationRequiredException(
                    $"游戏进程 {processName} 的权限高于当前扫描器，普通权限无法可靠截图或发送输入。");
            }

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

    public void ConfigureCaptureMode(CaptureMode mode, Action<string>? log = null)
    {
        _captureLog = log;
        if (mode == CaptureMode.Gdi)
        {
            SwitchCaptureSource(new GdiCaptureSource());
            log?.Invoke($"Capture backend active: gdi. captureFrameBackend={_captureSource.FrameBackendName}");
            return;
        }

        try
        {
            SwitchCaptureSource(DxgiDesktopCaptureSource.Create(_clientScreenRect));
            log?.Invoke($"Capture backend active: dxgi. client={_clientScreenRect}, captureFrameBackend={_captureSource.FrameBackendName}");
        }
        catch (Exception ex)
        {
            SwitchCaptureSource(new GdiCaptureSource());
            log?.Invoke($"Capture backend fallback: requested=dxgi, active=gdi, captureFrameBackend={_captureSource.FrameBackendName}, reason={ex.GetType().Name}: {ex.Message}");
        }
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
        try
        {
            return _captureSource.Capture(screenRect);
        }
        catch (Exception ex) when (_captureSource is not GdiCaptureSource)
        {
            _captureLog?.Invoke($"Capture backend fallback during Capture: active={_captureSource.Name}, fallback=gdi, reason={ex.GetType().Name}: {ex.Message}");
            SwitchCaptureSource(new GdiCaptureSource());
            return _captureSource.Capture(screenRect);
        }
    }

    internal CapturedFrame CaptureFrame(Rectangle screenRect)
    {
        try
        {
            return _captureSource.CaptureFrame(screenRect);
        }
        catch (Exception ex) when (_captureSource is not GdiCaptureSource)
        {
            _captureLog?.Invoke($"Capture backend fallback during CaptureFrame: active={_captureSource.Name}, fallback=gdi, captureFrameBackend=bitmap-fallback, reason={ex.GetType().Name}: {ex.Message}");
            SwitchCaptureSource(new GdiCaptureSource());
            return _captureSource.CaptureFrame(screenRect);
        }
    }

    public Color GetPixel(Point point)
    {
        try
        {
            return _captureSource.GetPixel(point);
        }
        catch (Exception ex) when (_captureSource is not GdiCaptureSource)
        {
            _captureLog?.Invoke($"Capture backend fallback during GetPixel: active={_captureSource.Name}, fallback=gdi, reason={ex.GetType().Name}: {ex.Message}");
            SwitchCaptureSource(new GdiCaptureSource());
            return _captureSource.GetPixel(point);
        }
    }

    public Point ToScreenPoint(PointF normalized, bool clientToScreen = true)
    {
        return MapToScreenPoint(_clientScreenRect, normalized, Dpi, clientToScreen);
    }

    internal static Point MapToScreenPoint(
        Rectangle clientScreenRect,
        PointF normalized,
        int dpi,
        bool clientToScreen = true)
    {
        _ = dpi;
        var x = (int)Math.Round(normalized.X * clientScreenRect.Width);
        var y = (int)Math.Round(normalized.Y * clientScreenRect.Height);
        return clientToScreen ? new Point(clientScreenRect.X + x, clientScreenRect.Y + y) : new Point(x, y);
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

    private void SwitchCaptureSource(IWindowCaptureSource captureSource)
    {
        var previous = _captureSource;
        _captureSource = captureSource;
        previous.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _captureSource.Dispose();
    }

    private readonly record struct ClientMetrics(Rectangle ScreenRect, float Scale);
}

public sealed class ScannerElevationRequiredException : InvalidOperationException
{
    public ScannerElevationRequiredException(string message)
        : base(message)
    {
    }
}
