using ZZZScannerNext.Core;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;
using System.Drawing;
using System.Text.Json;

static partial class Program
{
    private static bool TryRunSelectionAnchorCommand(string[] args, out int exitCode)
    {
        exitCode = 0;
        var commandName = args[0].ToLowerInvariant();
        if (commandName == "--selection-anchor-eval")
        {
            try { exitCode = EvaluateSelectionAnchors(args); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            return true;
        }
        if (commandName is not ("--selection-anchor-capture" or "--wheel-anchor-probe")) return false;
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(false, @"Local\ZZZScannerNext.ScanOnce");
        var acquired = false;
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            acquired = mutex.WaitOne(0);
            if (!acquired) { exitCode = 73; return true; }
            var options = BuildScanOptions(ParseScanRunCommand(args));
            var controller = new ScanController(ScanProfileFile.Load(), WikiData.Load());
            string output;
            if (commandName == "--wheel-anchor-probe")
            {
                options.RowAdvanceMode = RowAdvanceMode.WheelVerified;
                options.SelectionAnchorMode ??= SelectionAnchorMode.Shadow;
                var runs = TryReadIntOption(args, "--runs", out var n) ? Math.Clamp(n, 1, 200) : 30;
                var inspectText = ReadOption(args, "--inspect-indices");
                var inspectIndices = string.IsNullOrWhiteSpace(inspectText) ? [] : inspectText.Split(',').Select(int.Parse).Order().ToArray();
                if (inspectIndices.Any(index => index < 1 || index > 9999)) throw new ArgumentException("Invalid inventory indices.");
                if (inspectIndices.Length > 0) runs = Math.Max(1, (inspectIndices.Max() - 1) / 9 - 2);
                var result = controller.ProbeWheelAnchorAsync(options, runs, args.Contains("--continuous") || inspectIndices.Length > 0, cts.Token, inspectIndices, args.Contains("--force-reset-drag")).GetAwaiter().GetResult();
                output = result.OutputDirectory;
                exitCode = result.Success ? 0 : 1;
                Console.WriteLine($"success={result.Success}, one_row_runs={result.OneRowRuns}, failed_runs={result.FailedRuns}");
            }
            else output = controller.CaptureSelectionAnchorAsync(options, cts.Token).GetAwaiter().GetResult();
            Console.WriteLine($"output_dir={output}");
        }
        catch (OperationCanceledException) { exitCode = 130; }
        catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (acquired) mutex.ReleaseMutex();
        }
        return true;
    }

    private static int EvaluateSelectionAnchors(string[] args)
    {
        if (args.Length < 2) throw new ArgumentException("Expected an image or capture directory.");
        if (!Directory.Exists(args[1]) && !File.Exists(args[1])) throw new FileNotFoundException("Evaluation input not found", args[1]);
        var directory = Directory.Exists(args[1]) ? args[1] : Path.GetDirectoryName(Path.GetFullPath(args[1]))!;
        var manifestPath = ReadOption(args, "--manifest") ?? Path.Combine(directory, "manifest.json");
        var fullClientImages = !File.Exists(manifestPath);
        if (fullClientImages && ReadOption(args, "--manifest") is not null) throw new FileNotFoundException("Manifest not found", manifestPath);
        var manifestText = fullClientImages ? JsonSerializer.Serialize(new
        {
            clientWidth = 1920, clientHeight = 1080, cropX = 0, cropY = 0,
            samples = (File.Exists(args[1]) ? [Path.GetFullPath(args[1])] : Directory.GetFiles(directory, "*.png"))
                .Select(file => new { file = Path.GetFileName(file) })
        }) : File.ReadAllText(manifestPath);
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;
        var profile = ScanProfileFile.Load().ResolveRequired(ReadOption(args, "--profile") ?? ScanOptions.DefaultProfileName);
        var client = new Rectangle(0, 0, root.GetProperty("clientWidth").GetInt32(), root.GetProperty("clientHeight").GetInt32());
        var cropOrigin = new Point(root.GetProperty("cropX").GetInt32(), root.GetProperty("cropY").GetInt32());
        var mismatches = 0;
        var labelled = 0;
        var results = new List<object>();
        foreach (var sample in root.GetProperty("samples").EnumerateArray())
        {
            var name = sample.GetProperty("file").GetString()!;
            if (File.Exists(args[1]) && Path.GetFileName(args[1]) != name) continue;
            using var image = new Bitmap(Path.Combine(directory, name));
            var geometry = SelectionAnchorGeometry.FromProfile(profile, fullClientImages ? new Rectangle(Point.Empty, image.Size) : client, new Rectangle(cropOrigin, image.Size));
            var observation = SelectionAnchorDetector.FindUnique(image, geometry, new SelectionAnchorPolicy());
            var hasExpected = sample.TryGetProperty("expectedRow", out var expected) && expected.ValueKind == JsonValueKind.Number;
            bool? match = hasExpected ? observation.Unique && observation.Row == expected.GetInt32()
                && observation.Column == sample.GetProperty("expectedColumn").GetInt32() : null;
            if (hasExpected) labelled++;
            if (match == false) mismatches++;
            Console.WriteLine($"file={name}, {observation.Describe()}, expectedMatch={match?.ToString() ?? "not_labeled"}");
            results.Add(new { file = name, observation, expectedMatch = match });
        }
        File.WriteAllText(Path.Combine(directory, "anchor-evaluation.json"), JsonSerializer.Serialize(results, JsonDefaults.Write));
        Console.WriteLine($"mismatch_count={mismatches}");
        Console.WriteLine($"labelled_count={labelled}, geometry={(fullClientImages ? "full_client_image" : "manifest")}");
        return mismatches == 0 ? 0 : 1;
    }
}
