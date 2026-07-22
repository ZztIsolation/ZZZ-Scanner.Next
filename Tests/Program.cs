using System.IO.Compression;
using System.Drawing;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
            ("manifest v3 file verification", TestManifestV3FileVerificationAsync),
            ("runtime path containment", TestRuntimePathContainmentAsync),
            ("installed runtime verification", TestInstalledRuntimeVerificationAsync),
            ("single-version storage cleanup", TestSingleVersionStorageCleanupAsync),
            ("legacy Helper takeover selection", TestLegacyHelperTakeoverSelectionAsync),
            ("Helper update transaction confirmation", TestHelperUpdateTransactionConfirmationAsync),
            ("Helper update interruption recovery", TestHelperUpdateInterruptionRecoveryAsync),
            ("managed output root", TestManagedOutputRootAsync),
            ("managed OCR preprocessing", TestManagedOcrPreprocessingAsync),
            ("OCR output equivalence", TestOcrOutputEquivalenceAsync),
            ("variable substat cleaning", TestVariableSubstatCleaningAsync),
            ("wind main-stat cleaning", TestWindMainStatCleaningAsync),
            ("main-stat value rule coverage", TestMainStatValueRuleCoverageAsync),
            ("fast OCR required label coverage", TestFastOcrRequiredLabelCoverageAsync),
            ("legacy wire capacity", TestLegacyWireCapacityAsync),
            ("browser origin allowlist", TestBrowserOriginAllowlistAsync),
            ("capture source serialization", TestCaptureSourceSerializationAsync),
            ("capture source busy probe and replacement", TestCaptureSourceBusyProbeAndReplacementAsync),
            ("capture source atomic fallback", TestCaptureSourceAtomicFallbackAsync),
            ("scrollbar top probe", TestScrollbarTopProbeAsync),
            ("Scanner supervisor lifecycle", TestScannerSupervisorLifecycleAsync),
            ("Helper scanner message limit", TestHelperScannerMessageLimitAsync),
            ("scan activity terminal gate", TestScanActivityTerminalGateAsync),
            ("canceled progress forwarding", TestCanceledProgressForwardingAsync),
            ("Helper protocol v4 error contract", TestHelperProtocolV4Async),
            ("typed Scanner failure contract", TestScannerFailureContractAsync),
            ("WebSocket origin and token handshake", TestWebSocketHandshakeAsync),
            ("fast mode defaults", TestFastModeDefaultsAsync),
            ("strict profile selection", TestStrictProfileSelectionAsync),
            ("visual probe display transforms", TestVisualProbeDisplayTransformsAsync),
            ("visual fixture transform matrix", TestVisualFixtureTransformMatrixAsync),
            ("privacy-safe visual fixtures", TestPrivacySafeVisualFixturesAsync),
            ("warehouse header semantics", TestWarehouseHeaderSemanticsAsync),
            ("warehouse color-independent structure", TestWarehouseColorIndependentStructureAsync),
            ("warehouse capture health and monitor", TestWarehouseCaptureHealthAndMonitorAsync),
            ("warehouse input guard fast region", TestWarehouseInputGuardFastRegionAsync),
            ("warehouse input guard confirmation", TestWarehouseInputGuardConfirmationAsync),
            ("visual preflight gate", TestVisualPreflightGateAsync),
            ("visual rarity ambiguity", TestVisualRarityAmbiguityAsync),
            ("relative row presence", TestRelativeRowPresenceAsync),
            ("variable panel ROI layout", TestVariablePanelRoiLayoutAsync),
            ("relative row probe overhead", TestRelativeRowProbeOverheadAsync),
            ("DPI-independent client coordinates", TestDpiIndependentClientCoordinatesAsync),
            ("first-cell panel change gate", TestFirstCellPanelChangeGateAsync),
            ("selection refresh wait", TestSelectionRefreshWaitAsync),
            ("partial full-scan benchmark terminal", TestPartialFullScanBenchmarkTerminalAsync),
            ("luminance normalization", TestLuminanceNormalizationAsync),
            ("structured panel timeout diagnostics", TestPanelTimeoutDiagnosticsAsync),
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

    private static async Task TestManifestV3FileVerificationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-scanner-v3-test", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "Data"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(runtime, "ZZZ-Scanner.Next.exe"), "scanner-v3");
            await File.WriteAllTextAsync(Path.Combine(runtime, "Data", "config.json"), "{}");
            var package = PackageFromDirectory(runtime);
            var manifest = ValidV2Manifest();
            manifest.SchemaVersion = 3;
            manifest.LauncherMinVersion = "1.2.0";
            manifest.Packages = [package, CloneAsSelfContained(package)];
            HelperSecurity.ValidateManifest(manifest, new Uri("https://example.com/manifest.json"), new Version(1, 2, 0));
            await HelperSecurity.VerifyInstalledRuntimeAsync(package, runtime, CancellationToken.None);

            await File.WriteAllTextAsync(Path.Combine(runtime, "Data", "config.json"), "tampered");
            await AssertThrowsAsync<InvalidDataException>(() =>
                HelperSecurity.VerifyInstalledRuntimeAsync(package, runtime, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task TestSingleVersionStorageCleanupAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-scanner-storage-test", Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new HelperStorageManager(root);
            var active = Path.Combine(manager.RuntimeRoot, "1.0.38", "win-x64-fdd");
            Directory.CreateDirectory(active);
            File.WriteAllText(Path.Combine(active, "ZZZ-Scanner.Next.exe"), "active");
            manager.SaveActiveRuntime("1.0.38", "win-x64-fdd", ScannerPackageModes.FrameworkDependent, "ZZZ-Scanner.Next.exe");
            var persisted = new HelperStorageManager(root).LoadActiveRuntime();
            AssertTrue(persisted is not null);
            AssertEqual("1.0.38", persisted!.Version);
            AssertEqual("win-x64-fdd", persisted.PackageId);

            var oldScans = Path.Combine(manager.RuntimeRoot, "1.0.37", "win-x64-fdd", "Scans");
            var success = Path.Combine(oldScans, "2026-01-01-success");
            var failed = Path.Combine(oldScans, "2026-01-02-failed");
            Directory.CreateDirectory(success);
            Directory.CreateDirectory(failed);
            File.WriteAllText(Path.Combine(success, "export.json"), "[]");
            File.WriteAllText(Path.Combine(failed, "scan.log"), "failed");
            Directory.SetLastWriteTimeUtc(success, DateTime.UtcNow.AddMinutes(-2));
            Directory.SetLastWriteTimeUtc(failed, DateTime.UtcNow.AddMinutes(-1));

            Directory.CreateDirectory(manager.PackageRoot);
            File.WriteAllText(Path.Combine(manager.PackageRoot, "scanner-1.0.37.zip"), "old-package");
            File.WriteAllText(Path.Combine(manager.PackageRoot, "scanner-1.0.38.zip"), "current-package");
            var rootTemporary = Path.Combine(root, "staging.tmp");
            Directory.CreateDirectory(rootTemporary);
            File.WriteAllText(Path.Combine(rootTemporary, "runtime.download"), "partial-download");

            var before = manager.Inspect("1.0.38", "win-x64-fdd");
            AssertTrue(before.ReclaimableBytes >= "partial-download".Length);

            var result = manager.Cleanup("1.0.38", "win-x64-fdd");
            AssertTrue(Directory.Exists(active));
            AssertTrue(!Directory.Exists(Path.Combine(manager.RuntimeRoot, "1.0.37")));
            AssertTrue(!Directory.Exists(rootTemporary));
            AssertEqual(0, Directory.EnumerateFiles(manager.PackageRoot).Count());
            var outputs = Directory.EnumerateDirectories(manager.OutputRoot).ToList();
            AssertEqual(2, outputs.Count);
            AssertEqual(1, outputs.Count(path => File.Exists(Path.Combine(path, "export.json"))));
            AssertTrue(result.ReclaimedBytes > 0);
            AssertEqual(0, result.Errors.Count);
            return Task.CompletedTask;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task TestLegacyHelperTakeoverSelectionAsync()
    {
        const string currentPath = @"E:\downloads\ZZZ-Scanner-Helper.exe";
        var selected = HelperInstallationManager.SelectTakeoverCandidate(
            "1.1.0",
            currentPath,
            [
                new HelperProcessCandidate(10, currentPath, "1.2.1"),
                new HelperProcessCandidate(20, @"E:\legacy\ZZZ-Scanner-Helper.exe", "1.1.0+build"),
                new HelperProcessCandidate(30, @"E:\other\ZZZ-Scanner-Helper.exe", "1.0.2"),
            ]);
        AssertTrue(selected.Candidate is not null);
        AssertEqual(20, selected.Candidate!.ProcessId);

        var ambiguous = HelperInstallationManager.SelectTakeoverCandidate(
            "1.1.0",
            currentPath,
            [
                new HelperProcessCandidate(20, @"E:\legacy-a\ZZZ-Scanner-Helper.exe", "1.1.0"),
                new HelperProcessCandidate(21, @"E:\legacy-b\ZZZ-Scanner-Helper.exe", "1.1.0"),
            ]);
        AssertTrue(ambiguous.Candidate is null);
        AssertTrue(ambiguous.Reason.Contains("多个", StringComparison.Ordinal));

        var missing = HelperInstallationManager.SelectTakeoverCandidate(
            "1.1.0",
            currentPath,
            [new HelperProcessCandidate(30, @"E:\other\ZZZ-Scanner-Helper.exe", "1.0.2")]);
        AssertTrue(missing.Candidate is null);
        AssertTrue(missing.Reason.Contains("未找到", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task TestHelperUpdateTransactionConfirmationAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-helper-update-test", Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT", root);
            var target = HelperInstallationManager.ManagedHelperPath();
            var backup = target + ".previous";
            var updater = Path.Combine(root, "helper", ".staging", "helper-1.3.1.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
            await File.WriteAllTextAsync(target, "candidate");
            await File.WriteAllTextAsync(backup, "previous");
            await File.WriteAllTextAsync(updater, "candidate");

            var receipt = new HelperUpdateTransactionReceipt
            {
                TransactionId = "0123456789abcdef0123456789abcdef",
                State = "pending",
                Stage = "helper-started",
                PreviousVersion = "1.2.1",
                TargetPath = target,
                BackupPath = backup,
                UpdaterPath = updater,
                CandidateSha256 = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            HelperUpdateTransactionManager.WriteReceiptForTests(receipt);
            HelperUpdateTransactionManager.ResetForTests();

            var info = HelperUpdateTransactionManager.CurrentInfo();
            AssertTrue(info is not null);
            AssertEqual("pending_confirmation", info!.State);
            AssertEqual("1.2.1", info.PreviousVersion);
            AssertThrows<InvalidDataException>(() => HelperUpdateTransactionManager.Confirm("wrong"));

            var committed = HelperUpdateTransactionManager.Confirm(receipt.TransactionId);
            AssertTrue(committed.Committed);
            AssertEqual(receipt.TransactionId, committed.TransactionId);
            AssertTrue(HelperUpdateTransactionManager.CurrentInfo() is null);
            AssertTrue(HelperUpdateTransactionManager.Confirm(receipt.TransactionId).Committed);

            HelperUpdateTransactionManager.ResetForTests();
            AssertTrue(!await HelperUpdateTransactionManager.RecoverInterruptedAsync());
            AssertTrue(HelperUpdateTransactionManager.ReadReceiptForTests() is null);
            AssertTrue(!File.Exists(backup));
            AssertTrue(HelperUpdateTransactionManager.ConsumeStoragePreservation());
            AssertTrue(!HelperUpdateTransactionManager.ConsumeStoragePreservation());
        }
        finally
        {
            HelperUpdateTransactionManager.ResetForTests();
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT", previousRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestHelperUpdateInterruptionRecoveryAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-helper-update-recovery-test", Guid.NewGuid().ToString("N"));
        var previousRoot = Environment.GetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT", root);
            var target = HelperInstallationManager.ManagedHelperPath();
            var backup = target + ".previous";
            var updater = Path.Combine(root, "helper", ".staging", "helper-1.3.1.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(updater)!);
            await File.WriteAllTextAsync(updater, "candidate");

            HelperUpdateTransactionReceipt Receipt(string id, string state = "pending") => new()
            {
                TransactionId = id,
                State = state,
                Stage = "prepared",
                PreviousVersion = "1.2.1",
                TargetPath = target,
                BackupPath = backup,
                UpdaterPath = updater,
                CandidateSha256 = "test",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            await File.WriteAllTextAsync(target, "previous-before-replace");
            HelperUpdateTransactionManager.WriteReceiptForTests(Receipt("11111111111111111111111111111111"));
            HelperUpdateTransactionManager.ResetForTests();
            AssertTrue(!await HelperUpdateTransactionManager.RecoverInterruptedForTestsAsync());
            AssertEqual("previous-before-replace", await File.ReadAllTextAsync(target));

            await File.WriteAllTextAsync(target, "candidate-after-replace");
            await File.WriteAllTextAsync(backup, "previous-after-replace");
            HelperUpdateTransactionManager.WriteReceiptForTests(Receipt("22222222222222222222222222222222"));
            HelperUpdateTransactionManager.ResetForTests();
            AssertTrue(await HelperUpdateTransactionManager.RecoverInterruptedForTestsAsync());
            AssertEqual("previous-after-replace", await File.ReadAllTextAsync(target));
            AssertTrue(!File.Exists(backup));

            await File.WriteAllTextAsync(target, "confirmed-candidate");
            await File.WriteAllTextAsync(backup, "previous-confirmed");
            var confirmed = Receipt("33333333333333333333333333333333", "confirmed");
            HelperUpdateTransactionManager.WriteReceiptForTests(confirmed);
            HelperUpdateTransactionManager.ResetForTests();
            AssertTrue(!await HelperUpdateTransactionManager.RecoverInterruptedForTestsAsync());
            AssertEqual("confirmed-candidate", await File.ReadAllTextAsync(target));
            AssertTrue(!File.Exists(backup));
            AssertTrue(HelperUpdateTransactionManager.ReadReceiptForTests() is null);
        }
        finally
        {
            HelperUpdateTransactionManager.ResetForTests();
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_DATA_ROOT", previousRoot);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static Task TestManagedOutputRootAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "zzz-scanner-output-test", Guid.NewGuid().ToString("N"));
        var previous = Environment.GetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT", root);
            var output = AppPaths.CreateScanDirectory();
            AssertTrue(Path.GetFullPath(output).StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT", previous);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
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
                var stopBytes = Encoding.UTF8.GetBytes("{\"cmd\":\"scan_stop\",\"data\":{}}");
                await allowedSocket.SendAsync(stopBytes, WebSocketMessageType.Text, true, timeout.Token);
                var stopAck = await ReceiveMessageAsync(allowedSocket, timeout.Token);
                AssertTrue(stopAck.Contains("scan_stop_ack", StringComparison.Ordinal));
                AssertTrue(stopAck.Contains("false", StringComparison.Ordinal));
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

    private static Task TestVariableSubstatCleaningAsync()
    {
        var cleaner = new DriveDiscCleaner(WikiData.Load());
        var core = new List<OcrResult>
        {
            new(1, "呼啸沙龙[1]"),
            new(1, "等级12/15"),
            new(1, "生命值"),
            new(1, "2200")
        };
        var substats = new[]
        {
            ("暴击率", "2.4%"),
            ("攻击力", "19"),
            ("生命值", "112"),
            ("暴击伤害", "4.8%")
        };

        for (var count = 0; count <= substats.Length; count++)
        {
            var ocr = new List<OcrResult>(core);
            foreach (var (name, value) in substats.Take(count))
            {
                ocr.Add(new OcrResult(1, name));
                ocr.Add(new OcrResult(1, value));
            }

            var export = cleaner.Clean(count + 1, "S", ocr);
            AssertEqual(12, export.Level);
            AssertEqual(15, export.MaxLevel);
            AssertEqual(count, export.SubStats.Count);
        }

        return Task.CompletedTask;
    }

    private static Task TestWindMainStatCleaningAsync()
    {
        var wikiData = WikiData.Load();
        var cleaner = new DriveDiscCleaner(wikiData);
        var cases = new[]
        {
            (Rarity: "S", MaxLevel: 15, Value: "30%"),
            (Rarity: "A", MaxLevel: 12, Value: "20%"),
            (Rarity: "B", MaxLevel: 9, Value: "10%")
        };

        foreach (var testCase in cases)
        {
            var export = cleaner.Clean(1, testCase.Rarity,
            [
                new OcrResult(1, "呼啸沙龙[5]"),
                new OcrResult(1, $"等级{testCase.MaxLevel:D2}/{testCase.MaxLevel:D2}"),
                new OcrResult(1, "风属性伤害加成"),
                new OcrResult(1, testCase.Value)
            ]);

            AssertEqual(5, export.Slot);
            AssertEqual("风属性伤害加成", export.MainStat.Single().Key);
            AssertEqual(testCase.Value, (string)export.MainStat.Single().Value);
        }

        var inferred = cleaner.Clean(2, "S",
        [
            new OcrResult(1, "呼啸沙龙"),
            new OcrResult(1, "等级15/15"),
            new OcrResult(1, "风属性伤害加成"),
            new OcrResult(1, "30%")
        ]);
        AssertEqual(5, inferred.Slot);

        foreach (var invalidSlot in new[] { 4, 6 })
        {
            var invalid = new DriveDiscExport
            {
                Slot = invalidSlot,
                Rarity = "S",
                Level = 15,
                MaxLevel = 15,
                MainStat = new Dictionary<string, object> { ["风属性伤害加成"] = "30%" }
            };
            var issues = DriveDiscSlotSafety.Validate(invalid, wikiData.StatRules);
            AssertTrue(issues.Any(issue => issue.Code == DriveDiscSlotSafety.SlotMainStatViolation),
                $"Expected slot_mainstat_violation for wind damage in slot {invalidSlot}.");
        }

        return Task.CompletedTask;
    }

    private static Task TestMainStatValueRuleCoverageAsync()
    {
        var rules = WikiData.Load().StatRules;
        foreach (var (slotText, candidates) in rules.SlotMainStats)
        {
            var slot = int.Parse(slotText, CultureInfo.InvariantCulture);
            foreach (var (rarity, values) in rules.MainStatValues)
            {
                foreach (var candidate in candidates)
                {
                    var requiresPercent = slot >= 4 && candidate is "生命值" or "攻击力" or "防御力";
                    var hasRule = requiresPercent
                        ? values.ContainsKey($"{candidate}%")
                        : values.ContainsKey(candidate) || values.ContainsKey($"{candidate}%");
                    AssertTrue(hasRule, $"Missing {rarity} main-stat value rule for slot {slot} candidate {candidate}.");
                }
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestFastOcrRequiredLabelCoverageAsync()
    {
        const string requestedProfile = "local-1920x1080-current";
        const string otherProfile = "cloud-1920x1080-current";
        var root = Path.Combine(Path.GetTempPath(), "zzz-fast-ocr-coverage-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var indexFile = Path.Combine(root, "index.json");

        try
        {
            using var image = new Bitmap(32, 16);
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var light = x < 16 ? (x + y) % 4 == 0 : (x * 3 + y) % 5 <= 1;
                    image.SetPixel(x, y, light ? Color.White : Color.Black);
                }
            }

            var mainStatRoi = new Rectangle(0, 0, 16, 16);
            var levelRoi = new Rectangle(16, 0, 16, 16);
            var mainStatBits = FastOcrImageFeature.FromBitmap(image, mainStatRoi).ToHexWords();
            var levelBits = FastOcrImageFeature.FromBitmap(image, levelRoi).ToHexWords();
            var alternateBits = mainStatBits
                .Select(word => (~ulong.Parse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString("X16", CultureInfo.InvariantCulture))
                .ToArray();
            var requiredLabels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["mainStat"] = ["攻击力", "风属性伤害加成"]
            };

            SaveIndex(includeWindInRequestedProfile: false);
            var logs = new List<string>();
            var guarded = FastOcrAssistEngine.TryCreate(
                indexFile,
                ["mainStat", "level"],
                requestedProfile,
                ProfileRoutingMode.Strict,
                requiredLabels,
                logs.Add) ?? throw new InvalidOperationException("Expected Fast OCR assist engine.");
            var guardedPlan = guarded.Plan(1, "S", image, [mainStatRoi, levelRoi]);
            AssertEqual(1, guardedPlan.PpOcrIndices.Length);
            AssertEqual(0, guardedPlan.PpOcrIndices[0]);
            AssertEqual("template_label_coverage_incomplete", guardedPlan.Decisions.Single(decision => decision.FieldKey == "mainStat").Reason);
            AssertEqual("fast", guardedPlan.Decisions.Single(decision => decision.FieldKey == "level").Source);
            AssertTrue(logs.Any(line => line.Contains("FAST_OCR_TEMPLATE_COVERAGE", StringComparison.Ordinal)
                && line.Contains("missingLabels=风属性伤害加成", StringComparison.Ordinal)),
                "Expected startup coverage diagnostics to name the missing wind label.");

            SaveIndex(includeWindInRequestedProfile: true);
            var complete = FastOcrAssistEngine.TryCreate(
                indexFile,
                ["mainStat", "level"],
                requestedProfile,
                ProfileRoutingMode.Strict,
                requiredLabels,
                _ => { }) ?? throw new InvalidOperationException("Expected Fast OCR assist engine.");
            var completePlan = complete.Plan(2, "S", image, [mainStatRoi, levelRoi]);
            AssertEqual(0, completePlan.PpOcrIndices.Length);
            var mainStatDecision = completePlan.Decisions.Single(decision => decision.FieldKey == "mainStat");
            AssertEqual("fast", mainStatDecision.Source);
            AssertEqual("攻击力", mainStatDecision.FastLabel);

            void SaveIndex(bool includeWindInRequestedProfile)
            {
                var templates = new List<FastOcrTemplate>
                {
                    Template("mainStat", "攻击力", requestedProfile, mainStatBits),
                    Template("mainStat", "风属性伤害加成", otherProfile, alternateBits),
                    Template("level", "15/15", requestedProfile, levelBits)
                };
                if (includeWindInRequestedProfile)
                {
                    templates.Add(Template("mainStat", "风属性伤害加成", requestedProfile, alternateBits));
                }

                new FastOcrTemplateIndex
                {
                    Feature = FastOcrTemplateIndex.CurrentFeature,
                    Templates = templates
                }.Save(indexFile);
            }

            static FastOcrTemplate Template(string fieldKey, string label, string profile, string[] bits)
            {
                return new FastOcrTemplate
                {
                    FieldKey = fieldKey,
                    Label = label,
                    VisualProfileId = profile,
                    ProfileFamilyId = FastOcrTemplateIndex.ProfileFamilyId(profile),
                    Bits = bits
                };
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task TestLegacyWireCapacityAsync()
    {
        var item = new Dictionary<string, object?>
        {
            ["序号"] = 9999,
            ["名称"] = "呼啸沙龙",
            ["槽位"] = 6,
            ["品质"] = "S",
            ["等级"] = 15,
            ["最大等级"] = 15,
            ["主属性"] = new Dictionary<string, object?> { ["攻击力"] = "30%" },
            ["副属性"] = new object[]
            {
                new Dictionary<string, object?> { ["暴击率"] = "7.2%" },
                new Dictionary<string, object?> { ["暴击伤害"] = "14.4%" },
                new Dictionary<string, object?> { ["攻击力"] = 57 },
                new Dictionary<string, object?> { ["异常精通"] = 36 }
            }
        };
        var items = Enumerable.Range(1, 9999)
            .Select(index => new Dictionary<string, object?>(item) { ["序号"] = index })
            .ToArray();
        var compact = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            new { cmd = "scan_complete", data = new { items, completed = items.Length } },
            JsonDefaults.Wire));
        AssertTrue(compact < ZZZScannerHelper.Program.MaxWebSocketMessageBytes,
            $"9999-item compact message exceeded the Helper fallback cap: {compact} bytes.");

        var streamed = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(
            new { cmd = "scan_complete", data = new { resultDelivery = "stream-items-v1", itemCount = items.Length, completed = items.Length } },
            JsonDefaults.Wire));
        AssertTrue(streamed < 1024, $"Streamed completion summary is unexpectedly large: {streamed} bytes.");
        return Task.CompletedTask;
    }

    private static async Task TestCaptureSourceSerializationAsync()
    {
        var source = new BlockingCaptureSource();
        using var coordinator = new CaptureSourceCoordinator(source);
        var bounds = new Rectangle(0, 0, 2, 2);
        var bitmapTask = Task.Run(() =>
        {
            using var image = coordinator.Capture(bounds);
        });
        AssertTrue(source.Entered.Wait(TimeSpan.FromSeconds(2)), "The first capture did not enter the source.");

        var frameTask = Task.Run(() =>
        {
            using var frame = coordinator.CaptureFrame(bounds);
        });
        await Task.Delay(50);
        AssertEqual(1, source.Calls);
        AssertEqual(1, source.MaximumConcurrency);

        source.Release.Set();
        await Task.WhenAll(bitmapTask, frameTask);
        AssertEqual(2, source.Calls);
        AssertEqual(1, source.MaximumConcurrency);
    }

    private static async Task TestCaptureSourceBusyProbeAndReplacementAsync()
    {
        var source = new BlockingCaptureSource();
        using var coordinator = new CaptureSourceCoordinator(source);
        var bounds = new Rectangle(0, 0, 2, 2);
        var captureTask = Task.Run(() =>
        {
            using var image = coordinator.Capture(bounds);
        });
        AssertTrue(source.Entered.Wait(TimeSpan.FromSeconds(2)), "The blocking capture did not start.");

        var timer = Stopwatch.StartNew();
        AssertTrue(!coordinator.TryCapture(bounds, out var skipped));
        timer.Stop();
        AssertTrue(skipped is null);
        AssertTrue(timer.Elapsed < TimeSpan.FromMilliseconds(100), "A low-priority probe waited for the capture gate.");

        var replacement = new RecordingCaptureSource("replacement");
        var replaceTask = Task.Run(() => coordinator.Replace(replacement));
        await Task.Delay(50);
        AssertTrue(!replaceTask.IsCompleted, "Capture source replacement ran during an active capture.");
        AssertTrue(!source.Disposed, "The active capture source was disposed while in use.");

        source.Release.Set();
        await Task.WhenAll(captureTask, replaceTask);
        AssertTrue(source.Disposed);
        using var replacementImage = coordinator.Capture(bounds);
        AssertEqual(1, replacement.Calls);
    }

    private static Task TestCaptureSourceAtomicFallbackAsync()
    {
        var failing = new FailingCaptureSource();
        var fallback = new RecordingCaptureSource("fallback");
        using var coordinator = new CaptureSourceCoordinator(failing, () => fallback);
        using var image = coordinator.Capture(new Rectangle(0, 0, 2, 2));

        AssertEqual(1, failing.Calls);
        AssertTrue(failing.Disposed);
        AssertEqual(1, fallback.Calls);
        AssertEqual("fallback", coordinator.Name);
        return Task.CompletedTask;
    }

    private static Task TestScrollbarTopProbeAsync()
    {
        var topRect = new Rectangle(0, 0, 9, 18);
        var trackRect = new Rectangle(0, 38, 9, 18);

        using (var bitmap = new Bitmap(9, 56))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.Gray, topRect);
            using var frame = new BitmapCapturedFrame((Bitmap)bitmap.Clone(), "test");
            var result = ScanController.EvaluateScrollTopProbe(frame, topRect, trackRect);
            AssertTrue(result.Detected);
            AssertTrue(result.TopLuminance - result.TrackLuminance >= 16);
        }

        using (var bitmap = new Bitmap(9, 56))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Gray);
            using var frame = new BitmapCapturedFrame((Bitmap)bitmap.Clone(), "test");
            AssertTrue(!ScanController.EvaluateScrollTopProbe(frame, topRect, trackRect).Detected);
        }

        using (var bitmap = new Bitmap(9, 56))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.Gray, trackRect);
            using var frame = new BitmapCapturedFrame((Bitmap)bitmap.Clone(), "test");
            AssertTrue(!ScanController.EvaluateScrollTopProbe(frame, topRect, trackRect).Detected);
        }

        return Task.CompletedTask;
    }

    private static async Task TestScannerSupervisorLifecycleAsync()
    {
        var exitCount = 0;
        var reportedExitCode = 0;
        var terminateCount = 0;
        var stoppedPump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ZZZScannerHelper.Program.RunScannerSupervisorAsync(
            _ => stoppedPump.Task,
            _ => Task.FromResult(unchecked((int)0xC0000005)),
            _ =>
            {
                terminateCount++;
                return Task.FromResult(-1);
            },
            () => false,
            () => stoppedPump.TrySetCanceled(),
            (code, _) =>
            {
                exitCount++;
                reportedExitCode = code;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => { },
            CancellationToken.None);
        AssertEqual(1, exitCount);
        AssertEqual(unchecked((int)0xC0000005), reportedExitCode);
        AssertEqual(0, terminateCount);

        exitCount = 0;
        terminateCount = 0;
        var pumpFaults = 0;
        var transportFailures = 0;
        await ZZZScannerHelper.Program.RunScannerSupervisorAsync(
            _ => Task.FromException(new WebSocketException("child socket failed")),
            cancellation => Task.Run(async () =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                return 0;
            }, cancellation),
            _ =>
            {
                terminateCount++;
                return Task.FromResult(unchecked((int)0xC0000005));
            },
            () => false,
            () => { },
            (_, _) =>
            {
                exitCount++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                transportFailures++;
                return Task.CompletedTask;
            },
            _ => pumpFaults++,
            CancellationToken.None);
        AssertEqual(1, pumpFaults);
        AssertEqual(1, terminateCount);
        AssertEqual(0, exitCount);
        AssertEqual(1, transportFailures);

        using var cancellation = new CancellationTokenSource();
        var expectedShutdown = false;
        exitCount = 0;
        var canceledSupervisor = ZZZScannerHelper.Program.RunScannerSupervisorAsync(
            token => Task.Delay(Timeout.InfiniteTimeSpan, token),
            token => Task.Run(async () =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }, token),
            _ => Task.FromResult(-1),
            () => expectedShutdown,
            () => { },
            (_, _) =>
            {
                exitCount++;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            _ => { },
            cancellation.Token);
        expectedShutdown = true;
        cancellation.Cancel();
        await canceledSupervisor;
        AssertEqual(0, exitCount);
    }

    private static Task TestHelperScannerMessageLimitAsync()
    {
        ZZZScannerHelper.Program.EnsureScannerMessageSize(0, ZZZScannerHelper.Program.MaxWebSocketMessageBytes);
        ZZZScannerHelper.Program.EnsureScannerMessageSize(ZZZScannerHelper.Program.MaxWebSocketMessageBytes - 1, 1);
        AssertThrows<ZZZScannerHelper.Program.ScannerMessageTooLargeException>(() =>
            ZZZScannerHelper.Program.EnsureScannerMessageSize(ZZZScannerHelper.Program.MaxWebSocketMessageBytes, 1));
        AssertThrows<ZZZScannerHelper.Program.ScannerMessageTooLargeException>(() =>
            ZZZScannerHelper.Program.EnsureScannerMessageSize(-1, 1));
        return Task.CompletedTask;
    }

    private static Task TestVariablePanelRoiLayoutAsync()
    {
        var keys = new[]
        {
            "name", "level", "mainStat", "mainStatValue",
            "subStat1", "subStatValue1", "subStat2", "subStatValue2",
            "subStat3", "subStatValue3", "subStat4", "subStatValue4"
        };

        foreach (var readableCount in new[] { 4, 6, 8, 10, 12 })
        {
            var presence = Enumerable.Range(0, keys.Length).Select(index => index < readableCount).ToArray();
            var layout = ScanController.EvaluateVariableRoiLayout(presence, keys);
            AssertTrue(layout.ValidBoundary, $"Expected a valid {readableCount}-ROI boundary.");
            AssertEqual(readableCount, layout.Count);
        }

        var incompletePair = Enumerable.Repeat(true, keys.Length).ToArray();
        incompletePair[9] = false;
        var incomplete = ScanController.EvaluateVariableRoiLayout(incompletePair, keys);
        AssertTrue(!incomplete.ValidBoundary);
        AssertEqual("incomplete_substat_pair", incomplete.InvalidReason);

        var gap = Enumerable.Repeat(true, keys.Length).ToArray();
        gap[6] = false;
        gap[7] = false;
        var gapped = ScanController.EvaluateVariableRoiLayout(gap, keys);
        AssertTrue(!gapped.ValidBoundary);
        AssertEqual("substat_gap", gapped.InvalidReason);

        var missingCore = Enumerable.Repeat(true, keys.Length).ToArray();
        missingCore[1] = false;
        var core = ScanController.EvaluateVariableRoiLayout(missingCore, keys);
        AssertTrue(!core.ValidBoundary);
        AssertEqual("required_core_missing", core.InvalidReason);
        AssertEqual(1, ScanController.RequiredRoiBoundaryFrames(12, 12, 1));
        AssertEqual(3, ScanController.RequiredRoiBoundaryFrames(10, 12, 1));
        AssertEqual(4, ScanController.RequiredRoiBoundaryFrames(4, 12, 4));
        return Task.CompletedTask;
    }

    private static Task TestScanActivityTerminalGateAsync()
    {
        var gate = new ZZZScannerHelper.Program.ScanActivityGate();
        AssertTrue(!gate.IsActive);
        gate.Start();
        AssertTrue(gate.IsActive);
        AssertTrue(gate.Finish());
        AssertTrue(!gate.Finish(), "A scan emitted more than one terminal transition.");
        gate.Start();
        AssertTrue(gate.Finish());
        return Task.CompletedTask;
    }

    private static Task TestCanceledProgressForwardingAsync()
    {
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            WebSocketHost.ForwardProgressSafely(
                () => Task.FromCanceled(canceled.Token),
                canceled);
        }

        using (var disconnected = new CancellationTokenSource())
        {
            WebSocketHost.ForwardProgressSafely(
                () => Task.FromException(new WebSocketException("socket closed")),
                disconnected);
            AssertTrue(disconnected.IsCancellationRequested);
        }

        using (var disposed = new CancellationTokenSource())
        {
            WebSocketHost.ForwardProgressSafely(
                () => Task.FromException(new ObjectDisposedException("socket")),
                disposed);
            AssertTrue(disposed.IsCancellationRequested);
        }

        return Task.CompletedTask;
    }

    private static Task TestHelperProtocolV4Async()
    {
        AssertEqual("1.3.1", ZZZScannerHelper.Program.HelperVersion);
        AssertEqual(4, ZZZScannerHelper.Program.ProtocolVersion);
        AssertTrue(ZZZScannerHelper.Program.IsAllowedOrigin("https://zzzcaculator.top"));
        AssertTrue(!ZZZScannerHelper.Program.IsAllowedOrigin("https://evil.example"));
        AssertTrue(ZZZScannerHelper.Program.IsCorsReadableOrigin("https://evil.example"));
        AssertTrue(!ZZZScannerHelper.Program.IsCorsReadableOrigin("file:///tmp/test"));
        return Task.CompletedTask;
    }

    private static Task TestScannerFailureContractAsync()
    {
        var failure = new ScannerFailureException(
            "inventory_count_ocr_failed",
            "仓库数量识别失败",
            "无法识别仓库数量。",
            "请确认数量区域完整可见。",
            new Dictionary<string, object?> { ["attempts"] = 3 });
        AssertEqual("inventory_count_ocr_failed", failure.Code);
        AssertTrue(failure.Title.Contains("识别失败", StringComparison.Ordinal));
        AssertTrue(failure.Remedy.Contains("请", StringComparison.Ordinal));
        AssertEqual(3, Convert.ToInt32(failure.DiagnosticDetails["attempts"]));
        AssertTrue(failure.Retryable);
        return Task.CompletedTask;
    }

    private static Task TestVisualProbeDisplayTransformsAsync()
    {
        var expected = Color.FromArgb(0, 186, 255);
        var variants = new Dictionary<string, Color>
        {
            ["neutral"] = expected,
            ["hdr clipped"] = Color.FromArgb(0, 255, 255),
            ["night light 15 percent"] = Color.FromArgb(8, 190, 217),
            ["night light 25 percent"] = Color.FromArgb(12, 186, 191),
            ["mild saturation reduction"] = Color.FromArgb(26, 174, 230),
            ["mild contrast increase"] = Color.FromArgb(0, 198, 255),
            ["warm gamma combination"] = Color.FromArgb(10, 202, 230),
        };

        foreach (var (name, color) in variants)
        {
            using var image = VisualAnchorFixture(color);
            var result = VisualProbeEvaluator.EvaluateChromaticAnchor(image, expected);
            AssertTrue(result.Passed, $"{name} should pass but scored {result.Score} with hue delta {result.HueDelta}.");
            AssertTrue(result.Score >= 60, $"{name} should retain a useful confidence score.");
        }

        using (var clipped = VisualAnchorFixture(Color.FromArgb(0, 255, 255)))
        {
            var result = VisualProbeEvaluator.EvaluateChromaticAnchor(clipped, expected);
            AssertEqual(VisualTransformClass.HighlightClipped, result.TransformClass);
        }

        using (var monochrome = VisualAnchorFixture(Color.FromArgb(190, 190, 190)))
        {
            AssertTrue(!VisualProbeEvaluator.EvaluateChromaticAnchor(monochrome, expected).Passed);
        }

        using (var inverted = VisualAnchorFixture(Color.FromArgb(255, 69, 0)))
        {
            AssertTrue(!VisualProbeEvaluator.EvaluateChromaticAnchor(inverted, expected).Passed);
        }

        return Task.CompletedTask;
    }

    private static Task TestVisualFixtureTransformMatrixAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Visual");
        using var anchor = new Bitmap(Path.Combine(fixtureDirectory, "preflight-anchor-hdr.png"));
        using var firstRow = new Bitmap(Path.Combine(fixtureDirectory, "first-row-drive-discs-hdr.png"));
        using var details = new Bitmap(Path.Combine(fixtureDirectory, "detail-panel-rows-hdr.png"));
        var candidates = new[]
        {
            new VisualRarityCandidate("S", Color.FromArgb(255, 181, 0)),
            new VisualRarityCandidate("A", Color.FromArgb(233, 0, 255)),
            new VisualRarityCandidate("B", Color.FromArgb(0, 169, 255)),
        };
        var firstRowPoints = Enumerable.Range(0, firstRow.Height)
            .SelectMany(y => Enumerable.Range(0, firstRow.Width).Select(x => new Point(x, y)))
            .ToArray();
        var transforms = new[]
        {
            new FixtureTransform("hdr highlight clip", 1, 1, 1, 1, 1, 0, true),
            new FixtureTransform("blue reduction 15 percent", 1, 0.85, 1, 1, 1, 0, false),
            new FixtureTransform("blue reduction 25 percent", 1, 0.75, 1, 1, 1, 0, false),
            new FixtureTransform("red gain 10 percent", 1.10, 1, 1, 1, 1, 0, false),
            new FixtureTransform("saturation 0.8", 1, 1, 0.8, 1, 1, 0, false),
            new FixtureTransform("saturation 1.25", 1, 1, 1.25, 1, 1, 0, false),
            new FixtureTransform("gamma 0.85", 1, 1, 1, 0.85, 1, 0, false),
            new FixtureTransform("gamma 1.15", 1, 1, 1, 1.15, 1, 0, false),
            new FixtureTransform("contrast 0.9", 1, 1, 1, 1, 0.9, 0, false),
            new FixtureTransform("contrast 1.1", 1, 1, 1, 1, 1.1, 0, false),
            new FixtureTransform("brightness minus 8", 1, 1, 1, 1, 1, -8, false),
            new FixtureTransform("brightness plus 8", 1, 1, 1, 1, 1, 8, false),
            new FixtureTransform("night warm combination", 1.08, 0.80, 0.9, 0.92, 1, 4, false),
            new FixtureTransform("hdr vivid combination", 1, 1, 1.20, 0.88, 1.08, 4, true),
        };

        foreach (var transform in transforms)
        {
            using var transformedAnchor = ApplyFixtureTransform(anchor, transform);
            var anchorResult = VisualProbeEvaluator.EvaluateChromaticAnchor(transformedAnchor, Color.FromArgb(0, 186, 255));
            AssertTrue(anchorResult.Passed, $"{transform.Name} anchor failed: score={anchorResult.Score}, hueDelta={anchorResult.HueDelta}.");

            using var transformedRow = ApplyFixtureTransform(firstRow, transform);
            var rarity = VisualProbeEvaluator.EvaluateRarity(transformedRow, candidates, firstRowPoints);
            AssertTrue(string.Equals("S", rarity.Rarity, StringComparison.Ordinal), $"{transform.Name} changed the first-row rarity classification to {rarity.Rarity ?? "unknown"}.");

            using var transformedDetails = ApplyFixtureTransform(details, transform);
            var rowProbe = VisualProbeEvaluator.EvaluateRelativeTextRowPresence(
                transformedDetails,
                new Rectangle(0, 58, 390, 45),
                new Rectangle(0, 137, 390, 45),
                new Point(18, 20));
            AssertTrue(rowProbe.Present, $"{transform.Name} changed detail-row presence: {rowProbe}.");
        }

        return Task.CompletedTask;
    }

    private static Task TestVisualPreflightGateAsync()
    {
        var gate = new VisualPreflightGate(requiredStableFrames: 2);
        AssertTrue(!gate.Observe(captureHealthy: false, headerDetected: false, gridPassed: false, layoutPassed: false));
        AssertTrue(!gate.Observe(captureHealthy: true, headerDetected: false, gridPassed: true, layoutPassed: false));
        AssertEqual(0, gate.StableFrames);
        AssertTrue(!gate.Observe(captureHealthy: true, headerDetected: true, gridPassed: true, layoutPassed: false));
        AssertEqual(1, gate.StableFrames);
        AssertTrue(!gate.Observe(captureHealthy: false, headerDetected: true, gridPassed: true, layoutPassed: false));
        AssertEqual(0, gate.StableFrames);
        AssertTrue(!gate.Observe(captureHealthy: true, headerDetected: true, gridPassed: false, layoutPassed: true));
        AssertTrue(gate.Observe(captureHealthy: true, headerDetected: true, gridPassed: false, layoutPassed: true));
        AssertTrue(gate.Accepted);
        return Task.CompletedTask;
    }

    private static Task TestWarehouseHeaderSemanticsAsync()
    {
        var policy = new WarehousePreflightPolicy();
        var exact = WarehousePreflightEvaluator.EvaluateHeader("驱动仓库【2875 / 3000】", 0.91f, policy);
        AssertTrue(exact.HeaderDetected);
        AssertEqual(2875, exact.InventoryCount!.Value);
        AssertEqual(3000, exact.InventoryCapacity!.Value);

        var fuzzy = WarehousePreflightEvaluator.EvaluateHeader("驱动苍库[614/3000]", 0.88f, policy);
        AssertTrue(fuzzy.HeaderDetected);
        AssertEqual(1, fuzzy.TitleEditDistance);

        var digitsOnly = WarehousePreflightEvaluator.EvaluateHeader("2875/3000", 0.99f, policy);
        AssertTrue(!digitsOnly.HeaderDetected, "A valid-looking count must not identify the warehouse page by itself.");
        AssertTrue(digitsOnly.InventoryCountDetected);

        var invalidCount = WarehousePreflightEvaluator.EvaluateHeader("驱动仓库[3001/3000]", 0.95f, policy);
        AssertTrue(invalidCount.HeaderDetected);
        AssertTrue(!invalidCount.InventoryCountDetected);

        var lowConfidence = WarehousePreflightEvaluator.EvaluateHeader("驱动仓库[614/3000]", 0.40f, policy);
        AssertTrue(!lowConfidence.HeaderDetected);
        var normalized = WarehousePreflightEvaluator.EvaluateHeader("驱动仓库[614/3000]", 0.92f, policy, usedNormalizedImage: true);
        var selected = WarehousePreflightEvaluator.ChooseHeaderResult(lowConfidence, normalized);
        AssertTrue(selected.HeaderDetected && selected.UsedNormalizedImage);
        return Task.CompletedTask;
    }

    private static Task TestWarehouseColorIndependentStructureAsync()
    {
        using var fixture = WarehouseStructureFixture();
        var listRect = new Rectangle(0, 0, 640, 360);
        var detailRect = new Rectangle(680, 30, 280, 500);
        var offset = new Point(-40, -30);
        var step = new Size(100, 120);
        var baseline = WarehousePreflightEvaluator.EvaluateStructure(fixture, listRect, detailRect, offset, step);
        AssertTrue(baseline.GridStructureScore >= 70, $"Synthetic grid probe was {baseline}.");
        AssertTrue(baseline.LayoutScore >= 70, $"Synthetic layout score was {baseline.LayoutScore}.");

        using var grayscale = ApplyFixtureTransform(fixture, new FixtureTransform("grayscale", 1, 1, 0, 1, 1, 0, false));
        var recolored = WarehousePreflightEvaluator.EvaluateStructure(grayscale, listRect, detailRect, offset, step);
        AssertTrue(recolored.GridStructureScore >= 70, $"Grayscale grid score was {recolored.GridStructureScore}.");
        AssertTrue(recolored.LayoutScore >= 70, $"Grayscale layout score was {recolored.LayoutScore}.");
        return Task.CompletedTask;
    }

    private static Task TestWarehouseCaptureHealthAndMonitorAsync()
    {
        using var black = new Bitmap(320, 180);
        using (var graphics = Graphics.FromImage(black))
        {
            graphics.Clear(Color.Black);
        }
        AssertTrue(!WarehousePreflightEvaluator.EvaluateCaptureHealth(black).Passed);

        using var fixture = WarehouseStructureFixture();
        var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(fixture);
        AssertTrue(health.Passed, $"Synthetic warehouse capture was rejected: {health}.");
        var regions = new[] { new Rectangle(0, 0, 320, 180), new Rectangle(680, 30, 280, 40) };
        var baseline = WarehousePreflightEvaluator.CreateMonitorSignature(fixture, regions);
        using var warm = ApplyFixtureTransform(fixture, new FixtureTransform("warm", 1.08, 0.80, 0.9, 0.92, 1, 4, false));
        var warmSignature = WarehousePreflightEvaluator.CreateMonitorSignature(warm, regions);
        AssertTrue(WarehousePreflightEvaluator.CompareMonitorSignature(baseline, warmSignature) >= 70);

        using var wrongPage = new Bitmap(fixture.Width, fixture.Height);
        using (var graphics = Graphics.FromImage(wrongPage))
        {
            graphics.Clear(Color.FromArgb(30, 30, 30));
            graphics.FillEllipse(Brushes.White, 100, 60, 700, 420);
        }
        var wrongSignature = WarehousePreflightEvaluator.CreateMonitorSignature(wrongPage, regions);
        AssertTrue(WarehousePreflightEvaluator.CompareMonitorSignature(baseline, wrongSignature) < 70);
        return Task.CompletedTask;
    }

    private static Task TestWarehouseInputGuardFastRegionAsync()
    {
        var headerRect = new Rectangle(0, 0, 160, 48);
        using var baselineImage = new Bitmap(480, 180);
        using (var graphics = Graphics.FromImage(baselineImage))
        {
            graphics.Clear(Color.FromArgb(20, 22, 25));
            using var pen = new Pen(Color.White, 3);
            graphics.DrawLine(pen, 12, 12, 145, 12);
            graphics.DrawLine(pen, 12, 25, 105, 25);
            graphics.DrawLine(pen, 12, 38, 130, 38);
        }

        var baseline = WarehousePreflightEvaluator.CreateMonitorSignature(baselineImage, [headerRect]);
        using var dynamicPanelChanged = (Bitmap)baselineImage.Clone();
        using (var graphics = Graphics.FromImage(dynamicPanelChanged))
        {
            graphics.FillRectangle(Brushes.White, 200, 20, 240, 140);
            graphics.FillEllipse(Brushes.Black, 240, 45, 160, 90);
        }

        var unchangedHeader = WarehousePreflightEvaluator.CreateMonitorSignature(dynamicPanelChanged, [headerRect]);
        AssertEqual(100, WarehousePreflightEvaluator.CompareMonitorSignature(baseline, unchangedHeader));

        using var headerChanged = (Bitmap)dynamicPanelChanged.Clone();
        using (var graphics = Graphics.FromImage(headerChanged))
        {
            graphics.FillRectangle(Brushes.White, headerRect);
            graphics.FillEllipse(Brushes.Black, 15, 5, 130, 38);
        }

        var changedHeader = WarehousePreflightEvaluator.CreateMonitorSignature(headerChanged, [headerRect]);
        AssertTrue(WarehousePreflightEvaluator.CompareMonitorSignature(baseline, changedHeader) < 70);
        return Task.CompletedTask;
    }

    private static Task TestWarehouseInputGuardConfirmationAsync()
    {
        var policy = new WarehousePreflightPolicy();
        var health = new CaptureHealthResult(true, 100, 50, 30, 5, 1, 200);
        var header = new WarehouseHeaderProbeResult(true, 95, 0, 0.95f, 729, 3000, false, "");
        var grid = new WarehouseStructureProbeResult(100, 20, 6, 100, 20, 20, 20);
        var layout = new WarehouseStructureProbeResult(20, 100, 1, 20, 100, 100, 100);
        var weakStructure = new WarehouseStructureProbeResult(20, 20, 1, 20, 20, 20, 20);
        AssertTrue(WarehousePreflightEvaluator.IsStrongConfirmationAccepted(health, header, grid, policy));
        AssertTrue(WarehousePreflightEvaluator.IsStrongConfirmationAccepted(health, header, layout, policy));
        AssertTrue(!WarehousePreflightEvaluator.IsStrongConfirmationAccepted(health, header, weakStructure, policy));
        AssertTrue(!WarehousePreflightEvaluator.IsStrongConfirmationAccepted(
            health,
            header with { HeaderDetected = false },
            grid,
            policy));

        var initialBaseline = new WarehouseMonitorSignature([1, 2, 3]);
        var rebuiltBaseline = new WarehouseMonitorSignature([4, 5, 6]);
        var state = new ScanController.WarehouseInputGuardState(300, 2, initialBaseline);
        AssertTrue(!state.AcceptFast(captureHealthy: true, score: 69, minimumScore: 70));
        AssertEqual(0, state.StrongFailures);
        state.BeginStrongConfirmation();
        AssertTrue(!state.AcceptStrong(passed: false, initialBaseline));
        AssertEqual(1, state.StrongFailures);
        AssertTrue(!state.ShouldBlock);
        state.BeginStrongConfirmation();
        AssertEqual(0, state.StrongFailures);
        AssertTrue(!state.AcceptStrong(passed: false, initialBaseline));
        AssertTrue(!state.AcceptStrong(passed: false, initialBaseline));
        AssertTrue(state.ShouldBlock);
        state.BeginStrongConfirmation();
        AssertTrue(!state.AcceptStrong(passed: false, initialBaseline));
        AssertTrue(state.AcceptStrong(passed: true, rebuiltBaseline));
        AssertEqual(0, state.StrongFailures);
        AssertTrue(state.FastBaseline.Samples.SequenceEqual(rebuiltBaseline.Samples));
        AssertTrue(state.IsFresh());
        return Task.CompletedTask;
    }

    private static Bitmap WarehouseStructureFixture()
    {
        var image = new Bitmap(1000, 600);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(22, 24, 27));
        using var cardBrush = new SolidBrush(Color.FromArgb(205, 214, 224));
        using var accentBrush = new SolidBrush(Color.FromArgb(255, 190, 0));
        using var borderPen = new Pen(Color.White, 4);
        for (var column = 0; column < 6; column++)
        {
            var rect = new Rectangle(20 + (column * 100), 35, 80, 105);
            graphics.FillRectangle(cardBrush, rect);
            graphics.DrawRectangle(borderPen, rect);
            graphics.FillEllipse(accentBrush, rect.X + 18, rect.Y + 20, 44, 44);
            graphics.DrawLine(Pens.Black, rect.Left + 8, rect.Bottom - 18, rect.Right - 8, rect.Bottom - 18);
        }

        var detail = new Rectangle(680, 30, 280, 500);
        graphics.DrawRectangle(borderPen, detail);
        graphics.DrawLine(borderPen, detail.Left + 10, detail.Top + 70, detail.Right - 10, detail.Top + 70);
        for (var row = 0; row < 6; row++)
        {
            var y = detail.Top + 115 + (row * 55);
            graphics.DrawLine(borderPen, detail.Left + 18, y, detail.Right - 18, y);
        }

        return image;
    }

    private static Task TestPrivacySafeVisualFixturesAsync()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Visual");
        using var anchor = new Bitmap(Path.Combine(fixtureDirectory, "preflight-anchor-hdr.png"));
        var anchorResult = VisualProbeEvaluator.EvaluateChromaticAnchor(anchor, Color.FromArgb(0, 186, 255));
        AssertTrue(anchorResult.Passed, $"Captured HDR anchor should pass, score={anchorResult.Score}.");
        AssertEqual(VisualTransformClass.HighlightClipped, anchorResult.TransformClass);

        using var firstRow = new Bitmap(Path.Combine(fixtureDirectory, "first-row-drive-discs-hdr.png"));
        var candidates = new[]
        {
            new VisualRarityCandidate("S", Color.FromArgb(255, 181, 0)),
            new VisualRarityCandidate("A", Color.FromArgb(233, 0, 255)),
            new VisualRarityCandidate("B", Color.FromArgb(0, 169, 255)),
        };
        var firstRowPoints = Enumerable.Range(0, firstRow.Height)
            .SelectMany(y => Enumerable.Range(0, firstRow.Width).Select(x => new Point(x, y)));
        var rarity = VisualProbeEvaluator.EvaluateRarity(firstRow, candidates, firstRowPoints);
        AssertEqual("S", rarity.Rarity!);

        using var scroll = new Bitmap(Path.Combine(fixtureDirectory, "scroll-region-hdr.png"));
        var baseline = LuminanceSamples(scroll);
        AssertEqual(0, VisualProbeEvaluator.MeasureLuminanceMovement(baseline, baseline));
        using var shiftedScroll = new Bitmap(scroll.Width, scroll.Height);
        using (var graphics = Graphics.FromImage(shiftedScroll))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImageUnscaled(scroll, 0, 28);
        }
        AssertTrue(VisualProbeEvaluator.MeasureLuminanceMovement(baseline, LuminanceSamples(shiftedScroll)) > 2);

        using var details = new Bitmap(Path.Combine(fixtureDirectory, "detail-panel-rows-hdr.png"));
        var rowProbe = VisualProbeEvaluator.EvaluateRelativeTextRowPresence(
            details,
            new Rectangle(0, 58, 390, 45),
            new Rectangle(0, 137, 390, 45),
            new Point(18, 20));
        AssertTrue(rowProbe.Present);
        AssertTrue(rowProbe.EdgeDensityPermille >= rowProbe.MinimumEdgeDensityPermille);
        return Task.CompletedTask;
    }

    private static Task TestVisualRarityAmbiguityAsync()
    {
        var candidates = new[]
        {
            new VisualRarityCandidate("S", Color.FromArgb(255, 181, 0)),
            new VisualRarityCandidate("A", Color.FromArgb(233, 0, 255)),
            new VisualRarityCandidate("B", Color.FromArgb(0, 169, 255)),
        };

        using var clear = new Bitmap(20, 20);
        using (var graphics = Graphics.FromImage(clear))
        {
            graphics.Clear(Color.FromArgb(25, 25, 25));
            graphics.FillRectangle(Brushes.Gold, 4, 4, 12, 12);
        }

        var points = Enumerable.Range(0, clear.Height)
            .SelectMany(y => Enumerable.Range(0, clear.Width).Select(x => new Point(x, y)))
            .ToArray();
        var clearResult = VisualProbeEvaluator.EvaluateRarity(clear, candidates, points);
        AssertEqual("S", clearResult.Rarity!);
        AssertTrue(clearResult.Margin >= 8);

        using var ambiguous = new Bitmap(20, 20);
        using (var graphics = Graphics.FromImage(ambiguous))
        {
            graphics.Clear(Color.FromArgb(25, 25, 25));
            using var sBrush = new SolidBrush(candidates[0].Color);
            using var aBrush = new SolidBrush(candidates[1].Color);
            graphics.FillRectangle(sBrush, 2, 2, 7, 16);
            graphics.FillRectangle(aBrush, 11, 2, 7, 16);
        }

        var ambiguousResult = VisualProbeEvaluator.EvaluateRarity(ambiguous, candidates, points);
        AssertTrue(ambiguousResult.Rarity is null);
        return Task.CompletedTask;
    }

    private static Task TestRelativeRowPresenceAsync()
    {
        var variants = new Dictionary<string, (Color Background, Color Text)>
        {
            ["neutral"] = (Color.FromArgb(22, 22, 22), Color.White),
            ["bright 1f"] = (Color.FromArgb(31, 31, 31), Color.White),
            ["dark"] = (Color.FromArgb(13, 13, 13), Color.FromArgb(224, 224, 224)),
            ["warm"] = (Color.FromArgb(31, 28, 24), Color.FromArgb(245, 230, 210)),
            ["saturation shifted"] = (Color.FromArgb(20, 24, 34), Color.FromArgb(220, 236, 255)),
            ["contrast shifted"] = (Color.FromArgb(8, 8, 8), Color.White),
        };

        var reference = new Rectangle(10, 10, 90, 30);
        var candidates = Enumerable.Range(0, 4)
            .Select(index => new Rectangle(110, 10 + (index * 40), 90, 30))
            .ToArray();
        var absent = new Rectangle(10, 190, 90, 30);
        var offset = new Point(10, 10);
        foreach (var (name, colors) in variants)
        {
            using var image = new Bitmap(220, 230);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.Black);
                using var rowBrush = new SolidBrush(colors.Background);
                using var textPen = new Pen(colors.Text);
                graphics.FillRectangle(rowBrush, reference);
                foreach (var candidate in candidates)
                {
                    graphics.FillRectangle(rowBrush, candidate);
                    graphics.DrawLine(textPen, candidate.Left + 10, candidate.Top + 12, candidate.Right - 10, candidate.Top + 12);
                    graphics.DrawLine(textPen, candidate.Left + 18, candidate.Top + 7, candidate.Left + 18, candidate.Bottom - 7);
                }
            }

            foreach (var candidate in candidates)
            {
                var result = VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, reference, candidate, offset);
                AssertTrue(result.Present, $"{name} row should be present: {result}");
                AssertTrue(result.LumaDelta <= result.AllowedLumaDelta, $"{name} luma gate should pass.");
                AssertTrue(result.EdgeDensityPermille >= result.MinimumEdgeDensityPermille, $"{name} edge gate should pass.");
            }

            var blank = VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, reference, absent, offset);
            AssertTrue(!blank.Present, $"{name} blank row should be absent: {blank}");
        }

        return Task.CompletedTask;
    }

    private static async Task TestSelectionRefreshWaitAsync()
    {
        AssertEqual(600, SelectionRefreshTiming.ResolveMaximumWaitMilliseconds(1200));
        AssertEqual(450, SelectionRefreshTiming.ResolveMaximumWaitMilliseconds(450));

        var observations = new Queue<SelectionRefreshObservation>(new[]
        {
            new SelectionRefreshObservation(false, false),
            new SelectionRefreshObservation(true, false),
            new SelectionRefreshObservation(true, true),
        });
        var ready = await SelectionRefreshWaiter.WaitAsync(
            () => observations.Dequeue(),
            maximumWaitMilliseconds: 100,
            pollMilliseconds: 1,
            CancellationToken.None);
        AssertTrue(ready.Ready);
        AssertEqual(3, ready.FrameCount);
        AssertEqual(2, ready.StableFrames);

        var timedOut = await SelectionRefreshWaiter.WaitAsync(
            () => new SelectionRefreshObservation(true, false),
            maximumWaitMilliseconds: 12,
            pollMilliseconds: 2,
            CancellationToken.None);
        AssertTrue(!timedOut.Ready);
        AssertTrue(timedOut.ChangedFromTarget);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await AssertCanceledAsync(SelectionRefreshWaiter.WaitAsync(
            () => new SelectionRefreshObservation(false, false),
            maximumWaitMilliseconds: 600,
            pollMilliseconds: 5,
            cancellation.Token));

        var indistinguishableNeighbor = await SelectionRefreshWaiter.WaitAsync(
            () => new SelectionRefreshObservation(false, true),
            maximumWaitMilliseconds: 12,
            pollMilliseconds: 2,
            CancellationToken.None);
        AssertTrue(!indistinguishableNeighbor.Ready, "An indistinguishable adjacent panel must not prove a refresh.");
        AssertTrue(!indistinguishableNeighbor.ChangedFromTarget);
    }

    private static Task TestFirstCellPanelChangeGateAsync()
    {
        var withBaseline = PanelCaptureGate.Initialize(hasPanelBaseline: true);
        AssertTrue(!withBaseline.SawPanelChange);
        AssertTrue(!withBaseline.SelectionChanged);
        AssertTrue(withBaseline.ChangeMilliseconds is null);

        var withoutBaseline = PanelCaptureGate.Initialize(hasPanelBaseline: false);
        AssertTrue(!withoutBaseline.SawPanelChange, "A missing baseline must never imply a panel change.");
        AssertTrue(!withoutBaseline.SelectionChanged, "A missing baseline must never imply a selection change.");
        AssertTrue(withoutBaseline.ChangeMilliseconds is null, "The first cell must not report changeMs=0 without evidence.");
        AssertTrue(PanelCaptureGate.RequiresFirstCellNeighborRoundTrip(firstQueuedItem: true));
        AssertTrue(!PanelCaptureGate.RequiresFirstCellNeighborRoundTrip(firstQueuedItem: false));
        AssertTrue(PanelCaptureGate.IsStrongChangeCurrentFrame(21, 30, 20, 25));
        AssertTrue(!PanelCaptureGate.IsStrongChangeCurrentFrame(0, 200, 20, 25), "A transient strong frame must not keep the final frame changed.");
        AssertTrue(!PanelCaptureGate.IsStrongChangeCurrentFrame(21, 20, 20, 25), "An early click animation must not prove a panel change.");
        return Task.CompletedTask;
    }

    private static Task TestPartialFullScanBenchmarkTerminalAsync()
    {
        var scanDirectory = Path.Combine(Path.GetTempPath(), "zzz-scanner-benchmark-terminal", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scanDirectory);
        try
        {
            File.WriteAllLines(Path.Combine(scanDirectory, "scan.log"),
            [
                "[2026-07-22T00:00:00.0000000Z] Start scan. OcrWorkers=1, OcrBatchSize=1, OcrQueueCapacity=4, OcrIntraOpThreads=1, MaxItems=0",
                "[2026-07-22T00:00:00.0100000Z] Traversal: overlap-signature-page. totalRows=20",
                "[2026-07-22T00:00:01.0000000Z] Progress visited=1, queued=1, completed=0, failed=0",
                "[2026-07-22T00:00:02.0000000Z] EVENT #1 SCAN_TERMINAL: visited=2, queued=2, completed=1, failed=0, partial=True, terminationCode=non_level_15_stop, exportFile=export.partial.json"
            ]);
            File.WriteAllText(
                Path.Combine(scanDirectory, "export.partial.json"),
                "[{\"序号\":1,\"名称\":\"啄木鸟电音\",\"槽位\":1,\"品质\":\"S\",\"等级\":15,\"最大等级\":15,\"主属性\":{\"生命值\":2200},\"副属性\":[]}]");
            File.WriteAllText(Path.Combine(scanDirectory, "0010.non15.txt"), "level=12");
            File.WriteAllText(
                Path.Combine(scanDirectory, "scan-once-result.json"),
                "{\"Visited\":1,\"Queued\":1,\"Completed\":0,\"Failed\":0}");

            var originalOut = Console.Out;
            using var capture = new StringWriter(CultureInfo.InvariantCulture);
            try
            {
                Console.SetOut(capture);
                AssertEqual(0, ScanBenchmark.Run(scanDirectory, baselineDirectory: null));
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            var output = capture.ToString();
            AssertTrue(output.Contains("current.last_completed=1", StringComparison.Ordinal));
            AssertTrue(output.Contains("current.partial=True", StringComparison.Ordinal));
            AssertTrue(output.Contains("current.termination_code=non_level_15_stop", StringComparison.Ordinal));
            AssertTrue(output.Contains("current.export_file=export.partial.json", StringComparison.Ordinal));
            AssertTrue(output.Contains("current.effective_full_scan_complete=true", StringComparison.Ordinal));
            AssertTrue(output.Contains("acceptance.full_scan_complete=pass", StringComparison.Ordinal));
            AssertTrue(output.Contains("acceptance.overlap_no_missing_rows=skip", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(scanDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private static Task TestDpiIndependentClientCoordinatesAsync()
    {
        var clientSizes = new[]
        {
            new Size(1280, 720),
            new Size(1600, 900),
            new Size(1920, 1080),
            new Size(3840, 2160),
        };
        var dpis = new[] { 96, 120, 144, 192 };
        foreach (var size in clientSizes)
        {
            var client = new Rectangle(317, 211, size.Width, size.Height);
            foreach (var dpi in dpis)
            {
                var local = GameWindow.MapToScreenPoint(client, new PointF(0.75f, 0.80f), dpi, clientToScreen: false);
                var screen = GameWindow.MapToScreenPoint(client, new PointF(0.75f, 0.80f), dpi, clientToScreen: true);
                AssertTrue(local.X == (int)Math.Round(size.Width * 0.75), $"Unexpected X at {size.Width}x{size.Height}, DPI {dpi}.");
                AssertTrue(local.Y == (int)Math.Round(size.Height * 0.80), $"Unexpected Y at {size.Width}x{size.Height}, DPI {dpi}.");
                AssertEqual(client.X + local.X, screen.X);
                AssertEqual(client.Y + local.Y, screen.Y);
            }
        }

        return Task.CompletedTask;
    }

    private static Task TestRelativeRowProbeOverheadAsync()
    {
        using var image = new Bitmap(220, 100);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Black);
            using var rowBrush = new SolidBrush(Color.FromArgb(31, 31, 31));
            graphics.FillRectangle(rowBrush, 10, 10, 90, 30);
            graphics.FillRectangle(rowBrush, 110, 10, 90, 30);
            graphics.DrawLine(Pens.White, 120, 22, 185, 22);
        }

        var reference = new Rectangle(10, 10, 90, 30);
        var candidate = new Rectangle(110, 10, 90, 30);
        var offset = new Point(10, 10);
        var policy = new RowPresenceProbePolicy();
        for (var i = 0; i < 100; i++)
        {
            AssertTrue(LegacyRelativeTextRowPresent(image, reference, candidate, offset, policy));
            AssertTrue(VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, reference, candidate, offset, policy).Present);
        }

        const int iterations = 1500;
        var ratios = new List<double>();
        for (var round = 0; round < 7; round++)
        {
            long legacyTicks;
            long structuredTicks;
            if (round % 2 == 0)
            {
                legacyTicks = MeasureRowProbe(() => LegacyRelativeTextRowPresent(image, reference, candidate, offset, policy), iterations);
                structuredTicks = MeasureRowProbe(() => VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, reference, candidate, offset, policy).Present, iterations);
            }
            else
            {
                structuredTicks = MeasureRowProbe(() => VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, reference, candidate, offset, policy).Present, iterations);
                legacyTicks = MeasureRowProbe(() => LegacyRelativeTextRowPresent(image, reference, candidate, offset, policy), iterations);
            }

            ratios.Add(structuredTicks / (double)Math.Max(1, legacyTicks));
        }

        ratios.Sort();
        var medianRatio = ratios[ratios.Count / 2];
        Console.WriteLine($"relative_row_probe.overhead_pct={(medianRatio - 1) * 100:F2}");
        AssertTrue(medianRatio <= 1.05, $"Structured row diagnostics exceeded the 5% overhead budget: {(medianRatio - 1) * 100:F2}%.");
        return Task.CompletedTask;
    }

    private static long MeasureRowProbe(Func<bool> probe, int iterations)
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            if (!probe())
            {
                throw new InvalidOperationException("Benchmark row unexpectedly failed the presence gate.");
            }
        }

        watch.Stop();
        return watch.ElapsedTicks;
    }

    private static bool LegacyRelativeTextRowPresent(
        Bitmap image,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy policy)
    {
        var reference = LegacyMedianPatchLuminance(image, referenceRoi, sampleOffset, policy.PatchRadius);
        var candidate = LegacyMedianPatchLuminance(image, candidateRoi, sampleOffset, policy.PatchRadius);
        var tolerance = Math.Max(policy.MinimumLuminanceTolerance, Math.Abs(reference) * policy.RelativeLuminanceTolerance);
        if (Math.Abs(reference - candidate) > tolerance)
        {
            return false;
        }

        return LegacyEdgeDensity(image, candidateRoi, policy.EdgeThreshold) >= policy.MinimumEdgeDensity;
    }

    private static double LegacyMedianPatchLuminance(Bitmap image, Rectangle roi, Point offset, int radius)
    {
        var centerX = Math.Clamp(roi.X + offset.X, 0, image.Width - 1);
        var centerY = Math.Clamp(roi.Y + offset.Y, 0, image.Height - 1);
        var values = new List<double>();
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                values.Add(LegacyLuminance(image.GetPixel(
                    Math.Clamp(x, 0, image.Width - 1),
                    Math.Clamp(y, 0, image.Height - 1))));
            }
        }

        values.Sort();
        return values[values.Count / 2];
    }

    private static double LegacyEdgeDensity(Bitmap image, Rectangle roi, int threshold)
    {
        var clipped = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height), roi);
        if (clipped.Width < 2 || clipped.Height < 2)
        {
            return 0;
        }

        var edges = 0;
        var comparisons = 0;
        for (var y = clipped.Top; y < clipped.Bottom - 1; y += 4)
        {
            for (var x = clipped.Left; x < clipped.Right - 1; x += 4)
            {
                var current = LegacyLuminance(image.GetPixel(x, y));
                if (Math.Abs(current - LegacyLuminance(image.GetPixel(x + 1, y))) >= threshold
                    || Math.Abs(current - LegacyLuminance(image.GetPixel(x, y + 1))) >= threshold)
                {
                    edges++;
                }

                comparisons++;
            }
        }

        return comparisons == 0 ? 0 : edges / (double)comparisons;
    }

    private static double LegacyLuminance(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

    private static Task TestLuminanceNormalizationAsync()
    {
        using var source = new Bitmap(100, 20);
        for (var x = 0; x < source.Width; x++)
        {
            var value = 60 + (x * 80 / (source.Width - 1));
            for (var y = 0; y < source.Height; y++)
            {
                source.SetPixel(x, y, Color.FromArgb(value, value, value));
            }
        }

        using var normalized = VisualProbeEvaluator.NormalizeLuminance(source);
        AssertTrue(normalized.GetPixel(0, 0).R <= 8);
        AssertTrue(normalized.GetPixel(normalized.Width - 1, 0).R >= 247);
        return Task.CompletedTask;
    }

    private static Bitmap VisualAnchorFixture(Color anchor)
    {
        var image = new Bitmap(48, 48);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(18, 18, 18));
        using var brush = new SolidBrush(anchor);
        graphics.FillEllipse(brush, 8, 8, 32, 32);
        return image;
    }

    private static int[] LuminanceSamples(Bitmap image)
    {
        const int columns = 8;
        const int rows = 12;
        var samples = new int[columns * rows];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            var y = Math.Min(image.Height - 1, (int)Math.Round((row + 0.5) * image.Height / rows));
            for (var column = 0; column < columns; column++)
            {
                var x = Math.Min(image.Width - 1, (int)Math.Round((column + 0.5) * image.Width / columns));
                var color = image.GetPixel(x, y);
                samples[index++] = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
            }
        }

        return samples;
    }

    private static Bitmap ApplyFixtureTransform(Bitmap source, FixtureTransform transform)
    {
        var output = new Bitmap(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                double red = color.R * transform.RedGain;
                double green = color.G;
                double blue = color.B * transform.BlueGain;
                var luma = (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
                red = luma + ((red - luma) * transform.Saturation);
                green = luma + ((green - luma) * transform.Saturation);
                blue = luma + ((blue - luma) * transform.Saturation);
                red = TransformChannel(red, transform);
                green = TransformChannel(green, transform);
                blue = TransformChannel(blue, transform);
                output.SetPixel(x, y, Color.FromArgb(color.A, (int)red, (int)green, (int)blue));
            }
        }

        return output;
    }

    private static double TransformChannel(double value, FixtureTransform transform)
    {
        var normalized = Math.Clamp(value, 0, 255) / 255.0;
        value = Math.Pow(normalized, transform.Gamma) * 255.0;
        value = ((value - 128) * transform.Contrast) + 128 + transform.Brightness;
        value = Math.Clamp(value, 0, 255);
        if (transform.ClipHighlights && value >= 230)
        {
            value = 255;
        }

        return Math.Round(value);
    }

    private readonly record struct FixtureTransform(
        string Name,
        double RedGain,
        double BlueGain,
        double Saturation,
        double Gamma,
        double Contrast,
        int Brightness,
        bool ClipHighlights);

    private static Task TestPanelTimeoutDiagnosticsAsync()
    {
        var details = ScanDiagnosticDetails.PanelCapture(
            logicalRow: 1,
            visualRow: 1,
            column: 1,
            maxColumns: 9,
            visibleRois: 10,
            totalRois: 12,
            firstMissingRoi: "subStat4",
            referenceLuma: 22,
            candidateLuma: 31,
            lumaDelta: 9,
            allowedLumaDelta: 18,
            edgeDensityPermille: 0,
            minimumEdgeDensityPermille: 3,
            acceptGateReason: "waiting_for_full_roi",
            sawPanelChange: true,
            selectionChanged: true,
            stableFrames: 2,
            requiredStableFrames: 2,
            attempts: 3,
            frameCount: 72,
            clientWidth: 1920,
            clientHeight: 1080,
            dpi: 96,
            captureMode: "dxgi",
            visualProfileId: "local-1920x1080-current");

        AssertEqual(10, (int)details["visibleRois"]!);
        AssertEqual(12, (int)details["totalRois"]!);
        AssertEqual("subStat4", (string)details["firstMissingRoi"]!);
        AssertEqual(31, (int)details["candidateLuma"]!);
        AssertEqual(3, (int)details["minimumEdgeDensityPermille"]!);
        AssertEqual("waiting_for_full_roi", (string)details["acceptGateReason"]!);
        AssertEqual(3, (int)details["attempts"]!);
        AssertEqual("local-1920x1080-current", (string)details["visualProfileId"]!);

        var exception = new DiagnosticTestException(details);
        AssertTrue(ReferenceEquals(details, ScanDiagnosticDetails.FromException(exception)));
        AssertTrue(ScanDiagnosticDetails.FromException(new InvalidOperationException()) is null);

        var preflight = ScanDiagnosticDetails.Preflight(
            preflightState: "inventory_screen_unreadable",
            visualTransformClass: "unknown",
            anchorScore: 35,
            gridScore: 67,
            warehouseHeaderDetected: true,
            headerScore: 88,
            gridStructureScore: 75,
            layoutScore: 80,
            inventoryCountDetected: true,
            countConsensusFrames: 2,
            hueDelta: 52,
            saturationDeltaPct: 12,
            valueDeltaPct: 8,
            stableFrames: 0,
            requiredStableFrames: 2,
            clientWidth: 1920,
            clientHeight: 1080,
            dpi: 192,
            captureMode: "dxgi",
            visualProfileId: "local-1920x1080-current");
        AssertEqual("inventory_screen_unreadable", (string)preflight["preflightState"]!);
        AssertEqual(35, (int)preflight["anchorScore"]!);
        AssertEqual(88, (int)preflight["headerScore"]!);
        AssertEqual(75, (int)preflight["gridStructureScore"]!);
        AssertEqual(80, (int)preflight["layoutScore"]!);
        AssertEqual(2, (int)preflight["countConsensusFrames"]!);
        AssertEqual(true, (bool)preflight["inventoryCountDetected"]!);
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

    private static ScannerPackage PackageFromDirectory(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => new ScannerPackageFile
            {
                Path = Path.GetRelativePath(directory, path).Replace('\\', '/'),
                Size = new FileInfo(path).Length,
                Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()
            })
            .ToList();
        return new ScannerPackage
        {
            Id = "win-x64-fdd",
            Mode = ScannerPackageModes.FrameworkDependent,
            Framework = new ScannerFramework { Name = "Microsoft.WindowsDesktop.App", Major = 8, MinVersion = "8.0.0" },
            PackageUrls = ["https://example.com/scanner.zip"],
            Sha256 = new string('a', 64),
            Size = 10,
            ExpandedSize = files.Sum(file => file.Size),
            Entry = "ZZZ-Scanner.Next.exe",
            Files = files,
        };
    }

    private static ScannerPackage CloneAsSelfContained(ScannerPackage source)
    {
        return new ScannerPackage
        {
            Id = "win-x64-self-contained",
            Mode = ScannerPackageModes.SelfContained,
            PackageUrls = source.PackageUrls.ToList(),
            Sha256 = new string('b', 64),
            Size = source.Size,
            ExpandedSize = source.ExpandedSize,
            Entry = source.Entry,
            Files = source.Files.Select(file => new ScannerPackageFile
            {
                Path = file.Path,
                Size = file.Size,
                Sha256 = file.Sha256,
            }).ToList(),
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

    private static void AssertTrue(bool condition, string message = "Assertion failed.")
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
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

    private sealed class BlockingCaptureSource : IWindowCaptureSource
    {
        private int _active;
        private int _maximumConcurrency;
        private int _calls;

        public string Name => "blocking";
        public string FrameBackendName => "test";
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool Disposed { get; private set; }

        public CapturedFrame CaptureFrame(Rectangle screenRect) =>
            CaptureCore(() => new BitmapCapturedFrame(new Bitmap(screenRect.Width, screenRect.Height), FrameBackendName));

        public Bitmap Capture(Rectangle screenRect) =>
            CaptureCore(() => new Bitmap(screenRect.Width, screenRect.Height));

        public Color GetPixel(Point point) => CaptureCore(() => Color.Black);

        public void Dispose()
        {
            Disposed = true;
            Entered.Dispose();
            Release.Dispose();
        }

        private T CaptureCore<T>(Func<T> create)
        {
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (active <= current || Interlocked.CompareExchange(ref _maximumConcurrency, active, current) == current)
                {
                    break;
                }
            }

            Entered.Set();
            try
            {
                AssertTrue(Release.Wait(TimeSpan.FromSeconds(5)), "Timed out waiting to release the test capture.");
                return create();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class RecordingCaptureSource(string name) : IWindowCaptureSource
    {
        private int _calls;

        public string Name { get; } = name;
        public string FrameBackendName => "test";
        public int Calls => Volatile.Read(ref _calls);
        public bool Disposed { get; private set; }

        public CapturedFrame CaptureFrame(Rectangle screenRect)
        {
            Interlocked.Increment(ref _calls);
            return new BitmapCapturedFrame(new Bitmap(screenRect.Width, screenRect.Height), FrameBackendName);
        }

        public Bitmap Capture(Rectangle screenRect)
        {
            Interlocked.Increment(ref _calls);
            return new Bitmap(screenRect.Width, screenRect.Height);
        }

        public Color GetPixel(Point point)
        {
            Interlocked.Increment(ref _calls);
            return Color.Black;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private sealed class FailingCaptureSource : IWindowCaptureSource
    {
        public string Name => "failing";
        public string FrameBackendName => "test";
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public CapturedFrame CaptureFrame(Rectangle screenRect) => throw Failure();
        public Bitmap Capture(Rectangle screenRect) => throw Failure();
        public Color GetPixel(Point point) => throw Failure();

        public void Dispose()
        {
            Disposed = true;
        }

        private Exception Failure()
        {
            Calls++;
            return new InvalidOperationException("simulated capture failure");
        }
    }

    private sealed class DiagnosticTestException(IReadOnlyDictionary<string, object?> details)
        : Exception, IScanDiagnosticException
    {
        public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; } = details;
    }
}
