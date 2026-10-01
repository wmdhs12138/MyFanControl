@echo off
rem 编译 x86 版模拟 ClevoEcInfo.dll（输出到本目录的 out\ClevoEcInfo.dll）
setlocal
for /f "usebackq delims=" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSDIR=%%i"
if not defined VSDIR (
  echo Visual Studio C++ tools not found
  exit /b 1
)
call "%VSDIR%\VC\Auxiliary\Build\vcvars32.bat" >nul || exit /b 1
if not exist "%~dp0out" mkdir "%~dp0out"
cl /nologo /LD /EHsc /O2 /W4 /utf-8 "%~dp0fake_ec.cpp" /Fe:"%~dp0out\ClevoEcInfo.dll" /Fo:"%TEMP%\fake_ec.obj"
