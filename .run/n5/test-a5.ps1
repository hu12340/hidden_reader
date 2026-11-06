# A5: 大书浮窗翻页 + 滚动后切模式/缩放/重启恢复。
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

# 状态重置：LastBookId=大书，普通模式，锚点 0
$stateFile = "$data\library.json"
$json = [IO.File]::ReadAllText($stateFile)
$json = $json.Replace('"LastBookId": "e662fc8a258141828977873ea156ef96"', '"LastBookId": "aa85bc4c715140b9810d77da1b025399"')
$json = $json.Replace('"LastMode": "float"', '"LastMode": "normal"')
$json = $json -replace '"Anchor": \d+', '"Anchor": 0'
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
function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $btn = $null
    for ($i = 0; $i -lt 20 -and -not $btn; $i++) { $btn = Find-ById $win $id; if (-not $btn) { Start-Sleep -Milliseconds 300 } }
    if (-not $btn) { throw "button $id not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
function Diff-Shots([string]$a, [string]$b) {
    $ba = [System.Drawing.Bitmap]::FromFile($a); $bb = [System.Drawing.Bitmap]::FromFile($b)
    $w = [Math]::Min($ba.Width, $bb.Width); $h = [Math]::Min($ba.Height, $bb.Height)
    $diff = 0
    for ($y = 0; $y -lt $h; $y += 2) { for ($x = 0; $x -lt $w; $x += 2) {
        $pa = $ba.GetPixel($x, $y); $pb = $bb.GetPixel($x, $y)
        if ([Math]::Abs($pa.R - $pb.R) + [Math]::Abs($pa.G - $pb.G) + [Math]::Abs($pa.B - $pb.B) -gt 30) { $diff++ }
    } }
    $ba.Dispose(); $bb.Dispose(); return $diff
}

# 等大书打开并算好总页数
$deadline = (Get-Date).AddSeconds(120)
$l = ''
while ((Get-Date) -lt $deadline) { $l = Get-Label 'PageLabel'; if ($l -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 500 }
Write-Host "big book ready: '$l'"

$wr = $main.Current.BoundingRectangle
$cx = [int]($wr.X + $wr.Width * 0.62); $cy = [int]($wr.Y + $wr.Height * 0.55)
Activate-Window $hwnd | Out-Null
Start-Sleep -Milliseconds 300
Move-CursorVerified $cx $cy
Start-Sleep -Milliseconds 200

# --- 1. 滚动停在两页之间（第1页内滚动，页码不变但视图变） ---
Save-Screenshot "$shots\a5-page1.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)
[GuiNative]::mouse_event(0x0800, 0, 0, 240, [UIntPtr]::Zero)   # 下滚 2 格
Start-Sleep -Milliseconds 500
Save-Screenshot "$shots\a5-scrolled.png" ([int]$wr.X) ([int]$wr.Y) ([int]$wr.Width) ([int]$wr.Height)
$dScroll = Diff-Shots "$shots\a5-page1.png" "$shots\a5-scrolled.png"
$lblScrolled = Get-Label 'PageLabel'
Write-Host "RESULT-scroll-between-pages: diff=$dScroll label='$lblScrolled' -> $(if ($dScroll -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 2. 滚动后进入浮窗（视野文字保持） ---
Invoke-Button $main 'FloatButton'
Start-Sleep -Milliseconds 1500
$fh = [IntPtr]::Zero
foreach ($w in (Get-ProcessWindows $p.Id)) { if ((Get-WindowClass $w) -eq 'HiddenReaderFloatWindow') { $fh = $w; break } }
if ($fh -eq [IntPtr]::Zero) { Write-Host 'FAIL: 浮窗未创建'; $p | Stop-Process -Force; exit 1 }
$fr = Get-WindowRect $fh
$fw = $fr.Right - $fr.Left; $fhgt = $fr.Bottom - $fr.Top
Save-Screenshot "$shots\a5-float-enter.png" $fr.Left $fr.Top $fw $fhgt
Write-Host 'float entered（浮窗首屏应含滚动后正在阅读的文字，证据 a5-float-enter.png / a5-scrolled.png）'

# --- 3. 大书浮窗固定尺寸翻页 ---
$fcx = [int]($fr.Left + $fw / 2); $fcy = [int]($fr.Top + $fhgt / 2)
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 500
Save-Screenshot "$shots\a5-f1.png" $fr.Left $fr.Top $fw $fhgt
$d1 = Diff-Shots "$shots\a5-float-enter.png" "$shots\a5-f1.png"
Activate-Window $fh | Out-Null; Start-Sleep -Milliseconds 300
Key-Press 0x27; Start-Sleep -Milliseconds 500
Save-Screenshot "$shots\a5-f2.png" $fr.Left $fr.Top $fw $fhgt
$d2 = Diff-Shots "$shots\a5-f1.png" "$shots\a5-f2.png"
Key-Press 0x25; Start-Sleep -Milliseconds 500
Save-Screenshot "$shots\a5-f3.png" $fr.Left $fr.Top $fw $fhgt
$d3 = Diff-Shots "$shots\a5-f2.png" "$shots\a5-f3.png"
Write-Host "RESULT-bigbook-float-click: diff=$d1 -> $(if ($d1 -gt 50) { 'PASS' } else { 'FAIL' })"
Write-Host "RESULT-bigbook-float-right: diff=$d2 -> $(if ($d2 -gt 50) { 'PASS' } else { 'FAIL' })"
Write-Host "RESULT-bigbook-float-left: diff=$d3 -> $(if ($d3 -gt 50) { 'PASS' } else { 'FAIL' })"

# --- 4. Esc 回普通模式，页码与浮窗翻页一致 ---
Key-Press 0x1B
Start-Sleep -Milliseconds 900
Write-Host "after esc back to normal: label='$(Get-Label 'PageLabel')'"

# --- 5. 以浮窗模式退出（强杀，状态已即时落盘），重启应直接恢复浮窗 ---
Invoke-Button $main 'FloatButton'
Start-Sleep -Milliseconds 1200
$beforeShot = "$shots\a5-restart-before.png"
$fr = Get-WindowRect $fh
Save-Screenshot $beforeShot $fr.Left $fr.Top ($fr.Right - $fr.Left) ($fr.Bottom - $fr.Top)
Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 1200

$p2 = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 4000
$main2 = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
$fh2 = [IntPtr]::Zero
for ($i = 0; $i -lt 12 -and $fh2 -eq [IntPtr]::Zero; $i++) {
    foreach ($w in (Get-ProcessWindows $p2.Id)) { if ((Get-WindowClass $w) -eq 'HiddenReaderFloatWindow') { $fh2 = $w; break } }
    if ($fh2 -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 300 }
}
if ($fh2 -eq [IntPtr]::Zero) { Write-Host 'RESULT-float-restart: FAIL 浮窗未恢复'; $p2 | Stop-Process -Force; exit 1 }
$fr2 = Get-WindowRect $fh2
$afterShot = "$shots\a5-restart-after.png"
Save-Screenshot $afterShot $fr2.Left $fr2.Top ($fr2.Right - $fr2.Left) ($fr2.Bottom - $fr2.Top)
$dR = Diff-Shots $beforeShot $afterShot
Write-Host "RESULT-float-restart: restored hwnd=$fh2 contentDiff=$dR（相同锚点+尺寸，内容应基本一致，diff 小）"
Write-Host "float rect before=($($fr.Left),$($fr.Top)) after=($($fr2.Left),$($fr2.Top))"

$p2 | Stop-Process -Force
Write-Host '--- A5 done ---'
