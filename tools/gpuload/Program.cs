// gpuload：用 OpenCL 让 NVIDIA 独显满载运行指定秒数，用于验证 GPU 限频是否生效
// 用法：gpuload [秒数，默认 10]
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

int seconds = args.Length > 0 ? int.Parse(args[0]) : 10;

IntPtr platform = FindPlatform("NVIDIA");
IntPtr device;
Check(Cl.clGetDeviceIDs(platform, Cl.DeviceTypeGpu, 1, out device, out _), "clGetDeviceIDs");
Console.WriteLine($"device={Info(device)}");

int err;
IntPtr context = Cl.clCreateContext(IntPtr.Zero, 1, ref device, IntPtr.Zero, IntPtr.Zero, out err);
Check(err, "clCreateContext");
IntPtr queue = Cl.clCreateCommandQueue(context, device, 0, out err);
Check(err, "clCreateCommandQueue");

//每个工作项做大量依赖链上的浮点运算，保持 SM 忙碌
const string source = """
    __kernel void burn(__global float* out) {
        int i = get_global_id(0);
        float x = i * 0.001f;
        for (int k = 0; k < 4096; k++) { x = sin(x) * 1.0001f + 0.5f; }
        out[i] = x;
    }
    """;
IntPtr program = Cl.clCreateProgramWithSource(context, 1, [source], null, out err);
Check(err, "clCreateProgramWithSource");
Check(Cl.clBuildProgram(program, 1, ref device, "", IntPtr.Zero, IntPtr.Zero), "clBuildProgram");
IntPtr kernel = Cl.clCreateKernel(program, "burn", out err);
Check(err, "clCreateKernel");
const int items = 1 << 20;
IntPtr buffer = Cl.clCreateBuffer(context, Cl.MemReadWrite, items * sizeof(float), IntPtr.Zero, out err);
Check(err, "clCreateBuffer");
Check(Cl.clSetKernelArg(kernel, 0, IntPtr.Size, ref buffer), "clSetKernelArg");

var sw = Stopwatch.StartNew();
long launches = 0;
nuint global = items;
while (sw.Elapsed.TotalSeconds < seconds)
{
    Check(Cl.clEnqueueNDRangeKernel(queue, kernel, 1, IntPtr.Zero, ref global, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueNDRangeKernel");
    Check(Cl.clFinish(queue), "clFinish");
    launches++;
}
Console.WriteLine($"launches={launches} seconds={sw.Elapsed.TotalSeconds:F1} launches_per_second={launches / sw.Elapsed.TotalSeconds:F2}");

Cl.clReleaseMemObject(buffer);
Cl.clReleaseKernel(kernel);
Cl.clReleaseProgram(program);
Cl.clReleaseCommandQueue(queue);
Cl.clReleaseContext(context);
return 0;

static IntPtr FindPlatform(string vendor)
{
    Check(Cl.clGetPlatformIDs(0, null, out uint count), "clGetPlatformIDs");
    var platforms = new IntPtr[count];
    Check(Cl.clGetPlatformIDs(count, platforms, out _), "clGetPlatformIDs");
    foreach (var p in platforms)
    {
        if (Info(p, platform: true).Contains(vendor, StringComparison.OrdinalIgnoreCase))
            return p;
    }
    throw new InvalidOperationException($"找不到 {vendor} 的 OpenCL 平台");
}

static string Info(IntPtr handle, bool platform = false)
{
    var buf = new byte[256];
    int rc = platform
        ? Cl.clGetPlatformInfo(handle, Cl.PlatformName, (nuint)buf.Length, buf, out var size)
        : Cl.clGetDeviceInfo(handle, Cl.DeviceName, (nuint)buf.Length, buf, out size);
    Check(rc, "clGet*Info");
    return Encoding.ASCII.GetString(buf, 0, (int)size).TrimEnd('\0');
}

static void Check(int rc, string what)
{
    if (rc != 0)
        throw new InvalidOperationException($"{what} 失败，错误码 {rc}");
}

static class Cl
{
    public const ulong DeviceTypeGpu = 1 << 2;
    public const uint PlatformName = 0x0902;
    public const uint DeviceName = 0x102B;
    public const ulong MemReadWrite = 1 << 0;
    private const string Lib = "OpenCL.dll";

    [DllImport(Lib)] public static extern int clGetPlatformIDs(uint num, IntPtr[]? platforms, out uint count);
    [DllImport(Lib)] public static extern int clGetPlatformInfo(IntPtr platform, uint name, nuint size, byte[] value, out nuint sizeRet);
    [DllImport(Lib)] public static extern int clGetDeviceIDs(IntPtr platform, ulong type, uint num, out IntPtr device, out uint count);
    [DllImport(Lib)] public static extern int clGetDeviceInfo(IntPtr device, uint name, nuint size, byte[] value, out nuint sizeRet);
    [DllImport(Lib)] public static extern IntPtr clCreateContext(IntPtr props, uint num, ref IntPtr device, IntPtr notify, IntPtr userData, out int err);
    [DllImport(Lib)] public static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong props, out int err);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, string[] sources, nuint[]? lengths, out int err);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern int clBuildProgram(IntPtr program, uint num, ref IntPtr device, string options, IntPtr notify, IntPtr userData);
    [DllImport(Lib, CharSet = CharSet.Ansi)] public static extern IntPtr clCreateKernel(IntPtr program, string name, out int err);
    [DllImport(Lib)] public static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, nuint size, IntPtr host, out int err);
    [DllImport(Lib)] public static extern int clSetKernelArg(IntPtr kernel, uint index, nint size, ref IntPtr value);
    [DllImport(Lib)] public static extern int clEnqueueNDRangeKernel(IntPtr queue, IntPtr kernel, uint dims, IntPtr offset, ref nuint global, IntPtr local, uint numEvents, IntPtr events, IntPtr evt);
    [DllImport(Lib)] public static extern int clFinish(IntPtr queue);
    [DllImport(Lib)] public static extern int clReleaseMemObject(IntPtr mem);
    [DllImport(Lib)] public static extern int clReleaseKernel(IntPtr kernel);
    [DllImport(Lib)] public static extern int clReleaseProgram(IntPtr program);
    [DllImport(Lib)] public static extern int clReleaseCommandQueue(IntPtr queue);
    [DllImport(Lib)] public static extern int clReleaseContext(IntPtr context);
}
