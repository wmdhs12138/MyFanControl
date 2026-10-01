using System.ComponentModel;
using System.Runtime.InteropServices;
using ClevoFan.Core;

namespace ClevoFan.Hardware;

/// <summary>
/// 通过 BIOS 的 WMI 方法 \_SB.WMI.WMBB 读写 Clevo 风扇（见 docs/ec-protocol.md）。
/// 由 Windows 自带的 ACPI 驱动执行，与系统自身的 EC 访问串行，不需要第三方驱动。需要管理员或 SYSTEM 权限。
/// </summary>
public sealed partial class ClevoWmiBackend : IFanBackend
{
    /// <summary>_WDG 中对象 ID 为 "BB" 的方法 GUID。</summary>
    public static readonly Guid MethodGuid = new("ABBC0F6D-8EA1-11D1-00A0-C90629100000");

    public const string DefaultInstance = @"ACPI\PNP0C14\0_0";

    private const uint MethodCpuFanInfo = 0x63;
    private const uint MethodGpuFanInfo = 0x64;
    private const uint MethodDeviceStatus = 0x0C;
    private const uint MethodSetDuty = 0x68;
    private const uint MethodSetAuto = 0x69;
    private const uint AllFans = 0x0F;
    private const uint WmiGuidExecute = 0x0010;

    private readonly string _instance;
    private IntPtr _block;

    public ClevoWmiBackend(string instance = DefaultInstance)
    {
        _instance = instance;
        var guid = MethodGuid;
        uint rc = WmiOpenBlock(ref guid, WmiGuidExecute, out _block);
        if (rc != 0)
            throw new Win32Exception((int)rc, $"无法打开 Clevo WMI 接口（{MethodGuid}），本机可能不支持");
    }

    public string Name => "Clevo WMI";

    public FanReading Read()
    {
        //0x63/0x64 返回 负载 | 本地温度<<8 | 远端温度<<16；0x0C 的第 2-5 字节为 CPU、GPU 转速计数（大端）
        uint cpu = CallUInt32(MethodCpuFanInfo, 0);
        uint gpu = CallUInt32(MethodGpuFanInfo, 0);
        var status = Call(MethodDeviceStatus, 0, 256);
        if (status.Length < 6)
            throw new InvalidOperationException($"WMI 方法 0x0C 返回长度 {status.Length}，无法解析转速");
        return new FanReading(
            CpuTemp: (int)((cpu >> 16) & 0xFF),
            GpuTemp: (int)((gpu >> 16) & 0xFF),
            CpuDuty: (int)(cpu & 0xFF),
            GpuDuty: (int)(gpu & 0xFF),
            CpuRpmRaw: (status[2] << 8) | status[3],
            GpuRpmRaw: (status[4] << 8) | status[5]);
    }

    public void SetDuty(int cpuDuty, int gpuDuty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cpuDuty);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cpuDuty, 255);
        ArgumentOutOfRangeException.ThrowIfNegative(gpuDuty);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(gpuDuty, 255);
        //0x68 一次设置 4 个风扇（参数第 0-3 字节）；风扇 3、4 与 GPU 风扇取相同值，与原程序对风扇 3 的处理一致
        uint g = (uint)gpuDuty;
        Call(MethodSetDuty, (uint)cpuDuty | g << 8 | g << 16 | g << 24, 4);
    }

    public void SetAuto() => Call(MethodSetAuto, AllFans, 4);

    public void Dispose()
    {
        if (_block != IntPtr.Zero)
        {
            WmiCloseBlock(_block);
            _block = IntPtr.Zero;
        }
    }

    private uint CallUInt32(uint method, uint arg)
    {
        var buf = Call(method, arg, 4);
        if (buf.Length < 4)
            throw new InvalidOperationException($"WMI 方法 0x{method:X2} 返回长度 {buf.Length}");
        return BitConverter.ToUInt32(buf);
    }

    private byte[] Call(uint method, uint arg, int outCapacity)
    {
        ObjectDisposedException.ThrowIf(_block == IntPtr.Zero, this);
        var output = new byte[outCapacity];
        uint size = (uint)output.Length;
        uint rc = WmiExecuteMethodW(_block, _instance, method, sizeof(uint), ref arg, ref size, output);
        if (rc != 0)
            throw new Win32Exception((int)rc, $"WMI 方法 0x{method:X2} 调用失败");
        return output.AsSpan(0, (int)Math.Min(size, (uint)output.Length)).ToArray();
    }

    //advapi32 导出的 WMI 用户态接口（wmium.h 未随 SDK 发布）
    [LibraryImport("advapi32.dll")]
    private static partial uint WmiOpenBlock(ref Guid guid, uint desiredAccess, out IntPtr dataBlockHandle);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint WmiExecuteMethodW(IntPtr dataBlockHandle, string instanceName, uint methodId,
        uint inputValueBufferSize, ref uint inputValueBuffer, ref uint outputBufferSize, [Out] byte[] outputBuffer);

    [LibraryImport("advapi32.dll")]
    private static partial uint WmiCloseBlock(IntPtr dataBlockHandle);
}
