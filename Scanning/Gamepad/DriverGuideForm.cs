using System.ComponentModel;
using System.Diagnostics;

namespace ZZZScannerNext.Scanning.Gamepad;

internal sealed class DriverGuideForm : Form
{
    private readonly TextBox instructions = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly Button install = new() { Text = "安装随包驱动（需要管理员权限）", AutoSize = true };
    private bool installing;

    public DriverGuideForm(DriverInfo info)
    {
        Text = "ViGEmBus 安装引导";
        ClientSize = new Size(700, 430);
        MinimumSize = new Size(600, 360);
        StartPosition = FormStartPosition.CenterParent;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        var website = new Button { Text = "打开官方 1.22.0 下载页", AutoSize = true };
        website.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(DriverSetup.ReleaseUrl) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开浏览器"); }
        };
        install.Click += async (_, _) => await InstallAsync();
        actions.Controls.AddRange([install, website]);
        Controls.Add(instructions);
        Controls.Add(actions);
        ShowInfo(info);
    }

    private void ShowInfo(DriverInfo info)
    {
        install.Enabled = info.State == DriverState.Missing && File.Exists(DriverSetup.InstallerPath);
        instructions.Text = (info.Summary + "\n\n" +
            "不需要实体手柄。驱动让 Windows 创建虚拟 Xbox 设备，本工具负责发送手柄输入。\n\n" +
            "首次安装：\n1. 点击下方安装按钮，确认 Windows 管理员权限提示。\n" +
            "2. 按官方安装器提示完成安装；本工具不静默安装。\n" +
            "3. 如果安装器提示重启，请先保存工作并自行重启。\n" +
            "4. 关闭并重新打开本引导以检测状态，驱动运行后即可开始扫描。验证工具也可执行设备自检。\n\n" +
            "固定安装包：1.22.0；实际驱动：1.21.442.0。这两个版本号不同是正常的。\n" +
            "已安装其他版本时，不自动覆盖或卸载，以免影响其他手柄软件。\n" +
            "驱动未运行时，先重启并重新检测；状态仍异常可查看日志与官方说明。\n" +
            "ViGEmBus 已停止维护，本包用于兼容性测试。\n\n" +
            (File.Exists(DriverSetup.InstallerPath) ? "随包安装器已找到；启动前会验证固定 SHA-256。\n" : "没有找到随包安装器，请完整解压测试包，或打开官方发行页下载。\n") +
            "\n" + DriverSetup.Diagnostics(info)).Replace("\n", Environment.NewLine);
    }

    private async Task InstallAsync()
    {
        installing = true;
        install.Enabled = false;
        try
        {
            var current = await Task.Run(DriverSetup.Inspect);
            if (current.State != DriverState.Missing)
            {
                ShowInfo(current);
                MessageBox.Show(this, "检测到已有驱动或状态无法确认，不自动覆盖。请回主窗口重新检测。", "安装已停止");
                return;
            }
            await Task.Run(() => DriverSetup.VerifyInstaller(DriverSetup.InstallerPath));
            using var process = Process.Start(new ProcessStartInfo(DriverSetup.InstallerPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(DriverSetup.InstallerPath)
            }) ?? throw new InvalidOperationException("安装器未启动。");
            await process.WaitForExitAsync();
            var code = process.ExitCode;
            var result = await Task.Run(DriverSetup.Inspect);
            ShowInfo(result);
            var message = code is 3010 or 1641 ? "安装器要求重启，请保存工作并重启电脑。"
                : code == 0 ? "安装器已结束，请根据下面的实际驱动状态继续；如安装器要求重启，请先重启。"
                : $"安装器返回代码 {code}。可能取消或安装失败，请查看安装器提示。";
            MessageBox.Show(this, message + "\n\n" + result.Summary, "安装结果");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show(this, "已取消管理员权限请求，未启动安装器。", "安装已取消");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法安装驱动"); }
        finally
        {
            installing = false;
            ShowInfo(DriverSetup.Inspect());
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (installing)
        {
            e.Cancel = true;
            MessageBox.Show(this, "请先在官方安装器中完成或取消安装。", "安装器仍在运行");
        }
        base.OnFormClosing(e);
    }
}
