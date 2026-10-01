using ClevoFan.Core;

namespace ClevoFan.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _forceCooling;
    private readonly ToolStripMenuItem _autostart;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private SettingsForm? _settings;
    private bool _polling;
    private string? _lastExternal;

    public TrayContext(bool showSettings)
    {
        _forceCooling = new ToolStripMenuItem("强制冷却", null, async (_, _) => await ToggleForcedCoolingAsync());
        _autostart = new ToolStripMenuItem("登录时启动托盘", null, (_, _) => ToggleAutostart()) { Checked = Autostart.IsEnabled };
        var menu = new ContextMenuStrip();
        menu.Items.Add("设置…", null, (_, _) => ShowSettings());
        menu.Items.Add(_forceCooling);
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出托盘（风扇控制服务继续运行）", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            Text = "Clevo 风扇控制",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowSettings();
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
        if (showSettings)
            ShowSettings();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _settings?.Close();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private async Task PollAsync()
    {
        if (_polling)
            return;
        _polling = true;
        try
        {
            var response = await ServiceConnection.TrySendAsync(new PipeRequest { Command = FanPipe.Commands.Status });
            var status = response?.Status;
            _icon.Text = status is null ? "Clevo 风扇控制：服务未运行" : StatusText.Tooltip(status);
            //冲突刚出现时提醒一次
            var external = status?.ExternalControlMessage;
            if (external is not null && _lastExternal is null)
                _icon.ShowBalloonTip(15000, "Clevo 风扇控制：检测到冲突", external, ToolTipIcon.Warning);
            _lastExternal = external;
            _forceCooling.Checked = status?.ForcedCooling ?? false;
            _forceCooling.Enabled = status is not null;
            _settings?.ShowStatus(status);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task ToggleForcedCoolingAsync()
    {
        var response = await ServiceConnection.TrySendAsync(new PipeRequest { Command = FanPipe.Commands.ForceCooling, On = !_forceCooling.Checked });
        if (response is not { Ok: true })
            _icon.ShowBalloonTip(3000, "Clevo 风扇控制", response?.Error ?? "风扇控制服务未运行", ToolTipIcon.Warning);
        await PollAsync();
    }

    private void ToggleAutostart()
    {
        Autostart.Set(!_autostart.Checked);
        _autostart.Checked = Autostart.IsEnabled;
    }

    private void ShowSettings()
    {
        if (_settings is null || _settings.IsDisposed)
        {
            _settings = new SettingsForm();
            _settings.FormClosed += (_, _) => _settings = null;
            _settings.Show();
        }
        else
        {
            if (_settings.WindowState == FormWindowState.Minimized)
                _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
        }
    }
}
