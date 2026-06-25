using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.WebSocket;

public sealed class WebSocketHost : IDisposable
{
    private readonly ScanController _controller;
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly string? _connectionToken;
    private bool _disposed;

    public WebSocketHost(ScanProfileFile profiles, WikiData wikiData, int port, string? connectionToken = null)
    {
        _controller = new ScanController(profiles, wikiData);
        _port = port > 0 ? port : throw new ArgumentOutOfRangeException(nameof(port));
        _connectionToken = string.IsNullOrWhiteSpace(connectionToken) ? null : connectionToken;
    }

    public string? BrowserUrl { get; set; }

    public async Task RunAsync(CancellationToken token)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();

        if (!string.IsNullOrWhiteSpace(BrowserUrl))
        {
            TryOpenBrowser(BrowserUrl);
        }

        while (!token.IsCancellationRequested)
        {
            var context = await _listener.GetContextAsync().WaitAsync(token);
            _ = Task.Run(() => HandleContextAsync(context, token), token);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
    {
        try
        {
            AddCorsHeaders(context.Response);
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                await SendJsonAsync(context.Response, 200, new
                {
                    service = "zzz-scanner",
                    version = "1.0.0"
                }, token);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!IsAllowedWebSocketPath(path))
            {
                context.Response.StatusCode = 403;
                context.Response.Close();
                return;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await RunSessionAsync(wsContext.WebSocket, token);
        }
        catch
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private bool IsAllowedWebSocketPath(string path)
    {
        if (_connectionToken is null)
        {
            return path.Equals("/ws", StringComparison.OrdinalIgnoreCase);
        }

        return path.Equals($"/ws/{Uri.EscapeDataString(_connectionToken)}", StringComparison.OrdinalIgnoreCase)
            || path.Equals($"/ws/{_connectionToken}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task RunSessionAsync(System.Net.WebSockets.WebSocket socket, CancellationToken hostToken)
    {
        using var socketCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        using var sendGate = new SemaphoreSlim(1, 1);
        CancellationTokenSource? scanCts = null;
        Task? scanTask = null;

        await SendAsync(socket, sendGate, "hello", new { service = "zzz-scanner", version = "1.0.0" }, socketCts.Token);

        while (socket.State == WebSocketState.Open && !socketCts.IsCancellationRequested)
        {
            var json = await ReceiveTextAsync(socket, socketCts.Token);
            if (json is null)
            {
                break;
            }

            HelperEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<HelperEnvelope>(json, JsonDefaults.Read);
            }
            catch
            {
                continue;
            }

            if (envelope is null)
            {
                continue;
            }

            switch (envelope.Cmd)
            {
                case "ping":
                    await SendAsync(socket, sendGate, "pong", new { time = DateTimeOffset.Now }, socketCts.Token);
                    break;

                case "scan_req":
                    if (scanTask is { IsCompleted: false })
                    {
                        await SendAsync(socket, sendGate, "scan_error", new { message = "已有扫描任务正在进行。" }, socketCts.Token);
                        break;
                    }

                    scanCts?.Dispose();
                    scanCts = CancellationTokenSource.CreateLinkedTokenSource(socketCts.Token);
                    var payload = envelope.Data.Deserialize<ScanRequestPayload>(JsonDefaults.Read) ?? new ScanRequestPayload();
                    scanTask = Task.Run(() => RunScanAsync(socket, sendGate, payload, scanCts.Token), scanCts.Token);
                    break;

                case "scan_stop":
                    scanCts?.Cancel();
                    break;
            }
        }

        scanCts?.Cancel();
        if (scanTask is not null)
        {
            try { await scanTask; } catch { }
        }

        scanCts?.Dispose();
    }

    private async Task RunScanAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, ScanRequestPayload payload, CancellationToken token)
    {
        try
        {
            var options = BuildOptions(payload);
            var progress = new Progress<ScanProgress>(progress =>
            {
                SendProgressAsync(socket, sendGate, progress, token).GetAwaiter().GetResult();
            });

            var result = await _controller.ScanAsync(options, progress, token);
            await SendAsync(socket, sendGate, "scan_complete", new
            {
                items = result.Items,
                visited = result.Visited,
                failed = result.Failed,
                outputDirectory = result.OutputDirectory,
                exportFile = result.ExportFile
            }, token);
        }
        catch (OperationCanceledException)
        {
            await SendAsync(socket, sendGate, "scan_error", new { message = "扫描已停止。" }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await SendAsync(socket, sendGate, "scan_error", new { message = ex.Message }, CancellationToken.None);
        }
    }

    private static ScanOptions BuildOptions(ScanRequestPayload payload)
    {
        var options = new ScanOptions
        {
            MaxItems = Math.Max(0, payload.MaxItems),
            StopAtNonLevel15 = payload.StopAtNonLevel15,
        };

        if (!string.IsNullOrWhiteSpace(payload.ProfileName))
        {
            options.ProfileName = payload.ProfileName;
        }

        options.Rarities.Clear();
        foreach (var rarity in payload.Rarities.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            options.Rarities.Add(rarity.Trim());
        }

        if (options.Rarities.Count == 0)
        {
            options.Rarities.Add("S");
        }

        return options;
    }

    private static async Task SendProgressAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, ScanProgress progress, CancellationToken token)
    {
        await SendAsync(socket, sendGate, "scan_progress", new
        {
            message = progress.Message,
            visited = progress.Visited,
            queued = progress.Queued,
            completed = progress.Completed,
            failed = progress.Failed
        }, token);

        if (progress.Item is not null)
        {
            await SendAsync(socket, sendGate, "scan_item", progress.Item, token);
        }
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

    private static async Task SendAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, string cmd, object? data, CancellationToken token)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), JsonDefaults.Write);
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendGate.WaitAsync(token);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
            }
        }
        finally
        {
            sendGate.Release();
        }
    }

    private static async Task SendJsonAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken token)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Write));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
        }
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
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, JsonDefaults.Write);
        }
    }

    private sealed class ScanRequestPayload
    {
        public int MaxItems { get; set; }
        public string[] Rarities { get; set; } = ["S"];
        public bool StopAtNonLevel15 { get; set; } = true;
        public string ProfileName { get; set; } = "";
    }
}
