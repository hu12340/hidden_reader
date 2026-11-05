# 150% 缩放下的浮窗验证。
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
$log = "$env:TEMP\hiddenreader-proto.log"
Get-Process FloatPrototype -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-Item $log -ErrorAction SilentlyContinue
$env:HR_PROTO_LOG_DIR = $env:TEMP
$p = Start-Process "$repo\prototype\FloatPrototype\bin\Debug\net10.0-windows\FloatPrototype.exe" -ArgumentList '--float' -PassThru
Start-Sleep -Seconds 5
$p.Refresh()
$float = [IntPtr]$p.MainWindowHandle
$r = Get-WindowRect $float
"rect: $($r.Left),$($r.Top) $($r.Width)x$($r.Height) (expect ~690x540)"
$dpi = [GuiNative]::GetDpiForWindow($float)
"dpi=$dpi (expect 144)"
Select-String -Path $log -Pattern 'float render' | Select-Object -First 2 | ForEach-Object { $_.Line }
Save-Screenshot "$repo\artifacts\proto-dpi150-bg85.png" $r.Left $r.Top $r.Width $r.Height

# 右键菜单把背景/边框都调到 0%
function Activate([IntPtr]$hwnd) {
    for ($i = 0; $i -lt 5; $i++) { [GuiNative]::SetForegroundWindow($hwnd) | Out-Null; Start-Sleep -Milliseconds 150; if ([GuiNative]::GetForegroundWindow() -eq $hwnd) { return $true } }
    return $false
}
function Invoke-MenuItem([IntPtr]$hwnd, [int]$menuIndex, [string]$tag) {
    $r = Get-WindowRect $hwnd
    $cx = $r.Left + [int]($r.Width / 2); $cy = $r.Top + [int]($r.Height / 2)
    Mouse-Click $cx $cy 'right'
    Start-Sleep -Milliseconds 700
    $popup = [MenuNative]::FindWindowW('#32768', $null)
    if ($popup -eq [IntPtr]::Zero) { throw "popup not found [$tag]" }
    $hmenu = [MenuNative]::SendMessageW($popup, 0x01E1, [IntPtr]::Zero, [IntPtr]::Zero)
    $rect = New-Object MenuNative+MRECT
    if (-not [MenuNative]::GetMenuItemRect([IntPtr]::Zero, $hmenu, [uint32]$menuIndex, [ref]$rect)) { throw "GetMenuItemRect failed [$tag]" }
    Mouse-Click ([int](($rect.Left + $rect.Right) / 2)) ([int](($rect.Top + $rect.Bottom) / 2))
    Start-Sleep -Milliseconds 900
}
Activate $float | Out-Null
Invoke-MenuItem $float 2 'bg0'
Invoke-MenuItem $float 6 'border0'
$r = Get-WindowRect $float
Save-Screenshot "$repo\artifacts\proto-dpi150-all0.png" $r.Left $r.Top $r.Width $r.Height

# 完全透明时点击翻页
$cx = $r.Left + [int]($r.Width / 2); $cy = $r.Top + [int]($r.Height / 2)
Mouse-Click $cx $cy
Start-Sleep -Milliseconds 700
Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1 | ForEach-Object { "click1: $($_.Line)" }

# 0% 边框右缘缩放 60px
$edgeX = $r.Right - 2; $edgeY = $r.Top + [int]($r.Height / 2)
Mouse-Down $edgeX $edgeY
Start-Sleep -Milliseconds 100
foreach ($i in 1..6) { Mouse-MoveTo ($edgeX + $i * 10) $edgeY }
Mouse-Up
Start-Sleep -Milliseconds 700
$r2 = Get-WindowRect $float
"after edge resize at 150%: $($r2.Width)x$($r2.Height) (was $($r.Width)x$($r.Height), expect ~+60 physical px)"
Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1 | ForEach-Object { "render: $($_.Line)" }
Save-Screenshot "$repo\artifacts\proto-dpi150-resized.png" $r2.Left $r2.Top $r2.Width $r2.Height
Get-Process FloatPrototype | Stop-Process
