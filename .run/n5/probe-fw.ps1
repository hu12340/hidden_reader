. 'D:\MyProjects\HiddenReader\tools\gui.ps1'
$h = [GuiNative]::FindWindow('HiddenReaderFloatWindow', $null)
Write-Host "FindWindow: $h"
$h2 = [GuiNative]::FindWindow('HiddenReaderFloatWindow', 'HiddenReader 浮窗')
Write-Host "FindWindow+title: $h2"
Get-Process HiddenReader -ErrorAction SilentlyContinue | ForEach-Object {
  $ws = Get-ProcessWindows $_.Id
  Write-Host "pid=$($_.Id) windows: $($ws -join ', ')"
}