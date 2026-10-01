// ecprobe：通过 PawnIO 的 LpcACPIEC 模块只读访问 Clevo EC，用于验证从 ClevoEcInfo.dll 逆向出的协议（见 docs/ec-protocol.md）
// 只包含读操作，不会改变 EC 状态
//
// 用法：ecprobe.exe [--rounds N] [--interval 毫秒] [--module LpcACPIEC.bin路径]
// 需要管理员权限，并已安装 PawnIO（https://pawnio.eu）

#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <string>
#include <vector>

typedef HRESULT(WINAPI *PFN_PAWNIO_VERSION)(PULONG);
typedef HRESULT(WINAPI *PFN_PAWNIO_OPEN)(PHANDLE);
typedef HRESULT(WINAPI *PFN_PAWNIO_LOAD)(HANDLE, const UCHAR *, SIZE_T);
typedef HRESULT(WINAPI *PFN_PAWNIO_EXECUTE)(HANDLE, PCSTR, const ULONG64 *, SIZE_T, PULONG64, SIZE_T, PSIZE_T);
typedef HRESULT(WINAPI *PFN_PAWNIO_CLOSE)(HANDLE);

static const uint8_t EC_DATA = 0x62;//数据端口
static const uint8_t EC_SC = 0x66;//命令/状态端口
static const uint8_t EC_OBF = 0x01;//输出缓冲区满：EC有数据待读取
static const uint8_t EC_IBF = 0x02;//输入缓冲区满：EC尚未取走上一个字节
static const double EC_WAIT_MS = 100.0;//等待IBF/OBF的超时，原DLL等待IBF没有超时
static const int EC_ATTEMPTS = 3;//读操作失败时的尝试次数：EC也会被系统ACPI驱动等访问，偶尔会被打断

static double NowMs()
{
	static LARGE_INTEGER freq = [] { LARGE_INTEGER f; QueryPerformanceFrequency(&f); return f; }();
	LARGE_INTEGER t;
	QueryPerformanceCounter(&t);
	return t.QuadPart * 1000.0 / freq.QuadPart;
}

class PawnIoEc
{
public:
	~PawnIoEc()
	{
		if (m_hPawnIo && m_pfnClose)
			m_pfnClose(m_hPawnIo);
		if (m_hMutex)
			CloseHandle(m_hMutex);
	}

	bool Open(const std::wstring &modulePath, std::string &err)
	{
		wchar_t lib[MAX_PATH];
		ExpandEnvironmentStringsW(L"%ProgramFiles%\\PawnIO\\PawnIOLib.dll", lib, MAX_PATH);
		HMODULE h = LoadLibraryW(lib);
		if (!h)
			h = LoadLibraryW(L"PawnIOLib.dll");
		if (!h)
			return Fail(err, "无法加载 PawnIOLib.dll，请先安装 PawnIO");
		auto pfnVersion = (PFN_PAWNIO_VERSION)GetProcAddress(h, "pawnio_version");
		auto pfnOpen = (PFN_PAWNIO_OPEN)GetProcAddress(h, "pawnio_open");
		auto pfnLoad = (PFN_PAWNIO_LOAD)GetProcAddress(h, "pawnio_load");
		m_pfnExecute = (PFN_PAWNIO_EXECUTE)GetProcAddress(h, "pawnio_execute");
		m_pfnClose = (PFN_PAWNIO_CLOSE)GetProcAddress(h, "pawnio_close");
		if (!pfnVersion || !pfnOpen || !pfnLoad || !m_pfnExecute || !m_pfnClose)
			return Fail(err, "PawnIOLib.dll 缺少导出函数");
		pfnVersion(&m_version);

		std::vector<UCHAR> blob;
		if (!ReadFile(modulePath, blob))
			return Fail(err, "无法读取模块文件 LpcACPIEC.bin");
		HRESULT hr = pfnOpen(&m_hPawnIo);
		if (FAILED(hr))
			return Fail(err, "pawnio_open 失败（需要管理员权限）", hr);
		hr = pfnLoad(m_hPawnIo, blob.data(), blob.size());
		if (FAILED(hr))
			return Fail(err, "pawnio_load 失败（模块签名或版本不匹配）", hr);

		//与 LibreHardwareMonitor、FanControl 等工具约定的 EC 访问互斥体
		m_hMutex = CreateMutexW(NULL, FALSE, L"Global\\Access_EC");
		if (!m_hMutex)
			return Fail(err, "无法创建 Global\\Access_EC 互斥体", HRESULT_FROM_WIN32(GetLastError()));
		return true;
	}

	ULONG Version() const { return m_version; }
	double MaxTransactionMs() const { return m_maxTransactionMs; }
	int Retries() const { return m_retries; }

	//一次完整的 EC 事务：持有互斥体，清掉残留输出，发命令，写参数，读返回；失败时重试
	//nRead < 0 表示读到 '$' 为止（最多 -nRead 字节）
	bool Transact(uint8_t cmd, const std::vector<uint8_t> &args, int nRead, std::vector<uint8_t> &out, std::string &err)
	{
		std::string lastErr;
		for (int attempt = 0; attempt < EC_ATTEMPTS; attempt++)
		{
			if (attempt > 0)
			{
				m_retries++;
				Sleep(5);
			}
			out.clear();
			lastErr.clear();
			DWORD w = WaitForSingleObject(m_hMutex, 1000);
			if (w != WAIT_OBJECT_0 && w != WAIT_ABANDONED)
			{
				lastErr = "1 秒内未能获得 Access_EC 互斥体";
				continue;
			}
			double t0 = NowMs();
			bool ok = TransactLocked(cmd, args, nRead, out, lastErr);
			double dt = NowMs() - t0;
			ReleaseMutex(m_hMutex);
			if (dt > m_maxTransactionMs)
				m_maxTransactionMs = dt;
			if (ok)
				return true;
		}
		return Fail(err, lastErr.c_str());
	}

	//标准 ACPI EC 读寄存器（命令 0x80）
	bool ReadRam(uint8_t addr, uint8_t &v, std::string &err)
	{
		std::vector<uint8_t> out;
		if (!Transact(0x80, { addr }, 1, out, err))
			return false;
		v = out[0];
		return true;
	}

private:
	bool TransactLocked(uint8_t cmd, const std::vector<uint8_t> &args, int nRead, std::vector<uint8_t> &out, std::string &err)
	{
		uint8_t s, v;
		//清掉上一次未读完的数据（例如别的程序的事务被打断）
		for (int i = 0; i < 16; i++)
		{
			if (!In(EC_SC, s))
				return Fail(err, "读状态端口失败");
			if (!(s & EC_OBF))
				break;
			In(EC_DATA, v);
		}
		if (!WaitStatus(EC_IBF, false, err) || !Out(EC_SC, cmd))
			return Fail(err, "发送命令失败");
		for (uint8_t a : args)
		{
			if (!WaitStatus(EC_IBF, false, err) || !Out(EC_DATA, a))
				return Fail(err, "发送参数失败");
		}
		int n = nRead < 0 ? -nRead : nRead;
		for (int i = 0; i < n; i++)
		{
			if (!WaitStatus(EC_OBF, true, err) || !In(EC_DATA, v))
				return Fail(err, "读取返回失败");
			if (nRead < 0 && v == '$')
				break;
			out.push_back(v);
		}
		return true;
	}

	bool WaitStatus(uint8_t bit, bool set, std::string &err)
	{
		double t0 = NowMs();
		uint8_t s;
		do
		{
			if (!In(EC_SC, s))
				return false;
			if (((s & bit) != 0) == set)
				return true;
		} while (NowMs() - t0 < EC_WAIT_MS);
		char buf[96];
		sprintf_s(buf, "等待%s超时（状态 0x%02X）", bit == EC_IBF ? "EC 取走输入" : "EC 输出数据", s);
		err = buf;
		return false;
	}

	bool In(uint8_t port, uint8_t &v)
	{
		ULONG64 in = port, out = 0;
		SIZE_T ret = 0;
		if (FAILED(m_pfnExecute(m_hPawnIo, "ioctl_pio_read", &in, 1, &out, 1, &ret)))
			return false;
		v = (uint8_t)out;
		return true;
	}

	bool Out(uint8_t port, uint8_t v)
	{
		ULONG64 in[2] = { port, v };
		SIZE_T ret = 0;
		return SUCCEEDED(m_pfnExecute(m_hPawnIo, "ioctl_pio_write", in, 2, NULL, 0, &ret));
	}

	static bool ReadFile(const std::wstring &path, std::vector<UCHAR> &data)
	{
		HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, 0, NULL);
		if (h == INVALID_HANDLE_VALUE)
			return false;
		LARGE_INTEGER size;
		bool ok = GetFileSizeEx(h, &size) && size.QuadPart > 0 && size.QuadPart < (1 << 20);
		if (ok)
		{
			data.resize((size_t)size.QuadPart);
			DWORD rd = 0;
			ok = ::ReadFile(h, data.data(), (DWORD)data.size(), &rd, NULL) && rd == data.size();
		}
		CloseHandle(h);
		return ok;
	}

	static bool Fail(std::string &err, const char *msg, HRESULT hr = S_OK)
	{
		if (err.empty())
		{
			err = msg;
			if (hr != S_OK)
			{
				char buf[32];
				sprintf_s(buf, "（0x%08lX）", (unsigned long)hr);
				err += buf;
			}
		}
		return false;
	}

	HANDLE m_hPawnIo = NULL;
	HANDLE m_hMutex = NULL;
	ULONG m_version = 0;
	PFN_PAWNIO_EXECUTE m_pfnExecute = NULL;
	PFN_PAWNIO_CLOSE m_pfnClose = NULL;
	double m_maxTransactionMs = 0;
	int m_retries = 0;
};

static std::wstring ExeDir()
{
	wchar_t path[MAX_PATH];
	GetModuleFileNameW(NULL, path, MAX_PATH);
	std::wstring s = path;
	return s.substr(0, s.find_last_of(L'\\'));
}

int wmain(int argc, wchar_t **argv)
{
	SetConsoleOutputCP(CP_UTF8);
	int rounds = 1, interval = 1000;
	std::wstring module = ExeDir() + L"\\LpcACPIEC.bin";
	for (int i = 1; i < argc; i++)
	{
		std::wstring a = argv[i];
		if (a == L"--rounds" && i + 1 < argc)
			rounds = _wtoi(argv[++i]);
		else if (a == L"--interval" && i + 1 < argc)
			interval = _wtoi(argv[++i]);
		else if (a == L"--module" && i + 1 < argc)
			module = argv[++i];
	}

	PawnIoEc ec;
	std::string err;
	if (!ec.Open(module, err))
	{
		printf("error=%s\n", err.c_str());
		return 2;
	}
	ULONG v = ec.Version();
	printf("pawnio_version=%lu.%lu.%lu\n", (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);

	std::vector<uint8_t> out;
	if (ec.Transact(0x93, {}, -30, out, err))//EC 版本字符串，以 '$' 结尾
		printf("ec_version=%s\n", std::string(out.begin(), out.end()).c_str());
	else
		printf("ec_version_error=%s\n", err.c_str());
	err.clear();

	int failures = 0;
	for (int r = 1; r <= rounds; r++)
	{
		printf("round=%d", r);
		uint8_t b = 0;
		if (ec.ReadRam(0xC8, b, err))
			printf(" fan_count=%u", b);
		else
			failures++;
		for (int fan = 1; fan <= 2; fan++)//命令 0x9E + 风扇编号，返回 远端温度、本地温度、负载(0-255)
		{
			if (ec.Transact(0x9E, { (uint8_t)fan }, 3, out, err))
				printf(" fan%d_remote=%u fan%d_local=%u fan%d_duty=%u", fan, out[0], fan, out[1], fan, out[2]);
			else
				failures++;
		}
		const struct { const char *name; uint8_t hi; } rpm[] = { { "cpu", 0xD0 }, { "gpu", 0xD2 }, { "gpu1", 0xD4 } };
		for (auto &f : rpm)//转速计数，高字节在前
		{
			uint8_t hi = 0, lo = 0;
			if (ec.ReadRam(f.hi, hi, err) && ec.ReadRam(f.hi + 1, lo, err))
				printf(" rpm_raw_%s=%u", f.name, (hi << 8) | lo);
			else
				failures++;
		}
		printf("\n");
		if (!err.empty())
		{
			printf("error=%s\n", err.c_str());
			err.clear();
		}
		if (r < rounds)
			Sleep(interval);
	}
	printf("max_transaction_ms=%.3f\nretries=%d\nfailures=%d\n", ec.MaxTransactionMs(), ec.Retries(), failures);
	return failures ? 1 : 0;
}
