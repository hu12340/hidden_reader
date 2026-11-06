# 诊断: 单击/方向键翻页为何 PageLabel 不变。
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
function Get-Label([string]$id) { (Find-ById $main $id).Current.Name }
function Get-ReaderRect {
    (Find-ById $main 'Reader').Current.BoundingRectangle
}

# 等总页数算好
$deadline = (Get-Date).AddSeconds(90)
while ((Get-Date) -lt $deadline) { if ((Get-Label 'PageLabel') -match '第 \d+/\d+ 页') { break }; Start-Sleep -Milliseconds 400 }
Write-Host "label=$(Get-Label 'PageLabel')"

$rr = Get-ReaderRect
$cx = [int]($rr.X + $rr.Width / 2); $cy = [int]($rr.Y + $rr.Height / 2)
Write-Host "reader rect: x=$($rr.X) y=$($rr.Y) w=$($rr.Width) h=$($rr.Height) click=($cx,$cy)"
Write-Host "fg before: '$([GuiNative]::GetForegroundWindow())' vs main=$hwnd"

Activate-Window $hwnd | Out-Null
Start-Sleep -Milliseconds 300
Write-Host "fg after activate: fg=$([GuiNative]::GetForegroundWindow()) match=$([GuiNative]::GetForegroundWindow() -eq $hwnd)"

$reader = Find-ById $main 'Reader'
Write-Host "reader HasKeyboardFocus=$($reader.Current.HasKeyboardFocus) IsOffscreen=$($reader.Current.IsOffscreen)"

# 步骤1: 鼠标移动到中心再单击
Move-CursorVerified $cx $cy
Start-Sleep -Milliseconds 200
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 600
Write-Host "after click: label='$(Get-Label 'PageLabel')' pagebox='$(Get-Label 'PageBox')' readerFocus=$((Find-ById $main 'Reader').Current.HasKeyboardFocus) fg=$([GuiNative]::GetForegroundWindow())"

# 步骤2: 方向键
Key-Press 0x27
Start-Sleep -Milliseconds 600
Write-Host "after right: label='$(Get-Label 'PageLabel')' pagebox='$(Get-Label 'PageBox')'"

# 步骤3: 再点一次并截图
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 600
Write-Host "after click2: label='$(Get-Label 'PageLabel')'"
Save-Screenshot "$shots\diag-flip.png" ([int]$rr.X) ([int]($rr.Y - 90)) 900 430

$p | Stop-Process -Force
