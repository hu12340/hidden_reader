# 定点实验: 滚动->进浮窗->单击翻页->强杀, 检查 json.Anchor。
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
. 'D:\MyProjects\HiddenReader\tools\gui.ps1'
$root = 'D:\MyProjects\HiddenReader'
$data = "$root\.run\n5\data"; $log = "$root\.run\n5\log"
$exe = "$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$env:HR_DATA_DIR = $data; $env:HR_LOG_DIR = $log

$stateFile = "$data\library.json"
$json = [IO.File]::ReadAllText($stateFile)
$json = $json -replace '"Anchor": \d+', '"Anchor": 0'
$json = $json.Replace('"LastMode": "float"', '"LastMode": "normal"')
[IO.File]::WriteAllText($stateFile, $json, (New-Object Text.UTF8Encoding $false))

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
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $e = Find-ById $main 'PageLabel'
    if ($e -and $e.Current.Name -match '第 \d+/\d+ 页') { break }
    Start-Sleep -Milliseconds 500
}
Write-Host "ready: '$($e.Current.Name)'"

$wr = $main.Current.BoundingRectangle
$cx = [int]($wr.X + $wr.Width * 0.62); $cy = [int]($wr.Y + $wr.Height * 0.55)
$hwnd0 = [IntPtr]$main.Current.NativeWindowHandle
Activate-Window $hwnd0 | Out-Null
Move-CursorVerified $cx $cy; Start-Sleep -Milliseconds 200
[GuiNative]::mouse_event(0x0800, 0, 0, 240, [UIntPtr]::Zero)  # 滚 2 格
Start-Sleep -Milliseconds 400
Get-Content $stateFile | Select-String 'Anchor' | Select-Object -First 1

Invoke-Button $main 'FloatButton'
Start-Sleep -Milliseconds 1500
$fh = [IntPtr]::Zero
foreach ($w in (Get-ProcessWindows $p.Id)) { if ((Get-WindowClass $w) -eq 'HiddenReaderFloatWindow') { $fh = $w; break } }
$fr = Get-WindowRect $fh
$fcx = [int](($fr.Left + $fr.Right) / 2); $fcy = [int](($fr.Top + $fr.Bottom) / 2)
Mouse-Click $fcx $fcy
Start-Sleep -Milliseconds 800
Write-Host "--- after float click, before kill:"
Select-String -Path $stateFile -Pattern '"Anchor": (\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 600
Write-Host "--- after kill:"
Select-String -Path $stateFile -Pattern '"Anchor": (\d+)' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1
