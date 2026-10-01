# 曲线编辑器界面测试：向编辑器发送鼠标拖动和方向键消息，截图到 %TEMP%（curve_editor_*.png）供人工核对。
# 不需要管理员权限，不点保存，不改配置。托盘只允许一个实例，测试期间会临时关闭正在运行的托盘，结束后重新启动。
# 预期：CPU 曲线 ≥80℃ 一档从 70% 拖到 50%，再按 3 次上键变为 53%，下方数字框同步为 53。
# 先编译：dotnet build src\ClevoFan.Tray
param([string]$Exe = (Join-Path $PSScriptRoot '..\..\src\ClevoFan.Tray\bin\Debug\net10.0-windows\win-x64\ClevoFan.Tray.exe'))
Add-Type -ReferencedAssemblies System.Drawing.Common, System.Drawing.Primitives, System.Private.Windows.Core, System.Private.Windows.GdiPlus -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices; using System.Text;
public static class EdUi {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public static IntPtr LargestLeafChild(IntPtr parent) {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumChildWindows(parent, (h, l) => { if (GetWindow(h, 5) == IntPtr.Zero) { RECT r; GetClientRect(h, out r); long a = (long)r.R * r.B; if (a > bestArea) { bestArea = a; best = h; } } return true; }, IntPtr.Zero);
        return best;
    }
    public static string Shot(IntPtr h, string path) {
        RECT r; GetWindowRect(h, out r);
        using (var b = new Bitmap(r.R - r.L, r.B - r.T)) using (var g = Graphics.FromImage(b)) { IntPtr dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); b.Save(path, ImageFormat.Png); }
        return (r.R - r.L) + "x" + (r.B - r.T);
    }
}
'@
[EdUi]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
function Lp($x, $y) { [IntPtr](([int]$y -shl 16) -bor [int]$x) }
$running = Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue | Select-Object -First 1
$runningPath = $running.Path
$running | Stop-Process -Force
$p = Start-Process $Exe -ArgumentList '--settings' -PassThru
try {
    Start-Sleep -Seconds 5
    $form = [EdUi]::FindWindow([NullString]::Value, 'Clevo 风扇控制')
    $ed = [EdUi]::LargestLeafChild($form)
    $rc = New-Object EdUi+RECT; [EdUi]::GetClientRect($ed, [ref]$rc) | Out-Null
    $s = [EdUi]::GetDpiForWindow($ed) / 96.0
    #与 CurveGeometry 相同的边距
    $left = 40 * $s; $top = 10 * $s; $right = $rc.R - 12 * $s; $bottom = $rc.B - 24 * $s
    $x = [int]($left + (80 - 40) / 55.0 * ($right - $left))
    $y1 = [int]($bottom - 0.70 * ($bottom - $top)); $y2 = [int]($bottom - 0.50 * ($bottom - $top))
    "editor client {0}x{1}, dpi scale {2}, drag ({3},{4}) -> ({3},{5})" -f $rc.R, $rc.B, $s, $x, $y1, $y2
    "before: " + [EdUi]::Shot($form, "$env:TEMP\curve_editor_before.png")
    [EdUi]::PostMessage($ed, 0x0201, [IntPtr]1, (Lp $x $y1)) | Out-Null
    foreach ($y in ($y1 - 10), ($y1 - 30), $y2) { [EdUi]::PostMessage($ed, 0x0200, [IntPtr]1, (Lp $x $y)) | Out-Null; Start-Sleep -Milliseconds 100 }
    [EdUi]::PostMessage($ed, 0x0202, [IntPtr]0, (Lp $x $y2)) | Out-Null
    Start-Sleep -Milliseconds 300
    "after drag: " + [EdUi]::Shot($form, "$env:TEMP\curve_editor_drag.png")
    1..3 | ForEach-Object { [EdUi]::PostMessage($ed, 0x0100, [IntPtr]0x26, [IntPtr]1) | Out-Null; [EdUi]::PostMessage($ed, 0x0101, [IntPtr]0x26, [IntPtr]0xC0000001) | Out-Null; Start-Sleep -Milliseconds 100 }
    Start-Sleep -Milliseconds 500
    "after keys: " + [EdUi]::Shot($form, "$env:TEMP\curve_editor_after.png")
} finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    if ($runningPath) {
        #通过资源管理器启动，托盘以普通权限运行在用户桌面
        Start-Process explorer.exe -ArgumentList "`"$runningPath`""
        Start-Sleep -Seconds 3
        "已重新启动托盘：" + [bool](Get-Process ClevoFan.Tray -ErrorAction SilentlyContinue)
    }
}
