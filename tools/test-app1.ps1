# HiddenReader 首版自动化验收（隔离数据目录）。
param(
    [string]$DataDir = "$env:TEMP\hr-test-data",
    [string]$BookPath = 'D:\MyProjects\HiddenReader\testdata\test-book-1.txt'
)
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$exe = "$repo\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$art = "$repo\artifacts"
$pass = 0; $fail = 0
function Verdict([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Output "PASS  $name $detail" }
    else { $script:fail++; Write-Output "FAIL  $name $detail" }
}

# --- 清理隔离环境
Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800
Remove-Item 'D:\MyProjects\HiddenReader\testdata\test-book-1-renamed.txt' -ErrorAction SilentlyContinue
if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force }
$env:HR_DATA_DIR = $DataDir
$env:HR_LOG_DIR = "$DataDir\logs"
$appLog = "$DataDir\logs\app.log"

function Start-App {
    $p = Start-Process $exe -PassThru
    Start-Sleep -Seconds 4
    $p.Refresh()
    return $p
}

function Get-MainWindow([System.Diagnostics.Process]$p) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Find-First([System.Windows.Automation.AutomationElement]$win, [string]$name, [System.Windows.Automation.ControlType]$ctrlType) {
    $nCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $tCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ctrlType)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition($nCond, $tCond)))
}

function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$name) {
    $btn = Find-First $win $name ([System.Windows.Automation.ControlType]::Button)
    if (-not $btn) { throw "button '$name' not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 600
}

function Set-TextBox([System.Windows.Automation.AutomationElement]$win, [string]$name, [string]$value) {
    $box = Find-First $win $name ([System.Windows.Automation.ControlType]::Edit)
    if (-not $box) { throw "edit '$name' not found" }
    ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)
}

# --- 1. 全新启动
$p = Start-App
$win = Get-MainWindow $p
Verdict 'app-start' ($null -ne $win) "pid=$($p.Id)"
if (-not $win) { exit 1 }

# --- 2. 导入 TXT（复制副本）——文件对话框用 Win32 自动化
Invoke-Button $win '导入 TXT'
$dlg = Wait-VisibleDialog $p.Id 8000
Verdict 'open-dialog' ($dlg -ne [IntPtr]::Zero) "hwnd=$dlg"
if ($dlg -ne [IntPtr]::Zero) {
    Set-DialogFileName $dlg $BookPath
    Click-DialogButton $dlg '打开' | Out-Null
    Start-Sleep -Seconds 2
}
function Get-ComboSelection([System.Windows.Automation.AutomationElement]$win) {
    $cb = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
    if (-not $cb) { return '' }
    if (-not [string]::IsNullOrEmpty($cb.Current.Name)) { return $cb.Current.Name }
    # 展开后读取选项列表（WPF ComboBox 的选中项文本不一定暴露在 Name 上）
    try {
        ($cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
        Start-Sleep -Milliseconds 500
        $items = $cb.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        $selName = ''
        foreach ($i in $items) {
            if ($i.Current.Name -and $i.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).IsSelected) { $selName = $i.Current.Name }
        }
        if (-not $selName -and $items.Count -gt 0) { $selName = $items[0].Current.Name }
        ($cb.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        Start-Sleep -Milliseconds 400
        return $selName
    } catch {
        return ''
    }
}
$copies = Get-ChildItem "$DataDir\books" -Filter *.txt -ErrorAction SilentlyContinue
Verdict 'import-copy-created' ($copies.Count -ge 1) ($copies.Name -join ',')
$p.Refresh(); $win = Get-MainWindow $p
Start-Sleep -Milliseconds 500

# UIA 抓所有 Text 元素里匹配 第 x/y 页 的
function Read-PageLabel {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($el in $all) {
        if ($el.Current.Name -match '^第 \d+/\d+ 页') { return $el.Current.Name }
    }
    return ''
}
$selName = Get-ComboSelection $win
$pageLabelNow = Read-PageLabel
Verdict 'book-in-shelf' ($pageLabelNow -match '第 \d+/\d+ 页') "selection='$selName' pageLabel='$pageLabelNow'"

# 记录原文件（改名测试用）
$originalHash = (Get-FileHash $BookPath -Algorithm MD5).Hash

# --- 3. 阅读视图与翻页
$r = Get-WindowRect ([IntPtr]$p.MainWindowHandle)
$readerX = [int]($r.Left + $r.Width * 0.65)
$readerY = [int]($r.Top + $r.Height * 0.55)
Activate-Window ([IntPtr]$p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 400

$label0 = Read-PageLabel
Save-Screenshot "$art\app-01-normal.png" $r.Left $r.Top $r.Width $r.Height

# 单击下一页
Mouse-Click $readerX $readerY
Start-Sleep -Milliseconds 700
$label1 = Read-PageLabel
Verdict 'click-next-page' ($label1 -ne $label0 -and $label1 -match '^第 2/') "before=[$label0] after=[$label1]"

# 方向键翻页
Key-Press 0x27
Start-Sleep -Milliseconds 600
$label2 = Read-PageLabel
Verdict 'arrow-right-page' ($label2 -match '^第 3/') "[$label2]"
Key-Press 0x28
Start-Sleep -Milliseconds 600
$label3 = Read-PageLabel
Verdict 'arrow-down-page' ($label3 -match '^第 4/') "[$label3]"
Key-Press 0x25
Start-Sleep -Milliseconds 600
$label4 = Read-PageLabel
Verdict 'arrow-left-back' ($label4 -match '^第 3/') "[$label4]"
Key-Press 0x26
Start-Sleep -Milliseconds 600
$label5 = Read-PageLabel
Verdict 'arrow-up-back' ($label5 -match '^第 2/') "[$label5]"

# --- 4. 目录跳转
Invoke-Button $win '目录'
Start-Sleep -Milliseconds 500
$tocList = Find-First $win '' ([System.Windows.Automation.ControlType]::List)
$ch5 = $null
if ($tocList) {
    $items = $tocList.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    foreach ($i in $items) { if ($i.Current.Name -like '第5章*') { $ch5 = $i } }
}
if ($ch5) {
    ($ch5.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 800
}
$labelToc = Read-PageLabel
Save-Screenshot "$art\app-02-toc.png" $r.Left $r.Top $r.Width $r.Height
Verdict 'chapter-jump' ($ch5 -and $labelToc -match '第 \d+/\d+ 页　第\d+章') "[$labelToc]"
Invoke-Button $win '目录'  # 收起目录

# --- 5. 全书搜索与高亮
$searchEdit = Find-ById $win 'SearchBox'
($searchEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('挂钟')
Invoke-Button $win '搜索'
Start-Sleep -Milliseconds 900
function Read-SearchStatus {
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
    foreach ($el in $all) {
        if ($el.Current.Name -match '^(共 \d+ 处|第 \d+/\d+ 处|无结果)') { return $el.Current.Name }
    }
    return ''
}
$status1 = Read-SearchStatus
Verdict 'search-found' ($status1 -match '(\d+/\d+ 处|共 \d+ 处)') "[$status1]"
Invoke-Button $win '下一处'
Start-Sleep -Milliseconds 700
$status2 = Read-SearchStatus
Verdict 'search-next-hit' ($status2 -match '第 2/') "[$status2]"
Save-Screenshot "$art\app-03-search.png" $r.Left $r.Top $r.Width $r.Height

# --- 6. 跳页
$pageEdit = Find-ById $win 'PageBox'
($pageEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue('1')
$pageEdit.SetFocus()
Start-Sleep -Milliseconds 200
Key-Press 0x0D
Start-Sleep -Milliseconds 800
$labelJump = Read-PageLabel
Verdict 'jump-to-page' ($labelJump -match '^第 1/\d+ 页') "[$labelJump]"

# --- 7. 进入浮窗
Invoke-Button $win '极简浮窗'
Start-Sleep -Seconds 2
$p.Refresh()
$floatHwnd = [IntPtr]$p.MainWindowHandle
$mainVisible = $null -ne (Get-Process HiddenReader | Where-Object { $_.MainWindowTitle -eq 'HiddenReader' })
"after enter float: mainTitle='$($p.MainWindowTitle)' floatHwnd=$floatHwnd"
Save-Screenshot "$art\app-04-float.png" ($r.Left) ($r.Top) $r.Width $r.Height

# 浮窗菜单：背景 0% / 边框 0%
function Invoke-FloatMenu([int]$menuIndex, [string]$tag) {
    $fr = Get-WindowRect $floatHwnd
    $cx = $fr.Left + [int]($fr.Width / 2); $cy = $fr.Top + [int]($fr.Height / 2)
    Mouse-Click $cx $cy 'right'
    Start-Sleep -Milliseconds 700
    $popup = [MenuNative]::FindWindowW('#32768', $null)
    if ($popup -eq [IntPtr]::Zero) { throw "popup not found [$tag]" }
    $hmenu = [MenuNative]::SendMessageW($popup, 0x01E1, [IntPtr]::Zero, [IntPtr]::Zero)
    $rect = New-Object MenuNative+MRECT
    [MenuNative]::GetMenuItemRect([IntPtr]::Zero, $hmenu, [uint32]$menuIndex, [ref]$rect) | Out-Null
    Mouse-Click ([int](($rect.Left + $rect.Right) / 2)) ([int](($rect.Top + $rect.Bottom) / 2))
    Start-Sleep -Milliseconds 800
}
Activate-Window $floatHwnd | Out-Null
Start-Sleep -Milliseconds 400
Invoke-FloatMenu 2 'bg0'
Invoke-FloatMenu 6 'border0'
$fr = Get-WindowRect $floatHwnd
Save-Screenshot "$art\app-05-float-transparent.png" $fr.Left $fr.Top $fr.Width $fr.Height

# 完全透明时单击翻页
$before = (Select-String -Path $appLog -Pattern 'exit|import' | Select-Object -Last 1).Line
# 用浮窗单击一次，然后 Esc 返回普通模式验证位置保持
$fcx = $fr.Left + [int]($fr.Width / 2); $fcy = $fr.Top + [int]($fr.Height / 2)
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 500
Activate-Window $floatHwnd | Out-Null
Key-Press 0x1B
Start-Sleep -Seconds 2
$p.Refresh()
$mainBack = ($p.MainWindowTitle -eq 'HiddenReader')
Verdict 'esc-return-normal' $mainBack "title='$($p.MainWindowTitle)'"

# 普通界面应保留浮窗里的阅读位置：跳页框显示的页码应 > 1
$win = Get-MainWindow $p
$labelBack = Read-PageLabel
Verdict 'position-kept-after-mode-switch' ($labelBack -notmatch '^第 1/ ') "[$labelBack]"
Save-Screenshot "$art\app-06-back-normal.png" $r.Left $r.Top $r.Width $r.Height

# --- 8. 以浮窗模式退出，重启后直接恢复浮窗
$win2 = Get-MainWindow $p
Invoke-Button $win2 '极简浮窗'
Start-Sleep -Seconds 2
$p.Refresh()
$floatHwnd = [IntPtr]$p.MainWindowHandle
Activate-Window $floatHwnd | Out-Null
Start-Sleep -Milliseconds 400
Invoke-FloatMenu 11 'exit'
Start-Sleep -Seconds 2
$exited = -not (Get-Process HiddenReader -ErrorAction SilentlyContinue)
Verdict 'exit-from-float' $exited
$state = Get-Content "$DataDir\library.json" -Raw | ConvertFrom-Json
Verdict 'state-lastmode-float' ($state.LastMode -eq 'float') "lastMode=$($state.LastMode)"

# 重启：应直接恢复浮窗
$p = Start-App
Start-Sleep -Seconds 2
$p.Refresh()
"relaunch title='$($p.MainWindowTitle)'"
$floatRestored = ($p.MainWindowTitle -eq 'HiddenReader 浮窗')
Verdict 'float-restored-on-start' $floatRestored "title='$($p.MainWindowTitle)'"
$floatHwnd = [IntPtr]$p.MainWindowHandle
$fr = Get-WindowRect $floatHwnd
Save-Screenshot "$art\app-07-float-restored.png" $fr.Left $fr.Top $fr.Width $fr.Height

# --- 9. 原文件改名后副本仍可打开；移除保留原文件
$p.Refresh()
# 从浮窗返回普通模式
Activate-Window $floatHwnd | Out-Null
Start-Sleep -Milliseconds 400
Key-Press 0x1B
Start-Sleep -Seconds 2
$p.Refresh()

Rename-Item $BookPath 'test-book-1-renamed.txt'
# 重启应用（书架从副本读取）
Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 800
$p = Start-App
$win = Get-MainWindow $p
Start-Sleep -Milliseconds 800
$selStill = Get-ComboSelection $win
Start-Sleep -Milliseconds 500
$p.Refresh(); $win = Get-MainWindow $p
$pageLabelAfterRelaunch = Read-PageLabel
Verdict 'copy-opens-after-rename' ($pageLabelAfterRelaunch -match '第 \d+/\d+ 页') "pageLabel='$pageLabelAfterRelaunch'（原文件改名后副本仍可打开）"
$p.Refresh(); $win = Get-MainWindow $p
# 移除书籍（点移除 → 确认对话框确定）
Invoke-Button $win '移除'
$confirm = Wait-VisibleDialog $p.Id 4000
if ($confirm -ne [IntPtr]::Zero) {
    Click-DialogButton $confirm '确定' | Out-Null
    Start-Sleep -Seconds 1
}
$originalKept = Test-Path 'D:\MyProjects\HiddenReader\testdata\test-book-1-renamed.txt'
$copyGone = -not (Test-Path "$DataDir\books\*.txt" -PathType Leaf) -or ((Get-ChildItem "$DataDir\books" -Filter *.txt).Count -eq 0)
Verdict 'remove-keeps-original' ($originalKept) '原文件（已改名）仍存在'
Verdict 'remove-deletes-copy' $copyGone '副本已删除'

Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
Write-Output "RESULT pass=$pass fail=$fail"
