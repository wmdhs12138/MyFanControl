// 模拟的 ClevoEcInfo.dll（x86），用于在不接触真实硬件的情况下测试 MyFanControl
// 导出函数与真实 DLL 相同（cdecl，见 docs/ec-protocol.md）
//
// 场景文件：DLL 所在目录下的 fake_ec.txt，每次调用都会重新读取，内容为一行：
//   CPU温度 GPU温度 [故障次数]
// 故障次数 > 0 时，接下来这么多次 GetTempFanDuty 返回 EC 异常时的读数（温度 1、负载 0），
// 之后恢复正常；文件内容变化时重新计数
// 程序的每次设置都追加记录到同目录的 fake_ec.log

#include <windows.h>
#include <cstdio>
#include <cstring>

struct ECData
{
	BYTE Remote;
	BYTE Local;
	BYTE FanDuty;
	BYTE Reserve;
};

static char g_dir[MAX_PATH];
static CRITICAL_SECTION g_cs;
static char g_lastScenario[256];
static int g_temp[2] = { 50, 50 };
static int g_glitchLeft = 0;
static int g_duty[4] = { -1, -1, -1, -1 };//-1 表示自动

static void Log(const char *fmt, ...)
{
	char path[MAX_PATH];
	sprintf_s(path, "%s\\fake_ec.log", g_dir);
	FILE *fp = _fsopen(path, "a", 0x40 /*_SH_DENYNO*/);
	if (!fp)
		return;
	SYSTEMTIME st;
	GetLocalTime(&st);
	fprintf(fp, "%02d:%02d:%02d.%03d ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
	va_list ap;
	va_start(ap, fmt);
	vfprintf(fp, fmt, ap);
	va_end(ap);
	fputs("\n", fp);
	fclose(fp);
}

static void LoadScenario()
{
	char path[MAX_PATH], line[256] = { 0 };
	sprintf_s(path, "%s\\fake_ec.txt", g_dir);
	FILE *fp = _fsopen(path, "r", 0x40);
	if (!fp)
		return;
	fgets(line, sizeof(line), fp);
	fclose(fp);
	if (strcmp(line, g_lastScenario) == 0)
		return;
	strcpy_s(g_lastScenario, line);
	int cpu = 50, gpu = 50, glitch = 0;
	if (sscanf_s(line, "%d %d %d", &cpu, &gpu, &glitch) >= 2)
	{
		g_temp[0] = cpu;
		g_temp[1] = gpu;
		g_glitchLeft = glitch;
		Log("scenario cpu=%d gpu=%d glitch=%d", cpu, gpu, glitch);
	}
}

BOOL WINAPI DllMain(HINSTANCE hInst, DWORD reason, LPVOID)
{
	if (reason == DLL_PROCESS_ATTACH)
	{
		GetModuleFileNameA(hInst, g_dir, MAX_PATH);
		*strrchr(g_dir, '\\') = 0;
		InitializeCriticalSection(&g_cs);
	}
	return TRUE;
}

extern "C" __declspec(dllexport) BOOL InitIo()
{
	Log("InitIo");
	return 1;
}

extern "C" __declspec(dllexport) ECData GetTempFanDuty(int fan)
{
	EnterCriticalSection(&g_cs);
	LoadScenario();
	ECData d = { 0 };
	int i = (fan >= 1 && fan <= 2) ? fan - 1 : 0;
	if (g_glitchLeft > 0)
	{
		g_glitchLeft--;
		d.Remote = 1;//实测 EC 异常时的读数
		d.Local = 1;
		d.FanDuty = 0;
		Log("GetTempFanDuty fan=%d -> glitch", fan);
	}
	else
	{
		d.Remote = (BYTE)g_temp[i];
		d.Local = 50;
		d.FanDuty = (BYTE)(g_duty[i] >= 0 ? g_duty[i] : 128);//自动模式下假定 EC 给 50%
	}
	LeaveCriticalSection(&g_cs);
	return d;
}

extern "C" __declspec(dllexport) void SetFanDuty(int fan, int duty)
{
	EnterCriticalSection(&g_cs);
	if (fan >= 1 && fan <= 4)
		g_duty[fan - 1] = duty;
	Log("SetFanDuty fan=%d duty=%d", fan, duty);
	LeaveCriticalSection(&g_cs);
}

extern "C" __declspec(dllexport) int SetFanDutyAuto(int fan)
{
	EnterCriticalSection(&g_cs);
	if (fan >= 1 && fan <= 4)
		g_duty[fan - 1] = -1;
	Log("SetFanDutyAuto fan=%d", fan);
	LeaveCriticalSection(&g_cs);
	return 0;
}

extern "C" __declspec(dllexport) int GetFanCount() { return 2; }
extern "C" __declspec(dllexport) const char *GetECVersion() { return "FAKE"; }
extern "C" __declspec(dllexport) int GetCpuFanRpm() { return 1335; }
extern "C" __declspec(dllexport) int GetGpuFanRpm() { return 1746; }
