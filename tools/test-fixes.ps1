# D3/D4/D5 定向复验（100% 缩放，隔离数据目录，按 PID 清理）。
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WFix {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$exe = "$repo\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$art = "$repo\artifacts"
$data = "$env:TEMP\hr-dfix-100"
$pass = 0; $fail = 0
$myPids = New-Object System.Collections.ArrayList
function Verdict([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "PASS  $name $detail" }
    else { $script:fail++; Write-Output "FAIL  $name $detail" }
}
function Get-Rect([IntPtr]$h) { $r = New-Object WFix+RECT; [WFix]::GetWindowRect($h, [ref]$r) | Out-Null; $r }
function Stop-Mine { foreach ($id in $script:myPids) { try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { } } }

# 状态种子：book1 已导入；$withWindow 控制是否写入已保存坐标
function Start-AppState([string]$json) {
    if (Test-Path $data) { Remove-Item $data -Recurse -Force }
    New-Item -ItemType Directory -Force -Path "$data\books" | Out-Null
    Copy-Item "$repo\testdata\test-book-1.txt" "$data\books\book1.txt"
    $json | Set-Content "$data\library.json" -Encoding UTF8
    $env:HR_DATA_DIR = $data
    $env:HR_LOG_DIR = "$data\logs"
    $p = Start-Process $exe -PassThru
    [void]$script:myPids.Add($p.Id)
    Start-Sleep -Seconds 4
    $p.Refresh()
    return $p
}
$bookEntry = '{ "Id": "b1", "Title": "book1", "FileName": "book1.txt", "SourcePath": "x", "AddedAt": "2026-01-01T00:00:00" }'
$perBook = '"PerBook": { "b1": { "Anchor": 0, "Float": { "BackgroundOpacity": 0.85, "BorderOpacity": 1.0, "Topmost": true, "X": -1, "Y": -1, "Width": 460, "Height": 360 } } }'

$screenW = [WFix]::GetSystemMetrics(0); $screenH = [WFix]::GetSystemMetrics(1)

# ===== D3-A：首次放置（无 WindowX）→ 右侧 =====
$p = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "normal", $perBook }
"@
$mh = [IntPtr]$p.MainWindowHandle
$r = Get-Rect $mh
Verdict 'D3-first-placement-right' ($r.Right -ge $screenW - 40 - ($screenW - $r.Right) * 0 -and $r.Left -gt $screenW * 0.5) "rect=$($r.Left),$($r.Top) screen=$screenW"
$saved = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).WindowX
Verdict 'D3-first-placement-saved' ($null -ne $saved) "WindowX=$saved"
Stop-Mine; $myPids.Clear()

# ===== D3-B1：已保存有效位置 100,100 → 精确恢复 =====
$p = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "normal", $perBook, "WindowX": 100, "WindowY": 100 }
"@
$mh = [IntPtr]$p.MainWindowHandle
$r = Get-Rect $mh
Verdict 'D3-restore-saved-position' ([Math]::Abs($r.Left - 100) -le 2 -and [Math]::Abs($r.Top - 100) -le 2) "rect=$($r.Left),$($r.Top)"

# ===== D3-B2：保存位置不可见（-1000,200，屏幕上 0 像素）→ 回退可见工作区 =====
$p2 = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "normal", $perBook, "WindowX": -1000, "WindowY": 200 }
"@
$mh2 = [IntPtr]$p2.MainWindowHandle
$r2 = Get-Rect $mh2
Verdict 'D3-fallback-invisible-position' ($r2.Left -ge 0 -and $r2.Top -ge 0 -and $r2.Right -le $screenW + 8) "rect=$($r2.Left),$($r2.Top)"
Save-Screenshot "$art\fix-01-d3-fallback.png" $r2.Left $r2.Top ($r2.Right - $r2.Left) ($r2.Bottom - $r2.Top)

# ===== D5（普通模式，继续用 B1 实例）=====
# 拖动选字：从正文 (50%,35%) 向右拖 120px
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Read-PageLabel([System.Windows.Automation.AutomationElement]$win) {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($el in $all) { if ($el.Current.Name -match '^第 \d+/\d+ 页') { return $el.Current.Name } }
    return ''
}
Activate-Window $mh | Out-Null
Start-Sleep -Milliseconds 400
$labelBefore = Read-PageLabel $win
$dx = [int]($r.Left + ($r.Right - $r.Left) * 0.5)
$dy = [int]($r.Top + ($r.Bottom - $r.Top) * 0.35)
Mouse-DownAt $dx $dy
Mouse-MoveTo ($dx + 60) $dy
Mouse-MoveTo ($dx + 120) $dy
Mouse-UpAt ($dx + 120) $dy
Start-Sleep -Milliseconds 500
Save-Screenshot "$art\fix-02-d5-normal-selection.png" $r.Left $r.Top ($r.Right - $r.Left) ($r.Bottom - $r.Top)
$labelAfterDrag = Read-PageLabel $win
Verdict 'D5-normal-drag-no-page-flip' ($labelAfterDrag -eq $labelBefore) "[$labelBefore] -> [$labelAfterDrag]"
# Ctrl+C 复制
Key-Down 0x11; Key-Press 0x43; Key-Up 0x11
Start-Sleep -Milliseconds 500
$clip = Get-Clipboard -Raw
$source = Get-Content "$repo\testdata\test-book-1.txt" -Raw -Encoding UTF8
$clipOk = -not [string]::IsNullOrWhiteSpace($clip) -and $source.Contains($clip)
Verdict 'D5-normal-ctrl-c-copy' $clipOk "clip='$($clip.Trim().Substring(0, [Math]::Min(20, $clip.Trim().Length)))'"
# 单击仍翻一页
Mouse-Click ([int]($r.Left + ($r.Right - $r.Left) * 0.5)) ([int]($r.Top + ($r.Bottom - $r.Top) * 0.5))
Start-Sleep -Milliseconds 700
$labelAfterClick = Read-PageLabel $win
Verdict 'D5-normal-single-click-pages' ($labelAfterClick -ne $labelAfterDrag) "[$labelAfterDrag] -> [$labelAfterClick]"

# 搜索框 Ctrl+C 不被拦截（真实点击放入焦点）
$searchBox = Find-ById $win 'SearchBox'
($searchBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('挂钟')
$sbRect = $searchBox.Current.BoundingRectangle
Mouse-Click ([int]($sbRect.X + $sbRect.Width / 2)) ([int]($sbRect.Y + $sbRect.Height / 2))
Start-Sleep -Milliseconds 400
Key-Down 0x11; Key-Press 0x41; Key-Up 0x11  # Ctrl+A 全选（TextBox 无选区时 Ctrl+C 是空操作）
Key-Down 0x11; Key-Press 0x43; Key-Up 0x11
Start-Sleep -Milliseconds 400
$clip2 = Get-Clipboard -Raw
Verdict 'D5-normal-searchbox-copy-not-intercepted' ($clip2 -eq '挂钟') "clip='$clip2'"
Stop-Mine; $myPids.Clear()

# ===== D4-C1：浮窗首次放置（-1/-1）→ 右下角 =====
$p = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "float", $perBook }
"@
$fh = [IntPtr]$p.MainWindowHandle
$fr = Get-Rect $fh
Verdict 'D4-float-first-placement-bottomright' ($fr.Right -ge $screenW - 60 - 0 -and $fr.Bottom -ge $screenH - 80) "rect=$($fr.Left),$($fr.Top) screen=${screenW}x$screenH"
Stop-Mine; $myPids.Clear()

# ===== D4-C2：浮窗有效保存位置 → 精确恢复 =====
$p = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "float", "PerBook": { "b1": { "Anchor": 0, "Float": { "BackgroundOpacity": 0.85, "BorderOpacity": 1.0, "Topmost": true, "X": 800, "Y": 700, "Width": 460, "Height": 360 } } } }
"@
$fh = [IntPtr]$p.MainWindowHandle
$fr = Get-Rect $fh
Verdict 'D4-float-restore-saved-position' ([Math]::Abs($fr.Left - 800) -le 3 -and [Math]::Abs($fr.Top - 700) -le 3) "rect=$($fr.Left),$($fr.Top)"

# ===== D5（浮窗）：拖选/Ctrl+C/移动缩放不翻页/单击翻页 =====
$bookState = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).PerBook.b1
$anchorBefore = $bookState.Anchor
Activate-Window $fh | Out-Null
Start-Sleep -Milliseconds 400
# Ctrl+拖动移动 -100,-80
$fcx = [int](($fr.Left + $fr.Right) / 2); $fcy = [int](($fr.Top + $fr.Bottom) / 2)
Key-Down 0x11
Mouse-DownAt $fcx $fcy
Mouse-MoveTo ($fcx - 100) ($fcy - 80)
Mouse-UpAt ($fcx - 100) ($fcy - 80)
Key-Up 0x11
Start-Sleep -Milliseconds 500
$fr1 = Get-Rect $fh
$anchorAfterMove = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).PerBook.b1.Anchor
$moved = [Math]::Abs(($fr1.Left - $fr.Left) + 100) -le 6 -and [Math]::Abs(($fr1.Top - $fr.Top) + 80) -le 6
Verdict 'D5-float-move-no-page-flip' ($moved -and $anchorAfterMove -eq $anchorBefore) "moved=$moved anchor=$anchorBefore->$anchorAfterMove"
# 边缘缩放（右边缘 +80）
$edgeX = $fr1.Right - 4; $edgeY = [int](($fr1.Top + $fr1.Bottom) / 2)
Mouse-DownAt $edgeX $edgeY
Mouse-MoveTo ($edgeX + 80) $edgeY
Mouse-UpAt ($edgeX + 80) $edgeY
Start-Sleep -Milliseconds 600
$fr2 = Get-Rect $fh
$anchorAfterResize = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).PerBook.b1.Anchor
$w0 = $fr1.Right - $fr1.Left; $w1 = $fr2.Right - $fr2.Left
Verdict 'D5-float-resize-no-page-flip' ($anchorAfterResize -eq $anchorBefore -and [Math]::Abs(($w1 - $w0) - 80) -le 12) "width $w0->$w1 anchor=$anchorAfterResize"
# 拖动选字（浮窗中心一行）
$fcx2 = [int](($fr2.Left + $fr2.Right) / 2); $fcy2 = [int](($fr2.Top + $fr2.Bottom) / 2)
Mouse-DownAt ($fcx2 - 80) $fcy2
Mouse-MoveTo $fcx2 $fcy2
Mouse-MoveTo ($fcx2 + 80) $fcy2
Mouse-UpAt ($fcx2 + 80) $fcy2
Start-Sleep -Milliseconds 500
Save-Screenshot "$art\fix-03-d5-float-selection.png" $fr2.Left $fr2.Top ($fr2.Right - $fr2.Left) ($fr2.Bottom - $fr2.Top)
$anchorAfterSelect = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).PerBook.b1.Anchor
Verdict 'D5-float-drag-no-page-flip' ($anchorAfterSelect -eq $anchorBefore) "anchor=$anchorAfterSelect"
# Ctrl+C
Key-Down 0x11; Key-Press 0x43; Key-Up 0x11
Start-Sleep -Milliseconds 500
$clip3 = Get-Clipboard -Raw
$clip3Ok = -not [string]::IsNullOrWhiteSpace($clip3) -and (Get-Content "$repo\testdata\test-book-1.txt" -Raw -Encoding UTF8).Contains($clip3)
Verdict 'D5-float-ctrl-c-copy' $clip3Ok "clip='$($clip3.Trim().Substring(0, [Math]::Min(20, $clip3.Trim().Length)))'"
# 单击翻页
Mouse-Click $fcx2 ([int]($fr2.Top + ($fr2.Bottom - $fr2.Top) * 0.5))
Start-Sleep -Milliseconds 800
$anchorAfterClick = (Get-Content "$data\library.json" -Raw | ConvertFrom-Json).PerBook.b1.Anchor
Verdict 'D5-float-single-click-pages' ($anchorAfterClick -gt $anchorBefore) "anchor=$anchorBefore->$anchorAfterClick"
Stop-Mine; $myPids.Clear()

# ===== D4-C3：浮窗保存位置不可见（-4000,700）→ 回退 =====
$p = Start-AppState @"
{ "Books": [ $bookEntry ], "LastBookId": "b1", "LastMode": "float", "PerBook": { "b1": { "Anchor": 0, "Float": { "BackgroundOpacity": 0.85, "BorderOpacity": 1.0, "Topmost": true, "X": -4000, "Y": 700, "Width": 460, "Height": 360 } } } }
"@
$fh = [IntPtr]$p.MainWindowHandle
$fr = Get-Rect $fh
Verdict 'D4-float-fallback-invisible-position' ($fr.Left -ge 0 -and $fr.Top -ge 0 -and $fr.Right -le $screenW + 8) "rect=$($fr.Left),$($fr.Top)"
Save-Screenshot "$art\fix-04-d4-float-fallback.png" $fr.Left $fr.Top ($fr.Right - $fr.Left) ($fr.Bottom - $fr.Top)
Stop-Mine; $myPids.Clear()

Write-Output "RESULT pass=$pass fail=$fail"
