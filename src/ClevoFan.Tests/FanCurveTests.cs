using ClevoFan.Core;

namespace ClevoFan.Tests;

public class FanCurveTests
{
    //原程序的默认曲线：90+、85+、80+、75+、70+、65+、60+、55+、50+、50 以下
    private static readonly int[] Curve = [95, 80, 70, 55, 35, 30, 25, 18, 18, 18];

    [Fact]
    public void Step_README示例_升温到80立即升档_降温到77以下才降档()
    {
        //原贴说明：过渡温度 3 度，从 75 上升到 80 时转速提高到 70%；下降时需要低于 77 度才降到 55%
        var (p, level) = FanCurve.Step(75, Curve, 3, 0);
        Assert.Equal((55, 7), (p, level));
        (p, level) = FanCurve.Step(80, Curve, 3, level);
        Assert.Equal((70, 8), (p, level));
        foreach (var t in new[] { 79, 78, 77 })
        {
            (p, level) = FanCurve.Step(t, Curve, 3, level);
            Assert.Equal((70, 8), (p, level));
        }
        (p, level) = FanCurve.Step(76, Curve, 3, level);
        Assert.Equal((55, 7), (p, level));
    }

    [Fact]
    public void Step_升温时过渡区间不会提前升档()
    {
        var (p, level) = FanCurve.Step(79, Curve, 3, 7);
        Assert.Equal((55, 7), (p, level));
    }

    [Theory]
    [InlineData(95, 95, 10)]
    [InlineData(90, 95, 10)]
    [InlineData(85, 80, 9)]
    [InlineData(64, 25, 4)]
    [InlineData(50, 18, 2)]
    [InlineData(49, 18, 1)]
    [InlineData(20, 18, 1)]
    public void Step_从零开始按阈值取档(int temp, int percent, int level)
    {
        Assert.Equal((percent, level), FanCurve.Step(temp, Curve, 3, 0));
    }

    [Fact]
    public void Step_过渡温度为0时没有滞后()
    {
        Assert.Equal((55, 7), FanCurve.Step(79, Curve, 0, 8));
    }

    [Fact]
    public void Step_使用各自的曲线()
    {
        int[] custom = [100, 90, 80, 70, 60, 50, 40, 30, 20, 10];
        Assert.Equal((10, 1), FanCurve.Step(30, custom, 3, 0));
        Assert.Equal((20, 2), FanCurve.Step(52, custom, 3, 0));
    }

    [Theory]
    [InlineData(60, 25)]
    [InlineData(61, 26)]
    [InlineData(62, 27)]
    [InlineData(63, 28)]
    [InlineData(64, 29)]
    [InlineData(65, 30)]
    public void Linear_README示例_60到65每升1度加1(int temp, int percent)
    {
        Assert.Equal(percent, FanCurve.Linear(temp, Curve, 3, 0).Percent);
    }

    [Fact]
    public void Linear_降温时计算温度最多滞后过渡温度()
    {
        var r = FanCurve.Linear(70, Curve, 3, 0);
        Assert.Equal(70, r.UsedTemp);
        r = FanCurve.Linear(68, Curve, 3, r.UsedTemp);
        Assert.Equal(70, r.UsedTemp);
        r = FanCurve.Linear(66, Curve, 3, r.UsedTemp);
        Assert.Equal(69, r.UsedTemp);
        Assert.Equal(34, r.Percent);
    }

    [Theory]
    [InlineData(40, 18, 0)]
    [InlineData(90, 95, 10)]
    [InlineData(99, 95, 10)]
    public void Linear_两端(int temp, int percent, int level)
    {
        var r = FanCurve.Linear(temp, Curve, 3, 0);
        Assert.Equal((percent, level), (r.Percent, r.Level));
    }
}
