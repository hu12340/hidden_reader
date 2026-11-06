# A1: 大 TXT 导入/打开 - 阶段显示、窗口响应、百分比、翻页、近书尾跳页。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class N5Native {
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr hwnd);
}
"@
. 'D:\MyProjects\HiddenReader\tools\gui.ps1'

$root = 'D:\MyProjects\HiddenReader'
$run  = "$root\.run\n5"
$data = "$run\data"; $log = "$run\log"; $shots = "$run\shots"
New-Item -ItemType Directory -Force -Path $data, $log, $shots | Out-Null
$exe = "$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$big = 'C:\Users\80756\Documents\凡人修仙传.txt'
$script:out = New-Object System.Text.StringBuilder
function Rec([string]$msg) { [void]$script:out.AppendLine($msg); Write-Host $msg }

$env:HR_DATA_DIR = $data
$env:HR_LOG_DIR  = $log
$p = Start-Process $exe -PassThru
Start-Sleep -Milliseconds 2500

$rootEl = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$main = $null
for ($i = 0; $i -lt 20 -and -not $main; $i++) { $main = $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond); if (-not $main) { Start-Sleep -Milliseconds 300 } }
if (-not $main) { Rec "FAIL: 主窗口未找到"; $p | Stop-Process -Force; exit 1 }
$hwnd = [IntPtr]$main.Current.NativeWindowHandle
Rec "main hwnd=$hwnd pid=$($p.Id)"

function Find-ById([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Invoke-Button([System.Windows.Automation.AutomationElement]$win, [string]$id) {
    $btn = Find-ById $win $id
    if (-not $btn) { throw "button $id not found" }
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}

function Get-Label([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $el = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if ($el) { return $el.Current.Name }
    return '<none>'
}
function Get-ReaderRect {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'Reader')
    $el = $main.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    return $el.Current.BoundingRectangle
}

# ---- 1. 导入大书（文件对话框自动化） ----
Invoke-Button $main 'ImportButton'
$dlg = Wait-VisibleDialog $p.Id 8000
if ($dlg -eq [IntPtr]::Zero) { Rec 'FAIL: 导入对话框未出现'; $p | Stop-Process -Force; exit 1 }
Set-DialogFileName $dlg $big
Click-DialogButton $dlg '打开'
$importStart = Get-Date

# ---- 2. 导入/打开期间响应性 + 阶段文字采样 ----
$samples = @()
$stageShots = 0
$deadline = (Get-Date).AddSeconds(120)
$computedAt = $null
$openedAt = $null
while ((Get-Date) -lt $deadline) {
    $hung = [N5Native]::IsHungAppWindow($hwnd)
    $label = Get-Label 'PageLabel'
    $samples += "$([int]((Get-Date)-$importStart).TotalMilliseconds)ms hung=$hung label=$label"
    if ($label -match '正在(导入|打开)' -and $stageShots -lt 2) {
        Save-Screenshot "$shots\a1-stage$stageShots.png" ([int](Get-ReaderRect).X - 20) ([int](Get-ReaderRect).Y - 80) 900 400
        $stageShots++
    }
    if ($label -match '阅读 [\d.]+%') { $openedAt = Get-Date }
    if ($label -match '第 (\d+)/(\d+) 页') { $computedAt = Get-Date; break }
    Start-Sleep -Milliseconds 250
}
Rec '--- 采样(每8条取1) ---'
for ($i = 0; $i -lt $samples.Count; $i += 8) { Rec $samples[$i] }
Rec "--- 末条: $($samples[-1])"
if (-not $computedAt) { Rec 'FAIL: 120s 内总页数未算出'; Save-Screenshot "$shots\a1-timeout.png" 0 0 1200 800; $p | Stop-Process -Force; exit 1 }
$hungCount = ($samples | Where-Object { $_ -match 'hung=True' }).Count
Rec "RESULT-1 阶段显示+响应: samples=$($samples.Count) hung=$hungCount -> $(if ($hungCount -eq 0) { 'PASS' } else { 'FAIL' })"

# ---- 3. 计算期间翻页（若计算已完成则直接对已算好状态翻页） ----
$rr = Get-ReaderRect
$cx = [int]($rr.X + $rr.Width / 2); $cy = [int]($rr.Y + $rr.Height / 2)
Activate-Window $hwnd | Out-Null
Start-Sleep -Milliseconds 300
$before = Get-Label 'PageLabel'
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 400
$after = Get-Label 'PageLabel'
Rec "RESULT-2a 单击翻页: '$before' -> '$after'"
Key-Press 0x27  # VK_RIGHT
Start-Sleep -Milliseconds 400
$after2 = Get-Label 'PageLabel'
Rec "RESULT-2b 方向键翻页: '$after' -> '$after2'"

# ---- 4. 近书尾跳页 ----
$pageBoxEl = Find-ById $main 'PageBox'
if ($after2 -match '第 (\d+)/(\d+) 页') {
    $total = [int]$Matches[2]; $target = $total - 2
    ($pageBoxEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue("$target")
    # 输入框有焦点后回车
    Key-Press 0x0D
    Start-Sleep -Milliseconds 500
    $jumped = Get-Label 'PageLabel'
    Rec "RESULT-3 近书尾跳页: 目标第 $target/$total 页 -> '$jumped' -> $(if ($jumped -match "第 $target/$total 页") { 'PASS' } else { 'FAIL' })"
    Save-Screenshot "$shots\a1-nearend.png" ([int]$rr.X) ([int]$rr.Y - 90) 900 420
} else {
    Rec "RESULT-3 跳页: 无法解析总页数 '$after2' -> FAIL"
}

Rec '--- A1 done ---'
$p | Stop-Process -Force
Start-Sleep -Milliseconds 500
$script:out.ToString() | Set-Content "$run\a1-result.txt" -Encoding UTF8
