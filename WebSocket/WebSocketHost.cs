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
    private const int MaxMessageBytes = 256 * 1024;

    private readonly ScanController _controller;
    private readonly HttpListener _listener = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
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
            var origin = context.Request.Headers["Origin"];
            AddCorsHeaders(context.Response, origin);
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = IsAllowedBrowserOrigin(origin) ? 204 : 403;
                context.Response.Close();
                return;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                await SendJsonAsync(context.Response, 200, new
                {
                    service = "zzz-scanner",
                    version = AppInfo.Version,
                    scanner = AppInfo.DiagnosticPayload()
                }, token);
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!IsAllowedWebSocketPath(path)
                || (_connectionToken is null && !IsAllowedBrowserOrigin(origin)))
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

        await SendAsync(socket, sendGate, "hello", new { service = "zzz-scanner", version = AppInfo.Version, scanner = AppInfo.DiagnosticPayload() }, socketCts.Token);

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
                    await SendAsync(socket, sendGate, "pong", new { time = DateTimeOffset.Now, scanner = AppInfo.DiagnosticPayload() }, socketCts.Token);
                    break;

                case "scan_req":
                    if (scanTask is { IsCompleted: false })
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_busy",
                            phase = "scan",
                            title = "已有扫描任务",
                            message = "已有扫描任务正在进行。",
                            remedy = "请等待当前任务完成，或先停止当前扫描。",
                            retryable = true,
                            actions = Array.Empty<object>()
                        }, socketCts.Token);
                        break;
                    }

                    ScanRequestPayload payload;
                    try
                    {
                        payload = envelope.Data.Deserialize<ScanRequestPayload>(JsonDefaults.Read) ?? new ScanRequestPayload();
                    }
                    catch (JsonException)
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_request_invalid",
                            phase = "scan",
                            title = "扫描请求无效",
                            message = "扫描请求格式无效。",
                            remedy = "请刷新 calculator 页面后重试。",
                            retryable = true,
                            actions = new[] { new { kind = "retry_scan", label = "重新扫描" } }
                        }, socketCts.Token);
                        break;
                    }

                    if (!await _scanGate.WaitAsync(0, socketCts.Token))
                    {
                        await SendAsync(socket, sendGate, "scan_error", new
                        {
                            code = "scan_busy",
                            phase = "scan",
                            title = "扫描器正被占用",
                            message = "另一个连接正在执行扫描任务。",
                            remedy = "请关闭其他 calculator 页面，或等待当前扫描完成。",
                            retryable = true,
                            actions = Array.Empty<object>()
                        }, socketCts.Token);
                        break;
                    }

                    scanCts?.Dispose();
                    scanCts = CancellationTokenSource.CreateLinkedTokenSource(socketCts.Token);
                    scanTask = RunScanWithGateAsync(socket, sendGate, payload, scanCts.Token);
                    break;

                case "scan_stop":
                    await SendAsync(socket, sendGate, "scan_stop_ack", new
                    {
                        accepted = scanTask is { IsCompleted: false },
                        time = DateTimeOffset.UtcNow,
                        scanner = AppInfo.DiagnosticPayload()
                    }, CancellationToken.None);
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

    private async Task RunScanWithGateAsync(
        System.Net.WebSockets.WebSocket socket,
        SemaphoreSlim sendGate,
        ScanRequestPayload payload,
        CancellationToken token)
    {
        try
        {
            await RunScanAsync(socket, sendGate, payload, token);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async Task RunScanAsync(System.Net.WebSockets.WebSocket socket, SemaphoreSlim sendGate, ScanRequestPayload payload, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var lastProgressTicks = DateTime.UtcNow.Ticks;
        var timeoutTriggered = 0;
        var visited = 0;
        var queued = 0;
        var completed = 0;
        var failed = 0;
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!linked.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), linked.Token);
                    var inactiveFor = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - Interlocked.Read(ref lastProgressTicks)));
                    await SendAsync(socket, sendGate, "scan_heartbeat", new
                    {
                        time = DateTimeOffset.UtcNow,
                        inactiveMs = (long)inactiveFor.TotalMilliseconds,
                        visited = Volatile.Read(ref visited),
                        queued = Volatile.Read(ref queued),
                        completed = Volatile.Read(ref completed),
                        failed = Volatile.Read(ref failed),
                        scanner = AppInfo.DiagnosticPayload()
                    }, CancellationToken.None);
                    if (inactiveFor < TimeSpan.FromSeconds(180))
                    {
                        continue;
                    }

                    Interlocked.Exchange(ref timeoutTriggered, 1);
                    linked.Cancel();
                    break;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        try
        {
            var options = BuildOptions(payload);
            var progress = new InlineProgress<ScanProgress>(progress =>
            {
                Interlocked.Exchange(ref lastProgressTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref visited, progress.Visited);
                Volatile.Write(ref queued, progress.Queued);
                Volatile.Write(ref completed, progress.Completed);
                Volatile.Write(ref failed, progress.Failed);
                ForwardProgressSafely(
                    () => SendProgressAsync(socket, sendGate, progress, linked.Token),
                    linked);
            });

            var result = await _controller.ScanAsync(options, progress, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var streamedResult = string.Equals(payload.ResultDelivery, "stream-items-v1", StringComparison.OrdinalIgnoreCase);
            await SendAsync(socket, sendGate, "scan_complete", new
            {
                resultDelivery = streamedResult ? "stream-items-v1" : "legacy-items",
                items = streamedResult ? null : result.Items,
                itemCount = result.Items.Count,
                visited = result.Visited,
                queued = result.Queued,
                completed = result.Completed,
                failed = result.Failed,
                partial = result.Partial,
                terminationCode = string.IsNullOrWhiteSpace(result.TerminationCode) ? null : result.TerminationCode,
                outputDirectory = result.OutputDirectory,
                exportFile = result.ExportFile,
                scanner = AppInfo.DiagnosticPayload(),
                diagnostics = result.Diagnostics
            }, linked.Token);
        }
        catch (OperationCanceledException)
        {
            var noProgress = Volatile.Read(ref timeoutTriggered) == 1;
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = noProgress ? "scan_no_progress_timeout" : "scan_cancelled",
                phase = "scan",
                severity = noProgress ? "error" : "warning",
                title = noProgress ? "扫描长时间没有进展" : "扫描已停止",
                message = noProgress ? "Scanner 连续 180 秒没有产生新的扫描进展，任务已停止。" : "扫描已停止。",
                remedy = noProgress ? "请确认游戏仍在驱动盘仓库且未被遮挡，然后重新扫描。" : "可以调整选项后重新开始扫描。",
                retryable = true,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = new
                {
                    inactiveMs = noProgress ? 180_000 : 0,
                    visited = Volatile.Read(ref visited),
                    queued = Volatile.Read(ref queued),
                    completed = Volatile.Read(ref completed),
                    failed = Volatile.Read(ref failed)
                },
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (ScannerElevationRequiredException ex)
        {
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = "elevation_required",
                phase = "scan",
                title = "需要管理员权限",
                message = ex.Message,
                remedy = "请以管理员权限重启扫描器；只有这一次启动会请求 UAC。",
                retryable = true,
                actions = new[]
                {
                    new { kind = "restart_elevated", label = "以管理员权限重启" },
                    new { kind = "open_logs", label = "打开日志目录" }
                },
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (VisualPreflightException ex)
        {
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = ex.Code,
                phase = "scan",
                title = ex.Title,
                message = ex.Message,
                remedy = ex.Remedy,
                retryable = ex.Retryable,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = ex.DiagnosticDetails,
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IScannerFailureException)
        {
            var ex = (IScannerFailureException)exception;
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = ex.Code,
                phase = "scan",
                title = ex.Title,
                message = exception.Message,
                remedy = ex.Remedy,
                retryable = ex.Retryable,
                actions = new[]
                {
                    new { kind = "retry_scan", label = "重新扫描" },
                    new { kind = "open_logs", label = "打开日志目录" }
                },
                details = ex.DiagnosticDetails,
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var gameNotFound = ex.Message.Contains("未找到游戏窗口进程", StringComparison.Ordinal);
            await SendAsync(socket, sendGate, "scan_error", new
            {
                code = gameNotFound ? "game_not_found" : "scan_failed",
                phase = "scan",
                title = gameNotFound ? "未找到绝区零窗口" : "扫描失败",
                message = ex.Message,
                remedy = gameNotFound ? "请启动游戏并进入驱动盘背包后重试。" : "请重试；如果问题持续，请打开 Helper 日志。",
                retryable = true,
                actions = new[] { new { kind = "retry_scan", label = "重新扫描" } },
                details = ScanDiagnosticDetails.FromException(ex),
                error = ex.ToString(),
                scanner = AppInfo.DiagnosticPayload()
            }, CancellationToken.None);
        }
        finally
        {
            linked.Cancel();
            try { await heartbeatTask; } catch { }
        }
    }

    private static ScanOptions BuildOptions(ScanRequestPayload payload)
    {
        var options = new ScanOptions
        {
            MaxItems = Math.Max(0, payload.MaxItems),
            StopAtNonLevel15 = payload.StopAtNonLevel15,
            OcrShadowDataset = payload.OcrShadowDataset,
            FastOcrShadow = payload.FastOcrShadow,
            FastOcrAssist = payload.FastMode || payload.FastOcrAssist,
            FastMode = payload.FastMode,
            AdaptiveTiming = payload.AdaptiveTiming,
            FastOcrTemplateIndexFile = payload.FastOcrTemplateIndexFile,
            CaptureMode = ParseCaptureMode(payload.CaptureMode),
            PanelStabilityMode = ParsePanelStabilityMode(payload.PanelStabilityMode, payload.FastMode),
            ScrollAcceptMode = string.IsNullOrWhiteSpace(payload.ScrollAcceptMode) && payload.FastMode
                ? ScanModeDefaults.ScrollAccept(true)
                : ParseScrollAcceptMode(payload.ScrollAcceptMode),
            PanelAcceptMode = string.IsNullOrWhiteSpace(payload.PanelAcceptMode) && payload.FastMode
                ? ScanModeDefaults.PanelAccept(true)
                : ParsePanelAcceptMode(payload.PanelAcceptMode),
            PostScrollPanelAcceptMode = ParsePostScrollPanelAcceptMode(payload.PostScrollPanelAcceptMode),
            PanelFloorMode = ParsePanelFloorMode(payload.PanelFloorMode),
            PanelMinAcceptFloorMs = Math.Clamp(payload.PanelMinAcceptFloorMs <= 0 ? 120 : payload.PanelMinAcceptFloorMs, 90, 120),
            SameRowPanelMinAcceptFloorMs = Math.Clamp(payload.SameRowPanelMinAcceptFloorMs <= 0 ? 105 : payload.SameRowPanelMinAcceptFloorMs, 100, 120),
            PostScrollPanelMinAcceptFloorMs = Math.Clamp(payload.PostScrollPanelMinAcceptFloorMs <= 0 ? 110 : payload.PostScrollPanelMinAcceptFloorMs, 100, 120),
            ScrollTickDelayOverrideMs = payload.ScrollTickDelayMs <= 0 ? 0 : Math.Clamp(payload.ScrollTickDelayMs, 50, 80),
            OverlapConflictMode = string.IsNullOrWhiteSpace(payload.OverlapConflictMode) && payload.FastMode
                ? ScanModeDefaults.OverlapConflict(true)
                : ParseOverlapConflictMode(payload.OverlapConflictMode),
            VisualProfileId = string.IsNullOrWhiteSpace(payload.VisualProfileId) ? "auto" : payload.VisualProfileId,
            VisualQualityLabel = string.IsNullOrWhiteSpace(payload.VisualProfileQuality) ? "current" : payload.VisualProfileQuality,
            VisualProfileClient = ParseVisualProfileClient(payload.VisualProfileClient),
            ProfileRouting = ParseProfileRouting(payload.ProfileRouting),
            CollectVisualProfile = !string.IsNullOrWhiteSpace(payload.CollectVisualProfile)
        };

        if (!string.IsNullOrWhiteSpace(payload.ProcessName))
        {
            options.ProcessName = payload.ProcessName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(payload.CollectVisualProfile))
        {
            options.VisualProfileId = payload.CollectVisualProfile;
            if (string.IsNullOrWhiteSpace(payload.ProfileName))
            {
                options.ProfileName = ScanOptions.FastProfileName;
            }

            options.OcrShadowDataset = true;
            options.FastMode = false;
            options.FastOcrAssist = false;
            options.FastOcrShadow = false;
            options.AdaptiveTiming = false;
            options.PanelAcceptMode = PanelAcceptMode.Safe;
            options.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            options.ScrollAcceptMode = ScrollAcceptMode.Safe;
            options.PanelStabilityMode = PanelStabilityMode.Panel;
            options.PanelFloorMode = PanelFloorMode.Static;
            options.PanelMinAcceptFloorMs = 120;
            options.OverlapConflictMode = OverlapConflictMode.Recover;
        }

        if (!string.IsNullOrWhiteSpace(payload.ProfileName))
        {
            options.ProfileName = payload.ProfileName;
        }
        else if (payload.FastMode)
        {
            options.ProfileName = ScanOptions.FastProfileName;
        }

        options.Rarities.Clear();
        options.Rarities.Add("S");

        return options;
    }

    private static CaptureMode ParseCaptureMode(string? value)
    {
        return Enum.TryParse<CaptureMode>(value, ignoreCase: true, out var parsed)
            ? parsed
            : CaptureMode.Gdi;
    }

    private static PanelStabilityMode ParsePanelStabilityMode(string? value, bool fastMode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelStabilityMode.Panel;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelStabilityMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelStabilityMode.Panel;
    }

    private static ScrollAcceptMode ParseScrollAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ScrollAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<ScrollAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : ScrollAcceptMode.Safe;
    }

    private static PanelAcceptMode ParsePanelAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelAcceptMode.Safe;
    }

    private static PostScrollPanelAcceptMode ParsePostScrollPanelAcceptMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PostScrollPanelAcceptMode.Safe;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PostScrollPanelAcceptMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PostScrollPanelAcceptMode.Safe;
    }

    private static PanelFloorMode ParsePanelFloorMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PanelFloorMode.Static;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<PanelFloorMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : PanelFloorMode.Static;
    }

    private static OverlapConflictMode ParseOverlapConflictMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return OverlapConflictMode.Recheck;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<OverlapConflictMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : OverlapConflictMode.Recheck;
    }

    private static VisualProfileClientKind ParseVisualProfileClient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VisualProfileClientKind.Auto;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<VisualProfileClientKind>(normalized, ignoreCase: true, out var mode)
            ? mode
            : VisualProfileClientKind.Auto;
    }

    private static ProfileRoutingMode ParseProfileRouting(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ProfileRoutingMode.Strict;
        }

        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<ProfileRoutingMode>(normalized, ignoreCase: true, out var mode)
            ? mode
            : ProfileRoutingMode.Strict;
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

    internal static void ForwardProgressSafely(Func<Task> send, CancellationTokenSource linked)
    {
        try
        {
            send().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (WebSocketException)
        {
            linked.Cancel();
        }
        catch (ObjectDisposedException)
        {
            linked.Cancel();
        }
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            handler(value);
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
                throw new InvalidDataException("Scanner WebSocket accepts text messages only.");
            }

            if (stream.Length + result.Count > MaxMessageBytes)
            {
                throw new InvalidDataException($"Scanner WebSocket message exceeds {MaxMessageBytes} bytes.");
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

        var json = JsonSerializer.Serialize(new HelperEnvelope(cmd, data), JsonDefaults.Wire);
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
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonDefaults.Wire));
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
        response.Close();
    }

    internal static bool IsAllowedBrowserOrigin(string? origin)
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
        if (IsAllowedBrowserOrigin(origin))
        {
            response.Headers["Access-Control-Allow-Origin"] = origin;
        }

        response.Headers["Access-Control-Allow-Methods"] = "GET,POST,OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
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
            Data = data is null ? default : JsonSerializer.SerializeToElement(data, JsonDefaults.Wire);
        }
    }

    private sealed class ScanRequestPayload
    {
        public int MaxItems { get; set; }
        public string[] Rarities { get; set; } = ["S"];
        public string ResultDelivery { get; set; } = "";
        public bool StopAtNonLevel15 { get; set; } = true;
        public bool OcrShadowDataset { get; set; }
        public bool FastOcrShadow { get; set; }
        public bool FastOcrAssist { get; set; }
        public bool FastMode { get; set; }
        public bool? AdaptiveTiming { get; set; }
        public string FastOcrTemplateIndexFile { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string ProcessName { get; set; } = "";
        public string CaptureMode { get; set; } = "gdi";
        public string PanelStabilityMode { get; set; } = "";
        public string ScrollAcceptMode { get; set; } = "";
        public string PanelAcceptMode { get; set; } = "";
        public string PostScrollPanelAcceptMode { get; set; } = "";
        public string PanelFloorMode { get; set; } = "";
        public int PanelMinAcceptFloorMs { get; set; } = 120;
        public int SameRowPanelMinAcceptFloorMs { get; set; } = 105;
        public int PostScrollPanelMinAcceptFloorMs { get; set; } = 110;
        public int ScrollTickDelayMs { get; set; }
        public string OverlapConflictMode { get; set; } = "";
        public string VisualProfileId { get; set; } = "";
        public string VisualProfileQuality { get; set; } = "";
        public string VisualProfileClient { get; set; } = "";
        public string ProfileRouting { get; set; } = "";
        public string CollectVisualProfile { get; set; } = "";
    }
}
