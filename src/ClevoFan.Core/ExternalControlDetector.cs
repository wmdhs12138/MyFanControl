namespace ClevoFan.Core;

/// <summary>
/// 判断是否有其他程序在争抢风扇控制：<see cref="Window"/> 内记录到 <see cref="Threshold"/> 次外部修改即判定为冲突，
/// 之后 <see cref="ClearAfter"/> 内没有新的修改则解除。单次修改（例如 EC 偶尔介入）不算冲突。
/// </summary>
public sealed class ExternalControlDetector(Func<long> nowMs)
{
    public const int Threshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ClearAfter = TimeSpan.FromMinutes(10);

    private readonly Queue<long> _events = new();
    private long _last;

    public bool Detected { get; private set; }

    /// <summary><see cref="Window"/> 内的外部修改次数。</summary>
    public int RecentCount
    {
        get
        {
            Trim(nowMs());
            return _events.Count;
        }
    }

    /// <summary>记录一次外部修改；刚判定为冲突时返回 true。</summary>
    public bool Record()
    {
        long now = nowMs();
        _events.Enqueue(now);
        _last = now;
        Trim(now);
        if (Detected || _events.Count < Threshold)
            return false;
        Detected = true;
        return true;
    }

    /// <summary>每轮调用；冲突刚解除时返回 true。</summary>
    public bool Update()
    {
        if (!Detected || nowMs() - _last < ClearAfter.TotalMilliseconds)
            return false;
        Detected = false;
        _events.Clear();
        return true;
    }

    private void Trim(long now)
    {
        while (_events.Count > 0 && now - _events.Peek() > Window.TotalMilliseconds)
            _events.Dequeue();
    }
}
