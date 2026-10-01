using System.Runtime.InteropServices;
using System.Text;
using ClevoFan.Core;

namespace ClevoFan.Hardware;

/// <summary>
/// 通过 NVIDIA 驱动自带的 NVML（nvml.dll）限制 GPU 频率，需要管理员或 SYSTEM 权限。
/// 独显是否通电通过 PnP 设备电源属性判断，不会唤醒显卡。
/// </summary>
public sealed partial class NvmlGpuBackend : IGpuBackend
{
    private const int NvmlSuccess = 0;
    private const int ClockGraphics = 0;
    private const int NvidiaVendorId = 0x10DE;

    private readonly IntPtr _device;
    private readonly int[] _supportedClocks;
    private readonly uint _devNode;
    private bool _initialized;

    public NvmlGpuBackend()
    {
        Check(nvmlInit_v2(), "nvmlInit");
        _initialized = true;
        try
        {
            Check(nvmlDeviceGetCount_v2(out uint count), "nvmlDeviceGetCount");
            if (count == 0)
                throw new InvalidOperationException("NVML 没有找到 GPU");
            Check(nvmlDeviceGetHandleByIndex_v2(0, out _device), "nvmlDeviceGetHandleByIndex");

            var name = new byte[96];
            Check(nvmlDeviceGetName(_device, name, (uint)name.Length), "nvmlDeviceGetName");
            Name = Encoding.UTF8.GetString(name).TrimEnd('\0');

            Check(nvmlDeviceGetMaxClockInfo(_device, ClockGraphics, out uint max), "nvmlDeviceGetMaxClockInfo");
            _supportedClocks = SupportedGraphicsClocks();
            MinClockMHz = _supportedClocks.Length > 0 ? _supportedClocks.Min() : 0;
            MaxClockMHz = (int)max;

            _devNode = FindDevNode();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Name { get; } = "";
    public int MinClockMHz { get; }
    public int MaxClockMHz { get; }

    public bool IsPoweredOn()
    {
        //CM_POWER_DATA：PD_Size、PD_MostRecentPowerState（1 = D0）
        var key = new DevPropKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 32 };
        var data = new byte[64];
        uint size = (uint)data.Length;
        uint rc = CM_Get_DevNode_PropertyW(_devNode, ref key, out _, data, ref size, 0);
        if (rc != 0 || size < 8)
            throw new InvalidOperationException($"读取 GPU 电源状态失败（CONFIGRET {rc}）");
        return BitConverter.ToInt32(data, 4) == 1;
    }

    public int LockMaxClock(int maxMHz)
    {
        //取不超过上限的最高可用档位，驱动不接受档位之间的值
        int max = _supportedClocks.Where(c => c <= maxMHz).DefaultIfEmpty(maxMHz).Max();
        Check(nvmlDeviceSetGpuLockedClocks(_device, (uint)MinClockMHz, (uint)max), "nvmlDeviceSetGpuLockedClocks");
        return max;
    }

    public void ResetClocks() => Check(nvmlDeviceResetGpuLockedClocks(_device), "nvmlDeviceResetGpuLockedClocks");

    public (int ClockMHz, int UtilizationPercent) ReadLive()
    {
        Check(nvmlDeviceGetClockInfo(_device, ClockGraphics, out uint clock), "nvmlDeviceGetClockInfo");
        Check(nvmlDeviceGetUtilizationRates(_device, out var utilization), "nvmlDeviceGetUtilizationRates");
        return ((int)clock, (int)utilization.Gpu);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            nvmlShutdown();
            _initialized = false;
        }
    }

    //最高显存频率下支持的图形频率档位
    private int[] SupportedGraphicsClocks()
    {
        uint memCount = 32;
        var mem = new uint[memCount];
        if (nvmlDeviceGetSupportedMemoryClocks(_device, ref memCount, mem) != NvmlSuccess || memCount == 0)
            return [];
        uint gfxCount = 512;
        var gfx = new uint[gfxCount];
        if (nvmlDeviceGetSupportedGraphicsClocks(_device, mem.Take((int)memCount).Max(), ref gfxCount, gfx) != NvmlSuccess)
            return [];
        return gfx.Take((int)gfxCount).Select(c => (int)c).ToArray();
    }

    //按 NVML 报告的 PCI 设备 ID 找到对应的 PnP 显示设备
    private uint FindDevNode()
    {
        var pci = new byte[128];
        Check(nvmlDeviceGetPciInfo_v3(_device, pci), "nvmlDeviceGetPciInfo");
        uint pciDeviceId = BitConverter.ToUInt32(pci, 28);
        string prefix = $@"PCI\VEN_{NvidiaVendorId:X4}&DEV_{pciDeviceId >> 16:X4}";

        const string displayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
        const uint flags = 0x200 | 0x100; //CM_GETIDLIST_FILTER_CLASS | CM_GETIDLIST_FILTER_PRESENT
        if (CM_Get_Device_ID_List_SizeW(out uint length, displayClass, flags) != 0)
            throw new InvalidOperationException("枚举显示设备失败");
        var buffer = new char[length];
        if (CM_Get_Device_ID_ListW(displayClass, buffer, length, flags) != 0)
            throw new InvalidOperationException("枚举显示设备失败");
        var id = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(i => i.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"找不到 {prefix} 对应的显示设备");
        if (CM_Locate_DevNodeW(out uint devNode, id, 0) != 0)
            throw new InvalidOperationException($"找不到设备 {id}");
        return devNode;
    }

    private static void Check(int rc, string what)
    {
        if (rc != NvmlSuccess)
            throw new InvalidOperationException($"{what} 失败：{Marshal.PtrToStringAnsi(nvmlErrorString(rc))}（{rc}）");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    [LibraryImport("nvml.dll")] private static partial int nvmlInit_v2();
    [LibraryImport("nvml.dll")] private static partial int nvmlShutdown();
    [LibraryImport("nvml.dll")] private static partial IntPtr nvmlErrorString(int result);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetCount_v2(out uint count);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetName(IntPtr device, [Out] byte[] name, uint length);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetPciInfo_v3(IntPtr device, [Out] byte[] pci);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetMaxClockInfo(IntPtr device, int type, out uint clock);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint clock);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetSupportedMemoryClocks(IntPtr device, ref uint count, [Out] uint[] clocks);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetSupportedGraphicsClocks(IntPtr device, uint memoryClock, ref uint count, [Out] uint[] clocks);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceSetGpuLockedClocks(IntPtr device, uint minClock, uint maxClock);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceResetGpuLockedClocks(IntPtr device);
    [LibraryImport("nvml.dll")] private static partial int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint devNode, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint devNode, ref DevPropKey key, out uint propertyType, [Out] byte[] buffer, ref uint size, uint flags);
}
