# Clevo EC 风扇控制协议

本文记录阶段 1 的调查结果：`ClevoEcInfo.dll` 的协议逆向、BIOS 提供的 WMI 接口，以及在 NH5x_7xRDx（Insyde BIOS，EC 版本 `07.05HE1`）上的实测对比。用于决定阶段 2 新架构的硬件访问方式。

## 1. 原方案：ClevoEcInfo.dll + NTPort

`ClevoEcInfo.dll`（x86，68KB）通过 `ntport.dll` 的 `Inport`/`Outport` 直接读写 ACPI EC 的两个端口：

| 端口 | 用途 |
|---|---|
| `0x66` | 写：命令；读：状态（bit0 = OBF，EC 有数据待读；bit1 = IBF，EC 尚未取走输入） |
| `0x62` | 数据 |

`InitIo` 用内嵌的 NTPort 商业授权调用 `LicenseInfo`，再 `EnablePorts(0, 0xFF)` 开放 0x00–0xFF 全部端口。

### 命令

| 导出函数 | EC 交互 |
|---|---|
| `GetTempFanDuty(n)` | 命令 `0x9E`，参数 n（1–4，越界按 1），读 3 字节：远端温度、本地温度、负载（0–255） |
| `SetFanDuty(n, d)` | 命令 `0x99`，参数 n（1–4），参数 d（0–255） |
| `SetFanDutyAuto(n)` | 命令 `0x99`，参数 `0xFF`，参数 n（1–4）；n = 5 时为 `FF FF` |
| `GetCpuFanRpm` / `GetGpuFanRpm` / `GetGpu1FanRpm` / `GetX72FanRpm` | 标准 EC 读（命令 `0x80`）地址 `0xD0/D1`、`0xD2/D3`、`0xD4/D5`、`0xD8/D9`，高字节在前，是转速周期计数 |
| `GetFanCount` | 标准 EC 读地址 `0xC8` |
| `GetECVersion` | 命令 `0x93`，读到 `$` 为止，最多 30 字节 |
| `GetOptionModual` | 命令 `0x9A`，读 1 字节 |

风扇 1 是 CPU，风扇 2 是 GPU。

### 缺陷
1. 等待 IBF 清零的循环**没有超时**，EC 不响应时调用线程永久卡死。
2. `SetFanDuty` / `SetFanDutyAuto` 在风扇编号越界时只发出命令 `0x99` 不写参数，EC 停在等待参数的状态。
3. 发命令前不清理 OBF 中的残留字节，会把其他访问者留下的数据当作返回值。
4. 不与任何其他 EC 访问者同步：Windows 自带的 ACPI EC 驱动（电池、温度事件等）、Control Center 服务、其他监控工具都可能同时访问这两个端口。

## 2. 替代方案 A：PawnIO

[PawnIO](https://pawnio.eu) 是有签名、仍在维护的驱动，LibreHardwareMonitor、FanControl 已从 WinRing0 迁移到它。官方模块 `LpcACPIEC` 只允许读写 `0x62`/`0x66` 两个端口，并约定访问前获取全局互斥体 `Global\Access_EC`，与遵守该约定的工具互斥。

`tools/ecprobe/ecprobe.cpp` 按上面的协议实现了只读访问，修正了第 1 节的缺陷：每一步等待有 100ms 超时，命令前清理 OBF，持有 `Access_EC`，失败重试最多 3 次。

## 3. 替代方案 B：BIOS 的 WMI 接口（推荐）

DSDT 中 `\_SB.WMI`（`PNP0C14`，`_UID 0`）的 `_WDG` 注册了：

| GUID | 对象 | 类型 |
|---|---|---|
| `ABBC0F6D-8EA1-11D1-00A0-C90629100000` | `BB` → `WMBB` | 方法 |
| `ABBC0F6B-8EA1-11D1-00A0-C90629100000` | `0xD0` | 事件 |
| `ABBC0F6C-8EA1-11D1-00A0-C90629100000` | `0xD1` | 事件 |

没有附带 MOF，不能用 CIM 类调用，但可以用 advapi32 导出的 `WmiOpenBlock` / `WmiExecuteMethodW` 按 GUID 调用，实例名 `ACPI\PNP0C14\0_0`，需要管理员权限。

`WMBB(实例, 方法号, 输入缓冲区)` 先获取 ACPI 互斥体 `EC.PATM`（100ms 超时），再分派：`0x0C` → `DEVT`，`0x0D` → `EEVT`，`0x0E` → `FEVT`，其余 → `\_SB.WMI.ZEVT`。`ZEVT` 把输入缓冲区转为 32 位整数 `ARGS`。BIOS 通过 EC 内存里的邮箱访问风扇：写 `FDAT`（参数）、`FBUF`（数据），再写 `FCMD`（`0xC0` 读，`0xC1` 写），从 `FDAT`/`FBUF`/`FBF1`/`FBF2` 取结果。这些访问由 Windows 的 ACPI 驱动执行，与系统自身的 EC 访问串行，**不需要任何第三方驱动**。

| 方法号 | 作用 | 返回 / 参数 |
|---|---|---|
| `0x63` | 风扇 1（CPU）信息 | 返回 `负载 | 本地温度<<8 | 远端温度<<16` |
| `0x64` | 风扇 2（GPU）信息 | 同上 |
| `0x0C` | `DEVT` 设备状态 | 256 字节；字节 2–3、4–5、6–7 为风扇 1–3 转速计数（大端），0x10–0x12 为风扇 1 负载/本地/远端温度，0x13–0x15 为风扇 2 |
| `0x68` | 设置负载 | 参数第 0–3 字节依次为风扇 1–4 的负载（0–255），4 个风扇一次设置 |
| `0x69` | 恢复自动 | 参数 bit0–3 对应风扇 1–4，等价于 EC 命令 `0x99 FF n` |

DSDT 中 `\_SB.DCHU.ZEVT` 是另一份同名方法，供 Control Center 通过 `AcpiBridge.sys` 调用，取参方式不同，不要混淆。

`tools/ecprobe/wmiprobe.cpp` 默认只调用 `0x63`、`0x64`、`0x0C` 三个读取方法；`--set-duty`、`--set-auto` 参数调用写方法（见第 6 节）。

## 4. 实测（2026-10-01，NH5x_7xRDx）

测试时停止 MyFanControl，风扇处于 EC 自动控制。

**原 DLL 与 PawnIO 探针交替读 20 轮**：14 轮完全一致。原 DLL 读到 3 次明显错误的值（风扇数 28 两次，CPU 转速计数 7223 一次，正常约 1335）；探针重试 4 次全部恢复，没有错误值，单次事务最长 2.7ms（不含重试）。

**WMI 与 PawnIO 探针对比**：`0x63` 返回 负载 64 / 本地 55 / 远端 63℃，`0x64` 返回 46 / 1 / 57℃，`0x0C` 中转速计数 1335、1746，与前后两次探针读数一致。

**EC 返回异常读数**：有约 10 秒，原 DLL 和探针都读到两个风扇温度为 1℃、负载为 0，而转速计数不变，说明是 EC 本身返回了异常值。原程序对温度跳变只会等 1 秒重读一次，之后会接受 1℃，在接管控制时把风扇降到最低档。新架构必须把这类读数判为无效并交还 EC 自动控制。

## 5. 对阶段 2 的建议
1. 优先使用 WMI 接口：不需要驱动，兼容内存完整性（HVCI），与系统 EC 访问同步。
2. 对没有 `WMBB` 的机型，退回 PawnIO + `Access_EC` + 超时 + 清理 OBF + 重试。
3. 写操作（`0x68`/`0x69`）先验证 `0x69` 恢复自动，再验证 `0x68`，每次写后读回确认，并保持退出、睡眠、读数异常时交还自动。已验证，见第 6 节。
4. 温度读数低于 10℃ 或高于 110℃ 视为无效。

## 6. WMI 写操作验证（2026-10-01，NH5x_7xRDx）

`tools/ecprobe/wmiprobe.exe --set-duty` / `--set-auto`，测试前正常关闭 MyFanControl（风扇处于 EC 自动控制），只设置不低于 45% 的负载：

| 操作 | 结果 |
|---|---|
| `0x69`，参数 `0x0F` | 返回 `0x69`，读数不变（本来就是自动） |
| `0x68`，参数 `0x99999999`（60%） | 返回 `0x68`；5 秒后 `0x63`/`0x64` 读回负载 153/153 |
| `0x68`，参数 `0x73737373`（45%） | 读回 115/115，转速计数 633→795、651→804（变慢） |
| `0x69`，参数 `0x0F` | EC 立即接管，CPU 负载随即自行调整为 99→89 |

- 写入后立即读回可能仍是旧值，EC 需要一点时间生效，下一轮（2 秒后）再确认即可。
- `0x68`/`0x69` 的返回值是固定的方法号，EC 未就绪（`ECOK` 为 0）时 BIOS 跳过写入但照样返回，不能用返回值判断是否生效。
- 单次 WMI 调用约 40ms，读取一轮（`0x63`、`0x64`、`0x0C`）约 120ms。

ClevoFan 服务（`src/`）基于此实现，真机集成测试见 `tests/integration/service_test.ps1`。
