using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ZZZScannerHelper;

internal static class Program
{
    private const string ServiceName = "zzz-scanner-helper";
    private const string HelperVersion = "1.0.0";
    private const int ProtocolVersion = 1;
    private const int HelperPort = 22355;
    private const string ProtocolName = "zzz-scanner";
    private const string ManifestPath = "/downloads/zzz-scanner/manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static async Task<int> Main(string[] args)
    {
        var launchOrigin = TryReadLaunchOrigin(args);
        RegisterProtocol();

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

        using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}");
        root?.SetValue("", "URL:ZZZ Scanner Protocol");
        root?.SetValue("URL Protocol", "");
        using var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProtocolName}\shell\open\command");
        command?.SetValue("", $"\"{exe}\" \"%1\"");
    }

    private sealed class HelperServer : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _ensureGate = new(1, 1);
        private ScannerManifest? _manifest;
        private string? _entryPath;
        private string? _launchOrigin;
        private bool _disposed;

        public HelperServer(string? launchOrigin)
        {
            if (IsAllowedOrigin(launchOrigin))
            {
                _launchOrigin = launchOrigin;
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            _listener.Prefixes.Add($"http://127.0.0.1:{HelperPort}/");
            _listener.Start();
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
                    origin = JsonSerializer.Deserialize<TokenRequest>(body, JsonOptions)?.Origin;
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
            _tokens[tokenValue] = DateTimeOffset.Now.AddMinutes(5);
            await SendJsonAsync(context.Response, 200, new { token = tokenValue }, token);
        }

        private async Task HandleWebSocketAsync(HttpListenerContext context, CancellationToken token)
        {
            var tokenValue = context.Request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (string.IsNullOrWhiteSpace(tokenValue) || !_tokens.TryGetValue(tokenValue, out var expires) || expires < DateTimeOffset.Now)
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            _tokens.Remove(tokenValue);
            var wsContext = await context.AcceptWebSocketAsync(null);
            var session = new BrowserSession(this, wsContext.WebSocket);
            await session.RunAsync(token);
        }

        private object HelperInfo()
        {
            return new
            {
                service = ServiceName,
                version = HelperVersion,
                protocolVersion = ProtocolVersion,
                scanner = CurrentScannerState()
            };
        }

        public object CurrentScannerState()
        {
            return new
            {
                version = _manifest?.ScannerVersion,
                installed = !string.IsNullOrWhiteSpace(_entryPath) && File.Exists(_entryPath),
                entry = _entryPath
            };
        }

        public async IAsyncEnumerable<object> EnsureScannerAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            await _ensureGate.WaitAsync(token);
            try
            {
                yield return new { stage = "manifest", message = "正在获取扫描器版本清单..." };
                var manifestUrl = ResolveManifestUrl();
                var manifest = await DownloadJsonAsync<ScannerManifest>(manifestUrl, token)
                    ?? throw new InvalidOperationException("Scanner manifest is empty.");
                _manifest = manifest;

                var installDir = RuntimeDirectory(manifest.ScannerVersion);
                var entryPath = Path.Combine(installDir, manifest.Entry);
                if (File.Exists(entryPath))
                {
                    _entryPath = entryPath;
                    yield return new { stage = "ready", message = "扫描器已是最新版本。", version = manifest.ScannerVersion };
                    yield break;
                }

                Directory.CreateDirectory(installDir);
                var packagePath = Path.Combine(PackageCacheDirectory(), $"scanner-{manifest.ScannerVersion}.zip");

                yield return new { stage = "download", message = "正在下载 OCR 扫描器...", version = manifest.ScannerVersion };
                await DownloadAndVerifyPackageAsync(manifestUrl, manifest, packagePath, token);

                yield return new { stage = "checksum", message = "正在校验扫描器文件...", version = manifest.ScannerVersion };
                await VerifyPackageSizeAsync(packagePath, manifest.Size);
                await VerifySha256Async(packagePath, manifest.Sha256, token);

                var tempDir = installDir + ".tmp";
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }

                Directory.CreateDirectory(tempDir);
                yield return new { stage = "extract", message = "正在安装扫描器...", version = manifest.ScannerVersion };
                ExtractZipSafe(packagePath, tempDir);
                if (Directory.Exists(installDir))
                {
                    Directory.Delete(installDir, recursive: true);
                }

                Directory.Move(tempDir, installDir);
                _entryPath = entryPath;
                yield return new { stage = "ready", message = "扫描器准备完成。", version = manifest.ScannerVersion };
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

            return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), ManifestPath.TrimStart('/'));
        }

        private string? ResolveDownloadBaseFromLaunchOrigin()
        {
            if (!IsAllowedOrigin(_launchOrigin))
            {
                return null;
            }

            if (Uri.TryCreate(_launchOrigin, UriKind.Absolute, out var origin)
                && origin.Port == 8443
                && (origin.Host.Equals("zzzcaculator.top", StringComparison.OrdinalIgnoreCase)
                    || origin.Host.Equals("www.zzzcaculator.top", StringComparison.OrdinalIgnoreCase)))
            {
                // Temporary pre-ICP fallback: the HTTPS page is valid for browser-local
                // access, while the hashed OCR package is fetched from the IP endpoint.
                return "http://121.199.21.10";
            }

            return _launchOrigin;
        }

        private async Task<T?> DownloadJsonAsync<T>(Uri url, CancellationToken token)
        {
            using var response = await _http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, token);
        }

        private async Task DownloadFileAsync(Uri url, string destination, CancellationToken token)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var temp = destination + ".download";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = File.Create(temp))
            {
                await input.CopyToAsync(output, token);
            }

            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(temp, destination);
        }

        private async Task DownloadAndVerifyPackageAsync(Uri manifestUrl, ScannerManifest manifest, string packagePath, CancellationToken token)
        {
            var urls = ResolvePackageUrls(manifestUrl, manifest).ToList();
            if (urls.Count == 0)
            {
                throw new InvalidOperationException("Scanner package URL is missing.");
            }

            var errors = new List<string>();
            foreach (var packageUrl in urls)
            {
                try
                {
                    await DownloadFileAsync(packageUrl, packagePath, token);
                    await VerifyPackageSizeAsync(packagePath, manifest.Size);
                    await VerifySha256Async(packagePath, manifest.Sha256, token);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{packageUrl}: {ex.Message}");
                    SafeDelete(packagePath);
                    SafeDelete(packagePath + ".download");
                }
            }

            throw new InvalidOperationException($"Scanner package download failed. {string.Join(" | ", errors)}");
        }

        private static IEnumerable<Uri> ResolvePackageUrls(Uri manifestUrl, ScannerManifest manifest)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawUrl in manifest.PackageUrls ?? [])
            {
                if (string.IsNullOrWhiteSpace(rawUrl))
                {
                    continue;
                }

                var url = new Uri(manifestUrl, rawUrl.Trim());
                if (seen.Add(url.AbsoluteUri))
                {
                    yield return url;
                }
            }

            if (!string.IsNullOrWhiteSpace(manifest.PackageUrl))
            {
                var url = new Uri(manifestUrl, manifest.PackageUrl);
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
                var full = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Scanner package contains an invalid path.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(full);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                entry.ExtractToFile(full, overwrite: true);
            }
        }

        private static string RuntimeDirectory(string version)
        {
            return Path.Combine(LocalDataRoot(), "runtime", version);
        }

        private static string PackageCacheDirectory()
        {
            var path = Path.Combine(LocalDataRoot(), "packages");
            Directory.CreateDirectory(path);
            return path;
        }

        private static string LocalDataRoot()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZScannerNext");
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

    private sealed class BrowserSession
    {
        private readonly HelperServer _server;
        private readonly System.Net.WebSockets.WebSocket _browser;
        private ManagedScannerProcess? _scanner;

        public BrowserSession(HelperServer server, System.Net.WebSockets.WebSocket browser)
        {
            _server = server;
            _browser = browser;
        }

        public async Task RunAsync(CancellationToken token)
        {
            await SendAsync("hello", new
            {
                service = ServiceName,
                version = HelperVersion,
                protocolVersion = ProtocolVersion,
                scanner = _server.CurrentScannerState()
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
                    envelope = JsonSerializer.Deserialize<HelperEnvelope>(json, JsonOptions);
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
                    await SendAsync("scan_error", new { message = ex.Message }, CancellationToken.None);
                }
            }
        }

        private async Task HandleEnvelopeAsync(HelperEnvelope envelope, CancellationToken token)
        {
            switch (envelope.Cmd)
            {
                case "ping":
                    await SendAsync("pong", new { time = DateTimeOffset.Now }, token);
                    break;

                case "ensure_scanner":
                    await EnsureScannerProcessAsync(token);
                    break;

                case "scan_req":
                    await EnsureScannerProcessAsync(token);
                    if (_scanner is not null)
                    {
                        await _scanner.SendRawAsync(JsonSerializer.Serialize(envelope, JsonOptions), token);
                    }
                    break;

                case "scan_stop":
                    if (_scanner is not null)
                    {
                        await _scanner.SendRawAsync(JsonSerializer.Serialize(envelope, JsonOptions), token);
                    }
                    break;
            }
        }

        private async Task EnsureScannerProcessAsync(CancellationToken token)
        {
            if (_scanner is { IsConnected: true })
            {
                await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
                return;
            }

            await SendAsync("launcher_progress", new { stage = "queue", message = "正在准备扫描器任务..." }, token);
            await foreach (var progress in _server.EnsureScannerAsync(token))
            {
                await SendAsync("launcher_progress", progress, token);
            }

            var childToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            _scanner = await ManagedScannerProcess.StartAsync(_server.CurrentEntryPath(), childToken, ForwardScannerMessageAsync, token);
            await SendAsync("scanner_ready", _server.CurrentScannerState(), token);
        }

        private async Task ForwardScannerMessageAsync(string json, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await _browser.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        private async Task SendAsync(string cmd, object? data, CancellationToken token)
        {
            if (_browser.State != WebSocketState.Open)
            {
                return;
            }

            var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _browser.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }
    }

    private sealed class ManagedScannerProcess : IAsyncDisposable
    {
        private readonly ClientWebSocket _scannerWs;
        private readonly Process _process;
        private readonly Task _pumpTask;

        private ManagedScannerProcess(ClientWebSocket scannerWs, Process process, Task pumpTask)
        {
            _scannerWs = scannerWs;
            _process = process;
            _pumpTask = pumpTask;
        }

        public bool IsConnected => _scannerWs.State == WebSocketState.Open && !_process.HasExited;

        public static async Task<ManagedScannerProcess> StartAsync(
            string entryPath,
            string childToken,
            Func<string, CancellationToken, Task> forward,
            CancellationToken token)
        {
            var port = ReserveTcpPort();
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = entryPath,
                Arguments = $"--ws-child {port} --child-token {childToken} --no-browser",
                WorkingDirectory = Path.GetDirectoryName(entryPath),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }) ?? throw new InvalidOperationException("Cannot start scanner process.");

            var ws = new ClientWebSocket();
            var uri = new Uri($"ws://127.0.0.1:{port}/ws/{childToken}");
            Exception? last = null;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                token.ThrowIfCancellationRequested();
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
                throw new InvalidOperationException($"Cannot connect to scanner child process: {last?.Message}");
            }

            var pump = Task.Run(async () =>
            {
                while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    var msg = await ReceiveTextAsync(ws, token);
                    if (msg is null)
                    {
                        break;
                    }

                    await forward(msg, token);
                }
            }, token);

            return new ManagedScannerProcess(ws, process, pump);
        }

        public async Task SendRawAsync(string json, CancellationToken token)
        {
            if (_scannerWs.State != WebSocketState.Open)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await _scannerWs.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_scannerWs.State == WebSocketState.Open)
                {
                    await _scannerWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
                }
            }
            catch
            {
            }

            _scannerWs.Dispose();
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

            try { await _pumpTask; } catch { }
            _process.Dispose();
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

    private static bool IsAllowedOrigin(string? origin)
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
        if (IsAllowedOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
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
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

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
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, JsonOptions);
        }
    }

    private sealed class TokenRequest
    {
        public string Origin { get; set; } = "";
    }

    private sealed class ScannerManifest
    {
        public int SchemaVersion { get; set; }
        public string LauncherMinVersion { get; set; } = "";
        public string ScannerVersion { get; set; } = "";
        public string PackageUrl { get; set; } = "";
        public List<string> PackageUrls { get; set; } = [];
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string Entry { get; set; } = "ZZZ-Scanner.Next.exe";
    }
}
