using System.ComponentModel;
using System.Drawing.Drawing2D;
using ClevoFan.Core;

namespace ClevoFan.Tray;

/// <summary>
/// 风扇曲线编辑器：拖动控制点修改当前曲线的负载，方向键微调（Shift 每次 5%）、左右键切换控制点。
/// 另一条曲线淡显，竖线标出当前温度。坐标换算见 <see cref="CurveGeometry"/>。
/// </summary>
internal sealed class CurveEditor : Control
{
    private static readonly Color[] FanColors = [Color.FromArgb(31, 119, 180), Color.FromArgb(214, 95, 0)];
    private static readonly string[] FanNames = ["CPU", "GPU"];

    private readonly int[][] _curves = [new int[FanConfig.LevelCount], new int[FanConfig.LevelCount]];
    private readonly int?[] _temps = new int?[2];
    private int _activeFan;
    private bool _linear;
    private int? _drag;
    private int? _selected;

    public CurveEditor()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = SystemColors.Window;
        AccessibleName = "风扇曲线";
        Height = FontHeight * HeightInLines;
    }

    //表格布局对非自动大小的控件直接使用其当前尺寸，所以按字体高度（已随 DPI 缩放）设置高度
    private const int HeightInLines = 11;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = FontHeight * HeightInLines;
    }

    /// <summary>用户拖动或用键盘修改了曲线。参数为风扇编号（0 CPU，1 GPU）。</summary>
    public event Action<int>? CurveChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ActiveFan
    {
        get => _activeFan;
        set
        {
            _activeFan = value;
            _selected = null;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Linear
    {
        get => _linear;
        set
        {
            _linear = value;
            Invalidate();
        }
    }

    public IReadOnlyList<int> Curve(int fan) => _curves[fan];

    public void SetCurve(int fan, int level, int percent)
    {
        if (_curves[fan][level] == percent)
            return;
        _curves[fan][level] = percent;
        Invalidate();
    }

    public void SetTemperatures(int? cpu, int? gpu)
    {
        if (_temps[0] == cpu && _temps[1] == gpu)
            return;
        (_temps[0], _temps[1]) = (cpu, gpu);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(200, FontHeight * HeightInLines);

    private float DpiScale => DeviceDpi / 96f;

    private CurveGeometry Geometry => new(ClientSize, DpiScale);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var geo = Geometry;
        var plot = geo.Plot;
        float s = DpiScale;

        using var grid = new Pen(Color.FromArgb(40, ForeColor), 1);
        using var text = new SolidBrush(SystemColors.GrayText);
        foreach (int temp in FanConfig.Thresholds)
        {
            g.DrawLine(grid, geo.X(temp), plot.Top, geo.X(temp), plot.Bottom);
            DrawCentered(g, temp + "℃", text, geo.X(temp), plot.Bottom + 4 * s);
        }
        for (int p = 0; p <= 100; p += 20)
        {
            g.DrawLine(grid, plot.Left, geo.Y(p), plot.Right, geo.Y(p));
            var size = g.MeasureString(p + "%", Font);
            g.DrawString(p + "%", Font, text, plot.Left - size.Width - 4 * s, geo.Y(p) - size.Height / 2);
        }
        g.DrawRectangle(SystemPens.ControlDark, plot.X, plot.Y, plot.Width, plot.Height);

        //当前温度
        for (int fan = 0; fan < 2; fan++)
        {
            if (_temps[fan] is not int t || t < CurveGeometry.MinTemp || t > CurveGeometry.MaxTemp)
                continue;
            using var pen = new Pen(Color.FromArgb(160, FanColors[fan]), 1.5f * s) { DashStyle = DashStyle.Dash };
            g.DrawLine(pen, geo.X(t), plot.Top, geo.X(t), plot.Bottom);
            using var brush = new SolidBrush(FanColors[fan]);
            g.DrawString($"{FanNames[fan]} {t}℃", Font, brush, geo.X(t) + 3 * s, plot.Top + 2 * s + fan * FontHeight);
        }

        //先画另一条曲线（淡显），再画正在编辑的曲线和控制点
        foreach (int fan in new[] { 1 - _activeFan, _activeFan })
        {
            bool active = fan == _activeFan;
            var color = active ? FanColors[fan] : Color.FromArgb(90, FanColors[fan]);
            using var pen = new Pen(color, (active ? 2.5f : 1.5f) * s);
            g.DrawLines(pen, _linear ? geo.LinearLine(_curves[fan]) : geo.StepLine(_curves[fan]));
            if (!active)
                continue;
            float r = 4.5f * s;
            using var fill = new SolidBrush(FanColors[fan]);
            for (int level = 0; level < FanConfig.LevelCount; level++)
            {
                var h = geo.Handle(_curves[fan], level);
                bool selected = level == _selected;
                g.FillEllipse(selected ? Brushes.White : fill, h.X - r, h.Y - r, 2 * r, 2 * r);
                g.DrawEllipse(selected ? pen : Pens.White, h.X - r, h.Y - r, 2 * r, 2 * r);
            }
            if (_selected is int sel)
            {
                var h = geo.Handle(_curves[fan], sel);
                g.DrawString($"{_curves[fan][sel]}%", Font, fill, h.X + 6 * s, h.Y - FontHeight - 2 * s);
            }
        }

        if (Focused)
            ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left)
            return;
        var hit = Geometry.HitTest(_curves[_activeFan], e.Location, 8 * DpiScale);
        _drag = hit;
        _selected = hit ?? _selected;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var geo = Geometry;
        if (_drag is int level)
        {
            Set(level, geo.PercentAt(e.Y));
            return;
        }
        Cursor = geo.HitTest(_curves[_activeFan], e.Location, 8 * DpiScale) is null ? Cursors.Default : Cursors.SizeNS;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _drag = null;
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & ~Keys.Shift) is Keys.Up or Keys.Down or Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        int step = e.Shift ? 5 : 1;
        //控制点按温度从低到高排列：左键选更低的温度（档位下标更大）
        switch (e.KeyCode)
        {
            case Keys.Left:
                _selected = Math.Min(FanConfig.LevelCount - 1, (_selected ?? -1) + 1);
                break;
            case Keys.Right:
                _selected = Math.Max(0, (_selected ?? FanConfig.LevelCount) - 1);
                break;
            case Keys.Up when _selected is int up:
                Set(up, Math.Min(100, _curves[_activeFan][up] + step));
                break;
            case Keys.Down when _selected is int down:
                Set(down, Math.Max(0, _curves[_activeFan][down] - step));
                break;
            default:
                return;
        }
        e.Handled = true;
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _drag = null;
        Invalidate();
    }

    private void Set(int level, int percent)
    {
        if (_curves[_activeFan][level] == percent)
            return;
        _curves[_activeFan][level] = percent;
        Invalidate();
        CurveChanged?.Invoke(_activeFan);
    }

    private void DrawCentered(Graphics g, string s, Brush brush, float x, float y)
    {
        var size = g.MeasureString(s, Font);
        g.DrawString(s, Font, brush, x - size.Width / 2, y);
    }
}
