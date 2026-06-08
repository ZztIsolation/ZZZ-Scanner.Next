using System.Diagnostics;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ui;

public sealed class MainForm : Form
{
    private const int StopHotKeyId = 0x5A5A;

    private readonly ScanProfileFile _profiles;
    private readonly WikiData _wikiData;
    private readonly ScanController _controller;

    private readonly ComboBox _profileCombo = new();
    private readonly ComboBox _traversalModeCombo = new();
    private readonly TextBox _processBox = new();
    private readonly NumericUpDown _maxItems = new();
    private readonly CheckBox _rarityS = new();
    private readonly CheckBox _rarityA = new();
    private readonly CheckBox _onlyLevel15 = new();
    private readonly CheckBox _bringToFront = new();
    private readonly CheckBox _showDebugImages = new();
    private readonly ComboBox _ocrEngineCombo = new();
    private readonly CheckBox _highSpeedOcr = new();
    private readonly NumericUpDown _ocrWorkers = new();
    private readonly NumericUpDown _ocrSampleLimit = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _detectButton = new();
    private readonly Button _previewButton = new();
    private readonly Button _openOutputButton = new();
    private readonly TextBox _log = new();
    private readonly DataGridView _grid = new();
    private readonly PictureBox _debugPreview = new();
    private readonly Label _counterLabel = new();
    private readonly ProgressBar _progress = new();

    private CancellationTokenSource? _scanCancellation;
    private string? _lastOutputDirectory;

    public MainForm()
    {
        Text = "ZZZ Scanner Next - DPI Fix";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        MinimumSize = new Size(840, 560);
        Size = FitInitialSize(new Size(1080, 720));
        StartPosition = FormStartPosition.CenterScreen;

        _profiles = ScanProfileFile.Load();
        _wikiData = WikiData.Load();
        _controller = new ScanController(_profiles, _wikiData);

        BuildUi();
        LoadDefaults();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var settingsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };
        root.Controls.Add(settingsScroll, 0, 0);

        var settings = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            RowCount = 24,
            ColumnCount = 1,
            Padding = new Padding(0, 0, 6, 0)
        };
        settings.RowStyles.Clear();
        for (var i = 0; i < 24; i++)
        {
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        settingsScroll.Controls.Add(settings);

        AddLabel(settings, "进程名");
        _processBox.Dock = DockStyle.Top;
        settings.Controls.Add(_processBox);

        AddLabel(settings, "扫描配置");
        _profileCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _profileCombo.Dock = DockStyle.Top;
        settings.Controls.Add(_profileCombo);

        AddLabel(settings, "遍历模式");
        _traversalModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _traversalModeCombo.Dock = DockStyle.Top;
        settings.Controls.Add(_traversalModeCombo);

        AddLabel(settings, "读取上限（0=不限制）");
        _maxItems.Minimum = 0;
        _maxItems.Maximum = 9999;
        _maxItems.Dock = DockStyle.Top;
        settings.Controls.Add(_maxItems);

        AddLabel(settings, "品质");
        var rarityPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
        _rarityS.Text = "S";
        _rarityA.Text = "A";
        rarityPanel.Controls.AddRange([_rarityS, _rarityA]);
        settings.Controls.Add(rarityPanel);

        _onlyLevel15.Text = "只读取15级驱动盘";
        _onlyLevel15.AutoSize = true;
        settings.Controls.Add(_onlyLevel15);

        _bringToFront.Text = "前置游戏窗口";
        _bringToFront.AutoSize = true;
        settings.Controls.Add(_bringToFront);

        _showDebugImages.Text = "临时显示调试截图";
        _showDebugImages.AutoSize = true;
        settings.Controls.Add(_showDebugImages);

        AddLabel(settings, "OCR引擎");
        _ocrEngineCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _ocrEngineCombo.Dock = DockStyle.Top;
        settings.Controls.Add(_ocrEngineCombo);

        _highSpeedOcr.Text = "高速 OCR（自动多线程）";
        _highSpeedOcr.AutoSize = true;
        settings.Controls.Add(_highSpeedOcr);

        AddLabel(settings, "OCR线程（0=自动）");
        _ocrWorkers.Minimum = 0;
        _ocrWorkers.Maximum = 4;
        _ocrWorkers.Dock = DockStyle.Top;
        settings.Controls.Add(_ocrWorkers);

        AddLabel(settings, "OCR样本保存上限（0=关闭）");
        _ocrSampleLimit.Minimum = 0;
        _ocrSampleLimit.Maximum = 2000;
        _ocrSampleLimit.Increment = 10;
        _ocrSampleLimit.Dock = DockStyle.Top;
        settings.Controls.Add(_ocrSampleLimit);

        _detectButton.Text = "检测窗口";
        _detectButton.Dock = DockStyle.Top;
        _detectButton.Click += (_, _) => DetectWindow();
        settings.Controls.Add(_detectButton);

        _previewButton.Text = "预览详情区";
        _previewButton.Dock = DockStyle.Top;
        _previewButton.Click += (_, _) => PreviewPanel();
        settings.Controls.Add(_previewButton);

        _startButton.Text = "开始扫描";
        _startButton.Dock = DockStyle.Top;
        _startButton.Height = 36;
        _startButton.Click += async (_, _) => await StartScanAsync();
        settings.Controls.Add(_startButton);

        _stopButton.Text = "停止（Ctrl+Shift+C）";
        _stopButton.Dock = DockStyle.Top;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => _scanCancellation?.Cancel();
        settings.Controls.Add(_stopButton);

        _openOutputButton.Text = "打开输出目录";
        _openOutputButton.Dock = DockStyle.Top;
        _openOutputButton.Enabled = false;
        _openOutputButton.Click += (_, _) => OpenOutputDirectory();
        settings.Controls.Add(_openOutputButton);

        _counterLabel.AutoSize = true;
        _counterLabel.Padding = new Padding(0, 12, 0, 6);
        settings.Controls.Add(_counterLabel);

        _progress.Dock = DockStyle.Top;
        _progress.Style = ProgressBarStyle.Blocks;
        settings.Controls.Add(_progress);

        var right = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1
        };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        root.Controls.Add(right, 1, 0);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.RowHeadersVisible = false;
        _grid.ScrollBars = ScrollBars.Both;
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Index", HeaderText = "序号", Width = 70, MinimumWidth = 60 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "名称", Width = 120, MinimumWidth = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "槽位", Width = 60, MinimumWidth = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Rarity", HeaderText = "品质", Width = 60, MinimumWidth = 55 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Level", HeaderText = "等级", Width = 75, MinimumWidth = 70 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Main", HeaderText = "主属性", Width = 180, MinimumWidth = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sub", HeaderText = "副属性", Width = 320, MinimumWidth = 180 });
        right.Controls.Add(_grid, 0, 0);

        _debugPreview.Dock = DockStyle.Fill;
        _debugPreview.BackColor = Color.Black;
        _debugPreview.SizeMode = PictureBoxSizeMode.Zoom;
        right.Controls.Add(_debugPreview, 0, 1);

        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.Font = new Font(FontFamily.GenericMonospace, 9);
        right.Controls.Add(_log, 0, 2);
    }

    private void LoadDefaults()
    {
        _processBox.Text = "ZenlessZoneZero";
        _maxItems.Value = 0;
        _rarityS.Checked = true;
        _rarityA.Checked = false;
        _onlyLevel15.Checked = true;
        _bringToFront.Checked = true;
        _highSpeedOcr.Checked = true;
        _ocrWorkers.Value = 0;
        _ocrSampleLimit.Value = 0;

        _ocrEngineCombo.Items.AddRange([
            "自动",
            "PP-OCRv5 通用",
            "ZZZ 高速字段感知"
        ]);
        _ocrEngineCombo.SelectedIndex = 0;

        _traversalModeCombo.Items.AddRange([
            "按配置（默认安全带）",
            "安全带扫描",
            "校准翻页",
            "旧版第3行"
        ]);
        _traversalModeCombo.SelectedIndex = 0;

        foreach (var profile in _profiles.Profiles)
        {
            _profileCombo.Items.Add(profile.Name);
        }

        if (_profileCombo.Items.Count > 0)
        {
            _profileCombo.SelectedIndex = 0;
        }

        UpdateCounters(new ScanProgress());
        AppendLog($"运行目录：{AppContext.BaseDirectory}");
        AppendLog($"DPI 修正版已载入：{_wikiData.DiscCatalog.Sets.Count} 个 wiki 套装名，{_wikiData.DiscCatalog.ExtraNameCandidates.Count} 个扩展候选名。");
        AppendLog("扫描时可按 Ctrl+Shift+C 全局停止。");
    }

    private async Task StartScanAsync()
    {
        var options = ReadOptions();
        if (options.Rarities.Count == 0)
        {
            MessageBox.Show("至少选择一个品质。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _grid.Rows.Clear();
        _log.Clear();
        _scanCancellation = new CancellationTokenSource();
        SetScanningState(true);

        var progress = new Progress<ScanProgress>(OnProgress);
        try
        {
            var result = await _controller.ScanAsync(options, progress, _scanCancellation.Token);
            _lastOutputDirectory = result.OutputDirectory;
            _openOutputButton.Enabled = Directory.Exists(_lastOutputDirectory);
            AppendLog($"导出文件：{result.ExportFile}");
        }
        catch (Exception ex)
        {
            AppendLog(ex.ToString());
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            SetScanningState(false);
        }
    }

    private ScanOptions ReadOptions()
    {
        var options = new ScanOptions
        {
            ProcessName = _processBox.Text.Trim(),
            ProfileName = _profileCombo.SelectedItem?.ToString() ?? "",
            TraversalMode = SelectedTraversalMode(),
            MaxItems = (int)_maxItems.Value,
            BringToFront = _bringToFront.Checked,
            ShowDebugImages = _showDebugImages.Checked,
            StopAtNonLevel15 = _onlyLevel15.Checked,
            OcrEngine = SelectedOcrEngineMode(),
            HighSpeedOcr = _highSpeedOcr.Checked,
            OcrWorkerCount = (int)_ocrWorkers.Value,
            OcrSampleLimit = (int)_ocrSampleLimit.Value
        };
        options.Rarities.Clear();
        if (_rarityS.Checked) options.Rarities.Add("S");
        if (_rarityA.Checked) options.Rarities.Add("A");
        return options;
    }

    private ScanTraversalMode SelectedTraversalMode()
    {
        return _traversalModeCombo.SelectedIndex switch
        {
            1 => ScanTraversalMode.SafeBandViewport,
            2 => ScanTraversalMode.CalibratedPage,
            3 => ScanTraversalMode.LegacyThirdRow,
            _ => ScanTraversalMode.FromProfile
        };
    }

    private OcrEngineMode SelectedOcrEngineMode()
    {
        return _ocrEngineCombo.SelectedIndex switch
        {
            1 => OcrEngineMode.PpOcrV5General,
            2 => OcrEngineMode.ZzzFastFieldAware,
            _ => OcrEngineMode.Auto
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

            AppendLog($"窗口检测成功：{window.ClientScreenRect}，DPI：{window.Dpi}，坐标倍率：{window.CoordinateScale:F2}");
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PreviewPanel()
    {
        try
        {
            var options = ReadOptions();
            var profile = _profiles.Profiles.First(p => p.Name == options.ProfileName);
            var window = GameWindow.Find(options.ProcessName);
            if (options.BringToFront)
            {
                window.BringToFront();
            }

            using var image = window.Capture(window.ToScreenRectangle(profile.Rectangle("detailPanel")));
            ShowDebugImage((Bitmap)image.Clone());
            AppendLog($"详情区预览已临时显示：{image.Width} x {image.Height}");
        }
        catch (Exception ex)
        {
            AppendLog(ex.Message);
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

    private void OnProgress(ScanProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            AppendLog(progress.Message);
        }

        if (progress.Item is not null)
        {
            AddItem(progress.Item);
        }

        if (progress.DebugImage is not null)
        {
            ShowDebugImage(progress.DebugImage);
        }

        UpdateCounters(progress);
    }

    private void AddItem(DriveDiscExport item)
    {
        var rowIndex = _grid.Rows.Add(
            item.Index,
            item.Name,
            item.Slot,
            item.Rarity,
            $"{item.Level}/{item.MaxLevel}",
            StatDictionaryToString(item.MainStat),
            string.Join("; ", item.SubStats.Select(StatDictionaryToString)));
        ScrollGridToLatest(rowIndex);
    }

    private void ScrollGridToLatest(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count)
        {
            return;
        }

        _grid.ClearSelection();
        _grid.Rows[rowIndex].Selected = true;
        _grid.CurrentCell = _grid.Rows[rowIndex].Cells[0];

        var displayedRows = Math.Max(1, _grid.DisplayedRowCount(includePartialRow: false));
        _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, rowIndex - displayedRows + 1);
    }

    private static string StatDictionaryToString(Dictionary<string, object> values)
    {
        return string.Join(", ", values.Select(kv => $"{kv.Key}: {FormatValue(kv.Value)}"));
    }

    private static string FormatValue(object value)
    {
        return value is JsonElement element ? element.ToString() : value.ToString() ?? "";
    }

    private void UpdateCounters(ScanProgress progress)
    {
        _counterLabel.Text = $"已访问 {progress.Visited}   入队 {progress.Queued}   完成 {progress.Completed}   失败 {progress.Failed}";
    }

    private void SetScanningState(bool scanning)
    {
        _startButton.Enabled = !scanning;
        _stopButton.Enabled = scanning;
        _detectButton.Enabled = !scanning;
        _previewButton.Enabled = !scanning;
        _progress.Style = scanning ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!NativeMethods.RegisterHotKey(Handle, StopHotKeyId, NativeMethods.ModControl | NativeMethods.ModShift, NativeMethods.VkC))
        {
            AppendLog("全局停止热键 Ctrl+Shift+C 注册失败，仍可点击“停止”按钮或退出背包停止。");
        }
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
            if (_scanCancellation is not null)
            {
                AppendLog("收到 Ctrl+Shift+C，正在停止扫描。");
                _scanCancellation.Cancel();
            }

            return;
        }

        base.WndProc(ref m);
    }

    private void AppendLog(string message)
    {
        _log.AppendText(message + Environment.NewLine);
    }

    private void ShowDebugImage(Image image)
    {
        var previous = _debugPreview.Image;
        _debugPreview.Image = image;
        previous?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _debugPreview.Image?.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void AddLabel(Control parent, string text)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Padding = new Padding(0, 10, 0, 2)
        });
    }

    private static Size FitInitialSize(Size preferred)
    {
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
        return new Size(
            Math.Min(preferred.Width, Math.Max(840, workArea.Width - 80)),
            Math.Min(preferred.Height, Math.Max(560, workArea.Height - 80)));
    }
}
