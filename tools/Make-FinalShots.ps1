$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$env:HR_DATA_DIR = "$env:TEMP\hr-final-shots"
$env:HR_LOG_DIR = "$env:TEMP\hr-final-shots\logs"
Get-Process HiddenReader -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 800
if (Test-Path $env:HR_DATA_DIR) { Remove-Item $env:HR_DATA_DIR -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$env:HR_DATA_DIR\books" | Out-Null
Copy-Item "$repo\testdata\test-book-1.txt" "$env:HR_DATA_DIR\books\book1.txt"
@"
{
  "Books": [ { "Id": "b1", "Title": "book1", "FileName": "book1.txt", "SourcePath": "x", "AddedAt": "2026-01-01T00:00:00" } ],
  "LastBookId": "b1",
  "LastMode": "normal",
  "PerBook": { "b1": { "Anchor": 0, "Float": { "X": -1, "Y": -1 } } },
  "WindowX": -1, "WindowY": -1
}
"@ | Set-Content "$env:HR_DATA_DIR\library.json" -Encoding UTF8
$p = Start-Process "$repo\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe" -PassThru
Start-Sleep -Seconds 4
# 全屏截图：主窗口（暗黑、右侧）
Save-Screenshot "$repo\artifacts\final-01-normal-right-dark.png" 0 0 3440 1440
# 进入浮窗
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)))
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, '极简浮窗')),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
Start-Sleep -Seconds 2
Save-Screenshot "$repo\artifacts\final-02-float-bottomright.png" 0 0 3440 1440
Get-Process HiddenReader | Stop-Process
Write-Output 'shots done'
