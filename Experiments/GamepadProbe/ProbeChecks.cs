using System.Text;

namespace ZZZ.GamepadProbe;

internal static class ProbeChecks
{
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        var output = new FakeOutput();
        var runner = new PulseRunner(output, () => { }, _ => { });
        await runner.RunAsync(Direction.Down, NavigationInput.DPad, 3, 20, 100, default);
        Require(output.Presses == 3 && !output.Held, "bounded pulse count and final neutral");

        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            var before = output.Presses;
            await Expect<OperationCanceledException>(() => runner.RunAsync(Direction.Up, NavigationInput.DPad, 1, 20, 100, canceled.Token));
            Require(output.Presses == before && !output.Held, "pre-canceled request sends no press");
        }

        using (var canceled = new CancellationTokenSource())
        {
            output = new FakeOutput { OnPress = canceled.Cancel };
            runner = new PulseRunner(output, () => { }, _ => { });
            await Expect<OperationCanceledException>(() => runner.RunAsync(Direction.Up, NavigationInput.LeftStick, 2, 80, 100, canceled.Token));
            Require(output.Presses == 1 && !output.Held, "cancel while held releases without extra input");
        }

        output = new FakeOutput();
        runner = new PulseRunner(output, () => { if (output.Held) throw new InvalidOperationException("lost focus"); }, _ => { });
        await Expect<InvalidOperationException>(() => runner.RunAsync(Direction.Left, NavigationInput.DPad, 3, 80, 100, default));
        Require(output.Presses == 1 && !output.Held, "focus loss releases and stops sequence");

        output = new FakeOutput { OnPress = () => throw new IOException("report fault after press") };
        runner = new PulseRunner(output, () => { }, _ => { });
        await Expect<IOException>(() => runner.RunAsync(Direction.Right, NavigationInput.DPad, 1, 20, 100, default));
        Require(!output.Held, "partial press failure still neutralizes");
        output.OnPress = null;
        await runner.RunAsync(Direction.Right, NavigationInput.DPad, 1, 20, 100, default);
        Require(output.Presses == 2 && !output.Held, "runner can recover after failed sequence");

        output = new FakeOutput();
        runner = new PulseRunner(output, () => { }, _ => { });
        var active = runner.RunAsync(Direction.Up, NavigationInput.DPad, 1, 80, 100, default);
        await Expect<InvalidOperationException>(() => runner.RunAsync(Direction.Down, NavigationInput.DPad, 1, 20, 100, default));
        await active;
        Require(output.Presses == 1 && !output.Held, "concurrent requests cannot interleave");
        await Expect<ArgumentOutOfRangeException>(() => runner.RunAsync(Direction.Up, NavigationInput.DPad, 101, 20, 100, default));
        Require(output.Presses == 1, "invalid bounds send no input");

        output = new FakeOutput();
        runner = new PulseRunner(output, () => { }, _ => throw new IOException("log fault"));
        await Expect<IOException>(() => runner.RunAsync(Direction.Up, NavigationInput.DPad, 1, 20, 100, default));
        Require(!output.Held, "logging fault cannot leave direction held");
        Require(DriverSetup.Classify(false, false, null, "", "").State == DriverState.Missing,
            "missing driver opens installation path");
        Require(DriverSetup.Classify(true, true, "1.21.442.0", "", "").CanConnect,
            "running baseline driver permits connection");
        Require(DriverSetup.Classify(true, true, "1.17.333.0", "", "").State == DriverState.OtherVersion,
            "other driver version is identified without automatic replacement");
        Require(!DriverSetup.Classify(true, false, "1.21.442.0", "", "").CanConnect,
            "stopped driver blocks connection");
        Require(DriverSetup.Classify(true, true, null, "", "").State == DriverState.Unknown,
            "unreadable version is not misreported as missing");
        var tampered = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tampered, "not the official installer");
            await Expect<InvalidDataException>(() => Task.Run(() => DriverSetup.VerifyInstaller(tampered)));
            report.AppendLine("PASS modified installer rejected before execution");
        }
        finally { File.Delete(tampered); }
        report.Append(GridChecks.Run());
        report.Append(await GridChecks.RunTraversalAsync());
        report.Append(await OcrChecks.RunAsync());
        return report.ToString();

        void Require(bool result, string name)
        {
            if (!result) throw new Exception("FAIL " + name);
            report.AppendLine("PASS " + name);
        }
    }

    // Neutral-only integration check: does not send directions to a running game.
    public static async Task<string> CheckDeviceAsync()
    {
        var before = Slots();
        if (before.Count == 4) throw new InvalidOperationException("全部 XInput 槽位已占用，无法区分新设备。");
        uint index;
        using (var device = new GamepadDevice())
        {
            List<uint> added = [];
            for (var attempt = 0; attempt < 30; attempt++)
            {
                added = Slots().Except(before).ToList();
                if (added.Count == 1) break;
                await Task.Delay(100);
            }
            if (added.Count != 1) throw new InvalidOperationException("未找到唯一新 XInput 设备；检查驱动或其他手柄软件。");
            index = added[0];
            device.Neutral();
            Native.XInputState state = default;
            uint result = 0;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                result = Native.XInputGetState(index, out state);
                if (result == 0 && state.IsNeutral) break;
                await Task.Delay(100);
            }
            if (result != 0 || !state.IsNeutral)
                throw new InvalidOperationException($"新设备未处于全部释放、摇杆回中的状态：slot={index} result={result} buttons={state.Buttons:X4} triggers={state.LeftTrigger},{state.RightTrigger} axes={state.LeftX},{state.LeftY},{state.RightX},{state.RightY}");
        }
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (Native.XInputGetState(index, out _) != 0)
                return $"PASS virtual Xbox360 created without physical controller dependency\nPASS XInput slot={index}, all buttons released and axes neutral\nPASS virtual device removed on dispose\nExisting slots before check: {string.Join(',', before)}\nInitialization primes LeftThumbX=1 then resets all axes to zero.\nNo D-pad/button presses or navigation-sized stick input sent; in-game navigation remains unverified.\n";
            await Task.Delay(100);
        }
        throw new InvalidOperationException("释放后 XInput 槽位仍连接，需要检查设备清理。");
    }

    private static List<uint> Slots() => Enumerable.Range(0, 4).Select(i => (uint)i)
        .Where(i => Native.XInputGetState(i, out _) == 0).ToList();

    private static async Task Expect<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FakeOutput : IGamepadOutput
    {
        public bool Held { get; private set; }
        public int Presses { get; private set; }
        public Action? OnPress { get; set; }
        public void Press(Direction direction, NavigationInput input) { Held = true; Presses++; OnPress?.Invoke(); }
        public void Neutral() => Held = false;
    }
}
