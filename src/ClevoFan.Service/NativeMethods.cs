using System.Runtime.InteropServices;

namespace ClevoFan.Service;

internal static partial class NativeMethods
{
    /// <summary>不含睡眠、休眠时间的系统运行时间（毫秒），用于判断睡眠通知后实际醒着了多久。</summary>
    public static long AwakeMilliseconds()
    {
        QueryUnbiasedInterruptTime(out ulong t);
        return (long)(t / 10_000);
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
