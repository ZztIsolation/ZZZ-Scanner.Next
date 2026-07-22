using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZZZScannerHelper;

internal static partial class Program
{
    private const string ServiceName = "zzz-scanner-helper";
    internal const string HelperVersion = "1.3.1";
    internal const int ProtocolVersion = 4;
    private const int HelperPort = 22355;
    private const string ProtocolName = "zzz-scanner";
    private const string ManifestPath = "/downloads/zzz-scanner/manifest.json";
    private const int PackageDownloadMaxAttempts = 5;
    private const int DownloadProgressIntervalMs = 300;
    internal const int MaxWebSocketMessageBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var lifecycle = await HelperInstallationManager.PrepareAsync(args);
            return lifecycle.Exit ? 0 : await RunAsync(lifecycle.Arguments);
        }
        catch (Exception ex)
        {
            if (HelperInstallationManager.TryRollbackPendingUpdate())
            {
                return 1;
            }
            var error = HelperErrors.FromException(ex, "startup");
            ShowStartupError(error);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var launchOrigin = TryReadLaunchOrigin(args);
        try
        {
            RegisterProtocol();
        }
        catch (Exception ex)
        {
            var warning = new HelperFailureException(
                "protocol_registration_failed",
                "startup",
                "无法注册扫描助手启动协议",
                ex.Message,
                "请检查当前用户注册表权限；仍可手动运行 Helper 后回到网页重连。",
                retryable: true,
                innerException: ex);
            ShowStartupError(HelperErrors.FromException(warning, "startup"));
        }

        using var mutex = new Mutex(initiallyOwned: true, "Local\\ZZZScannerHelper", out var ownsMutex);
        if (!ownsMutex)
        {
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var server = new HelperServer(launchOrigin);
        await server.RunAsync(cts.Token);
        return 0;
    }

    private static void ShowStartupError(HelperErrorMessage error)
    {
        var text = $"{error.Message}\n\n处理方法：{error.Remedy}\n诊断编号：{error.DiagnosticId}\n日志：{HelperLog.DirectoryPath}";
        try
        {
            MessageBox(IntPtr.Zero, text, error.Title, 0x10);
        }
        catch
        {
            Console.Error.WriteLine(text);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private static string? TryReadLaunchOrigin(string[] args)
    {
        if (args.Length == 0 || !Uri.TryCreate(args[0], UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!uri.Scheme.Equals(ProtocolName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals("origin", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[1]);
            }
        }

        return null;
    }

    private static void RegisterProtocol()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        try
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}");
            root?.SetValue("", "URL:ZZZ Scanner Protocol");
            root?.SetValue("URL Protocol", "");
            using var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}\shell\open\command");
            command?.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new HelperFailureException(
                "protocol_registration_failed",
                "startup",
                "无法注册扫描助手启动协议",
                "Windows 不允许 Helper 注册 zzz-scanner 启动协议。",
                "可以手动运行 Helper；如需网页自动唤起，请检查账户注册表策略。",
                retryable: false,
                innerException: ex);
        }
    }

    private sealed class HelperServer : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly HttpListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _ensureGate = new(1, 1);
        private readonly HelperStorageManager _storage = new();
        private ScannerManifest? _manifest;
        private ScannerPackage? _selectedPackage;
        private HelperEnvironmentSnapshot? _environment;
        private string? _entryPath;
        private string? _activeVersion;
        private string? _activePackageId;
        private string? _activePackageMode;
        private string? _launchOrigin;
        private bool _disposed;

        public HelperServer(string? launchOrigin)
        {
            var active = _storage.LoadActiveRuntime();
            if (active is not null)
            {
                _activeVersion = active.Version;
                _activePackageId = active.PackageId;
                _activePackageMode = active.PackageMode;
                _entryPath = active.EntryPath;
            }
            if (IsAllowedOrigin(launchOrigin))
            {
                _launchOrigin = launchOrigin;
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{HelperPort}/");
            _listener.Start();
            HelperInstallationManager.CompletePendingUpdate();
            while (!token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(token);
                _ = Task.Run(() => HandleContextAsync(context, token), token);
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            var origin = context.Request.Headers["Origin"];
            AddCorsHeaders(context.Response, origin);
            try
            {
                if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = IsAllowedOrigin(origin) ? 204 : 403;
                    context.Response.Close();
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                if (context.Request.IsWebSocketRequest && path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleWebSocketAsync(context, token);
                    return;
                }

                if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) && path == "/")
                {
                    if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin))
                    {
                        await SendTextAsync(context.Response, 403, "Bad Origin", token);
                        return;
                    }
                    await SendJsonAsync(context.Response, 200, HelperInfo(), token);
                    return;
                }

                if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) && path == "/token")
                {
                    await HandleTokenAsync(context, origin, token);
                    return;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
            catch (Exception ex)
            {
                if (context.Response.OutputStream.CanWrite)
                {
                    await SendTextAsync(context.Response, 500, ex.Message, token);
                }
            }
        }

        private async Task HandleTokenAsync(HttpListenerContext context, string? originHeader, CancellationToken token)
        {
            var origin = originHeader;
            if (string.IsNullOrWhiteSpace(origin))
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync(token);
                try
                {
                    origin = JsonSerializer.Deserialize(body, HelperJsonContext.Default.TokenRequest)?.Origin;
                }
                catch
                {
                }
            }

            if (!IsAllowedOrigin(origin))
            {
                await SendTextAsync(context.Response, 403, "Bad Origin", token);
                return;
            }

            _launchOrigin = origin;
            var tokenValue = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            var now = DateTimeOffset.Now;
            foreach (var expiredToken in _tokens.Where(pair => pair.Value < now).Select(pair => pair.Key))
            {
                _tokens.TryRemove(expiredToken, out _);
            }

            _tokens[tokenValue] = now.AddMinutes(5);
            await SendJsonAsync(context.Response, 200, new TokenResponse { Token = tokenValue }, token);
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken token)
        {
            var tokenValue = context.Request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(tokenValue)
                || !_tokens.TryRemove(tokenValue, out var expires)
                || expires < DateTimeOffset.Now)
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(null);
            await using var session = new BrowserSession(this, wsContext.WebSocket);
            await session.RunAsync(token);
        }

        private HelperInfoResponse HelperInfo()
        {
            return new HelperInfoResponse
            {
                Service = ServiceName,
                Version = HelperVersion,
                ProtocolVersion = ProtocolVersion,
                Scanner = CurrentScannerState(),
                HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
            };
        }

        public ScannerState CurrentScannerState()
        {
            return new ScannerState
            {
                Version = _activeVersion ?? _manifest?.ScannerVersion,
                Installed = !string.IsNullOrWhiteSpace(_entryPath) && File.Exists(_entryPath),
                Entry = _entryPath,
                PackageId = _activePackageId ?? _selectedPackage?.Id,
                PackageMode = _activePackageMode ?? _selectedPackage?.Mode,
                DesktopRuntimeAvailable = _environment?.DesktopRuntimeAvailable
            };
        }

        public HelperStorageSnapshot CurrentStorageInfo()
        {
            return _storage.Inspect(_activeVersion, _activePackageId);
        }

        public HelperStorageCleanupResult CleanupStorage()
        {
            return _storage.Cleanup(_activeVersion, _activePackageId);
        }

        public void MarkScannerActive()
        {
            if (_manifest is null || _selectedPackage is null)
            {
                throw new InvalidOperationException("Scanner manifest and package selection are unavailable.");
            }
            var active = _storage.SaveActiveRuntime(
                _manifest.ScannerVersion,
                _selectedPackage.Id,
                _selectedPackage.Mode,
                _selectedPackage.Entry);
            _activeVersion = active.Version;
            _activePackageId = active.PackageId;
            _activePackageMode = active.PackageMode;
            _entryPath = active.EntryPath;
        }

        public Uri ResolveHelperManifestUrl()
        {
            return new Uri(ResolveManifestUrl(), "helper-manifest.json");
        }

        public async Task EnsureScannerAsync(
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token,
            bool forceRepair = false,
            bool forceSelfContained = false)
        {
            await _ensureGate.WaitAsync(token);
            try
            {
                await report(new LauncherProgress { Stage = "manifest", Message = "正在获取扫描器版本清单..." }, token);
                var manifestUrl = ResolveManifestUrl();
                ScannerManifest manifest;
                try
                {
                    manifest = await DownloadManifestAsync(manifestUrl, token)
                        ?? throw new InvalidDataException("Scanner manifest is empty.");
                    HelperSecurity.ValidateManifest(manifest, manifestUrl, Version.Parse(HelperVersion));
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not HelperFailureException)
                {
                    throw new HelperFailureException(
                        ex is HttpRequestException ? "manifest_unreachable" : "manifest_invalid",
                        "manifest",
                        ex is HttpRequestException ? "无法获取扫描器版本信息" : "扫描器版本信息无效",
                        ex.Message,
                        ex is HttpRequestException ? "请检查网络后重试。" : "请更新 Helper；如果问题持续，请打开日志。",
                        retryable: true,
                        innerException: ex);
                }

                _manifest = manifest;

                var environment = HelperPlatform.Inspect(manifest);
                _environment = environment;
                var runtimeRoot = RuntimeRootDirectory();
                var packageRoot = PackageCacheDirectory();
                HelperPlatform.EnsureWritableDirectory(runtimeRoot);
                HelperPlatform.EnsureWritableDirectory(packageRoot);
                var selection = forceSelfContained && manifest.SchemaVersion == 2
                    ? new PackageSelection(
                        manifest.Packages.Single(package => package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase)),
                        "desktop-runtime-disappeared")
                    : HelperPlatform.SelectPackage(manifest, environment);
                var package = selection.Package;
                _selectedPackage = package;
                var installRelative = Path.Combine(manifest.ScannerVersion, package.Id);
                var installDir = HelperSecurity.ResolvePathWithinRoot(runtimeRoot, installRelative);
                var entryPath = HelperSecurity.ResolvePathWithinRoot(installDir, package.Entry);
                var packagePath = HelperSecurity.ResolvePathWithinRoot(
                    packageRoot,
                    $"scanner-{manifest.ScannerVersion}-{package.Id}.zip");

                var selectionMessage = selection.Reason == "desktop-runtime-missing"
                    ? "未检测到 .NET 8 Desktop Runtime，正在使用兼容包，无需安装 .NET。"
                    : selection.Reason == "desktop-runtime-disappeared"
                        ? ".NET 8 在启动前不可用，正在自动切换兼容包。"
                    : selection.Reason == "desktop-runtime-available"
                        ? "已检测到 .NET 8 Desktop Runtime，使用精简包。"
                        : "正在使用兼容的旧版扫描器包。";
                await report(new LauncherProgress
                {
                    Stage = "select",
                    Message = selectionMessage,
                    Version = manifest.ScannerVersion,
                    PackageId = package.Id,
                    PackageMode = package.Mode,
                    SelectionReason = selection.Reason,
                    TotalBytes = package.Size,
                    RequiredBytes = checked(package.Size + package.ExpandedSize + 100L * 1024 * 1024)
                }, token);

                if (forceRepair)
                {
                    SafeDelete(packagePath);
                    SafeDelete(packagePath + ".download");
                    if (Directory.Exists(installDir))
                    {
                        Directory.Delete(installDir, recursive: true);
                    }
                }

                if (File.Exists(entryPath) && (manifest.SchemaVersion >= 3 || File.Exists(packagePath)))
                {
                    try
                    {
                        if (manifest.SchemaVersion >= 3)
                        {
                            await HelperSecurity.VerifyInstalledRuntimeAsync(package, installDir, token);
                        }
                        else
                        {
                            await VerifyPackageSizeAsync(packagePath, package.Size);
                            await VerifySha256Async(packagePath, package.Sha256, token);
                            await HelperSecurity.VerifyInstalledRuntimeAsync(packagePath, installDir, package.Entry, token);
                        }
                        _entryPath = entryPath;
                        await report(PackageProgress("ready", "扫描器已是最新版本。", manifest.ScannerVersion, package), token);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await report(new LauncherProgress
                        {
                            Stage = "repair",
                            Message = $"扫描器完整性校验失败，正在自动修复：{ex.Message}",
                            Version = manifest.ScannerVersion,
                            PackageId = package.Id,
                            PackageMode = package.Mode
                        }, token);
                    }
                }

                Directory.CreateDirectory(installDir);
                var packageReady = false;
                if (File.Exists(packagePath))
                {
                    try
                    {
                        await VerifyPackageSizeAsync(packagePath, package.Size);
                        await VerifySha256Async(packagePath, package.Sha256, token);
                        packageReady = true;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        SafeDelete(packagePath);
                        await report(new LauncherProgress
                        {
                            Stage = "download",
                            Message = $"本地安装包校验失败，正在重新下载：{ex.Message}",
                            Version = manifest.ScannerVersion,
                            PackageId = package.Id,
                            PackageMode = package.Mode,
                            TotalBytes = package.Size
                        }, token);
                    }
                }

                if (!packageReady)
                {
                    HelperPlatform.EnsureDiskSpace(package, packageRoot);
                    await report(new LauncherProgress
                    {
                        Stage = "download",
                        Message = "正在下载 OCR 扫描器...",
                        Version = manifest.ScannerVersion,
                        PackageId = package.Id,
                        PackageMode = package.Mode,
                        TotalBytes = package.Size
                    }, token);
                    await DownloadAndVerifyPackageAsync(manifestUrl, manifest.ScannerVersion, package, packagePath, report, token);
                }

                await report(PackageProgress("checksum", "正在校验扫描器文件...", manifest.ScannerVersion, package), token);
                await VerifyPackageSizeAsync(packagePath, package.Size);
                await VerifySha256Async(packagePath, package.Sha256, token);

                var tempDir = HelperSecurity.ResolvePathWithinRoot(runtimeRoot, installRelative + ".tmp");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                Directory.CreateDirectory(tempDir);
                await report(PackageProgress("extract", "正在安装扫描器...", manifest.ScannerVersion, package), token);
                try
                {
                    ExtractZipSafe(packagePath, tempDir);
                    if (manifest.SchemaVersion >= 3)
                    {
                        await HelperSecurity.VerifyInstalledRuntimeAsync(package, tempDir, token);
                    }
                    else
                    {
                        await HelperSecurity.VerifyInstalledRuntimeAsync(packagePath, tempDir, package.Entry, token);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not HelperFailureException)
                {
                    throw new HelperFailureException(
                        ex is InvalidDataException ? "package_corrupt" : "install_failed",
                        "install",
                        ex is InvalidDataException ? "扫描器安装包内容损坏" : "扫描器安装失败",
                        ex.Message,
                        "请选择重新下载并修复；Helper 会清除临时文件后重新安装。",
                        retryable: true,
                        new Dictionary<string, string> { ["packageId"] = package.Id },
                        ex);
                }
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }

                Directory.Move(tempDir, installDir);
                if (manifest.SchemaVersion >= 3)
                {
                    SafeDelete(packagePath);
                }
                _entryPath = entryPath;
                await report(PackageProgress("ready", "扫描器准备完成。", manifest.ScannerVersion, package), token);
            }
            finally
            {
                _ensureGate.Release();
            }
        }

        public string CurrentEntryPath()
        {
            if (string.IsNullOrWhiteSpace(_entryPath) || !File.Exists(_entryPath))
            {
                throw new InvalidOperationException("Scanner runtime is not installed.");
            }

            return _entryPath;
        }

        public bool ShouldFallbackToSelfContained()
        {
            return _manifest?.SchemaVersion == 2
                && _selectedPackage?.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase) == true
                && _selectedPackage.Framework is not null
                && !HelperPlatform.HasRequiredFramework(_selectedPackage.Framework);
        }

        private Uri ResolveManifestUrl()
        {
            var baseUrl = Environment.GetEnvironmentVariable("ZZZ_SCANNER_DOWNLOAD_BASE");
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = ResolveDownloadBaseFromLaunchOrigin();
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                baseUrl = "http://localhost:8787";
            }

            var manifestUri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ManifestPath.TrimStart('/'));
            HelperSecurity.EnsureTrustedDownloadUri(manifestUri, "manifest");
            return manifestUri;
        }

        private static LauncherProgress PackageProgress(string stage, string message, string version, ScannerPackage package)
        {
            return new LauncherProgress
            {
                Stage = stage,
                Message = message,
                Version = version,
                PackageId = package.Id,
                PackageMode = package.Mode,
                TotalBytes = package.Size
            };
        }

        private string? ResolveDownloadBaseFromLaunchOrigin()
        {
            if (!IsAllowedOrigin(_launchOrigin))
            {
                return null;
            }

            return _launchOrigin;
        }

        private async Task<ScannerManifest?> DownloadManifestAsync(Uri url, CancellationToken token)
        {
            using var response = await _http.GetAsync(url, token);
            HelperSecurity.EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? url, "manifest redirect");
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync(stream, HelperJsonContext.Default.ScannerManifest, token);
        }

        private async Task DownloadFileAsync(
            Uri url,
            string destination,
            string version,
            ScannerPackage package,
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token)
        {
            var expectedSize = package.Size;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".download";

            var stopwatch = Stopwatch.StartNew();
            Exception? lastError = null;
            for (var attempt = 1; attempt <= PackageDownloadMaxAttempts; attempt++)
            {
                var existingBytes = File.Exists(temp) ? new FileInfo(temp).Length : 0L;
                if (expectedSize > 0 && existingBytes == expectedSize)
                {
                    await report(DownloadProgress(version, package, url, existingBytes, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                    MoveCompletedPackage(temp, destination);
                    return;
                }

                if (expectedSize > 0 && existingBytes > expectedSize)
                {
                    SafeDelete(temp);
                    existingBytes = 0;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existingBytes > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                    }

                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    HelperSecurity.EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? url, "package redirect");
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                        && expectedSize > 0
                        && existingBytes == expectedSize)
                    {
                        await report(DownloadProgress(version, package, url, existingBytes, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                        MoveCompletedPackage(temp, destination);
                        return;
                    }

                    if (existingBytes > 0 && response.StatusCode == HttpStatusCode.OK)
                    {
                        SafeDelete(temp);
                        existingBytes = 0;
                    }

                    response.EnsureSuccessStatusCode();
                    var responseLength = response.Content.Headers.ContentLength ?? 0;
                    var totalBytes = expectedSize > 0 ? expectedSize : existingBytes + responseLength;
                    await report(DownloadProgress(version, package, url, existingBytes, totalBytes, stopwatch, attempt, "正在下载 OCR 扫描器..."), token);

                    var downloaded = existingBytes;
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(
                        temp,
                        existingBytes > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 128 * 1024,
                        useAsync: true))
                    {
                        var buffer = new byte[128 * 1024];
                        var lastReport = Stopwatch.StartNew();
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
                            if (read <= 0)
                            {
                                break;
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                            downloaded += read;
                            if (lastReport.ElapsedMilliseconds >= DownloadProgressIntervalMs
                                || (totalBytes > 0 && downloaded >= totalBytes))
                            {
                                await report(DownloadProgress(version, package, url, downloaded, totalBytes, stopwatch, attempt, "正在下载 OCR 扫描器..."), token);
                                lastReport.Restart();
                            }
                        }

                        await output.FlushAsync(token);
                    }

                    await report(DownloadProgress(version, package, url, downloaded, totalBytes, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                    MoveCompletedPackage(temp, destination);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    var downloaded = File.Exists(temp) ? new FileInfo(temp).Length : 0L;
                    if (expectedSize > 0 && downloaded == expectedSize)
                    {
                        await report(DownloadProgress(version, package, url, downloaded, expectedSize, stopwatch, attempt, "OCR 扫描器下载完成。"), token);
                        MoveCompletedPackage(temp, destination);
                        return;
                    }

                    if (attempt >= PackageDownloadMaxAttempts)
                    {
                        break;
                    }

                    await report(DownloadProgress(
                        version,
                        package,
                        url,
                        downloaded,
                        expectedSize,
                        stopwatch,
                        attempt + 1,
                        $"下载连接中断，正在第 {attempt + 1}/{PackageDownloadMaxAttempts} 次重试..."), token);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, attempt * 2)), token);
                }
            }

            SafeDelete(temp);
            throw new IOException($"Scanner package download failed after {PackageDownloadMaxAttempts} attempts: {lastError?.Message}", lastError);
        }

        private static void MoveCompletedPackage(string temp, string destination)
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temp, destination);
        }

        private static LauncherProgress DownloadProgress(
            string version,
            ScannerPackage package,
            Uri url,
            long downloaded,
            long total,
            Stopwatch stopwatch,
            int attempt,
            string message)
        {
            var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
            double? percent = total > 0 ? Math.Clamp(downloaded * 100d / total, 0d, 100d) : null;
            return new LauncherProgress
            {
                Stage = "download",
                Message = message,
                Version = version,
                PackageId = package.Id,
                PackageMode = package.Mode,
                Url = url.ToString(),
                BytesDownloaded = Math.Max(0, downloaded),
                TotalBytes = total > 0 ? total : null,
                Percent = percent,
                BytesPerSecond = Math.Max(0, downloaded / seconds),
                Attempt = attempt,
                MaxAttempts = PackageDownloadMaxAttempts
            };
        }

        private async Task DownloadAndVerifyPackageAsync(
            Uri manifestUrl,
            string scannerVersion,
            ScannerPackage package,
            string packagePath,
            Func<LauncherProgress, CancellationToken, Task> report,
            CancellationToken token)
        {
            var urls = ResolvePackageUrls(manifestUrl, package).ToList();
            if (urls.Count == 0)
            {
                throw new InvalidOperationException("Scanner package URL is missing.");
            }

            var errors = new List<string>();
            foreach (var packageUrl in urls)
            {
                try
                {
                    await DownloadFileAsync(packageUrl, packagePath, scannerVersion, package, report, token);
                    await VerifyPackageSizeAsync(packagePath, package.Size);
                    await VerifySha256Async(packagePath, package.Sha256, token);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{packageUrl}: {ex.Message}");
                    SafeDelete(packagePath);
                    SafeDelete(packagePath + ".download");
                }
            }

            throw new HelperFailureException(
                "download_failed",
                "download",
                "扫描器下载失败",
                $"所有下载地址均失败：{string.Join(" | ", errors)}",
                "请检查网络后重试；Helper 会重新尝试所有备用地址。",
                retryable: true,
                new Dictionary<string, string> { ["packageId"] = package.Id });
        }

        private static IEnumerable<Uri> ResolvePackageUrls(Uri manifestUrl, ScannerPackage package)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawUrl in package.PackageUrls)
            {
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    continue;
                }

                var url = new Uri(manifestUrl, rawUrl.Trim());
                HelperSecurity.EnsureTrustedDownloadUri(url, "package");
                if (seen.Add(url.AbsoluteUri))
                {
                    yield return url;
                }
            }

        }

        private static Task VerifyPackageSizeAsync(string packagePath, long expectedSize)
        {
            if (expectedSize > 0)
            {
                var actualSize = new FileInfo(packagePath).Length;
                if (actualSize != expectedSize)
                {
                    throw new InvalidOperationException($"Scanner package size mismatch. Expected {expectedSize}, got {actualSize}.");
                }
            }

            return Task.CompletedTask;
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static async Task VerifySha256Async(string path, string expectedHex, CancellationToken token)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (!actual.Equals(expectedHex.Trim().ToLowerInvariant(), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Scanner package checksum mismatch.");
            }
        }

        private static void ExtractZipSafe(string packagePath, string destination)
        {
            var rootFull = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using var archive = ZipFile.OpenRead(packagePath);
            foreach (var entry in archive.Entries)
            {
                var full = HelperSecurity.ResolvePathWithinRoot(rootFull, entry.FullName);

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }
        }

        private static string RuntimeRootDirectory()
        {
            var path = Path.Combine(HelperStorageManager.DefaultDataRoot(), "runtime");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string PackageCacheDirectory()
        {
            var path = Path.Combine(HelperStorageManager.DefaultDataRoot(), "packages");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string LocalDataRoot()
        {
            var root = HelperStorageManager.DefaultDataRoot();
            Directory.CreateDirectory(root);
            return root;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_listener.IsListening)
            {
                _listener.Stop();
            }

            _listener.Close();
            _http.Dispose();
        }
    }

    private sealed class BrowserSession : IAsyncDisposable
    {
        private readonly HelperServer _server;
        private readonly System.Net.WebSockets.WebSocket _browser;
        private readonly SemaphoreSlim _browserSendGate = new(1, 1);
        private ManagedScannerProcess? _scanner;
        private readonly ScanActivityGate _scanActivity = new();

        public BrowserSession(HelperServer server, System.Net.WebSockets.WebSocket browser)
        {
            _server = server;
            _browser = browser;
        }

        public async Task RunAsync(CancellationToken token)
        {
            await SendAsync("hello", new HelperHello
            {
                Service = ServiceName,
                Version = HelperVersion,
                ProtocolVersion = ProtocolVersion,
                Scanner = _server.CurrentScannerState(),
                HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
            }, token);

            while (_browser.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var json = await ReceiveTextAsync(_browser, token);
                if (json is null)
                {
                    break;
                }

                HelperEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperEnvelope);
                }
                catch
                {
                    continue;
                }

                if (envelope is null)
                {
                    continue;
                }

                try
                {
                    await HandleEnvelopeAsync(envelope, token);
                }
                catch (Exception ex)
                {
                    _scanActivity.Finish();
                    await SendAsync("scan_error", HelperErrors.FromException(ex, "prepare"), CancellationToken.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_scanner is not null)
            {
                await _scanner.DisposeAsync();
                _scanner = null;
            }

            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    await _browser.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
            }
            catch
            {
            }

            _browser.Dispose();
            _browserSendGate.Dispose();
        }

        private async Task HandleEnvelopeAsync(HelperEnvelope envelope, CancellationToken token)
        {
            switch (envelope.Cmd)
            {
                case "ping":
                    await SendAsync("pong", new PongMessage { Time = DateTimeOffset.Now }, token);
                    break;

                case "ensure_scanner":
                    await EnsureScannerProcessAsync(token);
                    break;

                case "repair_scanner":
                    await EnsureScannerProcessAsync(token, forceRepair: true);
                    break;

                case "restart_scanner_elevated":
                    await EnsureScannerProcessAsync(token, elevated: true, forceRestart: true);
                    break;

                case "open_log_folder":
                    OpenLogFolder();
                    await SendAsync("launcher_progress", new LauncherProgress
                    {
                        Stage = "logs",
                        Message = "已打开 Helper 日志目录。"
                    }, token);
                    break;

                case "get_diagnostics":
                    await SendAsync("helper_diagnostics", new HelperDiagnosticsResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        HelperVersion = HelperVersion,
                        ProtocolVersion = ProtocolVersion,
                        LogDirectory = HelperLog.DirectoryPath,
                        Scanner = _server.CurrentScannerState(),
                        HelperUpdate = HelperInstallationManager.CurrentPendingUpdate(),
                    }, token);
                    break;

                case "confirm_helper_update":
                    var confirmRequestId = RequestId(envelope.Data);
                    try
                    {
                        var transactionId = StringProperty(envelope.Data, "transactionId");
                        var commit = HelperInstallationManager.ConfirmPendingUpdate(transactionId);
                        await SendAsync("helper_update_commit_result", new HelperUpdateCommitResponse
                        {
                            RequestId = confirmRequestId,
                            TransactionId = commit.TransactionId,
                            Committed = commit.Committed,
                            PreviousVersion = commit.PreviousVersion,
                        }, token);
                    }
                    catch (Exception ex)
                    {
                        var error = HelperErrors.FromException(ex, "helper_update");
                        error.Code = "helper_update_confirmation_failed";
                        error.Phase = "helper";
                        error.Title = "扫描助手更新确认失败";
                        error.Remedy = "旧版 Helper 会自动恢复；请等待网页重新连接后重试。";
                        error.Retryable = true;
                        error.Details["requestId"] = confirmRequestId;
                        await SendAsync("helper_update_error", error, CancellationToken.None);
                    }
                    break;

                case "get_storage_info":
                    await SendAsync("storage_info", new StorageInfoResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        Storage = _server.CurrentStorageInfo()
                    }, token);
                    break;

                case "cleanup_storage":
                    var cleanup = _server.CleanupStorage();
                    await SendAsync("storage_cleanup_result", new StorageCleanupResponse
                    {
                        RequestId = RequestId(envelope.Data),
                        Result = cleanup
                    }, token);
                    break;

                case "update_helper":
                    var updateRequestId = RequestId(envelope.Data);
                    try
                    {
                        var preparation = await HelperUpdateManager.PrepareAsync(
                            _server.ResolveHelperManifestUrl(),
                            HelperVersion,
                            async (progress, ct) =>
                            {
                                progress.RequestId = updateRequestId;
                                await SendAsync("helper_update_progress", progress, ct);
                            },
                            token);
                        await SendAsync("helper_update_result", new HelperUpdateResponse
                        {
                            RequestId = updateRequestId,
                            UpdateAvailable = preparation.UpdateAvailable,
                            CurrentVersion = preparation.CurrentVersion,
                            AvailableVersion = preparation.AvailableVersion,
                            Restarting = preparation.UpdateAvailable
                        }, token);
                        if (preparation.UpdateAvailable)
                        {
                            HelperInstallationManager.LaunchPreparedUpdate(preparation.ExecutablePath);
                            await Task.Delay(250, CancellationToken.None);
                            Environment.Exit(0);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        var source = HelperErrors.FromException(ex, "helper_update");
                        var sourceCode = source.Code;
                        source.Code = "helper_update_failed";
                        source.Phase = "helper";
                        source.Title = "扫描助手更新失败";
                        source.Remedy = "请检查网络后重试；仍然失败时请手动下载最新版 Helper。";
                        source.Retryable = true;
                        source.Actions =
                        [
                            new HelperErrorAction { Kind = "update_helper", Label = "重试自动更新" },
                            new HelperErrorAction { Kind = "download_helper", Label = "手动下载" },
                            new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                        ];
                        source.Details["requestId"] = updateRequestId;
                        source.Details["sourceCode"] = sourceCode;
                        await SendAsync("helper_update_error", source, CancellationToken.None);
                    }
                    break;

                case "scan_req":
                    _scanActivity.Start();
                    await EnsureScannerProcessAsync(token);
                    if (_scanner is not null)
                    {
                        var delivered = await _scanner.TrySendRawAsync(
                            JsonSerializer.Serialize(envelope, HelperJsonContext.Default.HelperEnvelope),
                            token);
                        if (!delivered)
                        {
                            await ScannerExitedAsync(_scanner.ExitCodeOrUnknown, CancellationToken.None);
                        }
                    }
                    break;

                case "scan_stop":
                    if (_scanner is not null)
                    {
                        var delivered = await _scanner.TrySendRawAsync(
                            JsonSerializer.Serialize(envelope, HelperJsonContext.Default.HelperEnvelope),
                            token);
                        if (!delivered)
                        {
                            await ScannerExitedAsync(_scanner.ExitCodeOrUnknown, CancellationToken.None);
                        }
                    }
                    else
                    {
                        await ScannerExitedAsync(-1, CancellationToken.None);
                    }
                    break;
            }
        }

        private async Task EnsureScannerProcessAsync(
            CancellationToken token,
            bool forceRepair = false,
            bool elevated = false,
            bool forceRestart = false)
        {
            if (!forceRestart && !forceRepair && _scanner is { IsConnected: true })
            {
                await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
                return;
            }

            if (_scanner is not null)
            {
                await _scanner.DisposeAsync();
                _scanner = null;
            }

            await SendAsync("launcher_progress", new LauncherProgress { Stage = "queue", Message = "正在准备扫描器任务..." }, token);
            await _server.EnsureScannerAsync(
                (progress, ct) => SendAsync("launcher_progress", progress, ct),
                token,
                forceRepair);

            var childToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            try
            {
                _scanner = await ManagedScannerProcess.StartAsync(
                    _server.CurrentEntryPath(),
                    childToken,
                    elevated,
                    ForwardScannerMessageAsync,
                    ScannerExitedAsync,
                    ScannerTransportFailedAsync,
                    token);
            }
            catch (HelperFailureException) when (_server.ShouldFallbackToSelfContained())
            {
                await SendAsync("launcher_progress", new LauncherProgress
                {
                    Stage = "fallback",
                    Message = ".NET 8 在启动前不可用，正在自动切换兼容包。",
                    SelectionReason = "desktop-runtime-disappeared"
                }, token);
                await _server.EnsureScannerAsync(
                    (progress, ct) => SendAsync("launcher_progress", progress, ct),
                    token,
                    forceRepair: false,
                    forceSelfContained: true);
                childToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                _scanner = await ManagedScannerProcess.StartAsync(
                    _server.CurrentEntryPath(),
                    childToken,
                    elevated,
                    ForwardScannerMessageAsync,
                    ScannerExitedAsync,
                    ScannerTransportFailedAsync,
                    token);
            }
            _server.MarkScannerActive();
            await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
            if (HelperInstallationManager.ConsumePostUpdateStoragePreservation())
            {
                HelperLog.Write("AUTOMATIC_CLEANUP_SKIPPED reason=helper-update-preservation");
            }
            else
            {
                try
                {
                    _server.CleanupStorage();
                }
                catch (Exception ex)
                {
                    HelperLog.Write($"AUTOMATIC_CLEANUP_FAILED error={ex.Message}");
                }
            }
        }

        private static string RequestId(JsonElement data)
        {
            return data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("requestId", out var requestId)
                ? requestId.GetString() ?? ""
                : "";
        }

        private static string StringProperty(JsonElement data, string name)
        {
            return data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(name, out var value)
                ? value.GetString() ?? ""
                : "";
        }

        private static void OpenLogFolder()
        {
            Directory.CreateDirectory(HelperLog.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{HelperLog.DirectoryPath}\"",
                UseShellExecute = true
            });
        }

        private async Task ForwardScannerMessageAsync(string json, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var cmd = root.TryGetProperty("cmd", out var lowerCmd)
                    ? lowerCmd.GetString()
                    : root.TryGetProperty("Cmd", out var upperCmd) ? upperCmd.GetString() : null;
                if (cmd is "scan_complete" or "scan_error")
                {
                    _scanActivity.Finish();
                }
            }
            catch
            {
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await SendBrowserBytesAsync(bytes, token);
        }

        private async Task ScannerExitedAsync(int exitCode, CancellationToken token)
        {
            if (!_scanActivity.Finish() || _browser.State != WebSocketState.Open)
            {
                return;
            }

            var unsignedCode = unchecked((uint)exitCode);
            var exception = new InvalidOperationException($"Scanner exited during an active scan with code 0x{unsignedCode:X8}.");
            var diagnosticId = HelperLog.RecordException("scanner_process_exited", "scan", exception);
            await SendAsync("scan_error", new HelperErrorMessage
            {
                Code = "scanner_process_exited",
                Phase = "scan",
                Title = "Scanner 进程意外退出",
                Message = $"扫描过程中 Scanner 子进程已退出（退出码 0x{unsignedCode:X8}）。",
                Remedy = "请重新连接并扫描；持续发生时请打开日志并提供退出码。",
                Retryable = true,
                Actions =
                {
                    new HelperErrorAction { Kind = "retry_connect", Label = "重新连接" },
                    new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                },
                DiagnosticId = diagnosticId,
                Details = new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" }
            }, token);
        }

        private async Task ScannerTransportFailedAsync(Exception exception, CancellationToken token)
        {
            if (!_scanActivity.Finish() || _browser.State != WebSocketState.Open)
            {
                return;
            }

            var messageTooLarge = exception is ScannerMessageTooLargeException;
            var code = messageTooLarge ? "scanner_message_too_large" : "scanner_transport_failed";
            var diagnosticId = HelperLog.RecordException(code, "scan", exception);
            await SendAsync("scan_error", new HelperErrorMessage
            {
                Code = code,
                Phase = "scan",
                Title = messageTooLarge ? "扫描结果消息过大" : "Scanner 结果传输失败",
                Message = messageTooLarge
                    ? $"Scanner 返回的单条消息超过 {MaxWebSocketMessageBytes / 1024 / 1024} MiB 安全上限。"
                    : "Helper 读取或转发 Scanner 结果时发生异常。",
                Remedy = "已识别结果会由网页安全保留；请更新 Scanner 后重试，持续发生时请打开日志。",
                Retryable = true,
                Actions =
                {
                    new HelperErrorAction { Kind = "retry_scan", Label = "重新扫描" },
                    new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" }
                },
                DiagnosticId = diagnosticId
            }, token);
        }

        private async Task SendAsync(string cmd, object? data, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), HelperJsonContext.Default.HelperEnvelope);
            var bytes = Encoding.UTF8.GetBytes(json);
            await SendBrowserBytesAsync(bytes, token);
        }

        private async Task SendBrowserBytesAsync(byte[] bytes, CancellationToken token)
        {
            await _browserSendGate.WaitAsync(token);
            try
            {
                if (_browser.State == WebSocketState.Open)
                {
                    await _browser.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                }
            }
            finally
            {
                _browserSendGate.Release();
            }
        }
    }

    internal sealed class ScanActivityGate
    {
        private int _active;

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public void Start()
        {
            Volatile.Write(ref _active, 1);
        }

        public bool Finish()
        {
            return Interlocked.Exchange(ref _active, 0) != 0;
        }
    }

    internal static async Task RunScannerSupervisorAsync(
        Func<CancellationToken, Task> pumpMessages,
        Func<CancellationToken, Task<int>> waitForExit,
        Func<CancellationToken, Task<int>> terminateAndWait,
        Func<bool> expectedShutdown,
        Action stopMessagePump,
        Func<int, CancellationToken, Task> onExited,
        Func<Exception, CancellationToken, Task> onPumpFailed,
        Action<Exception> onPumpFault,
        CancellationToken token)
    {
        using var supervisorCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pumpTask = pumpMessages(supervisorCts.Token);
        var exitTask = waitForExit(supervisorCts.Token);
        var completed = await Task.WhenAny(pumpTask, exitTask);

        if (token.IsCancellationRequested || expectedShutdown())
        {
            supervisorCts.Cancel();
            stopMessagePump();
            await ObserveAsync(pumpTask, null);
            await ObserveAsync(exitTask, null);
            return;
        }

        int exitCode;
        Exception? pumpException = null;
        if (completed == exitTask)
        {
            try
            {
                exitCode = await exitTask;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || expectedShutdown())
            {
                return;
            }
            catch (Exception ex)
            {
                onPumpFault(ex);
                exitCode = await TerminateSafelyAsync(terminateAndWait, onPumpFault);
            }

            supervisorCts.Cancel();
            stopMessagePump();
            if (!token.IsCancellationRequested && !expectedShutdown())
            {
                await onExited(exitCode, CancellationToken.None);
            }

            await ObserveAsync(pumpTask, onPumpFault);
            return;
        }
        else
        {
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                pumpException = ex;
                onPumpFault(ex);
            }
            if (token.IsCancellationRequested || expectedShutdown())
            {
                supervisorCts.Cancel();
                await ObserveAsync(exitTask, null);
                return;
            }

            supervisorCts.Cancel();
            stopMessagePump();
            await ObserveAsync(exitTask, null);
            exitCode = await TerminateSafelyAsync(terminateAndWait, onPumpFault);
        }

        if (!token.IsCancellationRequested && !expectedShutdown())
        {
            if (pumpException is not null)
            {
                await onPumpFailed(pumpException, CancellationToken.None);
            }
            else
            {
                await onExited(exitCode, CancellationToken.None);
            }
        }

        static async Task<int> TerminateSafelyAsync(
            Func<CancellationToken, Task<int>> terminate,
            Action<Exception> report)
        {
            try
            {
                return await terminate(CancellationToken.None);
            }
            catch (Exception ex)
            {
                report(ex);
                return -1;
            }
        }

        static async Task ObserveAsync(Task task, Action<Exception>? report)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                report?.Invoke(ex);
            }
        }
    }

    private sealed class ManagedScannerProcess : IAsyncDisposable
    {
        private readonly ClientWebSocket _scannerWs;
        private readonly Process _process;
        private readonly CancellationTokenSource _lifetimeCts;
        private Task _supervisorTask = Task.CompletedTask;
        private int _expectedShutdown;
        private int _disposed;

        private ManagedScannerProcess(ClientWebSocket scannerWs, Process process, CancellationToken token)
        {
            _scannerWs = scannerWs;
            _process = process;
            _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        }

        public bool IsConnected => _scannerWs.State == WebSocketState.Open && !_process.HasExited;

        public int ExitCodeOrUnknown
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : -1;
                }
                catch
                {
                    return -1;
                }
            }
        }

        public static async Task<ManagedScannerProcess> StartAsync(
            string entryPath,
            string childToken,
            bool elevated,
            Func<string, CancellationToken, Task> forward,
            Func<int, CancellationToken, Task> onExited,
            Func<Exception, CancellationToken, Task> onPumpFailed,
            CancellationToken token)
        {
            var port = ReserveTcpPort();
            Process process;
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = entryPath,
                    Arguments = $"--ws-child {port} --child-token {childToken} --no-browser --output-root \"{Path.Combine(HelperStorageManager.DefaultDataRoot(), "outputs")}\"",
                    WorkingDirectory = Path.GetDirectoryName(entryPath),
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                if (elevated)
                {
                    startInfo.Verb = "runas";
                }

                process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Cannot start scanner process.");
                HelperLog.Write($"SCANNER_PROCESS_STARTED pid={process.Id} entry={entryPath} shell={startInfo.UseShellExecute} elevated={elevated}");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new HelperFailureException(
                    "uac_cancelled",
                    "launch",
                    "已取消管理员授权",
                    "你取消了 Windows 管理员权限确认，扫描器没有启动。",
                    "如游戏以管理员身份运行，请重新选择管理员启动并确认 UAC。",
                    retryable: true,
                    innerException: ex);
            }
            catch (Exception ex) when (ex is not HelperFailureException)
            {
                throw new HelperFailureException(
                    "child_start_failed",
                    "launch",
                    "无法启动 OCR 扫描器",
                    ex.Message,
                    "请选择重新下载并修复；如果问题持续，请打开日志。",
                    retryable: true,
                    innerException: ex);
            }

            var ws = new ClientWebSocket();
            try
            {
                var uri = new Uri($"ws://127.0.0.1:{port}/ws/{childToken}");
                Exception? last = null;
                for (var attempt = 0; attempt < 80; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (process.HasExited)
                    {
                        throw ChildExited(process.ExitCode);
                    }

                    try
                    {
                        await ws.ConnectAsync(uri, token);
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        await Task.Delay(250, token);
                    }
                }

                if (ws.State != WebSocketState.Open)
                {
                    throw new HelperFailureException(
                        "child_handshake_timeout",
                        "launch",
                        "扫描器启动超时",
                        $"扫描器进程已启动，但 20 秒内没有完成本地连接：{last?.Message}",
                        "请重试；如果问题持续，请打开日志查看启动阶段错误。",
                        retryable: true);
                }

                var managed = new ManagedScannerProcess(ws, process, token);
                managed.StartSupervisor(forward, onExited, onPumpFailed);
                return managed;
            }
            catch
            {
                ws.Dispose();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                }

                process.Dispose();
                throw;
            }
        }

        public async Task<bool> TrySendRawAsync(string json, CancellationToken token)
        {
            if (!IsConnected)
            {
                return false;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                await _scannerWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
                return true;
            }
            catch (Exception ex) when (
                ex is WebSocketException or InvalidOperationException or ObjectDisposedException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                try { _scannerWs.Abort(); } catch { }
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Volatile.Write(ref _expectedShutdown, 1);
            _lifetimeCts.Cancel();
            try { _scannerWs.Abort(); } catch { }
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }

            try { await _supervisorTask; } catch { }
            _scannerWs.Dispose();
            _process.Dispose();
            _lifetimeCts.Dispose();
        }

        private void StartSupervisor(
            Func<string, CancellationToken, Task> forward,
            Func<int, CancellationToken, Task> onExited,
            Func<Exception, CancellationToken, Task> onPumpFailed)
        {
            _supervisorTask = RunScannerSupervisorAsync(
                PumpMessagesAsync,
                cancellation => WaitForExitCodeAsync(_process, cancellation),
                cancellation => TerminateAndWaitAsync(_process, cancellation),
                () => Volatile.Read(ref _expectedShutdown) != 0,
                () =>
                {
                    try { _scannerWs.Abort(); } catch { }
                },
                onExited,
                onPumpFailed,
                ex => HelperLog.RecordException("scanner_process_pump", "scan", ex),
                _lifetimeCts.Token);

            async Task PumpMessagesAsync(CancellationToken cancellation)
            {
                while (_scannerWs.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    var message = await ReceiveTextAsync(_scannerWs, cancellation);
                    if (message is null)
                    {
                        break;
                    }

                    await forward(message, cancellation);
                }
            }
        }

        private static async Task<int> WaitForExitCodeAsync(Process process, CancellationToken token)
        {
            var processId = process.Id;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!IsProcessAlive(processId))
                {
                    return -1;
                }

                await Task.Delay(100, token);
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using var probe = Process.GetProcessById(processId);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static async Task<int> TerminateAndWaitAsync(Process process, CancellationToken token)
        {
            if (!process.HasExited)
            {
                try
                {
                    using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    graceCts.CancelAfter(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(graceCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            return process.ExitCode;
        }

        private static HelperFailureException ChildExited(int exitCode)
        {
            var unsignedCode = unchecked((uint)exitCode);
            if (unsignedCode == 0xC0000135)
            {
                return new HelperFailureException(
                    "native_dependency_missing",
                    "launch",
                    "扫描器缺少运行组件",
                    $"扫描器启动时找不到原生 DLL（退出码 0x{unsignedCode:X8}）。",
                    "请选择重新下载并修复；VC 运行组件应已随包提供。",
                    retryable: true,
                    new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" });
            }

            return new HelperFailureException(
                "child_exited",
                "launch",
                "扫描器启动后立即退出",
                $"扫描器尚未连接就退出，退出码为 0x{unsignedCode:X8}。",
                "请选择重新下载并修复；如果问题持续，请打开日志并提供诊断编号。",
                retryable: true,
                new Dictionary<string, string> { ["exitCode"] = $"0x{unsignedCode:X8}" });
        }

        private static int ReserveTcpPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        return origin.Equals("http://localhost:8787", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("http://127.0.0.1:8787", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zzzcaculator.top", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zzzcaculator.top:8443", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://www.zzzcaculator.top", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://www.zzzcaculator.top:8443", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://zztisolation.github.io", StringComparison.OrdinalIgnoreCase)
            || origin.Equals("https://jahooyoung.github.io", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCorsHeaders(HttpListenerResponse response, string? origin)
    {
        if (IsCorsReadableOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    internal static bool IsCorsReadableOrigin(string? origin)
    {
        return Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https";
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, ResolveJsonTypeInfo(payload.GetType())));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    private static async Task SendTextAsync(HttpListenerResponse response, int statusCode, string text, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    private static async Task<string?> ReceiveTextAsync(System.Net.WebSockets.WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        await using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(
                        result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription,
                        CancellationToken.None);
                }

                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("Helper WebSocket accepts text messages only.");
            }

            EnsureScannerMessageSize(stream.Length, result.Count);

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    internal static void EnsureScannerMessageSize(long currentLength, int incomingLength)
    {
        if (currentLength < 0 || incomingLength < 0 || currentLength + incomingLength > MaxWebSocketMessageBytes)
        {
            throw new ScannerMessageTooLargeException(MaxWebSocketMessageBytes);
        }
    }

    internal sealed class ScannerMessageTooLargeException(int maximumBytes)
        : IOException($"Helper WebSocket message exceeds {maximumBytes} bytes.");

    private sealed class HelperEnvelope
    {
        public string Cmd { get; set; } = "";
        public JsonElement Data { get; set; }

        public HelperEnvelope()
        {
        }

        public HelperEnvelope(string cmd, object? data)
        {
            Cmd = cmd;
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, ResolveJsonTypeInfo(data.GetType()));
        }

    }

    private sealed class TokenRequest
    {
        public string Origin { get; set; } = "";
    }

    private sealed class TokenResponse
    {
        public string Token { get; set; } = "";
    }

    private sealed class HelperInfoResponse
    {
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class HelperHello
    {
        public string Service { get; set; } = "";
        public string Version { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class ScannerState
    {
        public string? Version { get; set; }
        public bool Installed { get; set; }
        public string? Entry { get; set; }
        public string? PackageId { get; set; }
        public string? PackageMode { get; set; }
        public bool? DesktopRuntimeAvailable { get; set; }
    }

    private sealed class LauncherProgress
    {
        public string Stage { get; set; } = "";
        public string Message { get; set; } = "";
        public string? Version { get; set; }
        public string? Url { get; set; }
        public long? BytesDownloaded { get; set; }
        public long? TotalBytes { get; set; }
        public double? Percent { get; set; }
        public double? BytesPerSecond { get; set; }
        public int? Attempt { get; set; }
        public int? MaxAttempts { get; set; }
        public string? PackageId { get; set; }
        public string? PackageMode { get; set; }
        public string? SelectionReason { get; set; }
        public long? RequiredBytes { get; set; }
    }

    private sealed class HelperDiagnosticsResponse
    {
        public string RequestId { get; set; } = "";
        public string HelperVersion { get; set; } = "";
        public int ProtocolVersion { get; set; }
        public string LogDirectory { get; set; } = "";
        public ScannerState Scanner { get; set; } = new();
        public HelperUpdateTransactionInfo? HelperUpdate { get; set; }
    }

    private sealed class HelperUpdateCommitResponse
    {
        public string RequestId { get; set; } = "";
        public string TransactionId { get; set; } = "";
        public bool Committed { get; set; }
        public string PreviousVersion { get; set; } = "";
    }

    private sealed class StorageInfoResponse
    {
        public string RequestId { get; set; } = "";
        public HelperStorageSnapshot Storage { get; set; } = new();
    }

    private sealed class StorageCleanupResponse
    {
        public string RequestId { get; set; } = "";
        public HelperStorageCleanupResult Result { get; set; } = new();
    }

    private sealed class PongMessage
    {
        public DateTimeOffset Time { get; set; }
    }

    private static JsonTypeInfo ResolveJsonTypeInfo(Type type)
    {
        return HelperJsonContext.Default.GetTypeInfo(type)
            ?? throw new InvalidOperationException($"JSON type is not registered: {type.FullName}");
    }

    [JsonSerializable(typeof(HelperEnvelope))]
    [JsonSerializable(typeof(TokenRequest))]
    [JsonSerializable(typeof(TokenResponse))]
    [JsonSerializable(typeof(ScannerManifest))]
    [JsonSerializable(typeof(HelperInfoResponse))]
    [JsonSerializable(typeof(HelperHello))]
    [JsonSerializable(typeof(ScannerState))]
    [JsonSerializable(typeof(LauncherProgress))]
    [JsonSerializable(typeof(HelperErrorMessage))]
    [JsonSerializable(typeof(HelperErrorAction))]
    [JsonSerializable(typeof(HelperDiagnosticsResponse))]
    [JsonSerializable(typeof(HelperStorageSnapshot))]
    [JsonSerializable(typeof(HelperStorageCleanupResult))]
    [JsonSerializable(typeof(StorageInfoResponse))]
    [JsonSerializable(typeof(StorageCleanupResponse))]
    [JsonSerializable(typeof(HelperUpdateProgress))]
    [JsonSerializable(typeof(HelperUpdateResponse))]
    [JsonSerializable(typeof(HelperUpdateTransactionInfo))]
    [JsonSerializable(typeof(HelperUpdateCommitResponse))]
    [JsonSerializable(typeof(PongMessage))]
    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false)]
    private sealed partial class HelperJsonContext : JsonSerializerContext
    {
    }
}
