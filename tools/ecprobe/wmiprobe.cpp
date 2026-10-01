// wmiprobe：通过 BIOS 提供的 WMI 方法（ACPI \_SB.WMI.WMBB）只读获取 Clevo 风扇信息
// 这条路由 Windows 自带的 ACPI 驱动执行，与系统自身的 EC 访问串行，不需要第三方驱动（见 docs/ec-protocol.md）
// 只调用读取类方法：0x63、0x64（风扇信息）、0x0C（DEVT 设备状态），不会改变 EC 状态
//
// 用法：wmiprobe.exe [实例名，默认 ACPI\PNP0C14\0_0]
// 需要管理员权限

#include <windows.h>
#include <cstdio>
#include <cstdint>

//advapi32 导出的 WMI 用户态接口（wmium.h 未随 SDK 发布）
typedef ULONG(WINAPI *PFN_WMI_OPEN_BLOCK)(GUID *, ULONG, PVOID *);
typedef ULONG(WINAPI *PFN_WMI_EXECUTE_METHOD)(PVOID, LPCWSTR, ULONG, ULONG, PVOID, ULONG *, PVOID);
typedef ULONG(WINAPI *PFN_WMI_CLOSE_BLOCK)(PVOID);
static const ULONG WMIGUID_EXECUTE = 0x0010;

//_WDG 中对象 ID 为 "BB" 的方法 GUID
static GUID CLEVO_WMI_METHOD_GUID = { 0xABBC0F6D, 0x8EA1, 0x11D1, { 0x00, 0xA0, 0xC9, 0x06, 0x29, 0x10, 0x00, 0x00 } };

static PFN_WMI_EXECUTE_METHOD g_pfnExecute;

static bool Call(PVOID h, LPCWSTR instance, ULONG method, uint32_t arg, BYTE *out, ULONG &outSize)
{
	ULONG size = outSize;
	ULONG rc = g_pfnExecute(h, instance, method, sizeof(arg), &arg, &size, out);
	if (rc != ERROR_SUCCESS)
	{
		printf("method_0x%02lX_error=%lu\n", method, rc);
		return false;
	}
	outSize = size;
	return true;
}

static void PrintHex(const char *name, const BYTE *p, ULONG n)
{
	printf("%s_size=%lu %s_hex=", name, n, name);
	for (ULONG i = 0; i < n; i++)
		printf("%02X", p[i]);
	printf("\n");
}

int wmain(int argc, wchar_t **argv)
{
	LPCWSTR instance = argc > 1 ? argv[1] : L"ACPI\\PNP0C14\\0_0";
	HMODULE h = LoadLibraryW(L"advapi32.dll");
	auto pfnOpen = (PFN_WMI_OPEN_BLOCK)GetProcAddress(h, "WmiOpenBlock");
	g_pfnExecute = (PFN_WMI_EXECUTE_METHOD)GetProcAddress(h, "WmiExecuteMethodW");
	auto pfnClose = (PFN_WMI_CLOSE_BLOCK)GetProcAddress(h, "WmiCloseBlock");
	if (!pfnOpen || !g_pfnExecute || !pfnClose)
	{
		printf("error=advapi32 缺少 WMI 接口\n");
		return 2;
	}
	PVOID block = NULL;
	ULONG rc = pfnOpen(&CLEVO_WMI_METHOD_GUID, WMIGUID_EXECUTE, &block);
	if (rc != ERROR_SUCCESS)
	{
		printf("open_error=%lu\n", rc);
		return 2;
	}

	int failures = 0;
	BYTE buf[512];
	const ULONG infoMethods[] = { 0x63, 0x64 };
	for (ULONG method : infoMethods)
	{
		ULONG n = sizeof(buf);
		char name[16];
		sprintf_s(name, "m%02lX", method);
		if (Call(block, instance, method, 0, buf, n))
			PrintHex(name, buf, n);
		else
			failures++;
	}
	ULONG n = sizeof(buf);
	if (Call(block, instance, 0x0C, 0, buf, n))//DEVT：返回 256 字节状态缓冲区，只打印有定义的前 0x1A 字节
		PrintHex("m0C", buf, n < 0x1A ? n : 0x1A);
	else
		failures++;

	pfnClose(block);
	printf("failures=%d\n", failures);
	return failures ? 1 : 0;
}
