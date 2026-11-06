# 诊断2: 用主窗口矩形推算正文区坐标，重测单击/方向键翻页与滚轮滚动。
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

$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) { $l = Get-Label 'PageLabel'; if ($l -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 400 }
Write-Host "start label='$l'"

$wr = $main.Current.BoundingRectangle
# 正文区：窗口下半偏右（避开顶部工具栏与左侧目录栏）
$cx = [int]($wr.X + $wr.Width * 0.62); $cy = [int]($wr.Y + $wr.Height * 0.55)
Write-Host "win=($($wr.X),$($wr.Y),$($wr.Width),$($wr.Height)) click=($cx,$cy)"

Activate-Window $hwnd | Out-Null
Start-Sleep -Milliseconds 300
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 500
Write-Host "after click1: '$(Get-Label 'PageLabel')'"
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 500
Write-Host "after click2: '$(Get-Label 'PageLabel')'"
Key-Press 0x27
Start-Sleep -Milliseconds 500
Write-Host "after right:  '$(Get-Label 'PageLabel')'"
Key-Press 0x25
Start-Sleep -Milliseconds 500
Write-Host "after left:   '$(Get-Label 'PageLabel')'"

# 滚轮: 向下 3 格
[GuiNative]::mouse_event(0x0800, 0, 0, 360, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Write-Host "after wheel-down: '$(Get-Label 'PageLabel')'"
[GuiNative]::mouse_event(0x0800, 0, 0, -360, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Write-Host "after wheel-up:   '$(Get-Label 'PageLabel')'"
Save-Screenshot "$shots\diag-flip2.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)

# 记录锚点供重启对比
$labelBefore = Get-Label 'PageLabel'
$labelBefore | Set-Content "$run\diag2-before.txt" -Encoding UTF8
Write-Host "saved: $labelBefore"

# 正常关闭（WM_CLOSE）以便重启恢复
PostClose 2>$null
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class N5C{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);}'
[N5C]::PostMessage($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Wait-Process -Id $p.Id -Timeout 5 -ErrorAction SilentlyContinue
Write-Host "exited=$($p.HasExited)"
if (-not $p.HasExited) { $p | Stop-Process -Force }
