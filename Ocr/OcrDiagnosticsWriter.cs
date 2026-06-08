using System.Globalization;
using System.Text;

namespace ZZZScannerNext.Ocr;

public sealed class OcrDiagnosticsWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public OcrDiagnosticsWriter(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
        _writer = new StreamWriter(file, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("timestamp,worker_id,batch_size,roi_count,max_width,run_count,bitmap_to_mat_ms,preprocess_ms,inference_ms,decode_ms,total_ms,clean_ms,fallback_count,queued_completed_backlog");
        _writer.Flush();
    }

    public void Write(
        int workerId,
        int batchSize,
        PaddleOcrRecognizer.OcrBatchDiagnostics diagnostics,
        double bitmapToMatMs,
        double cleanMs,
        int fallbackCount,
        int backlog)
    {
        lock (_sync)
        {
            _writer.WriteLine(string.Join(",", [
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                workerId.ToString(CultureInfo.InvariantCulture),
                batchSize.ToString(CultureInfo.InvariantCulture),
                diagnostics.RoiCount.ToString(CultureInfo.InvariantCulture),
                diagnostics.MaxWidth.ToString(CultureInfo.InvariantCulture),
                diagnostics.RunCount.ToString(CultureInfo.InvariantCulture),
                bitmapToMatMs.ToString("F3", CultureInfo.InvariantCulture),
                diagnostics.PreprocessMs.ToString("F3", CultureInfo.InvariantCulture),
                diagnostics.InferenceMs.ToString("F3", CultureInfo.InvariantCulture),
                diagnostics.DecodeMs.ToString("F3", CultureInfo.InvariantCulture),
                diagnostics.TotalMs.ToString("F3", CultureInfo.InvariantCulture),
                cleanMs.ToString("F3", CultureInfo.InvariantCulture),
                fallbackCount.ToString(CultureInfo.InvariantCulture),
                backlog.ToString(CultureInfo.InvariantCulture)
            ]));
            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}
