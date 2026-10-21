using System.Drawing.Imaging;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    internal static bool IsInventoryEntryHeader(string text, float confidence) =>
        confidence >= .55f && (StringMatcher.SimplifyChinese(text, true).Contains("仓库", StringComparison.Ordinal)
            || StringMatcher.SimplifyChinese(text, true).Contains("背包", StringComparison.Ordinal));

    private static async Task OpenInventoryFromWorldAsync(GameWindow window, ScanProfile profile,
        PaddleOcrRecognizer recognizer, string outputDirectory, ScanLog log, CancellationToken token)
    {
        var directory = Path.Combine(outputDirectory, "entry");
        Directory.CreateDirectory(directory);
        var header = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        header.Offset(-window.ClientScreenRect.Left, -window.ClientScreenRect.Top);
        window.EnsureGamepadTarget();
        using (var before = window.Capture(window.ClientScreenRect))
        {
            before.Save(Path.Combine(directory, "01-world.png"), ImageFormat.Png);
            var title = recognizer.Recognize(before, [header])[0];
            if (IsInventoryEntryHeader(title.Text, title.Score))
                throw new InvalidOperationException("完整流程测试要求从主场景开始，当前已在背包中；未发送 B。");
        }
        token.ThrowIfCancellationRequested();
        log.WriteEvent("ENTRY_OPEN_BACKPACK", "key=B; source=world; repeat=0");
        await window.OpenBackpackKeyAsync(token);
        var consecutive = 0;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(8))
        {
            await Task.Delay(250, token);
            window.EnsureGamepadTarget();
            using var frame = window.Capture(window.ClientScreenRect);
            var healthy = WarehousePreflightEvaluator.EvaluateCaptureHealth(frame).Passed;
            var title = recognizer.Recognize(frame, [header])[0];
            log.WriteEvent("ENTRY_BACKPACK_HEADER", $"text={title.Text}, score={title.Score:F3}, healthy={healthy}");
            consecutive = healthy && IsInventoryEntryHeader(title.Text, title.Score) ? consecutive + 1 : 0;
            if (consecutive < 2) continue;
            frame.Save(Path.Combine(directory, "02-backpack.png"), ImageFormat.Png);
            token.ThrowIfCancellationRequested();
            window.EnsureGamepadTarget();
            log.WriteEvent("ENTRY_SELECT_DISC_TAB", "single mouse click; next gate=drive disc warehouse preflight");
            window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
            await Task.Delay(350, token);
            return;
        }
        using (var failure = window.Capture(window.ClientScreenRect))
            failure.Save(Path.Combine(directory, "entry-failure.png"), ImageFormat.Png);
        throw new InvalidOperationException("按 B 后未确认背包标题，已停止；没有盲点页签或再次按 B。");
    }

    private static async Task SelectFirstDiscForGamepadAsync(GameWindow window, ScanProfile profile,
        string directory, IProgress<ScanProgress> progress, Counters counters, ScanLog log, CancellationToken token)
    {
        window.EnsureGamepadTarget();
        await ResetListToTopAsync(window, profile, progress, counters, log, token);
        window.EnsureGamepadTarget();
        token.ThrowIfCancellationRequested();
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var point = window.ToScreenPoint(new PointF(offset.X + step.X, offset.Y + step.Y));
        log.WriteEvent("ENTRY_SELECT_FIRST", $"point={point}; final acceptance requires dynamic grid row=0 column=0 and top scrollbar");
        window.LeftClick(point);
        await Task.Delay(350, token);
        window.EnsureGamepadTarget();
        using var frame = window.Capture(window.ClientScreenRect);
        frame.Save(Path.Combine(directory, "entry", "03-drive-discs-first.png"), ImageFormat.Png);
        log.WriteEvent("ENTRY_HANDOFF_GAMEPAD", "keyboard/mouse preparation ended; all item traversal uses virtual controller");
    }
}
