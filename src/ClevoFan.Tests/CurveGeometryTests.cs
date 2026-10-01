using System.Drawing;
using ClevoFan.Core;

namespace ClevoFan.Tests;

public class CurveGeometryTests
{
    private static readonly int[] Default = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];
    //非单调、两端不同的曲线，更容易暴露端点和方向错误
    private static readonly int[] Odd = [60, 100, 0, 45, 90, 10, 75, 5, 50, 33];
    private readonly CurveGeometry _g = new(new Size(600, 240), 1.25f);

    [Fact]
    public void 坐标范围与边距()
    {
        Assert.Equal(CurveGeometry.MarginLeft * 1.25f, _g.Plot.Left);
        Assert.Equal(600 - CurveGeometry.MarginRight * 1.25f, _g.Plot.Right, 3);
        Assert.Equal(_g.Plot.Left, _g.X(CurveGeometry.MinTemp));
        Assert.Equal(_g.Plot.Right, _g.X(CurveGeometry.MaxTemp), 3);
        Assert.Equal(_g.Plot.Bottom, _g.Y(0));
        Assert.Equal(_g.Plot.Top, _g.Y(100), 3);
    }

    [Fact]
    public void 纵坐标与负载互相换算()
    {
        for (int p = 0; p <= 100; p++)
            Assert.Equal(p, _g.PercentAt(_g.Y(p)));
        Assert.Equal(100, _g.PercentAt(_g.Plot.Top - 50));
        Assert.Equal(0, _g.PercentAt(_g.Plot.Bottom + 50));
    }

    [Fact]
    public void 控制点位于档位阈值()
    {
        var h = _g.Handle(Default, 2);
        Assert.Equal(_g.X(80), h.X);
        Assert.Equal(_g.Y(70), h.Y);
        Assert.Equal(_g.X(45), _g.Handle(Default, 9).X);
    }

    [Fact]
    public void 点击命中最近的控制点()
    {
        var h = _g.Handle(Default, 3);
        Assert.Equal(3, _g.HitTest(Default, h, 8));
        Assert.Equal(3, _g.HitTest(Default, new PointF(h.X + 5, h.Y - 4), 8));
        Assert.Null(_g.HitTest(Default, new PointF(h.X + 20, h.Y), 8));
        //两个点都在半径内时取更近的
        var a = _g.Handle(Odd, 7);
        var b = _g.Handle(Odd, 8);
        var nearA = new PointF(a.X + (b.X - a.X) * 0.3f, a.Y + (b.Y - a.Y) * 0.3f);
        Assert.Equal(7, _g.HitTest(Odd, nearA, 1000));
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void 阶梯曲线与控制算法一致(int[] curve)
    {
        var line = _g.StepLine(curve);
        for (int temp = CurveGeometry.MinTemp; temp <= CurveGeometry.MaxTemp; temp++)
        {
            int expected = FanCurve.Step(temp, curve, 0, 0).Percent;
            Assert.Equal(_g.Y(expected), StepValueAt(line, _g.X(temp)), 3);
        }
    }

    [Theory]
    [MemberData(nameof(Curves))]
    public void 线性曲线与控制算法一致(int[] curve)
    {
        var line = _g.LinearLine(curve);
        for (int temp = CurveGeometry.MinTemp; temp <= CurveGeometry.MaxTemp; temp++)
        {
            int expected = FanCurve.Linear(temp, curve, 0, 0).Percent;
            double drawn = _g.PercentAt(LinearValueAt(line, _g.X(temp)));
            //算法按 +0.5 截断取整，图上按四舍五入，最多差 1
            Assert.InRange(drawn, expected - 1, expected + 1);
        }
    }

    public static TheoryData<int[]> Curves => new() { Default, Odd };

    //阶梯线由水平线段组成，阈值处取右侧（升档后）的值
    private static float StepValueAt(PointF[] line, float x)
    {
        for (int i = 0; i < line.Length; i += 2)
        {
            bool last = i == line.Length - 2;
            if (x >= line[i].X - 0.001f && (x < line[i + 1].X - 0.001f || last))
                return line[i].Y;
        }
        throw new InvalidOperationException($"x={x} 不在曲线范围内");
    }

    private static float LinearValueAt(PointF[] line, float x)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            var (a, b) = (line[i], line[i + 1]);
            if (x >= a.X - 0.001f && x <= b.X + 0.001f)
                return b.X - a.X < 0.001f ? a.Y : a.Y + (b.Y - a.Y) * (x - a.X) / (b.X - a.X);
        }
        throw new InvalidOperationException($"x={x} 不在曲线范围内");
    }
}
