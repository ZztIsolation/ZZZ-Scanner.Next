using System.Diagnostics;

namespace ZZZ.GamepadProbe;

internal sealed class ProbeForm : Form
{
    private readonly Button connection = new() { Text = "连接虚拟 Xbox 手柄", AutoSize = true };
    private readonly TextBox processName = new() { Text = "ZenlessZoneZero", Width = 220 };
    private readonly CheckBox armed = new() { Text = "已进入背包驱动盘页，启用方向输入", AutoSize = true, Enabled = false };
    private readonly ComboBox input = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly NumericUpDown hold = Number(20, 300, 80);
    private readonly NumericUpDown gap = Number(100, 2000, 350);
    private readonly NumericUpDown count = Number(1, 100, 1);
    private readonly NumericUpDown expectedItems = Number(0, 10000, 0);
    private readonly NumericUpDown rowLimit = Number(0, 1000, 2);
    private readonly NumericUpDown pollMs = Number(20, 1000, 100);
    private readonly CheckBox enableOcr = new() { Text = "接入 OCR（逐件校验后移动）", Checked = true, AutoSize = true };
    private readonly CheckBox savePanels = new() { Text = "保存每件详情截图", AutoSize = true };
    private readonly TextBox gridArea = new() { Text = "2.5,17,68,67", Width = 140 };
    private readonly CheckBox firstItem = new() { Text = "已选中整个背包的第一个盘", AutoSize = true };
    private readonly Label status = new() { Text = "未连接；连接只创建中立设备，不发送方向。", AutoSize = true };
    private readonly Label driverStatus = new() { Text = "正在检测 ViGEmBus…", AutoSize = true };
    private readonly Button refreshDriver = new() { Text = "重新检测", AutoSize = true };
    private readonly Button checkDevice = new() { Text = "设备自检", AutoSize = true, Enabled = false };
    private readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly TextBox observation = new() { Width = 450, PlaceholderText = "记录：实际移动几格、是否换行、是否自动滚动……" };
    private readonly StreamWriter logFile;
    private readonly string logDirectory;
    private bool stopHotkeyRegistered, checkingDevice;
    private DriverInfo? driverInfo;
    private GamepadDevice? device;
    private Process? target;
    private CancellationTokenSource? activeCancellation;
    private Task activeTask = Task.CompletedTask;
    private bool closing, canClose;

    public ProbeForm()
    {
        Text = "ZZZ 手柄导航验证 0.4.0 · 网格 + OCR 实验";
        ClientSize = new Size(980, 780);
        MinimumSize = new Size(850, 600);
        AutoScaleMode = AutoScaleMode.Dpi;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZ-Scanner.Next", "GamepadProbe");
        Directory.CreateDirectory(directory);
        logDirectory = directory;
        var path = Path.Combine(directory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        logFile = new StreamWriter(path, append: false) { AutoFlush = true };

        input.Items.AddRange(["十字方向键 D-pad", "左摇杆"]);
        input.SelectedIndex = 0;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14), ColumnCount = 1, RowCount = 16 };
        for (var i = 0; i < 15; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var guide = new Button { Text = "驱动安装引导", AutoSize = true };
        guide.Click += async (_, _) =>
        {
            if (device is not null || checkingDevice) { Log("请先断开虚拟手柄并等待自检结束，再打开安装引导。"); return; }
            using var dialog = new DriverGuideForm(DriverSetup.Inspect());
            dialog.ShowDialog(this);
            await RefreshDriverAsync();
        };
        var openLogs = new Button { Text = "打开日志目录", AutoSize = true };
        openLogs.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(logDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { Error(ex); }
        };
        layout.Controls.Add(Row(driverStatus));
        layout.Controls.Add(Row(refreshDriver, guide, checkDevice, openLogs));
        layout.Controls.Add(Row(connection, Label("目标进程："), processName));
        layout.Controls.Add(Row(armed));
        layout.Controls.Add(Row(Label("输入："), input, Label("按压 ms："), hold, Label("释放间隔 ms："), gap, Label("次数："), count));
        var directions = Row();
        foreach (var (text, direction) in new[] { ("上 ↑", Direction.Up), ("下 ↓", Direction.Down), ("左 ←", Direction.Left), ("右 →", Direction.Right) })
        {
            var button = new Button { Text = text + "（3 秒后）", AutoSize = true };
            button.Click += async (_, _) => await StartAsync(direction);
            directions.Controls.Add(button);
        }
        var stop = new Button { Text = "停止 / 全部释放（F10）", AutoSize = true };
        stop.Click += (_, _) => Stop();
        directions.Controls.Add(stop);
        layout.Controls.Add(directions);
        var inspectGrid = new Button { Text = "识别网格预览（3 秒后）", AutoSize = true };
        var traverse = new Button { Text = "自动遍历（3 秒后）", AutoSize = true };
        inspectGrid.Click += async (_, _) => await RunGridAsync(run: false);
        traverse.Click += async (_, _) => await RunGridAsync(run: true);
        layout.Controls.Add(Row(Label("网格范围（左,上,宽,高 %）："), gridArea, inspectGrid));
        layout.Controls.Add(Row(firstItem, Label("人工总数（0=未知）："), expectedItems, Label("验证行数（0=不限）："), rowLimit, traverse));
        var speedPresets = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        speedPresets.Items.AddRange(["基线 80/350/100", "提速 1：80/200/100", "提速 2：80/100/50", "提速 3：60/100/30"]);
        speedPresets.SelectedIndexChanged += (_, _) =>
        {
            var speed = OcrBenchmark.Speeds[speedPresets.SelectedIndex];
            hold.Value = speed.HoldMs; gap.Value = speed.GapMs; pollMs.Value = speed.PollMs;
        };
        speedPresets.SelectedIndex = 0;
        layout.Controls.Add(Row(enableOcr, speedPresets, Label("观测间隔 ms："), pollMs, savePanels));
        layout.Controls.Add(Row(Label("先用 2 行建立 OCR 基线，再降低等待；连续 3 次观测稳定、详情至少等待 180ms。名称映射请核对原文。")));
        layout.Controls.Add(Row(Label("方向仅通过窗口按钮触发；F10 只用于紧急停止，可能使游戏切回键鼠模式。")));
        layout.Controls.Add(Row(Label("点击方向按钮后，3 秒内切回游戏。运行中失焦即停止；次数表示输入次数，不代表移动格数。")));
        layout.Controls.Add(Row(status));
        var note = new Button { Text = "记录观察", AutoSize = true };
        note.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(observation.Text)) { Log("OBSERVATION " + observation.Text); observation.Clear(); } };
        layout.Controls.Add(Row(observation, note));
        layout.Controls.Add(Row(Label("日志：" + path)));
        layout.Controls.Add(logBox);
        Controls.Add(layout);
        connection.Click += async (_, _) => await ToggleConnectionAsync();
        connection.Enabled = false;
        refreshDriver.Click += async (_, _) => await RefreshDriverAsync();
        checkDevice.Click += async (_, _) => await CheckDeviceAsync();
        Shown += async (_, _) =>
        {
            Height = Math.Min(Height, Screen.FromControl(this).WorkingArea.Height - 24);
            await RefreshDriverAsync();
        };
        Resize += (_, _) =>
        {
            status.MaximumSize = new Size(Math.Max(400, ClientSize.Width - 60), 0);
            driverStatus.MaximumSize = status.MaximumSize;
        };
        armed.CheckedChanged += (_, _) => ArmChanged();
        Log("READY driver=ViGEmBus; dynamic grid + optional serial PP-OCRv5; no physical controller required.");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        stopHotkeyRegistered = Native.RegisterHotKey(Handle, 104, 0x4000, (uint)Keys.F10);
        if (!stopHotkeyRegistered) Log("HOTKEY_UNAVAILABLE F10；请切出游戏停止输入，或使用窗口停止按钮。");
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (stopHotkeyRegistered) Native.UnregisterHotKey(Handle, 104);
        stopHotkeyRegistered = false;
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312)
        {
            var id = m.WParam.ToInt32();
            if (id == 104) Stop();
        }
        base.WndProc(ref m);
    }

    private async Task ToggleConnectionAsync()
    {
        connection.Enabled = false;
        try
        {
            if (device is not null) await DisconnectAsync();
            else
            {
                driverInfo = DriverSetup.Inspect();
                if (!driverInfo.CanConnect) throw new InvalidOperationException(driverInfo.Summary);
                device = new GamepadDevice();
                armed.Enabled = true;
                connection.Text = "断开虚拟手柄";
                status.Text = "已连接虚拟 Xbox 手柄；请在游戏内切换为手柄操作，再启用方向输入。";
                Log("CONNECTED Xbox360 virtual device; neutral report submitted.");
            }
        }
        catch (Exception ex) { Error(ex); }
        finally { UpdateDeviceButtons(); }
    }

    private async Task RefreshDriverAsync()
    {
        refreshDriver.Enabled = false;
        connection.Enabled = device is not null;
        try
        {
            var info = await Task.Run(DriverSetup.Inspect);
            if (closing) return;
            driverInfo = info;
            driverStatus.Text = info.Summary;
            var diagnostics = DriverSetup.Diagnostics(info);
            Log("DRIVER_CHECK " + diagnostics.Replace('\n', ' '));
            File.WriteAllText(Path.Combine(logDirectory, $"environment-{Environment.ProcessId}.txt"), diagnostics);
        }
        catch (Exception ex) { if (!closing) Error(ex); }
        finally
        {
            if (!closing) { refreshDriver.Enabled = true; UpdateDeviceButtons(); }
        }
    }

    private void UpdateDeviceButtons()
    {
        connection.Enabled = !checkingDevice && (device is not null || driverInfo?.CanConnect == true);
        checkDevice.Enabled = !checkingDevice && device is null && driverInfo?.CanConnect == true;
    }

    private async Task CheckDeviceAsync()
    {
        if (device is not null || checkingDevice || closing) return;
        checkingDevice = true;
        UpdateDeviceButtons();
        status.Text = "正在创建临时虚拟手柄进行自检，不发送导航指令…";
        try
        {
            var report = await ProbeChecks.CheckDeviceAsync();
            Log("DEVICE_CHECK " + report);
            File.WriteAllText(Path.Combine(logDirectory, $"device-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt"), report);
            status.Text = "设备自检通过，临时手柄已移除；可以连接手柄进行游戏测试。";
        }
        catch (Exception ex) { Error(ex); }
        finally { checkingDevice = false; UpdateDeviceButtons(); }
    }

    private void ArmChanged()
    {
        if (!armed.Checked)
        {
            activeCancellation?.Cancel();
            processName.Enabled = true;
            return;
        }
        try
        {
            if (device is null) throw new InvalidOperationException("请先连接虚拟手柄。");
            target?.Dispose();
            target = null;
            var matches = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName.Text.Trim()));
            var candidates = matches.Where(p => p.MainWindowHandle != 0).ToArray();
            if (candidates.Length != 1)
            {
                foreach (var p in matches) p.Dispose();
                throw new InvalidOperationException("必须找到且只找到一个目标游戏窗口，请检查进程名称。");
            }
            target = candidates[0];
            foreach (var p in matches) if (!ReferenceEquals(p, target)) p.Dispose();
            processName.Enabled = false;
            status.Text = "已启用；点击方向按钮后，在 3 秒倒计时内切回游戏。";
            Log($"ARMED process={target.ProcessName} pid={target.Id}; inventory page confirmed by user.");
        }
        catch (Exception ex) { armed.Checked = false; Error(ex); }
    }

    private void EnsureTarget()
    {
        if (!armed.Checked || target is null || target.HasExited || Native.ForegroundProcessId() != target.Id)
            throw new InvalidOperationException("目标游戏不在前台、已退出或方向输入未启用；已停止发送。");
    }

    private async Task StartAsync(Direction direction)
    {
        if (closing) return;
        if (!activeTask.IsCompleted) { Log("IGNORED 输入序列尚未结束。"); return; }
        if (device is null || !armed.Checked) { Log("IGNORED 请先连接并勾选启用方向输入。"); return; }
        activeCancellation = new CancellationTokenSource();
        var cts = activeCancellation;
        var runner = new PulseRunner(device, EnsureTarget, Log);
        var mode = (NavigationInput)input.SelectedIndex;
        var repetitions = (int)count.Value;
        var holdMs = (int)hold.Value;
        var gapMs = (int)gap.Value;
        activeTask = ExecuteAsync();
        try { await activeTask; }
        finally { activeCancellation = null; cts.Dispose(); }

        async Task ExecuteAsync()
        {
            try
            {
                Log($"COUNTDOWN direction={direction} count={repetitions}; 3 秒内切回游戏。");
                status.Text = "3 秒后发送，请切回游戏；F10 可取消。";
                await Task.Delay(3000, cts.Token);
                await runner.RunAsync(direction, mode, repetitions, holdMs, gapMs, cts.Token);
                status.Text = "输入序列已结束；请观察实际移动结果。";
                Log("SEQUENCE_COMPLETE movement=UNVERIFIED");
            }
            catch (OperationCanceledException) { Log("CANCELED 所有输入已释放。"); }
            catch (Exception ex) { armed.Checked = false; Error(ex); }
        }
    }

    private void Stop()
    {
        activeCancellation?.Cancel();
        armed.Checked = false;
        try { device?.Neutral(); }
        catch (Exception ex) { Error(ex); }
        status.Text = "已停止并解除启用；再次操作前需重新勾选。";
        Log("STOP requested; neutral report attempted; disarmed.");
    }

    private async Task RunGridAsync(bool run)
    {
        if (closing || !activeTask.IsCompleted) return;
        if (device is null || target is null || !armed.Checked) { Log("请先连接虚拟手柄并启用方向输入。"); return; }
        if (run && !firstItem.Checked) { Log("请先回到整个背包第一个盘，并勾选起点确认。"); return; }
        TraversalOptions options;
        try
        {
            var values = gridArea.Text.Replace('，', ',').Split(',').Select(s => float.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture) / 100f).ToArray();
            if (values.Length != 4 || values.Any(v => !float.IsFinite(v)) || values[0] < 0 || values[1] < 0
                || values[2] <= 0 || values[3] <= 0 || values[0] + values[2] > 1 || values[1] + values[3] > 1)
                throw new FormatException("网格范围需要四个百分比：左,上,宽,高，且必须在窗口内。");
            options = new TraversalOptions(new RectangleF(values[0], values[1], values[2], values[3]),
                (NavigationInput)input.SelectedIndex, (int)hold.Value, (int)gap.Value, (int)expectedItems.Value, RowLimit: (int)rowLimit.Value,
                PollMs: (int)pollMs.Value, SaveOcrPanels: savePanels.Checked);
        }
        catch (Exception ex) { Error(ex); return; }
        var directory = Path.Combine(logDirectory, $"grid-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var useOcr = run && enableOcr.Checked;
        activeCancellation = new CancellationTokenSource();
        var cts = activeCancellation;
        activeTask = ExecuteAsync();
        try { await activeTask; }
        finally { activeCancellation = null; cts.Dispose(); }

        async Task ExecuteAsync()
        {
            try
            {
                status.Text = useOcr ? "正在加载 OCR 模型；加载完后开始 3 秒倒计时。" : "准备网格操作。";
                using var reader = useOcr ? await Task.Run(() => new ItemOcr(), cts.Token) : null;
                cts.Token.ThrowIfCancellationRequested();
                if (reader is not null) Log($"OCR_INITIALIZED threads={reader.Threads}; initMs={reader.InitializationMs:F1}; model={ItemOcr.ModelHash}");
                var traversal = new GridTraversal(new ClientCapture(target!, EnsureTarget), device!, EnsureTarget, Log, directory, options, reader);
                Log($"GRID_COUNTDOWN run={run}; 3 秒内切回游戏；输出={directory}");
                status.Text = "3 秒后截图，请切回游戏。";
                await Task.Delay(3000, cts.Token);
                if (run)
                {
                    var result = await traversal.RunAsync(cts.Token);
                    armed.Checked = false;
                    firstItem.Checked = false;
                    status.Text = $"{result.Outcome}：访问 {result.VisitedItems} 个、OCR {result.OcrItems} 个、名称待核对 {result.OcrReviewItems} 个、{result.ElapsedSeconds:F1} 秒。{result.Reason}";
                }
                else
                {
                    var grid = await traversal.InspectAsync(cts.Token);
                    status.Text = $"识别 {grid.Columns} 列、{grid.Rows.Length} 个可见行；选中 ({grid.SelectedRow},{grid.SelectedColumn})。";
                    Log("GRID_CALIBRATED " + status.Text);
                    using var source = Image.FromFile(Path.Combine(directory, "calibration-grid.png"));
                    var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = new Bitmap(source) };
                    var preview = new Form { Text = "网格预览：青色为格子，紫色为选中框；确认未漏格再开始遍历", ClientSize = new Size(1200, 700), StartPosition = FormStartPosition.CenterScreen };
                    preview.Controls.Add(picture);
                    preview.FormClosed += (_, _) => picture.Image?.Dispose();
                    preview.Show(this);
                }
            }
            catch (OperationCanceledException) { Log("网格操作已取消。"); }
            catch (Exception ex) { armed.Checked = false; Error(ex); }
        }
    }

    private async Task DisconnectAsync()
    {
        Stop();
        await activeTask;
        var old = device;
        device = null;
        try { old?.Dispose(); }
        finally
        {
            target?.Dispose();
            target = null;
            armed.Enabled = false;
            connection.Text = "连接虚拟 Xbox 手柄";
            status.Text = "已断开虚拟手柄。";
            Log("DISCONNECTED");
        }
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (checkingDevice)
        {
            e.Cancel = true;
            status.Text = "请等待设备自检完成并移除临时设备后再关闭。";
            return;
        }
        if (!canClose)
        {
            e.Cancel = true;
            if (!closing)
            {
                closing = true;
                try { await DisconnectAsync(); }
                catch (Exception ex) { Error(ex); }
                canClose = true;
                BeginInvoke((Action)Close);
            }
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        logFile.Dispose();
        base.OnFormClosed(e);
    }

    private void Error(Exception ex)
    {
        status.Text = "错误：" + ex.Message;
        Log("ERROR " + ex);
    }

    private void Log(string text)
    {
        var line = $"{DateTimeOffset.Now:O} {text}";
        try { logFile.WriteLine(line); }
        catch (IOException) { status.Text = "日志写入失败；请检查磁盘空间。"; }
        if (logBox.TextLength > 60000) logBox.Text = logBox.Text[^30000..];
        logBox.AppendText(line + Environment.NewLine);
    }

    private static NumericUpDown Number(int min, int max, int value) => new() { Minimum = min, Maximum = max, Value = value, Width = 72 };
    private static Label Label(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 3, 0, 3) };
        row.Controls.AddRange(controls);
        return row;
    }
}
