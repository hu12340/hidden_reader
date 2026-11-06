# A3: 滚轮连续滚动（可停在页间）+ 重启恢复。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
. 'D:\MyProjects\HiddenReader\tools\gui.ps1'

$root = 'D:\MyProjects\HiddenReader'
$run  = "$root\.run\n5"
$data = "$run\data"; $log = "$run\log"; $shots = "$run\shots"
$exe = "$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$env:HR_DATA_DIR = $data; $env:HR_LOG_DIR = $log
$p = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 2500

$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$main = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$hwnd = [IntPtr]$main.Current.NativeWindowHandle
function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Get-Label([string]$id) { $e = Find-ById $main $id; if ($e) { $e.Current.Name } else { '<none>' } }

# 回到开头便于观察滚动：跳页 1
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) { $l = Get-Label 'PageLabel'; if ($l -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 400 }
$pb = Find-ById $main 'PageBox'
($pb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('1')
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class N5K{[DllImport("user32.dll")]public static extern void keybd_event(byte vk,byte s,uint f,UIntPtr e);}'
[N5K]::keybd_event(0x0D,0,0,[UIntPtr]::Zero); [N5K]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 600
Write-Host "after jump1: '$(Get-Label 'PageLabel')'"

$wr = $main.Current.BoundingRectangle
$cx = [int]($wr.X + $wr.Width * 0.62); $cy = [int]($wr.Y + $wr.Height * 0.55)
Activate-Window $hwnd | Out-Null
Start-Sleep -Milliseconds 300
Move-CursorVerified $cx $cy
Start-Sleep -Milliseconds 200

$wd = [uint32]::MaxValue - 359  # -360 (向上 3 格)
[GuiNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)   # 向下 1 格
Start-Sleep -Milliseconds 400
Write-Host "after wheel-down-1: '$(Get-Label 'PageLabel')' pagebox='$(Get-Label 'PageBox')'"
[GuiNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400
Write-Host "after wheel-down-2: '$(Get-Label 'PageLabel')'"
[GuiNative]::mouse_event(0x0800, 0, 0, $wd, [UIntPtr]::Zero)   # 向上 3 格
Start-Sleep -Milliseconds 400
Write-Host "after wheel-up-3:   '$(Get-Label 'PageLabel')'"
[GuiNative]::mouse_event(0x0800, 0, 0, $wd, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 400
Write-Host "after wheel-up-6:   '$(Get-Label 'PageLabel')'"
Save-Screenshot "$shots\a3-scroll.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)

# 记录滚动后位置，正常关闭再重启验证恢复
$before = Get-Label 'PageLabel'
$before | Set-Content "$run\a3-before.txt" -Encoding UTF8
Write-Host "before-exit: '$before'"

Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class N5C{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);}'
[N5C]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Wait-Process -Id $p.Id -Timeout 5 -ErrorAction SilentlyContinue
if (-not $p.HasExited) { $p | Stop-Process -Force; Write-Host 'WARN: 未正常退出，强杀' }

# --- 重启 ---
$p2 = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 2500
$main2 = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$deadline = (Get-Date).AddSeconds(90)
$l2 = ''
while ((Get-Date) -lt $deadline) { $l2 = Get-Label 'PageLabel'; if ($l2 -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 400 }
Write-Host "after-restart: '$l2'"
Write-Host "RESULT-restore: before='$before' after='$l2'"
$wr2 = $main2.Current.BoundingRectangle
Save-Screenshot "$shots\a3-restore.png" ([int]$wr2.X) ([int]$wr2.Y) ([int]$wr2.Width) ([int]$wr2.Height)
$p2 | Stop-Process -Force
