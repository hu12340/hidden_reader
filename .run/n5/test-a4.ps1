# A4: 浮窗综合 - 固定尺寸翻页刷新(任务2核心)、滚轮、全透明、缩放热区、防误翻页。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
. 'D:\MyProjects\HiddenReader\tools\gui.ps1'

$root = 'D:\MyProjects\HiddenReader'
$run  = "$root\.run\n5"
$data = "$run\data"; $log = "$run\log"; $shots = "$run\shots"
$exe = "$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$short = "$root\testdata\test-book-2.txt"
$env:HR_DATA_DIR = $data; $env:HR_LOG_DIR = $log
# 测试要求从普通模式起步：重置上次遗留的浮窗模式标记。
$stateFile = "$data\library.json"
if (Test-Path $stateFile) {
    $json = [IO.File]::ReadAllText($stateFile)
    $json = $json.Replace('"LastMode": "float"', '"LastMode": "normal"')
    [IO.File]::WriteAllText($stateFile, $json, (New-Object Text.UTF8Encoding $false))
}
$p = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 2500

$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$main = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $btn = $null
    for ($i = 0; $i -lt 20 -and -not $btn; $i++) { $btn = Find-ById $win $id; if (-not $btn) { Start-Sleep -Milliseconds 300 } }
    if (-not $btn) { throw "button $id not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Get-Label([string]$id) { $e = Find-ById $main $id; if ($e) { $e.Current.Name } else { '<none>' } }

function Diff-Shots([string]$a, [string]$b) {
    $ba = [System.Drawing.Bitmap]::FromFile($a); $bb = [System.Drawing.Bitmap]::FromFile($b)
    $w = [Math]::Min($ba.Width, $bb.Width); $h = [Math]::Min($ba.Height, $bb.Height)
    $diff = 0
    for ($y = 0; $y -lt $h; $y += 2) { for ($x = 0; $x -lt $w; $x += 2) {
        $pa = $ba.GetPixel($x, $y); $pb = $bb.GetPixel($x, $y)
        if ([Math]::Abs($pa.R - $pb.R) + [Math]::Abs($pa.G - $pb.G) + [Math]::Abs($pa.B - $pb.B) -gt 30) { $diff++ }
    } }
    $ba.Dispose(); $bb.Dispose()
    return $diff
}

# --- 导入短书（对话框自动化；已导入过则直接选中） ---
$curLabel = Get-Label 'PageLabel'
if ($curLabel -notmatch '第 \d+/\d+ 页') {
    Invoke-Button $main 'ImportButton'
    $dlg = Wait-VisibleDialog $p.Id 8000
    Set-DialogFileName $dlg $short
    Click-DialogButton $dlg '打开'
    Start-Sleep -Milliseconds 2000
}
Write-Host "short book label: '$(Get-Label 'PageLabel')'"

# --- 进入浮窗 ---
Invoke-Button $main 'FloatButton'
Start-Sleep -Milliseconds 1200
$fh = [IntPtr]::Zero
for ($i = 0; $i -lt 12 -and $fh -eq [IntPtr]::Zero; $i++) {
    foreach ($w in (Get-ProcessWindows $p.Id)) {
        if ((Get-WindowClass $w) -eq 'HiddenReaderFloatWindow') { $fh = $w; break }
    }
    if ($fh -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
}
if ($fh -eq [IntPtr]::Zero) { Write-Host 'FAIL: 浮窗未创建'; $p | Stop-Process -Force; exit 1 }
$fr = Get-WindowRect $fh
Write-Host "float hwnd=$fh rect=($($fr.Left),$($fr.Top),$($fr.Right-$fr.Left),$($fr.Bottom-$fr.Top))"
$fcx = [int](($fr.Left + $fr.Right) / 2); $fcy = [int](($fr.Top + $fr.Bottom) / 2)

function Float-Shot([string]$path) {
    $w = $fr.Right - $fr.Left; $h = $fr.Bottom - $fr.Top
    Save-Screenshot $path $fr.Left $fr.Top $w $h
}

# --- 1. 固定尺寸单击翻页刷新（任务2） ---
Float-Shot "$shots\a4-f0.png"
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-f1.png"
$d1 = Diff-Shots "$shots\a4-f0.png" "$shots\a4-f1.png"
Write-Host "RESULT-float-click-flip: diff=$d1 -> $(if ($d1 -gt 50) { 'PASS' } else { 'FAIL' })"
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-f2.png"
$d2 = Diff-Shots "$shots\a4-f1.png" "$shots\a4-f2.png"
Write-Host "RESULT-float-click-flip-2: diff=$d2 -> $(if ($d2 -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 2. 方向键翻页 ---
Activate-Window $fh | Out-Null
Start-Sleep -Milliseconds 300
Key-Press 0x27
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-f3.png"
$d3 = Diff-Shots "$shots\a4-f2.png" "$shots\a4-f3.png"
Write-Host "RESULT-float-key-flip: diff=$d3 -> $(if ($d3 -gt 50) { 'PASS' } else { 'FAIL' })"
Key-Press 0x25
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-f4.png"
$d4 = Diff-Shots "$shots\a4-f3.png" "$shots\a4-f4.png"
Write-Host "RESULT-float-key-back: diff=$d4 -> $(if ($d4 -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 3. 浮窗滚轮（点击已获得焦点） ---
[GuiNative]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-f5.png"
$d5 = Diff-Shots "$shots\a4-f4.png" "$shots\a4-f5.png"
Write-Host "RESULT-float-wheel: diff=$d5 -> $(if ($d5 -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 4. 全透明（F5x2 背景、F6x2 边框） ---
Key-Press 0x74; Start-Sleep -Milliseconds 200
Key-Press 0x74; Start-Sleep -Milliseconds 200
Key-Press 0x75; Start-Sleep -Milliseconds 200
Key-Press 0x75; Start-Sleep -Milliseconds 400
Float-Shot "$shots\a4-transparent.png"
Write-Host 'RESULT-transparent: 见截图 a4-transparent.png（正文应仍清晰、背景全透明）'
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-t-flip.png"
$d6 = Diff-Shots "$shots\a4-transparent.png" "$shots\a4-t-flip.png"
Write-Host "RESULT-transparent-click-flip: diff=$d6 -> $(if ($d6 -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 5. 边缘缩放（右下角向外拖） ---
Mouse-DownAt ($fr.Right - 3) ($fr.Bottom - 3)
Start-Sleep -Milliseconds 150
Mouse-MoveTo ($fr.Right + 80) ($fr.Bottom + 60)
Start-Sleep -Milliseconds 150
Mouse-UpAt ($fr.Right + 80) ($fr.Bottom + 60)
Start-Sleep -Milliseconds 600
$fr2 = Get-WindowRect $fh
$dw = ($fr2.Right - $fr2.Left) - ($fr.Right - $fr.Left); $dh = ($fr2.Bottom - $fr2.Top) - ($fr.Bottom - $fr.Top)
Write-Host "RESULT-float-resize: dw=$dw dh=$dh -> $(if ($dw -gt 40 -and $dh -gt 30) { 'PASS' } else { 'FAIL' })"
$fr = $fr2
$fcx = [int](($fr.Left + $fr.Right) / 2); $fcy = [int](($fr.Top + $fr.Bottom) / 2)

# --- 6. 拖动选字不翻页 ---
Float-Shot "$shots\a4-pre-drag.png"
Mouse-DownAt ($fcx - 60) $fcy
Start-Sleep -Milliseconds 120
Mouse-MoveTo ($fcx + 60) $fcy
Start-Sleep -Milliseconds 120
Mouse-UpAt ($fcx + 60) $fcy
Start-Sleep -Milliseconds 500
Float-Shot "$shots\a4-post-drag.png"
$d7 = Diff-Shots "$shots\a4-pre-drag.png" "$shots\a4-post-drag.png"
Write-Host "RESULT-drag-select-no-flip: diff=$d7（应远小于翻页 diff，选区有高亮变化）-> $(if ($d7 -lt 50) { 'PASS(未翻页)' } else { 'CHECK' })"

# --- 7. Ctrl+左键拖动移动浮窗 ---
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class N5M{[DllImport("user32.dll")]public static extern void keybd_event(byte vk,byte s,uint f,UIntPtr e);}'
[N5M]::keybd_event(0x11,0,0,[UIntPtr]::Zero)
Mouse-DownAt $fcx ($fr.Top + 20)
Start-Sleep -Milliseconds 120
Mouse-MoveTo ($fcx - 120) ($fr.Top + 20 + 60)
Start-Sleep -Milliseconds 120
Mouse-UpAt ($fcx - 120) ($fr.Top + 20 + 60)
[N5M]::keybd_event(0x11,0,2,[UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
$fr3 = Get-WindowRect $fh
$mx = $fr3.Left - $fr.Left; $my = $fr3.Top - $fr.Top
Write-Host "RESULT-float-move: d=($mx,$my) -> $(if ([Math]::Abs($mx) -gt 50 -and [Math]::Abs($my) -gt 30) { 'PASS' } else { 'FAIL' })"

# --- 8. Esc 返回普通模式 ---
Activate-Window $fh | Out-Null
Start-Sleep -Milliseconds 300
Key-Press 0x1B
Start-Sleep -Milliseconds 800
$mainAgain = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
Write-Host "RESULT-esc-normal: mainVisible=$($mainAgain -ne $null -and -not $mainAgain.Current.IsOffscreen)"

$p | Stop-Process -Force
Write-Host '--- A4 done ---'
