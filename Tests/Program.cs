using System.IO.Compression;
using System.Drawing;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ZZZScannerHelper;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.WebSocket;

namespace ZZZScannerNext.RegressionTests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new List<(string Name, Func<Task> Run)>
        {
            ("trusted download URIs", TestTrustedDownloadUrisAsync),
            ("manifest validation", TestManifestValidationAsync),
            ("manifest v2 package selection", TestManifestV2PackageSelectionAsync),
            ("runtime path containment", TestRuntimePathContainmentAsync),
            ("installed runtime verification", TestInstalledRuntimeVerificationAsync),
            ("managed OCR preprocessing", TestManagedOcrPreprocessingAsync),
            ("OCR output equivalence", TestOcrOutputEquivalenceAsync),
            ("browser origin allowlist", TestBrowserOriginAllowlistAsync),
            ("WebSocket origin and token handshake", TestWebSocketHandshakeAsync),
            ("fast mode defaults", TestFastModeDefaultsAsync),
            ("strict profile selection", TestStrictProfileSelectionAsync),
            ("assembly-backed app version", TestAssemblyBackedAppVersionAsync)
        };

        var currentPackage = Environment.GetEnvironmentVariable("ZZZ_SCANNER_TEST_PACKAGE");
        var currentRuntime = Environment.GetEnvironmentVariable("ZZZ_SCANNER_TEST_RUNTIME");
        if (!string.IsNullOrWhiteSpace(currentPackage) && !string.IsNullOrWhiteSpace(currentRuntime))
        {
            tests.Add(("current installed runtime", () => HelperSecurity.VerifyInstalledRuntimeAsync(
                currentPackage,
                currentRuntime,
                "ZZZ-Scanner.Next.exe",
                CancellationToken.None)));
        }

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
            }
        }

        Console.WriteLine($"regression_tests.total={tests.Count}");
        Console.WriteLine($"regression_tests.failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestTrustedDownloadUrisAsync()
    {
        HelperSecurity.EnsureTrustedDownloadUri(new Uri("https://example.com/manifest.json"), "test");
        HelperSecurity.EnsureTrustedDownloadUri(new Uri("http://127.0.0.1:8787/manifest.json"), "test");
        HelperSecurity.EnsureTrustedDownloadUri(new Uri("http://localhost:8787/manifest.json"), "test");

        AssertThrows<InvalidOperationException>(() =>
            HelperSecurity.EnsureTrustedDownloadUri(new Uri("http://121.199.21.10/manifest.json"), "test"));
        AssertThrows<InvalidOperationException>(() =>
            HelperSecurity.EnsureTrustedDownloadUri(new Uri("file:///C:/temp/manifest.json"), "test"));
        return Task.CompletedTask;
    }

    private static Task TestManifestValidationAsync()
    {
        var manifest = ValidManifest();
        HelperSecurity.ValidateManifest(manifest, new Uri("https://example.com/manifest.json"), new Version(1, 0, 2));

        AssertThrows<InvalidDataException>(() =>
        {
            var invalid = ValidManifest();
            invalid.ScannerVersion = "../1.0.36";
            HelperSecurity.ValidateManifest(invalid, new Uri("https://example.com/manifest.json"), new Version(1, 0, 2));
        });
        AssertThrows<InvalidDataException>(() =>
        {
            var invalid = ValidManifest();
            invalid.Entry = "../ZZZ-Scanner.Next.exe";
            HelperSecurity.ValidateManifest(invalid, new Uri("https://example.com/manifest.json"), new Version(1, 0, 2));
        });
        AssertThrows<InvalidOperationException>(() =>
        {
            var invalid = ValidManifest();
            invalid.LauncherMinVersion = "2.0.0";
            HelperSecurity.ValidateManifest(invalid, new Uri("https://example.com/manifest.json"), new Version(1, 0, 2));
        });
        return Task.CompletedTask;
    }

    private static Task TestManifestV2PackageSelectionAsync()
    {
        var manifest = ValidV2Manifest();
        HelperSecurity.ValidateManifest(manifest, new Uri("https://example.com/manifest.json"), new Version(1, 1, 0));
        AssertEqual("win-x64-fdd", HelperPlatform.SelectPackageForTesting(manifest, desktopRuntimeAvailable: true).Id);
        AssertEqual("win-x64-self-contained", HelperPlatform.SelectPackageForTesting(manifest, desktopRuntimeAvailable: false).Id);

        var invalid = ValidV2Manifest();
        invalid.Packages.RemoveAll(package => package.Mode == ScannerPackageModes.SelfContained);
        AssertThrows<InvalidDataException>(() =>
            HelperSecurity.ValidateManifest(invalid, new Uri("https://example.com/manifest.json"), new Version(1, 1, 0)));
        return Task.CompletedTask;
    }

    private static Task TestRuntimePathContainmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-scanner-path-test", Guid.NewGuid().ToString("N"));
        var expected = Path.GetFullPath(Path.Combine(root, "1.0.36"));
        AssertEqual(expected, HelperSecurity.ResolvePathWithinRoot(root, "1.0.36"));
        AssertThrows<InvalidDataException>(() => HelperSecurity.ResolvePathWithinRoot(root, "..\\outside"));
        AssertThrows<InvalidDataException>(() => HelperSecurity.ResolvePathWithinRoot(root, Path.GetPathRoot(root)!));
        return Task.CompletedTask;
    }

    private static async Task TestInstalledRuntimeVerificationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-scanner-runtime-test", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(root, "scanner.zip");
        var installDirectory = Path.Combine(root, "runtime");
        Directory.CreateDirectory(root);

        try
        {
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "ZZZ-Scanner.Next.exe", "scanner-binary");
                WriteEntry(archive, "Data/config.json", "{}");
            }

            ZipFile.ExtractToDirectory(packagePath, installDirectory);
            await HelperSecurity.VerifyInstalledRuntimeAsync(
                packagePath,
                installDirectory,
                "ZZZ-Scanner.Next.exe",
                CancellationToken.None);

            Directory.CreateDirectory(Path.Combine(installDirectory, "Scans"));
            await File.WriteAllTextAsync(Path.Combine(installDirectory, "Scans", "export.json"), "[]");
            await HelperSecurity.VerifyInstalledRuntimeAsync(
                packagePath,
                installDirectory,
                "ZZZ-Scanner.Next.exe",
                CancellationToken.None);

            await File.WriteAllTextAsync(Path.Combine(installDirectory, "ZZZ-Scanner.Next.exe"), "tampered");
            await AssertThrowsAsync<InvalidDataException>(() => HelperSecurity.VerifyInstalledRuntimeAsync(
                packagePath,
                installDirectory,
                "ZZZ-Scanner.Next.exe",
                CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task TestManagedOcrPreprocessingAsync()
    {
        using var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.FromArgb(10, 20, 30));
        bitmap.SetPixel(1, 0, Color.FromArgb(110, 120, 130));
        bitmap.SetPixel(0, 1, Color.FromArgb(210, 220, 230));
        bitmap.SetPixel(1, 1, Color.FromArgb(50, 60, 70));

        var identity = PaddleOcrPreprocessor.CreateTensorForTesting(bitmap, new Rectangle(0, 0, 2, 2), 2, 2);
        var expectedIdentity = new[]
        {
            30f, 130f, 230f, 70f,
            20f, 120f, 220f, 60f,
            10f, 110f, 210f, 50f
        };
        for (var index = 0; index < expectedIdentity.Length; index++)
        {
            AssertNear(expectedIdentity[index] / 255f, identity[index], 1e-6f);
        }

        var resized = PaddleOcrPreprocessor.CreateTensorForTesting(bitmap, new Rectangle(0, 0, 2, 2), 1, 1);
        AssertNear(115f / 255f, resized[0], 1e-6f);
        AssertNear(105f / 255f, resized[1], 1e-6f);
        AssertNear(95f / 255f, resized[2], 1e-6f);
        AssertThrows<ArgumentOutOfRangeException>(() =>
            PaddleOcrPreprocessor.CreateTensorForTesting(bitmap, new Rectangle(-1, 0, 2, 2), 2, 2));

        using var baselineBitmap = new Bitmap(3, 3);
        var baselineColors = new[]
        {
            Color.FromArgb(10, 20, 30), Color.FromArgb(40, 50, 60), Color.FromArgb(70, 80, 90),
            Color.FromArgb(100, 110, 120), Color.FromArgb(130, 140, 150), Color.FromArgb(160, 170, 180),
            Color.FromArgb(190, 200, 210), Color.FromArgb(220, 230, 240), Color.FromArgb(25, 35, 45)
        };
        for (var y = 0; y < baselineBitmap.Height; y++)
        {
            for (var x = 0; x < baselineBitmap.Width; x++)
            {
                baselineBitmap.SetPixel(x, y, baselineColors[(y * baselineBitmap.Width) + x]);
            }
        }

        // Frozen from OpenCvSharp 4.11 INTER_LINEAR output for this 3x3 -> 5x4 resize.
        var openCvPlanarBgr = new byte[]
        {
            30, 42, 60, 78, 90, 86, 98, 116, 134, 146, 154, 165, 184, 151, 129, 210, 222, 240, 123, 45,
            20, 32, 50, 68, 80, 76, 88, 106, 124, 136, 144, 155, 174, 141, 119, 200, 212, 230, 113, 35,
            10, 22, 40, 58, 70, 66, 78, 96, 114, 126, 134, 145, 164, 131, 109, 190, 202, 220, 103, 25
        };
        var baselineTensor = PaddleOcrPreprocessor.CreateTensorForTesting(
            baselineBitmap,
            new Rectangle(0, 0, 3, 3),
            5,
            4);
        var maximumError = baselineTensor
            .Select((actual, index) => Math.Abs(actual - (openCvPlanarBgr[index] / 255f)))
            .Max();
        if (maximumError > 1e-5f)
        {
            var mismatch = baselineTensor
                .Select((actual, index) => new
                {
                    Index = index,
                    Actual = actual,
                    Expected = openCvPlanarBgr[index] / 255f,
                    Error = Math.Abs(actual - (openCvPlanarBgr[index] / 255f))
                })
                .OrderByDescending(item => item.Error)
                .First();
            var mismatches = baselineTensor
                .Select((actual, index) => new { Index = index, ActualByte = (int)Math.Round(actual * 255), ExpectedByte = (int)openCvPlanarBgr[index] })
                .Where(item => item.ActualByte != item.ExpectedByte)
                .Select(item => $"{item.Index}:{item.ActualByte}/{item.ExpectedByte}");
            throw new InvalidOperationException(
                $"OpenCV preprocessing baseline max error was {maximumError:R} at {mismatch.Index}: " +
                $"actual={mismatch.Actual:R}, expected={mismatch.Expected:R}; mismatches={string.Join(',', mismatches)}.");
        }
        return Task.CompletedTask;
    }

    private static async Task TestOcrOutputEquivalenceAsync()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ocr-equivalence.png.b64");
        var modelPath = Path.Combine(AppContext.BaseDirectory, "Resources", "models", "PP-OCRv5_mobile_rec_infer.onnx");
        var dictionaryPath = Path.Combine(AppContext.BaseDirectory, "Resources", "models", "characterDict.txt");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The OCR equivalence release gate requires the PP-OCRv5 model.", modelPath);
        }

        var imageBytes = Convert.FromBase64String((await File.ReadAllTextAsync(fixturePath)).Trim());
        using var stream = new MemoryStream(imageBytes, writable: false);
        using var bitmap = new Bitmap(stream);
        var rois = new[]
        {
            new Rectangle(0, 0, 225, 40),
            new Rectangle(0, 40, 135, 30),
            new Rectangle(0, 70, 302, 41),
            new Rectangle(0, 111, 100, 41),
            new Rectangle(0, 152, 302, 41),
            new Rectangle(0, 193, 100, 41)
        };
        var openCvBaseline = new[]
        {
            new OcrResult(0.90290445f, "呼啸沙龙[1]"),
            new OcrResult(0.9952606f, "等级15/15"),
            new OcrResult(0.9990817f, "生命值"),
            new OcrResult(0.9960429f, "2200"),
            new OcrResult(0.9936991f, "攻击力+2"),
            new OcrResult(0.980103f, "9%")
        };

        using var recognizer = new PaddleOcrRecognizer(modelPath, dictionaryPath, intraOpThreads: 1);
        var actual = recognizer.Recognize(bitmap, rois);
        AssertEqual(openCvBaseline.Length, actual.Count);
        for (var index = 0; index < openCvBaseline.Length; index++)
        {
            AssertEqual(openCvBaseline[index].Text, actual[index].Text);
            AssertNear(openCvBaseline[index].Score, actual[index].Score, 0.005f);
        }
    }

    private static Task TestBrowserOriginAllowlistAsync()
    {
        AssertTrue(WebSocketHost.IsAllowedBrowserOrigin("http://localhost:8787"));
        AssertTrue(WebSocketHost.IsAllowedBrowserOrigin("https://zzzcaculator.top"));
        AssertTrue(!WebSocketHost.IsAllowedBrowserOrigin("https://evil.example"));
        AssertTrue(!WebSocketHost.IsAllowedBrowserOrigin(null));
        return Task.CompletedTask;
    }

    private static async Task TestWebSocketHandshakeAsync()
    {
        var profiles = ScanProfileFile.Load();
        var wikiData = WikiData.Load();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var legacyPort = ReserveTcpPort();
        using (var legacyHost = new WebSocketHost(profiles, wikiData, legacyPort))
        using (var legacyCancellation = new CancellationTokenSource())
        {
            var hostTask = legacyHost.RunAsync(legacyCancellation.Token);
            await WaitForPortAsync(legacyPort, timeout.Token);

            using (var rejectedSocket = new ClientWebSocket())
            {
                rejectedSocket.Options.SetRequestHeader("Origin", "https://evil.example");
                await AssertThrowsAsync<WebSocketException>(() => rejectedSocket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{legacyPort}/ws"),
                    timeout.Token));
            }

            using (var allowedSocket = new ClientWebSocket())
            {
                allowedSocket.Options.SetRequestHeader("Origin", "http://localhost:8787");
                await allowedSocket.ConnectAsync(
                    new Uri($"ws://127.0.0.1:{legacyPort}/ws"),
                    timeout.Token);
                AssertTrue((await ReceiveMessageAsync(allowedSocket, timeout.Token)).Contains("hello", StringComparison.Ordinal));
                await allowedSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
            }

            legacyCancellation.Cancel();
            await AssertCanceledAsync(hostTask);
        }

        var childToken = new string('a', 32);
        var childPort = ReserveTcpPort();
        using var childHost = new WebSocketHost(profiles, wikiData, childPort, childToken);
        using var childCancellation = new CancellationTokenSource();
        var childHostTask = childHost.RunAsync(childCancellation.Token);
        await WaitForPortAsync(childPort, timeout.Token);
        using (var childSocket = new ClientWebSocket())
        {
            await childSocket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{childPort}/ws/{childToken}"),
                timeout.Token);
            AssertTrue((await ReceiveMessageAsync(childSocket, timeout.Token)).Contains("hello", StringComparison.Ordinal));
            await childSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", timeout.Token);
        }

        childCancellation.Cancel();
        await AssertCanceledAsync(childHostTask);
    }

    private static Task TestFastModeDefaultsAsync()
    {
        AssertEqual(ScrollAcceptMode.EarlyOneRow, ScanModeDefaults.ScrollAccept(true));
        AssertEqual(PanelAcceptMode.AdaptiveEarlyFullRoi, ScanModeDefaults.PanelAccept(true));
        AssertEqual(OverlapConflictMode.Recover, ScanModeDefaults.OverlapConflict(true));
        AssertEqual(ScrollAcceptMode.Safe, ScanModeDefaults.ScrollAccept(false));
        AssertEqual(PanelAcceptMode.Safe, ScanModeDefaults.PanelAccept(false));
        AssertEqual(OverlapConflictMode.Recheck, ScanModeDefaults.OverlapConflict(false));
        return Task.CompletedTask;
    }

    private static Task TestStrictProfileSelectionAsync()
    {
        var profiles = new ScanProfileFile
        {
            Profiles =
            [
                new ScanProfile { Name = "Default" },
                new ScanProfile { Name = "FAST" }
            ]
        };

        AssertEqual("FAST", profiles.ResolveRequired("fast").Name);
        AssertThrows<ArgumentException>(() => profiles.ResolveRequired("missing"));
        AssertThrows<InvalidDataException>(() => new ScanProfileFile().ResolveRequired("missing"));
        return Task.CompletedTask;
    }

    private static Task TestAssemblyBackedAppVersionAsync()
    {
        var version = typeof(AppInfo).Assembly.GetName().Version
            ?? throw new InvalidOperationException("Scanner assembly version is missing.");
        AssertEqual($"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}", AppInfo.Version);
        return Task.CompletedTask;
    }

    private static ScannerManifest ValidManifest()
    {
        return new ScannerManifest
        {
            SchemaVersion = 1,
            LauncherMinVersion = "1.0.0",
            ScannerVersion = "1.0.36",
            PackageUrl = "./1.0.36/ZZZ-Scanner.Next-win-x64.zip",
            Sha256 = new string('a', 64),
            Size = 1024,
            Entry = "ZZZ-Scanner.Next.exe"
        };
    }

    private static ScannerManifest ValidV2Manifest()
    {
        return new ScannerManifest
        {
            SchemaVersion = 2,
            LauncherMinVersion = "1.1.0",
            ScannerVersion = "1.0.37",
            Support = new ScannerSupport
            {
                Os = "windows",
                Architectures = ["x64"],
                MinWindowsBuild = 17763
            },
            Packages =
            [
                new ScannerPackage
                {
                    Id = "win-x64-fdd",
                    Mode = ScannerPackageModes.FrameworkDependent,
                    Framework = new ScannerFramework
                    {
                        Name = "Microsoft.WindowsDesktop.App",
                        Major = 8,
                        MinVersion = "8.0.0"
                    },
                    PackageUrls = ["./1.0.37/ZZZ-Scanner.Next-win-x64-fdd.zip"],
                    Sha256 = new string('a', 64),
                    Size = 1024,
                    ExpandedSize = 4096,
                    Entry = "ZZZ-Scanner.Next.exe"
                },
                new ScannerPackage
                {
                    Id = "win-x64-self-contained",
                    Mode = ScannerPackageModes.SelfContained,
                    PackageUrls = ["./1.0.37/ZZZ-Scanner.Next-win-x64-self-contained.zip"],
                    Sha256 = new string('b', 64),
                    Size = 2048,
                    ExpandedSize = 8192,
                    Entry = "ZZZ-Scanner.Next.exe"
                }
            ]
        };
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static int ReserveTcpPort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, CancellationToken token)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(20, token);
            }
        }

        throw new InvalidOperationException($"WebSocket test listener on port {port} did not start.", last);
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, token);
        if (!result.EndOfMessage || result.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidOperationException("Expected one complete text WebSocket message.");
        }

        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    private static void AssertTrue(bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Assertion failed.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
        }
    }

    private static void AssertNear(float expected, float actual, float tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected {expected}, got {actual} (tolerance {tolerance}).");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected exception {typeof(TException).Name}.");
    }

    private static async Task AssertCanceledAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new InvalidOperationException("Expected task cancellation.");
    }
}
