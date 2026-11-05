# 浮窗原型自动化验收脚本（阶段 A：透明度与输入）。
# 用法: powershell -File tools\test-proto.ps1
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public class W3 {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(WinApiPoint p);
    [DllImport("user32.dll")] public static extern int GetWindowLongW(IntPtr h, int index);
    [StructLayout(LayoutKind.Sequential)] public struct WinApiPoint { public int X, Y; }
}
'@

$log = "$env:TEMP\hiddenreader-proto.log"
Get-Process | Where-Object { $_.ProcessName -like '*FloatPrototype*' } | Stop-Process -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $log -ErrorAction SilentlyContinue

$env:HR_PROTO_LOG_DIR = $env:TEMP
$p = Start-Process "$repo\prototype\FloatPrototype\bin\Debug\net10.0-windows\FloatPrototype.exe" -ArgumentList '--float' -PassThru
Start-Sleep -Seconds 5
$p.Refresh()
$float = [IntPtr]$p.MainWindowHandle
"float hwnd=$float"
if ($float -eq [IntPtr]::Zero) { throw 'float window missing' }

$r = Get-WindowRect $float
"rect: $($r.Left),$($r.Top) $($r.Width)x$($r.Height)"
Save-Screenshot "$repo\artifacts\proto-bg85.png" $r.Left $r.Top $r.Width $r.Height

# --- 不透明度循环：F5 背景（0.85->0->100->50->0），F6 边框（100->50->0）
Key-Press 0x74; Start-Sleep -Milliseconds 600
Key-Press 0x74; Start-Sleep -Milliseconds 600
Key-Press 0x74; Start-Sleep -Milliseconds 800
Save-Screenshot "$repo\artifacts\proto-bg50.png" $r.Left $r.Top $r.Width $r.Height
Key-Press 0x74; Start-Sleep -Milliseconds 800
Save-Screenshot "$repo\artifacts\proto-bg0.png" $r.Left $r.Top $r.Width $r.Height
Key-Press 0x75; Start-Sleep -Milliseconds 800
Save-Screenshot "$repo\artifacts\proto-bg0-border50.png" $r.Left $r.Top $r.Width $r.Height
Key-Press 0x75; Start-Sleep -Milliseconds 800
Save-Screenshot "$repo\artifacts\proto-bg0-border0.png" $r.Left $r.Top $r.Width $r.Height

# --- 完全透明时点击翻页：记录点击前后锚点
$before = (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
$cx = $r.Left + [int]($r.Width / 2)
$cy = $r.Top + [int]($r.Height / 2)
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 800
$after = (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
"CLICK1 before: $before"
"CLICK1 after : $after"
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 800
$after2 = (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
"CLICK2 after : $after2"
Save-Screenshot "$repo\artifacts\proto-clicked.png" $r.Left $r.Top $r.Width $r.Height

# --- 右键菜单
Mouse-Click $cx $cy 'right'
Start-Sleep -Milliseconds 800
$menu = [GuiNative]::GetForegroundWindow()
Save-Screenshot "$repo\artifacts\proto-menu.png" ($cx - 40) ($cy - 40) 420 420
# 按 Esc 关闭菜单
Key-Press 0x1B
Start-Sleep -Milliseconds 400

"LOG TAIL:"
Get-Content $log | Select-Object -Last 25
