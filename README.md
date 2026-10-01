# MyFanControl
## 贴吧大神作品、好用度爆表、
原贴：https://tieba.baidu.com/p/5971634018?red_tag=1969604114
感谢hqnklwsy

## 使用说明
1. 安装NTPortDrvSetup,压缩包中有。
2. 管理员权限启动MyFanControl，非管理员权限启动无法设置开机自启。

## 本 fork 的改动
- 修复睡眠唤醒后偶尔提示“检测到工作线程卡死”并退出的问题：原先用当天时分秒安排更新，
  唤醒时刻早于睡眠时刻（如 23 点睡眠、次日 8 点唤醒）或系统对时回调后会长时间不更新，被看门狗误判
- 睡眠前把风扇交还 EC 自动控制，唤醒后立即恢复接管
- 不再强制结束工作线程，线程长时间无响应时由用户选择是否退出
- 新增日志 `MyFanControl.log`（程序目录下），带 `/verbose` 参数启动时记录每轮温度和转速
- 工程升级到 VS2022，静态链接 MFC，只需一个 exe，不再需要 VC++ 2013 运行库

### 安装
1. 从[原作者的 Release](https://github.com/xl-Synapse/MyFanControl/releases) 下载 `MyFanControl-v1.0.zip`，
   解压并安装其中的 NTPortDrvSetup
2. 从本仓库的 Release（或 Actions 编译产物）下载 `MyFanControl.exe`，覆盖解压目录中的同名文件。
   原有的 `MyFanControl.cfg` 配置可以继续使用

### 编译
需要 Visual Studio 2022（或 Build Tools）的“使用 C++ 的桌面开发”和“C++ MFC”组件：
```
msbuild MyFanControl.sln /p:Configuration=Release /p:Platform=Win32
```
源码为 UTF-8，编译选项指定窄字符串按 GBK 生成（程序使用多字节字符集）。

## ClevoFan（新版）

用 .NET 10 重写的风扇控制，控制逻辑（阶梯/线性曲线、过渡温度、强制冷却）与原程序相同，配置可以直接导入。

与原程序的区别：
- **不需要任何驱动**：通过 BIOS 自带的 WMI 接口（`\_SB.WMI.WMBB`）读写风扇，由 Windows 的 ACPI 驱动执行，
  与系统自身的 EC 访问同步；不再需要 NTPort 和 `ClevoEcInfo.dll`，可以开启内存完整性（HVCI）。
  协议见 [docs/ec-protocol.md](docs/ec-protocol.md)
- **后台服务**：开机即运行，不需要登录或 UAC 确认；托盘程序以普通权限运行，关闭托盘不影响控温
- **失效保护**：睡眠前、服务停止时、温度读数异常（低于 10℃ 或高于 110℃）时、访问硬件出错时交还 EC 自动控制；
  启动时先交还一次，避免上次异常退出后停在手动模式；检测到原程序正在运行时暂停接管，避免两个程序同时控制
- **GPU 限频**：通过 NVIDIA 驱动自带的 NVML 限制独显最高频率（只限制，不再提供原程序的超频）。只在独显通电时操作，
  不会为此唤醒双显卡笔记本的独显；独显重新通电、系统唤醒后自动重新应用，服务停止时解除。设置窗口打开时显示实时频率和利用率

已在 NH5x_7xRDx（Insyde BIOS，EC 07.05HE1）上测试。其他 Clevo 机型的 BIOS 若没有这个 WMI 接口，服务无法启动，原因记录在日志中。

### 安装
需要 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)。从 Release 或 Actions 编译产物下载 `ClevoFan`，
以管理员身份运行（导入原程序配置为可选）：
```
powershell -ExecutionPolicy Bypass -File install.ps1 -ImportLegacy "D:\MyFanControl-v1.0\MyFanControl.cfg"
```
安装后请不要再运行原程序（同时运行时新服务会暂停接管）。卸载：`uninstall.ps1`（加 `-Purge` 同时删除配置和日志）。

- 配置：`%ProgramData%\ClevoFan\config.json`（通过托盘的“设置”修改）
- 日志：`%ProgramData%\ClevoFan\service.log`
- 托盘程序：双击图标打开设置，右键菜单可开启强制冷却

### 开发
```
dotnet test src/ClevoFan.slnx                       # 单元测试（模拟硬件）
tools\ecprobe\build.cmd                             # 编译 wmiprobe / ecprobe 硬件探针
tests\integration\service_test.ps1 -SourceDir ...   # 真机集成测试（管理员）
dotnet build tools\gpuload -c Release -o tools\gpuload\bin\out   # GPU 满载工具（OpenCL）
tests\integration\gpu_limit_test.ps1                # 真机 GPU 限频测试（管理员）
```

## 原贴说明
1. 输入数值后要点保存才能生效。
2. 程序退出时会还原所有更改，包括还原风扇控制策略到原厂默认、解除GPU限频。
3. 风扇转速RPM可能不准确，因为dll获得的转速与实际转速是负相关的，所以我根据CC显示的转速自己拟合出来公式，我不能保证在别的电脑上显示的转速准确。
4. 过渡温度，为了避免风扇转速在2档之间频繁切换，温度下降时会延迟降低转速，比如在默认设置下，过渡温度3度，从75度温度上升到80度时转速会提高到70%，而温度下降时，需要温度低于80-3，也就是低于77度时转速才会降到55%。
5. 线性控制，平滑温度转速曲线。打开后，比如温度从60度上升到65时，每上升1度，转速会提高1%。
6. 强制冷却，强制风扇转速为95%，直到CPU和GPU温度都低于设定温度，我一般用于打完游戏关机前，把温度降下来再关机。
7. GPU限频，这只是限制GPU最大工作频率，在没有负载时，GPU频率仍然会降到更低。如果输入0则默认不限频，与不点勾的效果一致。此功能可能会与蓝天的CC冲突，因为CC的后台进程开机自启动时检测到限频后会调用同样的dll来取消限频。
