using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZScannerHelper;

internal static partial class HelperUpdateManager
{
    public static async Task<HelperUpdatePreparation> PrepareAsync(
        Uri manifestUri,
        string currentVersion,
        Func<HelperUpdateProgress, CancellationToken, Task> report,
        CancellationToken token)
    {
        HelperSecurity.EnsureTrustedDownloadUri(manifestUri, "Helper update manifest");
        await report(new HelperUpdateProgress { Stage = "manifest", Message = "正在检查扫描助手更新..." }, token);

        using var http = new HttpClient();
        using var manifestResponse = await http.GetAsync(manifestUri, token);
        HelperSecurity.EnsureTrustedDownloadUri(
            manifestResponse.RequestMessage?.RequestUri ?? manifestUri,
            "Helper update manifest redirect");
        manifestResponse.EnsureSuccessStatusCode();
        var manifest = await JsonSerializer.DeserializeAsync(
            await manifestResponse.Content.ReadAsStreamAsync(token),
            HelperUpdateJsonContext.Default.HelperUpdateManifest,
            token) ?? throw new InvalidDataException("Helper update manifest is empty.");
        ValidateManifest(manifest);

        var current = Version.Parse(currentVersion);
        var available = Version.Parse(manifest.Version);
        if (available <= current)
        {
            return new HelperUpdatePreparation
            {
                UpdateAvailable = false,
                CurrentVersion = currentVersion,
                AvailableVersion = manifest.Version,
            };
        }

        var storage = new HelperStorageManager();
        var stagingRoot = Path.Combine(storage.HelperRoot, ".staging");
        Directory.CreateDirectory(stagingRoot);
        foreach (var stale in Directory.EnumerateFiles(stagingRoot))
        {
            try { File.Delete(stale); } catch { }
        }
        var downloadPath = Path.Combine(stagingRoot, $"helper-{manifest.Version}.exe.download");
        var executablePath = Path.Combine(stagingRoot, $"helper-{manifest.Version}.exe");

        Exception? lastError = null;
        foreach (var value in manifest.PackageUrls)
        {
            var packageUri = new Uri(manifestUri, value);
            HelperSecurity.EnsureTrustedDownloadUri(packageUri, "Helper update package");
            try
            {
                await DownloadAsync(http, packageUri, downloadPath, manifest.Size, manifest.Version, report, token);
                await VerifyAsync(downloadPath, manifest.Size, manifest.Sha256, token);
                if (File.Exists(executablePath))
                {
                    File.Delete(executablePath);
                }
                File.Move(downloadPath, executablePath);
                await report(new HelperUpdateProgress
                {
                    Stage = "ready",
                    Message = $"扫描助手 {manifest.Version} 已校验，正在重启...",
                    Version = manifest.Version,
                    TotalBytes = manifest.Size,
                    BytesDownloaded = manifest.Size,
                    Percent = 100,
                }, token);
                return new HelperUpdatePreparation
                {
                    UpdateAvailable = true,
                    CurrentVersion = currentVersion,
                    AvailableVersion = manifest.Version,
                    ExecutablePath = executablePath,
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                try { File.Delete(downloadPath); } catch { }
            }
        }

        throw new IOException($"Helper update download failed: {lastError?.Message}", lastError);
    }

    private static async Task DownloadAsync(
        HttpClient http,
        Uri uri,
        string destination,
        long expectedSize,
        string version,
        Func<HelperUpdateProgress, CancellationToken, Task> report,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        HelperSecurity.EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? uri, "Helper update redirect");
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        long downloaded = 0;
        var lastReport = Stopwatch.StartNew();
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0)
            {
                break;
            }
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            downloaded += read;
            if (lastReport.ElapsedMilliseconds >= 300 || downloaded == expectedSize)
            {
                await report(new HelperUpdateProgress
                {
                    Stage = "download",
                    Message = "正在下载扫描助手更新...",
                    Version = version,
                    BytesDownloaded = downloaded,
                    TotalBytes = expectedSize,
                    Percent = expectedSize > 0 ? Math.Clamp(downloaded * 100d / expectedSize, 0, 100) : null,
                }, token);
                lastReport.Restart();
            }
        }
        await output.FlushAsync(token);
    }

    private static async Task VerifyAsync(string path, long expectedSize, string expectedSha256, CancellationToken token)
    {
        if (new FileInfo(path).Length != expectedSize)
        {
            throw new InvalidDataException("Helper update size mismatch.");
        }
        await using var stream = File.OpenRead(path);
        var actual = await SHA256.HashDataAsync(stream, token);
        var expected = Convert.FromHexString(expectedSha256);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("Helper update SHA-256 mismatch.");
        }
    }

    private static void ValidateManifest(HelperUpdateManifest manifest)
    {
        if (manifest.SchemaVersion != 1
            || !Version.TryParse(manifest.Version, out _)
            || manifest.PackageUrls.Count == 0
            || manifest.Size <= 0
            || manifest.Sha256.Length != 64
            || manifest.Sha256.Any(ch => !Uri.IsHexDigit(ch)))
        {
            throw new InvalidDataException("Helper update manifest is invalid.");
        }
    }

    [JsonSerializable(typeof(HelperUpdateManifest))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
    private sealed partial class HelperUpdateJsonContext : JsonSerializerContext
    {
    }
}

internal sealed class HelperUpdateManifest
{
    public int SchemaVersion { get; set; }
    public string Version { get; set; } = "";
    public List<string> PackageUrls { get; set; } = [];
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class HelperUpdateProgress
{
    public string RequestId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public string Version { get; set; } = "";
    public long? BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public double? Percent { get; set; }
}

internal sealed class HelperUpdatePreparation
{
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
}

internal sealed class HelperUpdateResponse
{
    public string RequestId { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string AvailableVersion { get; set; } = "";
    public bool Restarting { get; set; }
}
