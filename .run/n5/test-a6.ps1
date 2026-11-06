# A6: 普通模式重启恢复（跳到中部页）+ 光标在正文区外时滚轮不滚动。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
. 'D:\MyProjects\HiddenReader\tools\gui.ps1'
$root = 'D:\MyProjects\HiddenReader'
$run  = "$root\.run\n5"
$data = "$run\data"; $log = "$run\log"; $shots = "$run\shots"
$exe = "$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$env:HR_DATA_DIR = $data; $env:HR_LOG_DIR = $log

$stateFile = "$data\library.json"
$json = [IO.File]::ReadAllText($stateFile)
$json = $json.Replace('"LastMode": "float"', '"LastMode": "normal"')
[IO.File]::WriteAllText($stateFile, $json, (New-Object Text.UTF8Encoding $false))

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

# 等开书并算好
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) { $l = Get-Label 'PageLabel'; if ($l -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 500 }

# 跳到第 5000 页
$pb = Find-ById $main 'PageBox'
($pb.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('5000')
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class N6K{[DllImport("user32.dll")]public static extern void keybd_event(byte vk,byte s,uint f,UIntPtr e);}'
[N6K]::keybd_event(0x0D,0,0,[UIntPtr]::Zero); [N6K]::keybd_event(0x0D,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
$before = Get-Label 'PageLabel'
$wr = $main.Current.BoundingRectangle
Save-Screenshot "$shots\a6-before.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)
Write-Host "before exit: '$before'"

# --- 光标在工具栏（跳页框附近）滚动，正文不应滚动 ---
$tbx = [int]($wr.X + 320); $tby = [int]($wr.Y + 70)   # 第二行工具栏区域
Move-CursorVerified $tbx $tby; Start-Sleep -Milliseconds 200
[GuiNative]::mouse_event(0x0800, 0, 0, 240, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Save-Screenshot "$shots\a6-wheel-toolbar.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)
$ba = [System.Drawing.Bitmap]::FromFile("$shots\a6-before.png"); $bb = [System.Drawing.Bitmap]::FromFile("$shots\a6-wheel-toolbar.png")
$diff = 0
for ($y = 200; $y -lt $ba.Height - 10; $y += 2) { for ($x = 250; $x -lt $ba.Width - 10; $x += 2) {
    $pa = $ba.GetPixel($x, $y); $pb2 = $bb.GetPixel($x, $y)
    if ([Math]::Abs($pa.R - $pb2.R) + [Math]::Abs($pa.G - $pb2.G) + [Math]::Abs($pa.B - $pb2.B) -gt 30) { $diff++ }
} }
$ba.Dispose(); $bb.Dispose()
Write-Host "RESULT-wheel-outside-reader: readerDiff=$diff -> $(if ($diff -eq 0) { 'PASS' } else { 'FAIL' })"

# --- 强杀重启，验证普通模式恢复 ---
Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 1200
$p2 = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 2500
$cond2 = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p2.Id)
$main2 = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond2)
function Get-Label2([string]$id) { $e = Find-ById $main2 $id; if ($e) { $e.Current.Name } else { '<none>' } }
$deadline = (Get-Date).AddSeconds(120)
$l2 = ''
while ((Get-Date) -lt $deadline) { $l2 = Get-Label2 'PageLabel'; if ($l2 -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 500 }
$wr2 = $main2.Current.BoundingRectangle
Save-Screenshot "$shots\a6-after.png" ([int]$wr2.X) ([int]$wr2.Y) ([int]$wr2.Width) ([int]$wr2.Height)
Write-Host "after restart: '$l2'"
Write-Host "RESULT-normal-restart: before='$($before.Substring(0,[Math]::Min(20,$before.Length)))' after='$($l2.Substring(0,[Math]::Min(20,$l2.Length)))' -> $(if ($before.Substring(0,8) -eq $l2.Substring(0,8)) { 'PASS' } else { 'CHECK' })"
$p2 | Stop-Process -Force
Write-Host '--- A6 done ---'
