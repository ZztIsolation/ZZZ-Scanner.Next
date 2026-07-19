using System.Diagnostics;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ui;

public sealed class MainForm : Form
{
    private const int StopHotKeyId = 0x5A5A;

    private readonly ScanProfileFile _profiles;
    private readonly ScanController _controller;

    private readonly ComboBox _traversalModeCombo = new();
    private readonly TextBox _processBox = new();
    private readonly NumericUpDown _maxItems = new();
    private readonly CheckBox _rarityS = new();
    private readonly CheckBox _rarityA = new();
    private readonly CheckBox _onlyLevel15 = new();
    private readonly CheckBox _bringToFront = new();
    private readonly CheckBox _showDebugImages = new();
    private readonly CheckBox _highSpeedOcr = new();
    private readonly CheckBox _ocrShadowDataset = new();
    private readonly CheckBox _fastOcrShadow = new();
    private readonly CheckBox _fastOcrAssist = new();
    private readonly CheckBox _fastMode = new();
    private readonly CheckBox _adaptiveTiming = new();
    private readonly ComboBox _captureModeCombo = new();
    private readonly ComboBox _panelStabilityModeCombo = new();
    private readonly ComboBox _scrollAcceptModeCombo = new();
    private readonly ComboBox _panelAcceptModeCombo = new();
    private readonly ComboBox _postScrollPanelAcceptModeCombo = new();
    private readonly NumericUpDown _panelMinAcceptFloorMs = new();
    private readonly NumericUpDown _ocrWorkers = new();
    private readonly NumericUpDown _ocrBatchSize = new();
    private readonly NumericUpDown _ocrQueueCapacity = new();
    private readonly NumericUpDown _ocrIntraOpThreads = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _detectButton = new();
    private readonly Button _openOutputButton = new();
    private readonly Button _advancedToggleButton = new();
    private readonly LinkLabel _outputLink = new();
    private readonly Label _statusLabel = new();

    private CancellationTokenSource? _scanCancellation;
    private string? _lastOutputDirectory;
    private GroupBox? _advancedGroup;
    private bool _advancedExpanded;

    public MainForm()
    {
        Text = "ZZZ Scanner Next";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimumSize = new Size(430, 520);
        ClientSize = FitInitialSize(new Size(430, 560));
        StartPosition = FormStartPosition.CenterScreen;

        _profiles = ScanProfileFile.Load();
        _controller = new ScanController(_profiles, WikiData.Load());

        BuildUi();
        LoadDefaults();
    }

    private void BuildUi()
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(0)
        };
        Controls.Add(scroll);

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(14),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(root);

        var basic = CreateGroup(root, "基础设置");
        AddLabel(basic, "进程名");
        ConfigureSingleLine(_processBox);
        basic.Controls.Add(_processBox);

        AddLabel(basic, "读取上限（0=不限制）");
        _maxItems.Minimum = 0;
        _maxItems.Maximum = 9999;
        ConfigureSingleLine(_maxItems);
        basic.Controls.Add(_maxItems);

        AddLabel(basic, "品质");
        var rarityPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 2)
        };
        _rarityS.Text = "S";
        _rarityS.AutoSize = true;
        _rarityA.Text = "A";
        _rarityA.AutoSize = true;
        rarityPanel.Controls.AddRange([_rarityS, _rarityA]);
        basic.Controls.Add(rarityPanel);

        _onlyLevel15.Text = "只读取15级驱动盘";
        _onlyLevel15.AutoSize = true;
        basic.Controls.Add(_onlyLevel15);

        _bringToFront.Text = "前置游戏窗口";
        _bringToFront.AutoSize = true;
        basic.Controls.Add(_bringToFront);

        var action = CreateGroup(root, "操作与产物");
        var actionRow = new TableLayoutPanel
        {
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Height = 38,
            Margin = new Padding(0, 2, 0, 8)
        };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        action.Controls.Add(actionRow);

        _detectButton.Text = "检测窗口";
        ConfigureButton(_detectButton);
        _detectButton.Click += (_, _) => DetectWindow();
        actionRow.Controls.Add(_detectButton, 0, 0);

        _startButton.Text = "开始扫描";
        ConfigureButton(_startButton);
        _startButton.Click += async (_, _) => await StartScanAsync();
        actionRow.Controls.Add(_startButton, 1, 0);

        _stopButton.Text = "停止";
        ConfigureButton(_stopButton);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => _scanCancellation?.Cancel();
        actionRow.Controls.Add(_stopButton, 2, 0);

        _openOutputButton.Text = "打开产物文件夹";
        _openOutputButton.Dock = DockStyle.Top;
        _openOutputButton.Height = 34;
        _openOutputButton.Enabled = false;
        _openOutputButton.Margin = new Padding(0, 0, 0, 8);
        _openOutputButton.Click += (_, _) => OpenOutputDirectory();
        action.Controls.Add(_openOutputButton);

        AddLabel(action, "产物链接");
        _outputLink.Text = "扫描完成后显示产物文件夹";
        _outputLink.Dock = DockStyle.Top;
        _outputLink.Height = 42;
        _outputLink.Enabled = false;
        _outputLink.LinkBehavior = LinkBehavior.HoverUnderline;
        _outputLink.TextAlign = ContentAlignment.MiddleLeft;
        _outputLink.LinkClicked += (_, _) => OpenOutputDirectory();
        action.Controls.Add(_outputLink);

        _statusLabel.Text = "就绪";
        _statusLabel.AutoSize = false;
        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = 24;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        action.Controls.Add(_statusLabel);

        _advancedToggleButton.Text = "展开高级设置";
        _advancedToggleButton.Dock = DockStyle.Top;
        _advancedToggleButton.Height = 34;
        _advancedToggleButton.Margin = new Padding(0, 0, 0, 10);
        _advancedToggleButton.Click += (_, _) => ToggleAdvancedSettings();
        root.Controls.Add(_advancedToggleButton);

        var advanced = CreateGroup(root, "高级设置", out _advancedGroup);
        _advancedGroup.Visible = false;

        AddLabel(advanced, "遍历模式");
        _traversalModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_traversalModeCombo);
        advanced.Controls.Add(_traversalModeCombo);

        _highSpeedOcr.Text = "高速 OCR";
        _highSpeedOcr.AutoSize = true;
        advanced.Controls.Add(_highSpeedOcr);

        _showDebugImages.Text = "生成调试截图";
        _showDebugImages.AutoSize = true;
        advanced.Controls.Add(_showDebugImages);

        _ocrShadowDataset.Text = "采集 OCR 影子数据集";
        _ocrShadowDataset.AutoSize = true;
        advanced.Controls.Add(_ocrShadowDataset);

        _fastOcrShadow.Text = "OCR快路径影子对照";
        _fastOcrShadow.AutoSize = true;
        advanced.Controls.Add(_fastOcrShadow);

        _fastOcrAssist.Text = "OCR快路径辅助识别";
        _fastOcrAssist.AutoSize = true;
        advanced.Controls.Add(_fastOcrAssist);

        _fastMode.Text = "已验证高速模式";
        _fastMode.AutoSize = true;
        advanced.Controls.Add(_fastMode);

        _adaptiveTiming.Text = "本轮自适应等待";
        _adaptiveTiming.AutoSize = true;
        advanced.Controls.Add(_adaptiveTiming);

        AddLabel(advanced, "截图后端");
        _captureModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_captureModeCombo);
        advanced.Controls.Add(_captureModeCombo);

        AddLabel(advanced, "面板稳定判定");
        _panelStabilityModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_panelStabilityModeCombo);
        advanced.Controls.Add(_panelStabilityModeCombo);

        AddLabel(advanced, "滚动接受策略");
        _scrollAcceptModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_scrollAcceptModeCombo);
        advanced.Controls.Add(_scrollAcceptModeCombo);

        AddLabel(advanced, "面板接受策略");
        _panelAcceptModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_panelAcceptModeCombo);
        advanced.Controls.Add(_panelAcceptModeCombo);

        AddLabel(advanced, "滚动后首格策略");
        _postScrollPanelAcceptModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        ConfigureSingleLine(_postScrollPanelAcceptModeCombo);
        advanced.Controls.Add(_postScrollPanelAcceptModeCombo);

        var numericGrid = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 0)
        };
        numericGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        numericGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        advanced.Controls.Add(numericGrid);

        AddNumericSetting(numericGrid, "OCR线程（0=自动）", _ocrWorkers, 0, 4);
        AddNumericSetting(numericGrid, "OCR批量", _ocrBatchSize, 1, 16);
        AddNumericSetting(numericGrid, "队列容量", _ocrQueueCapacity, 1, 256);
        AddNumericSetting(numericGrid, "IntraOp线程", _ocrIntraOpThreads, 1, 8);
        AddNumericSetting(numericGrid, "面板下限ms", _panelMinAcceptFloorMs, 90, 120);
    }

    private void LoadDefaults()
    {
        var defaults = new ScanOptions();

        _processBox.Text = defaults.ProcessName;
        _maxItems.Value = defaults.MaxItems;
        _rarityS.Checked = defaults.Rarities.Contains("S");
        _rarityA.Checked = defaults.Rarities.Contains("A");
        _onlyLevel15.Checked = defaults.StopAtNonLevel15;
        _bringToFront.Checked = defaults.BringToFront;
        _highSpeedOcr.Checked = defaults.HighSpeedOcr;
        _showDebugImages.Checked = defaults.ShowDebugImages;
        _ocrShadowDataset.Checked = defaults.OcrShadowDataset;
        _fastOcrShadow.Checked = defaults.FastOcrShadow;
        _fastOcrAssist.Checked = defaults.FastOcrAssist;
        _fastMode.Checked = defaults.FastMode;
        _adaptiveTiming.Checked = defaults.AdaptiveTiming == true;
        _captureModeCombo.Items.AddRange(["gdi", "dxgi"]);
        _captureModeCombo.SelectedItem = defaults.CaptureMode == CaptureMode.Dxgi ? "dxgi" : "gdi";
        _panelStabilityModeCombo.Items.AddRange(["panel", "text-core", "auto"]);
        _panelStabilityModeCombo.SelectedItem = "panel";
        _scrollAcceptModeCombo.Items.AddRange(["safe", "early-one-row"]);
        _scrollAcceptModeCombo.SelectedItem = "safe";
        _panelAcceptModeCombo.Items.AddRange(["safe", "adaptive-early-full-roi"]);
        _panelAcceptModeCombo.SelectedItem = "safe";
        _postScrollPanelAcceptModeCombo.Items.AddRange(["safe", "adaptive-after-scroll"]);
        _postScrollPanelAcceptModeCombo.SelectedItem = "safe";
        _panelMinAcceptFloorMs.Value = defaults.PanelMinAcceptFloorMs;
        _ocrWorkers.Value = defaults.OcrWorkerCount;
        _ocrBatchSize.Value = defaults.OcrBatchSize;
        _ocrQueueCapacity.Value = defaults.OcrQueueCapacity;
        _ocrIntraOpThreads.Value = defaults.OcrIntraOpThreads;
        _fastMode.CheckedChanged += (_, _) => SyncFastModeDefaults();

        _traversalModeCombo.Items.AddRange([
            "按配置（默认重叠签名）",
            "重叠签名扫描",
            "安全带扫描",
            "校准翻页",
            "旧版第3行"
        ]);
        _traversalModeCombo.SelectedIndex = 0;
        SyncFastModeDefaults();
    }

    private void SyncFastModeDefaults()
    {
        if (_panelStabilityModeCombo.SelectedItem is null)
        {
            _panelStabilityModeCombo.SelectedItem = "panel";
        }

        if (!_fastMode.Checked)
        {
            _scrollAcceptModeCombo.SelectedItem = ScanModeDefaults.ScrollAccept(false) == ScrollAcceptMode.Safe
                ? "safe"
                : "early-one-row";
            _panelAcceptModeCombo.SelectedItem = ScanModeDefaults.PanelAccept(false) == PanelAcceptMode.Safe
                ? "safe"
                : "adaptive-early-full-roi";
            return;
        }

        _scrollAcceptModeCombo.SelectedItem = ScanModeDefaults.ScrollAccept(true) == ScrollAcceptMode.EarlyOneRow
            ? "early-one-row"
            : "safe";
        _panelAcceptModeCombo.SelectedItem = ScanModeDefaults.PanelAccept(true) == PanelAcceptMode.AdaptiveEarlyFullRoi
            ? "adaptive-early-full-roi"
            : "safe";
    }

    private async Task StartScanAsync()
    {
        var options = ReadOptions();
        if (options.Rarities.Count == 0)
        {
            MessageBox.Show("至少选择一个品质。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetOutputDirectory(null);
        _statusLabel.Text = "扫描中";
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);

        try
        {
            var result = await Task.Run(
                async () => await _controller.ScanAsync(options, NoOpScanProgress.Instance, _scanCancellation.Token),
                _scanCancellation.Token);
            SetOutputDirectory(result.OutputDirectory);
            _statusLabel.Text = $"完成：输出 {result.Items.Count} 条，失败 {result.Failed} 条";
            MessageBox.Show(
                $"扫描完成，输出 {result.Items.Count} 条，失败 {result.Failed} 条。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (OperationCanceledException)
        {
            _statusLabel.Text = "已停止";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "扫描异常";
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            SetScanningState(false);
        }
    }

    private ScanOptions ReadOptions()
    {
        var options = new ScanOptions
        {
            ProcessName = _processBox.Text.Trim(),
            ProfileName = _fastMode.Checked ? ResolveProfileName(ScanOptions.FastProfileName) : ResolveProfileName(),
            TraversalMode = SelectedTraversalMode(),
            MaxItems = (int)_maxItems.Value,
            BringToFront = _bringToFront.Checked,
            ShowDebugImages = _showDebugImages.Checked,
            StopAtNonLevel15 = _onlyLevel15.Checked,
            HighSpeedOcr = _highSpeedOcr.Checked,
            OcrShadowDataset = _ocrShadowDataset.Checked,
            FastOcrShadow = _fastOcrShadow.Checked,
            FastOcrAssist = _fastMode.Checked || _fastOcrAssist.Checked,
            FastMode = _fastMode.Checked,
            AdaptiveTiming = _adaptiveTiming.Checked ? true : null,
            CaptureMode = SelectedCaptureMode(),
            PanelStabilityMode = SelectedPanelStabilityMode(),
            ScrollAcceptMode = SelectedScrollAcceptMode(),
            PanelAcceptMode = SelectedPanelAcceptMode(),
            PostScrollPanelAcceptMode = SelectedPostScrollPanelAcceptMode(),
            PanelMinAcceptFloorMs = (int)_panelMinAcceptFloorMs.Value,
            OverlapConflictMode = ScanModeDefaults.OverlapConflict(_fastMode.Checked),
            ProfileRouting = ProfileRoutingMode.Strict,
            VisualProfileClient = VisualProfileClientKind.Auto,
            OcrBatchSize = (int)_ocrBatchSize.Value,
            OcrWorkerCount = (int)_ocrWorkers.Value,
            OcrQueueCapacity = (int)_ocrQueueCapacity.Value,
            OcrIntraOpThreads = (int)_ocrIntraOpThreads.Value
        };
        options.Rarities.Clear();
        if (_rarityS.Checked) options.Rarities.Add("S");
        if (_rarityA.Checked) options.Rarities.Add("A");
        return options;
    }

    private CaptureMode SelectedCaptureMode()
    {
        return string.Equals(_captureModeCombo.SelectedItem?.ToString(), "dxgi", StringComparison.OrdinalIgnoreCase)
            ? CaptureMode.Dxgi
            : CaptureMode.Gdi;
    }

    private PanelStabilityMode SelectedPanelStabilityMode()
    {
        return _panelStabilityModeCombo.SelectedItem?.ToString() switch
        {
            "auto" => PanelStabilityMode.Auto,
            "text-core" => PanelStabilityMode.TextCore,
            _ => PanelStabilityMode.Panel
        };
    }

    private ScrollAcceptMode SelectedScrollAcceptMode()
    {
        return string.Equals(_scrollAcceptModeCombo.SelectedItem?.ToString(), "early-one-row", StringComparison.OrdinalIgnoreCase)
            ? ScrollAcceptMode.EarlyOneRow
            : ScrollAcceptMode.Safe;
    }

    private PanelAcceptMode SelectedPanelAcceptMode()
    {
        return string.Equals(_panelAcceptModeCombo.SelectedItem?.ToString(), "adaptive-early-full-roi", StringComparison.OrdinalIgnoreCase)
            ? PanelAcceptMode.AdaptiveEarlyFullRoi
            : PanelAcceptMode.Safe;
    }

    private PostScrollPanelAcceptMode SelectedPostScrollPanelAcceptMode()
    {
        return string.Equals(_postScrollPanelAcceptModeCombo.SelectedItem?.ToString(), "adaptive-after-scroll", StringComparison.OrdinalIgnoreCase)
            ? PostScrollPanelAcceptMode.AdaptiveAfterScroll
            : PostScrollPanelAcceptMode.Safe;
    }

    private string ResolveProfileName(string? requestedProfileName = null)
    {
        var defaultProfileName = ScanOptions.DefaultProfileName;
        if (!string.IsNullOrWhiteSpace(requestedProfileName)
            && _profiles.Find(requestedProfileName) is not null)
        {
            return requestedProfileName;
        }

        if (_profiles.Find(defaultProfileName) is not null)
        {
            return defaultProfileName;
        }

        return _profiles.Profiles.FirstOrDefault()?.Name ?? defaultProfileName;
    }

    private ScanTraversalMode SelectedTraversalMode()
    {
        return _traversalModeCombo.SelectedIndex switch
        {
            1 => ScanTraversalMode.OverlapSignaturePage,
            2 => ScanTraversalMode.SafeBandViewport,
            3 => ScanTraversalMode.CalibratedPage,
            4 => ScanTraversalMode.LegacyThirdRow,
            _ => ScanTraversalMode.FromProfile
        };
    }

    private void DetectWindow()
    {
        try
        {
            var window = GameWindow.Find(_processBox.Text.Trim());
            if (_bringToFront.Checked)
            {
                window.BringToFront();
            }

            _statusLabel.Text = "窗口检测成功";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "窗口检测失败";
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenOutputDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_lastOutputDirectory) && Directory.Exists(_lastOutputDirectory))
        {
            Process.Start(new ProcessStartInfo { FileName = _lastOutputDirectory, UseShellExecute = true });
        }
    }

    private void SetOutputDirectory(string? outputDirectory)
    {
        _lastOutputDirectory = outputDirectory;
        var canOpen = !string.IsNullOrWhiteSpace(outputDirectory) && Directory.Exists(outputDirectory);
        _openOutputButton.Enabled = canOpen;
        _outputLink.Enabled = canOpen;
        _outputLink.Text = canOpen ? outputDirectory! : "扫描完成后显示产物文件夹";
    }

    private void SetScanningState(bool scanning)
    {
        _startButton.Enabled = !scanning;
        _stopButton.Enabled = scanning;
        _detectButton.Enabled = !scanning;
        _advancedToggleButton.Enabled = !scanning;
    }

    private void ToggleAdvancedSettings()
    {
        _advancedExpanded = !_advancedExpanded;
        if (_advancedGroup is not null)
        {
            _advancedGroup.Visible = _advancedExpanded;
        }

        _advancedToggleButton.Text = _advancedExpanded ? "收起高级设置" : "展开高级设置";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.RegisterHotKey(Handle, StopHotKeyId, NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkC);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        NativeMethods.UnregisterHotKey(Handle, StopHotKeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WmHotKey && m.WParam.ToInt32() == StopHotKeyId)
        {
            _scanCancellation?.Cancel();
            return;
        }

        base.WndProc(ref m);
    }

    private static TableLayoutPanel CreateGroup(Control parent, string title)
    {
        return CreateGroup(parent, title, out _);
    }

    private static TableLayoutPanel CreateGroup(Control parent, string title, out GroupBox group)
    {
        group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Padding = new Padding(10, 8, 10, 10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Margin = new Padding(0)
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        group.Controls.Add(table);
        parent.Controls.Add(group);
        return table;
    }

    private static void AddLabel(Control parent, string text)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 2),
            Margin = new Padding(0)
        });
    }

    private static void AddNumericSetting(TableLayoutPanel parent, string label, NumericUpDown numeric, int minimum, int maximum)
    {
        var panel = new TableLayoutPanel
        {
            RowCount = 2,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 8, 6)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        panel.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 2),
            Margin = new Padding(0)
        });

        numeric.Minimum = minimum;
        numeric.Maximum = maximum;
        ConfigureSingleLine(numeric);
        panel.Controls.Add(numeric);

        var index = parent.Controls.Count;
        parent.Controls.Add(panel, index % 2, index / 2);
    }

    private static void ConfigureSingleLine(Control control)
    {
        control.Dock = DockStyle.Top;
        control.Margin = new Padding(0, 0, 0, 4);
        control.Height = 26;
    }

    private static void ConfigureButton(Button button)
    {
        button.Dock = DockStyle.Fill;
        button.Height = 34;
        button.Margin = new Padding(2, 0, 2, 0);
    }

    private static Size FitInitialSize(Size preferred)
    {
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        return new Size(
            Math.Min(preferred.Width, Math.Max(430, workArea.Width - 80)),
            Math.Min(preferred.Height, Math.Max(620, workArea.Height - 80)));
    }

    private sealed class NoOpScanProgress : IProgress<ScanProgress>
    {
        public static readonly NoOpScanProgress Instance = new();

        private NoOpScanProgress()
        {
        }

        public void Report(ScanProgress value)
        {
            value.DebugImage?.Dispose();
        }
    }
}
