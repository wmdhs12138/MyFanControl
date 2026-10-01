using System.Drawing;

namespace ClevoFan.Core;

/// <summary>
/// 曲线编辑器的坐标换算：横轴温度 <see cref="MinTemp"/>-<see cref="MaxTemp"/>℃，纵轴负载 0-100%。
/// 不依赖界面库，便于测试。边距按 96 DPI 设计，乘以缩放比例。
/// 控制点 k 位于（档位阈值 k，该档负载），与 <see cref="FanCurve"/> 使用的曲线定义一致。
/// </summary>
public sealed class CurveGeometry(Size client, float scale)
{
    public const int MinTemp = 40;
    public const int MaxTemp = 95;
    public const float MarginLeft = 40;
    public const float MarginTop = 10;
    public const float MarginRight = 12;
    public const float MarginBottom = 24;

    public RectangleF Plot { get; } = RectangleF.FromLTRB(MarginLeft * scale, MarginTop * scale,
        Math.Max(MarginLeft * scale + 1, client.Width - MarginRight * scale), Math.Max(MarginTop * scale + 1, client.Height - MarginBottom * scale));

    public float X(double temp) => Plot.Left + (float)((temp - MinTemp) / (MaxTemp - MinTemp) * Plot.Width);

    public float Y(double percent) => Plot.Bottom - (float)(percent / 100.0 * Plot.Height);

    /// <summary>纵坐标换算为负载，取整并限制在 0-100。</summary>
    public int PercentAt(float y) =>
        Math.Clamp((int)Math.Round((Plot.Bottom - y) / Plot.Height * 100, MidpointRounding.AwayFromZero), 0, 100);

    public PointF Handle(IReadOnlyList<int> curve, int level) => new(X(FanConfig.Thresholds[level]), Y(curve[level]));

    /// <summary>距离 <paramref name="p"/> 最近、且不超过 <paramref name="radius"/> 的控制点。</summary>
    public int? HitTest(IReadOnlyList<int> curve, PointF p, float radius)
    {
        int? best = null;
        float bestDistance = radius;
        for (int level = 0; level < FanConfig.LevelCount; level++)
        {
            var h = Handle(curve, level);
            float d = MathF.Sqrt((h.X - p.X) * (h.X - p.X) + (h.Y - p.Y) * (h.Y - p.Y));
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = level;
            }
        }
        return best;
    }

    /// <summary>
    /// 阶梯控制的曲线（不含过渡温度造成的降温滞后）：温度不低于档位阈值时取该档负载，
    /// 50℃ 以下统一取最低档。
    /// </summary>
    public PointF[] StepLine(IReadOnlyList<int> curve)
    {
        var t = FanConfig.Thresholds;
        var points = new List<PointF>();
        for (int k = FanConfig.LevelCount - 1; k >= 0; k--)
        {
            double start = k == FanConfig.LevelCount - 1 ? MinTemp : t[k];
            double end = k == 0 ? MaxTemp : t[k - 1];
            points.Add(new PointF(X(start), Y(curve[k])));
            points.Add(new PointF(X(end), Y(curve[k])));
        }
        return [.. points];
    }

    /// <summary>线性控制的曲线：相邻控制点之间连线，两端水平延伸。</summary>
    public PointF[] LinearLine(IReadOnlyList<int> curve)
    {
        var points = new List<PointF> { new(X(MinTemp), Y(curve[FanConfig.LevelCount - 1])) };
        for (int k = FanConfig.LevelCount - 1; k >= 0; k--)
            points.Add(Handle(curve, k));
        points.Add(new PointF(X(MaxTemp), Y(curve[0])));
        return [.. points];
    }
}
