@echo off
rem 编译 x64 版 ecprobe.exe，需要 Visual Studio 2022（或 Build Tools）的 C++ 组件
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo 未找到 Visual Studio C++ 工具
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cl /nologo /EHsc /O2 /W4 /utf-8 /std:c++17 "%~dp0ecprobe.cpp" /Fe:"%~dp0ecprobe.exe" /Fo:"%TEMP%\ecprobe.obj" || exit /b 1
cl /nologo /EHsc /O2 /W4 /utf-8 /std:c++17 "%~dp0wmiprobe.cpp" /Fe:"%~dp0wmiprobe.exe" /Fo:"%TEMP%\wmiprobe.obj"
