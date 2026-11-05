# HiddenReader 首版验收第二阶段（需求 §7 剩余场景）。
# 隔离数据目录；书籍直接放入 books 目录以跳过导入对话框。
param(
    [string]$DataDir = "$env:TEMP\hr-test-data2"
)
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WinResize {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int h2, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
# gui.ps1 加载时已将线程切到 PerMonitorV2 上下文
function Save-ScreenshotRect([string]$path, $r) {
    Save-Screenshot $path $r.Left $r.Top ($r.Right - $r.Left) ($r.Bottom - $r.Top)
}

$exe = "$repo\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$art = "$repo\artifacts"
$pass = 0; $fail = 0
function Verdict([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "PASS  $name $detail" }
    else { $script:fail++; Write-Output "FAIL  $name $detail" }
}
function Get-Rect([IntPtr]$h) {
    $r = New-Object WinResize+RECT
    [WinResize]::GetWindowRect($h, [ref]$r) | Out-Null
    return $r
}

# --- 准备隔离环境：两本预置书（book1 含章节 / book3 无章节）
Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 800
Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$DataDir\books" | Out-Null
Copy-Item "$repo\testdata\test-book-1.txt" "$DataDir\books\book1.txt"
Copy-Item "$repo\testdata\test-book-3.txt" "$DataDir\books\book3.txt"
$env:HR_DATA_DIR = $DataDir
$env:HR_LOG_DIR = "$DataDir\logs"
@"
{
  "Books": [
    { "Id": "b1", "Title": "book1", "FileName": "book1.txt", "SourcePath": "x", "AddedAt": "2026-01-01T00:00:00" },
    { "Id": "b3", "Title": "book3", "FileName": "book3.txt", "SourcePath": "x", "AddedAt": "2026-01-01T00:00:00" }
  ],
  "LastBookId": "b1",
  "LastMode": "normal",
  "PerBook": { "b1": { "Anchor": 0, "Float": { "X": -1, "Y": -1 } }, "b3": { "Anchor": 0, "Float": { "X": -1, "Y": -1 } } },
  "WindowX": -1, "WindowY": -1
}
"@ | Set-Content "$DataDir\library.json" -Encoding UTF8

$root = [System.Windows.Automation.AutomationElement]::RootElement
function Get-MainWindow([System.Diagnostics.Process]$p) {
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
}
function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))
}
function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$name) {
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
    if (-not $btn) { throw "button '$name' not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 600
}
function Read-PageLabel([System.Windows.Automation.AutomationElement]$win) {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($el in $all) {
        if ($el.Current.Name -match '^第 \d+/\d+ 页') { return $el.Current.Name }
    }
    return ''
}

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 4
$p.Refresh()
$win = Get-MainWindow $p
Verdict 'app2-start' ($null -ne $win) "pid=$($p.Id)"

# --- A. 跨章节搜索（book1 从第1页搜后面章节的词）
$searchEdit = Find-ById $win 'SearchBox'
($searchEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('铅笔字')
Invoke-Button $win '搜索'
Start-Sleep -Milliseconds 900
$labelA = Read-PageLabel $win
Save-ScreenshotRect "$art\app2-01-cross-chapter-search.png" (Get-Rect ([IntPtr]$p.MainWindowHandle))
# 连续“下一处”跨到后面的章节
Invoke-Button $win '下一处'
Invoke-Button $win '下一处'
Invoke-Button $win '下一处'
Start-Sleep -Milliseconds 800
$labelA2 = Read-PageLabel $win
Verdict 'cross-chapter-search' ($labelA2 -match '第\d+章' -and $labelA2 -notmatch '第1章') "[$labelA] -> [$labelA2]"

# --- C. 焦点切走后方向键不翻页（最小化主窗口 → 焦点离开）
$labelC0 = Read-PageLabel $win
$mh = [IntPtr]$p.MainWindowHandle
[WinResize]::ShowWindow($mh, 6)   # SW_MINIMIZE
Start-Sleep -Milliseconds 700
Key-Press 0x27
Key-Press 0x27
Start-Sleep -Milliseconds 600
[WinResize]::ShowWindow($mh, 9)   # SW_RESTORE
Activate-Window $mh | Out-Null
Start-Sleep -Milliseconds 600
$labelC1 = Read-PageLabel $win
Verdict 'focus-loss-no-paging' ($labelC0 -eq $labelC1) "before=[$labelC0] after=[$labelC1]"

# --- D. 拖窄再放大，阅读位置保持
$mh = [IntPtr]$p.MainWindowHandle
$r0 = Get-Rect $mh
[WinResize]::SetWindowPos($mh, [IntPtr]::Zero, $r0.Left, $r0.Top, 500, 420, 0x0004) | Out-Null
Start-Sleep -Milliseconds 900
[WinResize]::SetWindowPos($mh, [IntPtr]::Zero, $r0.Left, $r0.Top, $r0.Right - $r0.Left, $r0.Bottom - $r0.Top, 0x0004) | Out-Null
Start-Sleep -Milliseconds 900
$labelD = Read-PageLabel $win
$stateD = (Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.b1.Anchor
Verdict 'resize-keeps-position' ($stateD -gt 0) "anchor=$stateD label=[$labelD]"

# --- 切到无章节书 book3（按索引选择第 2 项）
$combo = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
($combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
Start-Sleep -Milliseconds 600
$items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
if ($items.Count -ge 2) {
    ($items[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 900
} else {
    Write-Output "WARN  combo items count=$($items.Count)"
}
# 无章节书可翻页（点击点避开右下角的浮窗）
$r = Get-Rect $mh
Activate-Window $mh | Out-Null
Start-Sleep -Milliseconds 300
# 先点一次工具栏空白处，确保下拉框等弹出层已关闭
Mouse-Click ([int]($r.Right - 30)) ([int]($r.Bottom - 12))
Start-Sleep -Milliseconds 400
$labelB0 = Read-PageLabel $win
Mouse-Click ([int](($r.Left + $r.Right) / 2)) ([int]($r.Top + ($r.Bottom - $r.Top) * 0.28))
Start-Sleep -Milliseconds 700
$labelB1 = Read-PageLabel $win
Verdict 'no-chapter-paging' ($labelB0 -ne $labelB1) "before=[$labelB0] after=[$labelB1]"
# 无章节书可搜索
$searchEdit = Find-ById $win 'SearchBox'
($searchEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('挂钟')
Invoke-Button $win '搜索'
Start-Sleep -Milliseconds 900
$labelB2 = Read-PageLabel $win
Verdict 'no-chapter-search' ($labelB2 -match '^第 \d+/\d+ 页') "[$labelB2]"
Save-ScreenshotRect "$art\app2-02-no-chapter.png" $r

# --- E/F. 浮窗移动、缩放、缩放后翻页、进度持久化
Invoke-Button $win '极简浮窗'
Start-Sleep -Seconds 2
$p.Refresh()
$fh = [IntPtr]$p.MainWindowHandle
$fr0 = Get-Rect $fh
"float rect0: $($fr0.Left),$($fr0.Top) $($fr0.Right - $fr0.Left)x$($fr0.Bottom - $fr0.Top)"

# Ctrl+左键拖动移动
[WinResize]::SetForegroundWindow($fh) | Out-Null
Start-Sleep -Milliseconds 300
$cx = [int](($fr0.Left + $fr0.Right) / 2); $cy = [int](($fr0.Top + $fr0.Bottom) / 2)
Key-Down 0x11
Mouse-DownAt $cx $cy
Mouse-MoveTo ($cx - 120) ($cy - 150)
Start-Sleep -Milliseconds 200
Mouse-UpAt ($cx - 120) ($cy - 150)
Key-Up 0x11
Start-Sleep -Milliseconds 500
$fr1 = Get-Rect $fh
$moved = [Math]::Abs(($fr1.Left - $fr0.Left) + 120) -le 6 -and [Math]::Abs(($fr1.Top - $fr0.Top) + 150) -le 6
Verdict 'float-ctrl-drag-move' $moved "rect0=($($fr0.Left),$($fr0.Top)) rect1=($($fr1.Left),$($fr1.Top))"
Save-ScreenshotRect "$art\app2-03-float-moved.png" $fr1

# 边缘缩放（右边缘 +100px）
$edgeX = $fr1.Right - 4; $edgeY = [int](($fr1.Top + $fr1.Bottom) / 2)
Mouse-DownAt $edgeX $edgeY
Mouse-MoveTo ($edgeX + 100) $edgeY
Start-Sleep -Milliseconds 200
Mouse-UpAt ($edgeX + 100) $edgeY
Start-Sleep -Milliseconds 600
$fr2 = Get-Rect $fh
$w0 = $fr1.Right - $fr1.Left; $w1 = $fr2.Right - $fr2.Left
Verdict 'float-edge-resize' ([Math]::Abs(($w1 - $w0) - 100) -le 12) "width $w0 -> $w1"

# 缩放后仍可单击翻页（浮窗当前会话是 book3 → 读 b3 的进度）
$anchorBefore = (Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.b3.Anchor
Mouse-Click ([int](($fr2.Left + $fr2.Right) / 2)) ([int](($fr2.Top + $fr2.Bottom) / 2))
Start-Sleep -Milliseconds 800
$anchorAfter = (Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.b3.Anchor
Verdict 'float-click-after-resize' ($anchorAfter -gt $anchorBefore) "anchor $anchorBefore -> $anchorAfter"

# F5：背景 0.85→0 → F6：边框 1.0→0.5 → F7：置顶开→关
Key-Press 0x74
Start-Sleep -Milliseconds 500
Key-Press 0x75
Start-Sleep -Milliseconds 500
Key-Press 0x76
Start-Sleep -Milliseconds 500
$st = (Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.b3.Float
Verdict 'float-fkeys-settings' ($st.BackgroundOpacity -eq 0 -and $st.BorderOpacity -eq 0.5 -and $st.Topmost -eq $false) "bg=$($st.BackgroundOpacity) border=$($st.BorderOpacity) topmost=$($st.Topmost)"
Save-ScreenshotRect "$art\app2-04-float-settings.png" $fr2

# G. 浮窗退出（菜单）→ 以浮窗重启 → 恢复位置与进度
function Invoke-FloatMenu([int]$menuIndex, [string]$tag) {
    $fr = Get-Rect ([IntPtr](Get-Process HiddenReader).MainWindowHandle)
    $fcx = [int](($fr.Left + $fr.Right) / 2); $fcy = [int](($fr.Top + $fr.Bottom) / 2)
    Mouse-Click $fcx $fcy 'right'
    Start-Sleep -Milliseconds 700
    $popup = [MenuNative]::FindWindowW('#32768', $null)
    if ($popup -eq [IntPtr]::Zero) { throw "popup not found [$tag]" }
    $hmenu = [MenuNative]::SendMessageW($popup, 0x01E1, [IntPtr]::Zero, [IntPtr]::Zero)
    $rect = New-Object MenuNative+MRECT
    [MenuNative]::GetMenuItemRect([IntPtr]::Zero, $hmenu, [uint32]$menuIndex, [ref]$rect) | Out-Null
    Mouse-Click ([int](($rect.Left + $rect.Right) / 2)) ([int](($rect.Top + $rect.Bottom) / 2))
    Start-Sleep -Milliseconds 800
}
Activate-Window $fh | Out-Null
Start-Sleep -Milliseconds 300
Invoke-FloatMenu 11 'exit'
Start-Sleep -Seconds 2
$anchorSaved = (Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.b3.Anchor
$p2 = Start-Process $exe -PassThru
Start-Sleep -Seconds 4
$p2.Refresh()
$restoredFloat = ($p2.MainWindowTitle -eq 'HiddenReader 浮窗')
$fr3 = Get-Rect ([IntPtr]$p2.MainWindowHandle)
$posKept = [Math]::Abs($fr3.Left - $fr2.Left) -le 4 -and [Math]::Abs($fr3.Top - $fr2.Top) -le 4
Verdict 'float-exit-restart-restore' ($restoredFloat -and $posKept -and $anchorSaved -ge $anchorAfter) "restored=$restoredFloat posKept=$posKept anchor=$anchorSaved"
Save-ScreenshotRect "$art\app2-05-float-restored.png" $fr3

Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
Write-Output "RESULT pass=$pass fail=$fail"
