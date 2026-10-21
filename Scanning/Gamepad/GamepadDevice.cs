using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace ZZZScannerNext.Scanning.Gamepad;

internal enum Direction { Up, Down, Left, Right }
internal enum NavigationInput { DPad, LeftStick }

internal interface IGamepadOutput
{
    void Press(Direction direction, NavigationInput input);
    void Neutral();
}

internal sealed class GamepadDevice : IGamepadOutput, IDisposable
{
    private readonly ViGEmClient client;
    private readonly IXbox360Controller controller;
    private bool disposed;
    private readonly Semaphore deviceLease;

    public GamepadDevice()
    {
        deviceLease = new Semaphore(1, 1, @"Local\ZZZScanner.VirtualGamepad");
        if (!deviceLease.WaitOne(0))
        {
            deviceLease.Dispose();
            throw new InvalidOperationException("已有扫描器或验证工具连接虚拟手柄，请先断开。");
        }
        try
        {
            client = new ViGEmClient();
            controller = client.CreateXbox360Controller();
            controller.AutoSubmitReport = false;
            controller.Connect();
            // ViGEmBus 1.21.442 advertises nonzero sticks in its USB startup packet,
            // but caches a zero report and drops identical updates. Force a change
            // of one axis unit (well inside the standard dead zone), then reset.
            controller.SetAxisValue(Xbox360Axis.LeftThumbX, 1);
            controller.SubmitReport();
            Neutral();
        }
        catch
        {
            try { controller?.Disconnect(); }
            finally
            {
                try { (controller as IDisposable)?.Dispose(); }
                finally
                {
                    try { client?.Dispose(); }
                    finally { deviceLease.Release(); deviceLease.Dispose(); }
                }
            }
            throw;
        }
    }

    public void Press(Direction direction, NavigationInput input)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        controller.ResetReport();
        if (input == NavigationInput.DPad)
        {
            controller.SetButtonState(direction switch
            {
                Direction.Up => Xbox360Button.Up,
                Direction.Down => Xbox360Button.Down,
                Direction.Left => Xbox360Button.Left,
                Direction.Right => Xbox360Button.Right,
                _ => throw new ArgumentOutOfRangeException(nameof(direction))
            }, true);
        }
        else
        {
            var axis = direction is Direction.Left or Direction.Right
                ? Xbox360Axis.LeftThumbX : Xbox360Axis.LeftThumbY;
            short value = direction is Direction.Left or Direction.Down ? (short)-24000 : (short)24000;
            controller.SetAxisValue(axis, value);
        }
        controller.SubmitReport();
    }

    public void Neutral()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        controller.ResetReport();
        controller.SubmitReport();
    }

    public void Dispose()
    {
        if (disposed) return;
        try { Neutral(); }
        finally
        {
            disposed = true;
            try { controller.Disconnect(); }
            finally
            {
                try { (controller as IDisposable)?.Dispose(); }
                finally
                {
                    try { client.Dispose(); }
                    finally { deviceLease.Release(); deviceLease.Dispose(); }
                }
            }
        }
    }
}
