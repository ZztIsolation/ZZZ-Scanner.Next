using System.Diagnostics;

namespace ZZZScannerNext.Scanning.Gamepad;

internal sealed class ProbeMetrics
{
    private readonly Dictionary<string, List<double>> stages = new();
    public void Add(string stage, double milliseconds)
    {
        if (!stages.TryGetValue(stage, out var values)) stages[stage] = values = [];
        values.Add(milliseconds);
    }
    public IDisposable Measure(string stage) => new Measurement(this, stage);
    public object Report(double elapsedSeconds, int? ocrItems, double cpuMs, long peakWorkingSet) => new
    {
        ElapsedSeconds = elapsedSeconds, OcrItems = ocrItems,
        OcrItemsPerSecond = elapsedSeconds > 0 ? ocrItems / elapsedSeconds : 0,
        ProcessCpuMs = cpuMs, AverageCpuCores = elapsedSeconds > 0 ? cpuMs / 1000 / elapsedSeconds : 0,
        PeakWorkingSetBytes = peakWorkingSet,
        Note = "Stage totals may overlap (e.g. OCR inference is part of OCR total). Process CPU/peak include this process only, not game or driver; peak is process lifetime.",
        Stages = stages.ToDictionary(p => p.Key, p =>
        {
            var sorted = p.Value.Order().ToArray();
            return new { Count = sorted.Length, TotalMs = sorted.Sum(), MeanMs = sorted.Average(),
                P50Ms = sorted[(sorted.Length - 1) / 2], P95Ms = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], MaxMs = sorted[^1] };
        })
    };
    private sealed class Measurement(ProbeMetrics owner, string stage) : IDisposable
    {
        private readonly Stopwatch timer = Stopwatch.StartNew();
        public void Dispose() => owner.Add(stage, timer.Elapsed.TotalMilliseconds);
    }
}
