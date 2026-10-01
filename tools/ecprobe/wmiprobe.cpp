// wmiprobe：通过 BIOS 提供的 WMI 方法（ACPI \_SB.WMI.WMBB）访问 Clevo 风扇
// 这条路由 Windows 自带的 ACPI 驱动执行，与系统自身的 EC 访问串行，不需要第三方驱动（见 docs/ec-protocol.md）
//
// 用法：wmiprobe.exe [--instance 实例名] [--set-duty CPU负载 GPU负载] [--set-auto 掩码]
//   不带写参数时只调用读取类方法 0x63、0x64（风扇信息）、0x0C（DEVT 设备状态），不改变 EC 状态
//   --set-duty：方法 0x68，负载为 0-255；风扇 3、4 与 GPU 风扇取相同值（与原程序对风扇 3 的处理一致）
//   --set-auto：方法 0x69，掩码 bit0-3 对应风扇 1-4，0x0F 为全部恢复自动
//   写操作的返回值是固定值，不能说明是否生效，需要读回确认；写操作后会再读一次
// 需要管理员权限

#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cwchar>

//advapi32 导出的 WMI 用户态接口（wmium.h 未随 SDK 发布）
typedef ULONG(WINAPI *PFN_WMI_OPEN_BLOCK)(GUID *, ULONG, PVOID *);
typedef ULONG(WINAPI *PFN_WMI_EXECUTE_METHOD)(PVOID, LPCWSTR, ULONG, ULONG, PVOID, ULONG *, PVOID);
typedef ULONG(WINAPI *PFN_WMI_CLOSE_BLOCK)(PVOID);
static const ULONG WMIGUID_EXECUTE = 0x0010;

//_WDG 中对象 ID 为 "BB" 的方法 GUID
static GUID CLEVO_WMI_METHOD_GUID = { 0xABBC0F6D, 0x8EA1, 0x11D1, { 0x00, 0xA0, 0xC9, 0x06, 0x29, 0x10, 0x00, 0x00 } };

static PFN_WMI_EXECUTE_METHOD g_pfnExecute;
static PVOID g_block;
static LPCWSTR g_instance = L"ACPI\\PNP0C14\\0_0";
static double g_maxCallMs;

static double NowMs()
{
	static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
	LARGE_INTEGER t;
	QueryPerformanceCounter(&t);
	return t.QuadPart * 1000.0 / freq.QuadPart;
}

static bool Call(ULONG method, uint32_t arg, BYTE *out, ULONG &outSize)
{
	ULONG size = outSize;
	double t0 = NowMs();
	ULONG rc = g_pfnExecute(g_block, g_instance, method, sizeof(arg), &arg, &size, out);
	double dt = NowMs() - t0;
	if (dt > g_maxCallMs)
		g_maxCallMs = dt;
	if (rc != ERROR_SUCCESS)
	{
		printf("method_0x%02lX_error=%lu\n", method, rc);
		return false;
	}
	outSize = size;
	return true;
}

//读取风扇 1、2 的负载和温度（方法 0x63、0x64），以及转速计数（方法 0x0C）
static bool Read(const char *tag)
{
	BYTE buf[512];
	ULONG n;
	uint32_t info[2] = { 0, 0 };
	for (int i = 0; i < 2; i++)
	{
		n = sizeof(buf);
		if (!Call(0x63 + i, 0, buf, n) || n < 4)
			return false;
		info[i] = *(uint32_t *)buf;
	}
	n = sizeof(buf);
	if (!Call(0x0C, 0, buf, n) || n < 8)
		return false;
	printf("%s fan1_duty=%u fan1_local=%u fan1_remote=%u fan2_duty=%u fan2_local=%u fan2_remote=%u rpm_raw_cpu=%u rpm_raw_gpu=%u\n", tag,
		info[0] & 0xFF, (info[0] >> 8) & 0xFF, (info[0] >> 16) & 0xFF,
		info[1] & 0xFF, (info[1] >> 8) & 0xFF, (info[1] >> 16) & 0xFF,
		(buf[2] << 8) | buf[3], (buf[4] << 8) | buf[5]);
	return true;
}

int wmain(int argc, wchar_t **argv)
{
	int setDuty[2] = { -1, -1 };
	int setAuto = -1;
	for (int i = 1; i < argc; i++)
	{
		if (!wcscmp(argv[i], L"--instance") && i + 1 < argc)
			g_instance = argv[++i];
		else if (!wcscmp(argv[i], L"--set-duty") && i + 2 < argc)
		{
			setDuty[0] = _wtoi(argv[++i]);
			setDuty[1] = _wtoi(argv[++i]);
		}
		else if (!wcscmp(argv[i], L"--set-auto") && i + 1 < argc)
			setAuto = (int)wcstol(argv[++i], NULL, 0);
		else
		{
			printf("error=未知参数\n");
			return 2;
		}
	}
	if ((setDuty[0] >= 0 && (setDuty[0] > 255 || setDuty[1] < 0 || setDuty[1] > 255)) || setAuto > 0x0F)
	{
		printf("error=参数超出范围\n");
		return 2;
	}

	HMODULE h = LoadLibraryW(L"advapi32.dll");
	auto pfnOpen = (PFN_WMI_OPEN_BLOCK)GetProcAddress(h, "WmiOpenBlock");
	g_pfnExecute = (PFN_WMI_EXECUTE_METHOD)GetProcAddress(h, "WmiExecuteMethodW");
	auto pfnClose = (PFN_WMI_CLOSE_BLOCK)GetProcAddress(h, "WmiCloseBlock");
	if (!pfnOpen || !g_pfnExecute || !pfnClose)
	{
		printf("error=advapi32 缺少 WMI 接口\n");
		return 2;
	}
	ULONG rc = pfnOpen(&CLEVO_WMI_METHOD_GUID, WMIGUID_EXECUTE, &g_block);
	if (rc != ERROR_SUCCESS)
	{
		printf("open_error=%lu\n", rc);
		return 2;
	}

	bool ok = Read("before");
	BYTE buf[64];
	ULONG n;
	if (ok && setAuto >= 0)
	{
		n = sizeof(buf);
		ok = Call(0x69, (uint32_t)setAuto, buf, n);
		printf("set_auto mask=0x%X ret=0x%lX\n", setAuto, n >= 4 ? *(ULONG *)buf : 0);
	}
	if (ok && setDuty[0] >= 0)
	{
		uint32_t arg = setDuty[0] | (setDuty[1] << 8) | (setDuty[1] << 16) | ((uint32_t)setDuty[1] << 24);
		n = sizeof(buf);
		ok = Call(0x68, arg, buf, n);
		printf("set_duty cpu=%d gpu=%d arg=0x%08X ret=0x%lX\n", setDuty[0], setDuty[1], arg, n >= 4 ? *(ULONG *)buf : 0);
	}
	if (ok && (setAuto >= 0 || setDuty[0] >= 0))
		ok = Read("after");

	pfnClose(g_block);
	printf("max_call_ms=%.3f\nfailures=%d\n", g_maxCallMs, ok ? 0 : 1);
	return ok ? 0 : 1;
}
