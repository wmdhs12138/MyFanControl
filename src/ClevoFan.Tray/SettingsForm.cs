using ClevoFan.Core;

namespace ClevoFan.Tray;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown[,] _curve = new NumericUpDown[2, FanConfig.LevelCount];
    private readonly CurveEditor _editor = new() { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };
    private readonly RadioButton _editCpu = new() { Text = "编辑 CPU 曲线", AutoSize = true, Checked = true };
    private readonly RadioButton _editGpu = new() { Text = "编辑 GPU 曲线", AutoSize = true };
    private bool _syncing;
    private readonly CheckBox _takeOver = new() { Text = "接管风扇控制（关闭时由 EC 自动控制）", AutoSize = true };
    private readonly CheckBox _linear = new() { Text = "线性控制（相邻两档之间平滑过渡）", AutoSize = true };
    private readonly NumericUpDown _transition = Number(0, 10);
    private readonly NumericUpDown _interval = Number(1, 5);
    private readonly NumericUpDown _forceTemp = Number(40, 90);
    private readonly Label _cpuStatus = new() { AutoSize = true };
    private readonly Label _gpuStatus = new() { AutoSize = true };
    private readonly Label _state = new() { AutoSize = true };
    private readonly Label _message = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Button _save = new() { Text = "保存", AutoSize = true };
    private readonly Button _defaults = new() { Text = "恢复默认值", AutoSize = true };
    private readonly Button _reload = new() { Text = "重新读取", AutoSize = true };
    private readonly TableLayoutPanel _root = new() { ColumnCount = 1, AutoSize = true, Padding = new Padding(12) };
    private readonly CheckBox _gpuLimit = new() { Text = "限制 GPU 最高频率（只限制，不超频）", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly NumericUpDown _gpuMax = new()
    {
        Minimum = FanConfig.MinGpuClockMHz,
        Maximum = FanConfig.MaxGpuClockMHz,
        Increment = 15,
        Width = 80,
        TextAlign = HorizontalAlignment.Right,
    };
    private readonly Label _gpuInfo = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Label _gpuLive = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly System.Windows.Forms.Timer _liveTimer = new() { Interval = 2000 };
    private bool _gpuAvailable = true;
    private bool _gpuRangeSet;
    private bool _liveBusy;

    public SettingsForm()
    {
        Text = "Clevo 风扇控制";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        //按 96 DPI 设计，控件中写死的像素尺寸随系统缩放放大
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var root = _root;
        root.Controls.Add(Header("状态"));
        root.Controls.Add(_cpuStatus);
        root.Controls.Add(_gpuStatus);
        root.Controls.Add(_state);
        root.Controls.Add(Header("风扇曲线（负载 %）"));
        var editBar = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        editBar.Controls.AddRange([_editCpu, _editGpu, Cell("拖动圆点调整；方向键微调，按住 Shift 每次 5%")]);
        root.Controls.Add(editBar);
        root.Controls.Add(_editor);
        root.Controls.Add(BuildCurveTable());
        root.Controls.Add(Header("选项"));
        root.Controls.Add(BuildOptions());
        root.Controls.Add(Header("GPU 限频"));
        root.Controls.Add(BuildGpu());
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange([_save, _defaults, _reload]);
        root.Controls.Add(buttons);
        root.Controls.Add(_message);
        Controls.Add(root);

        AcceptButton = _save;
        _save.Click += async (_, _) => await SaveAsync();
        _defaults.Click += (_, _) => Fill(new FanConfig { TakeOver = _takeOver.Checked });
        _reload.Click += async (_, _) => await LoadConfigAsync();
        Load += async (_, _) =>
        {
            FitToContent();
            _liveTimer.Start();
            await LoadConfigAsync();
            await RefreshGpuLiveAsync();
        };
        FormClosed += (_, _) => _liveTimer.Dispose();
        DpiChanged += (_, _) => BeginInvoke(FitToContent);
        _liveTimer.Tick += async (_, _) => await RefreshGpuLiveAsync();
        _gpuLimit.CheckedChanged += (_, _) => _gpuMax.Enabled = _gpuAvailable && _gpuLimit.Checked;
        _linear.CheckedChanged += (_, _) => _editor.Linear = _linear.Checked;
        _editCpu.CheckedChanged += (_, _) => _editor.ActiveFan = _editCpu.Checked ? 0 : 1;
        _editor.CurveChanged += fan =>
        {
            //图上拖动后同步到数字框
            _syncing = true;
            try
            {
                for (int i = 0; i < FanConfig.LevelCount; i++)
                    _curve[fan, i].Value = _editor.Curve(fan)[i];
            }
            finally
            {
                _syncing = false;
            }
        };
        //读到服务端配置之前显示默认值，并禁止保存，避免把没读到的配置写回去
        Fill(new FanConfig());
        _save.Enabled = false;
        ShowStatus(null);
    }

    //Form.AutoSize 在高 DPI 下算不准内容尺寸，按内容的首选尺寸显式设置客户区大小
    private void FitToContent()
    {
        _root.PerformLayout();
        ClientSize = _root.GetPreferredSize(Size.Empty);
    }

    public void ShowStatus(FanStatus? s)
    {
        if (s is null)
        {
            _cpuStatus.Text = "风扇控制服务未运行";
            _gpuStatus.Text = "";
            _state.Text = "";
            return;
        }
        _cpuStatus.Text = StatusText.Fan("CPU", s.CpuTemp, s.CpuDutyPercent, s.CpuRpm, s.CpuTargetPercent);
        _gpuStatus.Text = StatusText.Fan("GPU", s.GpuTemp, s.GpuDutyPercent, s.GpuRpm, s.GpuTargetPercent);
        _state.Text = "状态：" + StatusText.State(s);
        _editor.SetTemperatures(s.CpuTemp > 0 ? s.CpuTemp : null, s.GpuTemp > 0 ? s.GpuTemp : null);
        ShowGpu(s);
    }

    private void ShowGpu(FanStatus s)
    {
        _gpuAvailable = s.GpuName is not null;
        _gpuLimit.Enabled = _gpuAvailable;
        _gpuMax.Enabled = _gpuAvailable && _gpuLimit.Checked;
        if (!_gpuAvailable)
        {
            _gpuInfo.Text = s.GpuMessage ?? "没有可用的 NVIDIA GPU";
            return;
        }
        if (!_gpuRangeSet && s.GpuMinClockMHz is int min && s.GpuMaxClockMHz is int max)
        {
            //改为显卡实际支持的范围，超出的值收回到范围内
            var value = _gpuMax.Value;
            _gpuMax.Maximum = Math.Max(max, FanConfig.MinGpuClockMHz);
            _gpuMax.Minimum = Math.Max(min, FanConfig.MinGpuClockMHz);
            _gpuMax.Value = Math.Clamp(value, _gpuMax.Minimum, _gpuMax.Maximum);
            _gpuRangeSet = true;
        }
        var applied = s.GpuClockLimitMHz is int limit ? $"当前限制为 {limit} MHz" : "当前未限频";
        _gpuInfo.Text = $"{s.GpuName}，可设 {s.GpuMinClockMHz}-{s.GpuMaxClockMHz} MHz。{applied}" + (s.GpuMessage is { } m ? $"\n{m}" : "");
    }

    //只在窗口打开时读取实时频率；独显未通电时服务不会去唤醒它
    private async Task RefreshGpuLiveAsync()
    {
        if (_liveBusy || !_gpuAvailable)
            return;
        _liveBusy = true;
        try
        {
            var response = await ServiceConnection.TrySendAsync(new PipeRequest { Command = FanPipe.Commands.GpuLive });
            _gpuLive.Text = response?.GpuLive switch
            {
                { PoweredOn: true } g => $"当前频率 {g.ClockMHz} MHz，利用率 {g.UtilizationPercent}%",
                { PoweredOn: false } => "独显未通电（不读取实时频率，避免唤醒）",
                null => response?.Error ?? "",
            };
        }
        finally
        {
            _liveBusy = false;
        }
    }

    private TableLayoutPanel BuildGpu()
    {
        var table = new TableLayoutPanel { ColumnCount = 1, AutoSize = true };
        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
        row.Controls.AddRange([_gpuLimit, _gpuMax, Cell("MHz")]);
        table.Controls.Add(row);
        table.Controls.Add(_gpuInfo);
        table.Controls.Add(_gpuLive);
        return table;
    }

    private TableLayoutPanel BuildCurveTable()
    {
        var table = new TableLayoutPanel { ColumnCount = FanConfig.LevelCount + 1, AutoSize = true };
        table.Controls.Add(Cell(""), 0, 0);
        for (int i = 0; i < FanConfig.LevelCount; i++)
        {
            int t = FanConfig.Thresholds[i];
            table.Controls.Add(Cell(i == FanConfig.LevelCount - 1 ? "<50℃" : $"≥{t}℃"), i + 1, 0);
        }
        string[] names = ["CPU", "GPU"];
        for (int fan = 0; fan < 2; fan++)
        {
            table.Controls.Add(Cell(names[fan]), 0, fan + 1);
            for (int i = 0; i < FanConfig.LevelCount; i++)
            {
                _curve[fan, i] = Number(0, 100);
                _curve[fan, i].Width = 52;
                table.Controls.Add(_curve[fan, i], i + 1, fan + 1);
                int f = fan, level = i;
                _curve[fan, i].ValueChanged += (_, _) =>
                {
                    if (!_syncing)
                        _editor.SetCurve(f, level, (int)_curve[f, level].Value);
                };
            }
        }
        return table;
    }

    private TableLayoutPanel BuildOptions()
    {
        var table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        table.Controls.Add(_takeOver, 0, 0);
        table.SetColumnSpan(_takeOver, 2);
        table.Controls.Add(_linear, 0, 1);
        table.SetColumnSpan(_linear, 2);
        AddRow(table, 2, "过渡温度（℃，降温时延迟降档）", _transition);
        AddRow(table, 3, "更新间隔（秒）", _interval);
        AddRow(table, 4, "强制冷却目标温度（℃）", _forceTemp);
        return table;
    }

    private async Task LoadConfigAsync()
    {
        var response = await ServiceConnection.TrySendAsync(new PipeRequest { Command = FanPipe.Commands.GetConfig });
        if (response?.Config is { } config)
        {
            Fill(config);
            _save.Enabled = true;
            _message.Text = "";
        }
        else
        {
            _message.Text = response?.Error ?? "风扇控制服务未运行，无法读取配置";
        }
    }

    private async Task SaveAsync()
    {
        var config = Collect();
        var errors = config.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join("\n", errors), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _save.Enabled = false;
        try
        {
            var response = await ServiceConnection.TrySendAsync(new PipeRequest { Command = FanPipe.Commands.SetConfig, Config = config });
            _message.Text = response switch
            {
                { Ok: true } => $"已保存（{DateTime.Now:HH:mm:ss}）",
                not null => "保存失败：" + response.Error,
                null => "风扇控制服务未运行，无法保存",
            };
        }
        finally
        {
            _save.Enabled = true;
        }
    }

    private void Fill(FanConfig config)
    {
        for (int i = 0; i < FanConfig.LevelCount; i++)
        {
            _curve[0, i].Value = config.CpuCurve[i];
            _curve[1, i].Value = config.GpuCurve[i];
        }
        _takeOver.Checked = config.TakeOver;
        _linear.Checked = config.Linear;
        _transition.Value = config.TransitionTemp;
        _interval.Value = config.UpdateIntervalSeconds;
        _forceTemp.Value = config.ForceCoolingTemp;
        _gpuLimit.Checked = config.GpuClockLimitEnabled;
        //未设置过（0）时显示最高频率，即不限
        _gpuMax.Value = Math.Clamp(config.GpuMaxClockMHz > 0 ? config.GpuMaxClockMHz : _gpuMax.Maximum, _gpuMax.Minimum, _gpuMax.Maximum);
        _gpuMax.Enabled = _gpuAvailable && _gpuLimit.Checked;
    }

    private FanConfig Collect() => new()
    {
        CpuCurve = Enumerable.Range(0, FanConfig.LevelCount).Select(i => (int)_curve[0, i].Value).ToArray(),
        GpuCurve = Enumerable.Range(0, FanConfig.LevelCount).Select(i => (int)_curve[1, i].Value).ToArray(),
        TakeOver = _takeOver.Checked,
        Linear = _linear.Checked,
        TransitionTemp = (int)_transition.Value,
        UpdateIntervalSeconds = (int)_interval.Value,
        ForceCoolingTemp = (int)_forceTemp.Value,
        GpuClockLimitEnabled = _gpuLimit.Checked,
        GpuMaxClockMHz = (int)_gpuMax.Value,
    };

    private static void AddRow(TableLayoutPanel table, int row, string label, Control control)
    {
        table.Controls.Add(Cell(label), 0, row);
        table.Controls.Add(control, 1, row);
    }

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        Margin = new Padding(0, 10, 0, 4),
    };

    private static Label Cell(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 3, 3) };

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 60, TextAlign = HorizontalAlignment.Right };
}
