using System.Diagnostics;

namespace ZZZScannerNext.Scanning.Gamepad;

// Only counts input reports. It deliberately makes no claim about in-game movement.
internal sealed class PulseRunner(IGamepadOutput output, Action ensureTarget, Action<string> log)
{
    private int busy;

    public async Task RunAsync(Direction direction, NavigationInput input, int count,
        int holdMs, int gapMs, CancellationToken token)
    {
        if (count is < 1 or > 100 || holdMs is < 20 or > 300 || gapMs is < 100 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(count), "次数 1–100；按压 20–300ms；释放间隔 100–2000ms。");
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            throw new InvalidOperationException("已有输入序列正在执行。");
        try
        {
            token.ThrowIfCancellationRequested();
            ensureTarget();
            output.Neutral();
            for (var i = 1; i <= count; i++)
            {
                token.ThrowIfCancellationRequested();
                ensureTarget();
                var watch = Stopwatch.StartNew();
                try
                {
                    output.Press(direction, input);
                    log($"PRESS direction={direction} input={input} pulse={i}/{count} holdMs={holdMs} gapMs={gapMs}");
                    await GuardedDelay(holdMs, token);
                }
                finally
                {
                    output.Neutral();
                }
                log($"RELEASE pulse={i}/{count} actualHoldMs={watch.Elapsed.TotalMilliseconds:F1}; movement=UNVERIFIED");
                await GuardedDelay(gapMs, token);
            }
        }
        finally
        {
            try { output.Neutral(); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }
    }

    private async Task GuardedDelay(int milliseconds, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            ensureTarget();
            await Task.Delay(Math.Min(10, Math.Max(1, milliseconds - (int)watch.ElapsedMilliseconds)), token);
        }
        token.ThrowIfCancellationRequested();
        ensureTarget();
    }
}
