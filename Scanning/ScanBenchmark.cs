using System.Globalization;
using System.Text.RegularExpressions;

namespace ZZZScannerNext.Scanning;

public static class ScanBenchmark
{
    private static readonly Regex EventRegex = new(
        @"^\[(?<timestamp>[^\]]+)\].*EVENT #\d+ (?<kind>[A-Z_]+): (?<detail>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex CellClickRegex = new(
        @"col=(?<col>\d+)/(?<max>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex CellTimingRegex = new(
        @"CELL_TIMING: index=(?<index>\d+).*?panelWaitMs=(?<panel>[\d.]+), enqueueWaitMs=(?<enqueue>[\d.]+), fallback=(?<fallback>True|False), visibleRois=(?<visible>\d+)/(?<total>\d+), totalMs=(?<cellTotal>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex StartRegex = new(
        @"Start scan\..*?OcrWorkers=(?<workers>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex CounterRegex = new(
        @"visited=(?<visited>\d+), queued=(?<queued>\d+), completed=(?<completed>\d+), failed=(?<failed>\d+)",
        RegexOptions.Compiled);

    public static int Run(string scanDirectory, string? baselineDirectory)
    {
        if (!Directory.Exists(scanDirectory))
        {
            Console.Error.WriteLine($"Scan directory not found: {scanDirectory}");
            return 2;
        }

        if (baselineDirectory is not null && !Directory.Exists(baselineDirectory))
        {
            Console.Error.WriteLine($"Baseline scan directory not found: {baselineDirectory}");
            return 2;
        }

        var current = Analyze(scanDirectory);
        if (!current.Valid)
        {
            Console.Error.WriteLine(current.ErrorMessage);
            return 2;
        }

        ScanReport? baseline = null;
        if (baselineDirectory is not null)
        {
            baseline = Analyze(baselineDirectory);
            if (!baseline.Valid)
            {
                Console.Error.WriteLine(baseline.ErrorMessage);
                return 2;
            }
        }

        WriteReport("current", current);
        if (baseline is not null)
        {
            WriteReport("baseline", baseline);
            WriteDeltaReport(current, baseline);
        }

        WriteDiagnosis(current);
        return 0;
    }

    private static ScanReport Analyze(string scanDirectory)
    {
        var logFile = Path.Combine(scanDirectory, "scan.log");
        if (!File.Exists(logFile))
        {
            return ScanReport.Invalid(scanDirectory, $"scan.log not found: {logFile}");
        }

        var lines = File.ReadAllLines(logFile);
        var events = ParseEvents(lines);
        var cellTimings = ParseCellTimings(lines);
        var ocrRows = ReadCsv(Path.Combine(scanDirectory, "ocr_diagnostics.csv"));
        var resourceRows = ReadCsv(Path.Combine(scanDirectory, "resource.csv"));
        var intervals = BuildClickIntervals(events);
        var scrollDurations = BuildScrollDurations(events);
        var lastCounters = ParseLastCounters(lines);
        var resourceCounters = ParseResourceCounters(resourceRows);

        var report = new ScanReport(scanDirectory)
        {
            Valid = true,
            StartTime = ParseStartTime(lines),
            EndTime = ParseEndTime(lines),
            StopReason = ParseStopReason(lines),
            OcrWorkers = ParseOcrWorkers(lines),
            ErrorFileCount = Directory.EnumerateFiles(scanDirectory, "*.error.txt").Count(),
            Non15FileCount = Directory.EnumerateFiles(scanDirectory, "*.non15.txt").Count(),
            CellTimingCount = cellTimings.Count,
            CellTimingFallbackCount = cellTimings.Count(x => x.Fallback),
            CellTimingFallbackLogCount = lines.Count(line => line.Contains("Panel probes stayed unchanged", StringComparison.Ordinal)),
            MinVisibleRois = cellTimings.Count > 0 ? cellTimings.Min(x => x.VisibleRois) : null,
            IncompleteRoiCount = cellTimings.Count(x => x.VisibleRois < x.TotalRois),
            LastVisited = lastCounters?.Visited ?? resourceCounters?.Visited,
            LastQueued = lastCounters?.Queued ?? resourceCounters?.Queued,
            LastCompleted = lastCounters?.Completed ?? resourceCounters?.Completed,
            LastFailed = lastCounters?.Failed ?? resourceCounters?.Failed,
            ClickSameRow = Stats.From(intervals.Where(x => !x.AfterScroll).Select(x => x.Milliseconds)),
            ClickAfterScroll = Stats.From(intervals.Where(x => x.AfterScroll).Select(x => x.Milliseconds)),
            ClickAll = Stats.From(intervals.Select(x => x.Milliseconds)),
            ScrollDuration = Stats.From(scrollDurations),
            PanelWait = Stats.From(cellTimings.Select(x => x.PanelWaitMs)),
            CellTotal = Stats.From(cellTimings.Select(x => x.TotalMs)),
            EnqueueWait = Stats.From(cellTimings.Select(x => x.EnqueueWaitMs)),
            FallbackPanelWait = Stats.From(cellTimings.Where(x => x.Fallback).Select(x => x.PanelWaitMs)),
            NormalPanelWait = Stats.From(cellTimings.Where(x => !x.Fallback).Select(x => x.PanelWaitMs)),
            OcrBatchSize = Stats.From(ReadColumn(ocrRows, "batch_size")),
            OcrBitmapToMat = Stats.From(ReadColumn(ocrRows, "bitmap_to_mat_ms")),
            OcrPreprocess = Stats.From(ReadColumn(ocrRows, "preprocess_ms")),
            OcrInference = Stats.From(ReadColumn(ocrRows, "inference_ms")),
            OcrDecode = Stats.From(ReadColumn(ocrRows, "decode_ms")),
            OcrTotal = Stats.From(ReadColumn(ocrRows, "total_ms")),
            OcrClean = Stats.From(ReadColumn(ocrRows, "clean_ms")),
            OcrBacklog = Stats.From(ReadColumn(ocrRows, "queued_completed_backlog")),
            ScannerCpu = Stats.From(ReadColumn(resourceRows, "scanner_cpu_percent")),
            ResourceBacklog = Stats.From(ReadColumn(resourceRows, "ocr_backlog"))
        };

        report.DurationSeconds = report.StartTime is not null && report.EndTime is not null
            ? Math.Max(0, (report.EndTime.Value - report.StartTime.Value).TotalSeconds)
            : null;

        return report;
    }

    private static List<ScanEvent> ParseEvents(IEnumerable<string> lines)
    {
        var events = new List<ScanEvent>();
        foreach (var line in lines)
        {
            var match = EventRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
            {
                continue;
            }

            events.Add(new ScanEvent(timestamp, match.Groups["kind"].Value, match.Groups["detail"].Value));
        }

        return events;
    }

    private static List<CellTiming> ParseCellTimings(IEnumerable<string> lines)
    {
        var timings = new List<CellTiming>();
        foreach (var line in lines)
        {
            var match = CellTimingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            timings.Add(new CellTiming(
                ParseInt(match.Groups["index"].Value),
                ParseDouble(match.Groups["panel"].Value),
                ParseDouble(match.Groups["enqueue"].Value),
                bool.Parse(match.Groups["fallback"].Value),
                ParseInt(match.Groups["visible"].Value),
                ParseInt(match.Groups["total"].Value),
                ParseDouble(match.Groups["cellTotal"].Value)));
        }

        return timings;
    }

    private static List<ClickInterval> BuildClickIntervals(IReadOnlyList<ScanEvent> events)
    {
        var intervals = new List<ClickInterval>();
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (item.Kind != "CELL_CLICK" || !CellClickRegex.IsMatch(item.Detail))
            {
                continue;
            }

            var afterScroll = false;
            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind == "ROW_SCROLL_START")
                {
                    afterScroll = true;
                }

                if (events[j].Kind == "CELL_MOVE")
                {
                    intervals.Add(new ClickInterval((events[j].Timestamp - item.Timestamp).TotalMilliseconds, afterScroll));
                    break;
                }
            }
        }

        return intervals;
    }

    private static List<double> BuildScrollDurations(IReadOnlyList<ScanEvent> events)
    {
        var durations = new List<double>();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Kind != "ROW_SCROLL_START")
            {
                continue;
            }

            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind is "ROW_SCROLL_DONE" or "ROW_SCROLL_VERIFY")
                {
                    durations.Add((events[j].Timestamp - events[i].Timestamp).TotalMilliseconds);
                    break;
                }
            }
        }

        return durations;
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadCsv(string file)
    {
        if (!File.Exists(file))
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var lines = File.ReadAllLines(file);
        if (lines.Length < 2)
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var headers = lines[0].Split(',');
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var values = lines[i].Split(',');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headers.Length, values.Length); c++)
            {
                row[headers[c]] = values[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<double> ReadColumn(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private static DateTime? ParseStartTime(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("Start scan.", StringComparison.Ordinal))
            {
                return ParseLineTime(line);
            }
        }

        return null;
    }

    private static DateTime? ParseEndTime(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var time = ParseLineTime(lines[i]);
            if (time is not null)
            {
                return time;
            }
        }

        return null;
    }

    private static DateTime? ParseLineTime(string line)
    {
        var end = line.IndexOf(']');
        if (end <= 1 || line[0] != '[')
        {
            return null;
        }

        return DateTime.TryParse(line.Substring(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
            ? timestamp
            : null;
    }

    private static string ParseStopReason(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("Scan canceled.", StringComparison.Ordinal)
                || line.Contains("Stop at #", StringComparison.Ordinal)
                || line.Contains("Scan failed:", StringComparison.Ordinal)
                || line.Contains("End:", StringComparison.Ordinal))
            {
                var close = line.IndexOf("] ", StringComparison.Ordinal);
                return close >= 0 ? line[(close + 2)..] : line;
            }
        }

        return "unknown";
    }

    private static int? ParseOcrWorkers(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = StartRegex.Match(line);
            if (match.Success)
            {
                return ParseInt(match.Groups["workers"].Value);
            }
        }

        return null;
    }

    private static CounterSnapshot? ParseLastCounters(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var match = CounterRegex.Match(lines[i]);
            if (match.Success)
            {
                return new CounterSnapshot(
                    ParseInt(match.Groups["visited"].Value),
                    ParseInt(match.Groups["queued"].Value),
                    ParseInt(match.Groups["completed"].Value),
                    ParseInt(match.Groups["failed"].Value));
            }
        }

        return null;
    }

    private static CounterSnapshot? ParseResourceCounters(IReadOnlyList<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var last = rows[^1];
        return new CounterSnapshot(
            ReadInt(last, "visited"),
            ReadInt(last, "queued"),
            ReadInt(last, "completed"),
            ReadInt(last, "failed"));
    }

    private static void WriteReport(string prefix, ScanReport report)
    {
        Write(prefix, "scan_dir", report.ScanDirectory);
        Write(prefix, "scan_name", Path.GetFileName(report.ScanDirectory));
        Write(prefix, "duration_sec", report.DurationSeconds);
        Write(prefix, "stop_reason", report.StopReason);
        Write(prefix, "ocr_workers", report.OcrWorkers);
        Write(prefix, "error_files", report.ErrorFileCount);
        Write(prefix, "non15_files", report.Non15FileCount);
        Write(prefix, "last_visited", report.LastVisited);
        Write(prefix, "last_queued", report.LastQueued);
        Write(prefix, "last_completed", report.LastCompleted);
        Write(prefix, "last_failed", report.LastFailed);
        Write(prefix, "cell_timing_count", report.CellTimingCount);
        Write(prefix, "fallback_count", report.EffectiveFallbackCount);
        Write(prefix, "fallback_rate_percent", Percent(report.EffectiveFallbackCount, Math.Max(report.CellTimingCount, report.ClickAll.Count)));
        Write(prefix, "min_visible_rois", report.MinVisibleRois);
        Write(prefix, "incomplete_roi_count", report.IncompleteRoiCount);

        WriteStats(prefix, "same_row_click_ms", report.ClickSameRow);
        WriteStats(prefix, "after_scroll_click_ms", report.ClickAfterScroll);
        WriteStats(prefix, "all_click_ms", report.ClickAll);
        WriteStats(prefix, "scroll_ms", report.ScrollDuration);
        WriteStats(prefix, "panel_wait_ms", report.PanelWait);
        WriteStats(prefix, "cell_total_ms", report.CellTotal);
        WriteStats(prefix, "enqueue_wait_ms", report.EnqueueWait);
        WriteStats(prefix, "fallback_panel_wait_ms", report.FallbackPanelWait);
        WriteStats(prefix, "normal_panel_wait_ms", report.NormalPanelWait);
        WriteStats(prefix, "ocr_batch_size", report.OcrBatchSize);
        WriteStats(prefix, "ocr_bitmap_to_mat_ms", report.OcrBitmapToMat);
        WriteStats(prefix, "ocr_preprocess_ms", report.OcrPreprocess);
        WriteStats(prefix, "ocr_inference_ms", report.OcrInference);
        WriteStats(prefix, "ocr_decode_ms", report.OcrDecode);
        WriteStats(prefix, "ocr_total_ms", report.OcrTotal);
        WriteStats(prefix, "ocr_clean_ms", report.OcrClean);
        WriteStats(prefix, "ocr_backlog", report.OcrBacklog);
        WriteStats(prefix, "scanner_cpu_percent", report.ScannerCpu);
        WriteStats(prefix, "resource_ocr_backlog", report.ResourceBacklog);
    }

    private static void WriteDeltaReport(ScanReport current, ScanReport baseline)
    {
        WriteDelta("same_row_click_delta_percent", current.ClickSameRow.Average, baseline.ClickSameRow.Average);
        WriteDelta("after_scroll_click_delta_percent", current.ClickAfterScroll.Average, baseline.ClickAfterScroll.Average);
        WriteDelta("all_click_delta_percent", current.ClickAll.Average, baseline.ClickAll.Average);
        WriteDelta("panel_wait_delta_percent", current.PanelWait.Average, baseline.PanelWait.Average);
        WriteDelta("ocr_total_delta_percent", current.OcrTotal.Average, baseline.OcrTotal.Average);
        WriteDelta("fallback_rate_delta_percent",
            PercentValue(current.EffectiveFallbackCount, Math.Max(current.CellTimingCount, current.ClickAll.Count)),
            PercentValue(baseline.EffectiveFallbackCount, Math.Max(baseline.CellTimingCount, baseline.ClickAll.Count)));
    }

    private static void WriteDiagnosis(ScanReport report)
    {
        var panelHigh = report.PanelWait.HasData && report.PanelWait.Average >= 180;
        var scrollHigh = report.ClickAfterScroll.HasData
            && report.ClickSameRow.HasData
            && report.ClickAfterScroll.Average >= report.ClickSameRow.Average * 2.0;
        var backlogHigh = report.ResourceBacklog.HasData
            && report.OcrWorkers is > 0
            && report.ResourceBacklog.Maximum >= report.OcrWorkers.Value * 2;
        var ocrHigh = report.OcrTotal.HasData && report.OcrBacklog.HasData && report.OcrBacklog.Maximum >= 4;

        Write("diagnosis", "panel_wait", panelHigh ? "high: tune panel settle/poll/fallback next" : "ok");
        Write("diagnosis", "scroll_wait", scrollHigh ? "high: tune list stable wait carefully" : "ok");
        Write("diagnosis", "ocr", backlogHigh || ocrHigh ? "high: collect samples and run --ocr-benchmark" : "ok");
        Write("diagnosis", "roi_integrity", report.IncompleteRoiCount > 0 ? "risk: incomplete ROI captures present" : "ok");
    }

    private static void WriteStats(string prefix, string name, Stats stats)
    {
        Write(prefix, $"{name}_count", stats.Count);
        Write(prefix, $"{name}_avg", stats.Average);
        Write(prefix, $"{name}_p50", stats.P50);
        Write(prefix, $"{name}_p90", stats.P90);
        Write(prefix, $"{name}_min", stats.Minimum);
        Write(prefix, $"{name}_max", stats.Maximum);
    }

    private static void Write(string prefix, string name, object? value)
    {
        var formatted = value switch
        {
            null => "N/A",
            double d => double.IsNaN(d) ? "N/A" : d.ToString("F3", CultureInfo.InvariantCulture),
            float f => float.IsNaN(f) ? "N/A" : f.ToString("F3", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "N/A"
        };

        Console.WriteLine($"{prefix}.{name}={formatted}");
    }

    private static void WriteDelta(string name, double? current, double? baseline)
    {
        if (current is null || baseline is null || Math.Abs(baseline.Value) < 0.000001)
        {
            Console.WriteLine($"delta.{name}=N/A");
            return;
        }

        var delta = (current.Value - baseline.Value) * 100.0 / baseline.Value;
        Console.WriteLine($"delta.{name}={delta.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    private static double? Percent(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static double? PercentValue(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> row, string column)
    {
        return row.TryGetValue(column, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private sealed class ScanReport
    {
        public ScanReport(string scanDirectory)
        {
            ScanDirectory = scanDirectory;
        }

        public string ScanDirectory { get; }
        public bool Valid { get; init; }
        public string ErrorMessage { get; init; } = "";
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public double? DurationSeconds { get; set; }
        public string StopReason { get; set; } = "";
        public int? OcrWorkers { get; set; }
        public int ErrorFileCount { get; set; }
        public int Non15FileCount { get; set; }
        public int CellTimingCount { get; set; }
        public int CellTimingFallbackCount { get; set; }
        public int CellTimingFallbackLogCount { get; set; }
        public int EffectiveFallbackCount => CellTimingCount > 0 ? CellTimingFallbackCount : CellTimingFallbackLogCount;
        public int? MinVisibleRois { get; set; }
        public int IncompleteRoiCount { get; set; }
        public int? LastVisited { get; set; }
        public int? LastQueued { get; set; }
        public int? LastCompleted { get; set; }
        public int? LastFailed { get; set; }
        public Stats ClickSameRow { get; set; }
        public Stats ClickAfterScroll { get; set; }
        public Stats ClickAll { get; set; }
        public Stats ScrollDuration { get; set; }
        public Stats PanelWait { get; set; }
        public Stats CellTotal { get; set; }
        public Stats EnqueueWait { get; set; }
        public Stats FallbackPanelWait { get; set; }
        public Stats NormalPanelWait { get; set; }
        public Stats OcrBatchSize { get; set; }
        public Stats OcrBitmapToMat { get; set; }
        public Stats OcrPreprocess { get; set; }
        public Stats OcrInference { get; set; }
        public Stats OcrDecode { get; set; }
        public Stats OcrTotal { get; set; }
        public Stats OcrClean { get; set; }
        public Stats OcrBacklog { get; set; }
        public Stats ScannerCpu { get; set; }
        public Stats ResourceBacklog { get; set; }

        public static ScanReport Invalid(string scanDirectory, string message)
        {
            return new ScanReport(scanDirectory)
            {
                Valid = false,
                ErrorMessage = message
            };
        }
    }

    private readonly record struct ScanEvent(DateTime Timestamp, string Kind, string Detail);

    private readonly record struct ClickInterval(double Milliseconds, bool AfterScroll);

    private readonly record struct CellTiming(int Index, double PanelWaitMs, double EnqueueWaitMs, bool Fallback, int VisibleRois, int TotalRois, double TotalMs);

    private readonly record struct CounterSnapshot(int Visited, int Queued, int Completed, int Failed);

    private readonly record struct Stats(int Count, double? Average, double? P50, double? P90, double? Minimum, double? Maximum)
    {
        public bool HasData => Count > 0;

        public static Stats From(IEnumerable<double> values)
        {
            var sorted = values
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
            {
                return new Stats(0, null, null, null, null, null);
            }

            return new Stats(
                sorted.Length,
                sorted.Average(),
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.90),
                sorted[0],
                sorted[^1]);
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            var index = Math.Clamp((int)(sorted.Count * percentile), 0, sorted.Count - 1);
            return sorted[index];
        }
    }
}
