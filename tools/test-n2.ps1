# N2 定向复验：移除失败保留阅读现场并提示（100% 缩放，隔离数据目录，按 PID 清理）。
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class WN2 {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$exe = "$repo\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$art = "$repo\artifacts"
$DataDir = "$env:TEMP\hr-n2-test"
$pass = 0; $fail = 0
$myPids = New-Object System.Collections.ArrayList
function Verdict([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "PASS  $name $detail" }
    else { $script:fail++; Write-Output "FAIL  $name $detail" }
}
function Get-Rect([IntPtr]$h) { $r = New-Object WN2+RECT; [WN2]::GetWindowRect($h, [ref]$r) | Out-Null; $r }
function Stop-Mine { foreach ($id in $script:myPids) { try { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue } catch { } } }

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
    foreach ($el in $all) { if ($el.Current.Name -match '^第 \d+/\d+ 页') { return $el.Current.Name } }
    return ''
}
# 点击移除按钮（真实坐标点击；Invoke 会因模态对话框阻塞）
function Click-RemoveButton([System.Windows.Automation.AutomationElement]$win) {
    $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'RemoveButton')),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
    if (-not $btn) { throw 'RemoveButton not found' }
    $rc = $btn.Current.BoundingRectangle
    Mouse-Click ([int]($rc.X + $rc.Width / 2)) ([int]($rc.Y + $rc.Height / 2))
    Start-Sleep -Milliseconds 700
}
# 等待本进程弹出模态对话框（前台窗口且不是主窗口），返回其 hwnd
function Wait-DialogHwnd([int]$procId, [IntPtr]$mainHwnd, [int]$timeoutMs = 8000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    while ([DateTime]::UtcNow -lt $deadline) {
        $fg = [GuiNative]::GetForegroundWindow()
        if ($fg -ne [IntPtr]::Zero -and $fg -ne $mainHwnd) {
            $pid2 = 0
            [DlgNative]::GetWindowThreadProcessId($fg, [ref]$pid2) | Out-Null
            if ($pid2 -eq $procId) { return $fg }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "dialog not found"
}

# MessageBox 的 UIA 子树在本环境为空，改用键盘：Enter 触发默认按钮（确定）。
# 截图留证；弹窗出现本身即代表走到了对应分支。
function Close-MessageBox([int]$procId, [IntPtr]$mainHwnd, [string]$screenshot = '') {
    $h = Wait-DialogHwnd $procId $mainHwnd
    if ($screenshot -ne '') {
        $r = New-Object WN2+RECT
        [WN2]::GetWindowRect($h, [ref]$r) | Out-Null
        Save-Screenshot $screenshot $r.Left $r.Top ($r.Right - $r.Left) ($r.Bottom - $r.Top)
    }
    Key-Press 0x0D
    Start-Sleep -Milliseconds 700
}

try {
    # --- 准备隔离书库：一本预置书（含进度）---
    Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
    Start-Sleep -Milliseconds 800
    Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path "$DataDir\books" | Out-Null
    $hexId = '0123456789abcdef0123456789abcdef'
    Copy-Item "$repo\testdata\test-book-1.txt" "$DataDir\books\$hexId.txt"
    $originalTxt = "$repo\testdata\test-book-1.txt"
    Copy-Item $originalTxt "$DataDir\original-backup.txt"
    $perBook = '"PerBook": { "bid": { "Anchor": 0, "Float": { "BackgroundOpacity": 0.85, "BorderOpacity": 1.0, "Topmost": true, "X": -1, "Y": -1, "Width": 460, "Height": 360 } } }'
    @"
{ "Books": [ { "Id": "bid", "Title": "book1", "FileName": "$hexId.txt", "SourcePath": "$($originalTxt -replace '\\', '\\')", "AddedAt": "2026-01-01T00:00:00" } ], "LastBookId": "bid", "LastMode": "normal", $perBook, "WindowX": 100, "WindowY": 100 }
"@ | Set-Content "$DataDir\library.json" -Encoding UTF8
    $env:HR_DATA_DIR = $DataDir
    $env:HR_LOG_DIR = "$DataDir\logs"

    $p = Start-Process $exe -PassThru
    [void]$myPids.Add($p.Id)
    Start-Sleep -Seconds 4
    $p.Refresh()
    $mh = [IntPtr]$p.MainWindowHandle
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
    if (-not $win) { throw 'main window not found' }
    Activate-Window $mh | Out-Null
    Start-Sleep -Milliseconds 400

    # --- 阅读到第 2 页（产生非零进度）---
    $r = Get-Rect $mh
    Mouse-Click ([int](($r.Left + $r.Right) / 2)) ([int]($r.Top + ($r.Bottom - $r.Top) * 0.5))
    Start-Sleep -Milliseconds 800
    $label1 = Read-PageLabel $win
    Verdict 'n2-setup-page2' ($label1 -match '^第 2/') "[$label1]"

    # --- 制造状态提交失败：library.json 只读 ---
    Set-ItemProperty "$DataDir\library.json" -Name IsReadOnly -Value $true
    Start-Sleep -Milliseconds 300

    # --- 点击移除 → 确认 → 期望失败提示（第二个弹窗出现即提示已展示，文本见截图）---
    Click-RemoveButton $win
    [void](Close-MessageBox $p.Id $mh)   # 确认对话框（Enter=确定）
    Close-MessageBox $p.Id $mh "$art\n2-01-remove-failed-warning.png" | Out-Null
    $warnOk = $true
    $logRolledBack = (Select-String -Path "$DataDir\logs\app.log" -Pattern 'remove rolled back' -Quiet)
    Verdict 'n2-failure-warning-shown' ($warnOk -and $logRolledBack) "logRolledBack=$logRolledBack"

    # --- 失败后现场核对 ---
    $label2 = Read-PageLabel $win
    Save-Screenshot "$art\n2-03-toolbar-after-failure.png" $r.Left $r.Top ($r.Right - $r.Left) 130
    Verdict 'n2-reader-preserved' ($label2 -eq $label1 -and $label2 -ne '') "[$label1] -> [$label2]"
    $stateText = Get-Content "$DataDir\library.json" -Raw
    Verdict 'n2-shelf-entry-kept' ($stateText -match '"bid"' -and $stateText -match '"Anchor": \d+')
    Verdict 'n2-copy-kept' (Test-Path "$DataDir\books\$hexId.txt")
    # 选择框显示的标题：展开下拉读取实际列表项（闭合态 UIA 不暴露内层文本）
    $sel = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'BookSelector')))
    ($sel.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    Start-Sleep -Milliseconds 600
    $items = @($sel.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))))
    $selectedItem = $items | Where-Object {
        try { ($_.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Current.IsSelected } catch { $false }
    } | Select-Object -First 1
    $selName = if ($selectedItem) { $selectedItem.Current.Name } else { ($items | ForEach-Object { $_.Current.Name }) -join '|' }
    ($sel.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
    Start-Sleep -Milliseconds 400
    Verdict 'n2-selection-kept' ($selName -eq 'book1') "[$selName] items=$($items.Count)"

    # --- 解除故障，重试移除：成功路径 ---
    Set-ItemProperty "$DataDir\library.json" -Name IsReadOnly -Value $false
    Start-Sleep -Milliseconds 300
    Mouse-Click ([int](($r.Left + $r.Right) / 2)) ([int]($r.Top + ($r.Bottom - $r.Top) * 0.5))
    Start-Sleep -Milliseconds 700
    $anchorBefore = ((Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json).PerBook.bid.Anchor)
    Click-RemoveButton $win
    [void](Close-MessageBox $p.Id $mh)   # 确认对话框（Enter=确定）
    Start-Sleep -Milliseconds 800
    $state2 = Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json
    $entryGone = -not ($state2.Books | Where-Object { $_.Id -eq 'bid' })
    Verdict 'n2-retry-entry-removed' $entryGone
    Verdict 'n2-retry-copy-removed' (-not (Test-Path "$DataDir\books\$hexId.txt"))
    Verdict 'n2-original-txt-kept' ((Get-FileHash $originalTxt).Hash -eq (Get-FileHash "$DataDir\original-backup.txt").Hash)
    $label3 = Read-PageLabel $win
    Verdict 'n2-reader-cleared-on-success' ($label3 -eq '') "[$label3]"
    Save-Screenshot "$art\n2-02-remove-success.png" $r.Left $r.Top ($r.Right - $r.Left) ($r.Bottom - $r.Top)
}
finally {
    Stop-Mine
    Remove-Item "$DataDir\original-backup.txt" -ErrorAction SilentlyContinue
}
Write-Output "RESULT pass=$pass fail=$fail"
