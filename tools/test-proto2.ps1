# 浮窗原型自动化验收脚本（阶段 B：菜单、移动缩放、置顶、模式切换）。
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
$log = "$env:TEMP\hiddenreader-proto.log"

function Log-Tail([int]$n = 12) { Get-Content $log | Select-Object -Last $n }

function Get-FloatWindow {
    $p = Get-Process | Where-Object { $_.ProcessName -eq 'FloatPrototype' } | Select-Object -First 1
    if (-not $p) { throw 'app not running' }
    $p.Refresh()
    return [IntPtr]$p.MainWindowHandle
}

function Activate([IntPtr]$hwnd) {
    for ($i = 0; $i -lt 5; $i++) {
        [GuiNative]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 150
        if ([GuiNative]::GetForegroundWindow() -eq $hwnd) { return $true }
    }
    return $false
}

# 菜单项: 0 置顶 | 2 背景a0% 3 背景50% 4 背景100% | 6 边框0% 7 边框50% 8 边框100% | 10 返回普通模式 11 退出
function Invoke-MenuItem([IntPtr]$hwnd, [int]$menuIndex, [string]$tag) {
    $r = Get-WindowRect $hwnd
    $cx = $r.Left + [int]($r.Width / 2)
    $cy = $r.Top + [int]($r.Height / 2)
    Mouse-Click $cx $cy 'right'
    Start-Sleep -Milliseconds 700
    $popup = [MenuNative]::FindWindowW('#32768', $null)
    if ($popup -eq [IntPtr]::Zero) { throw "popup menu not found for [$tag]" }
    $hmenu = [MenuNative]::SendMessageW($popup, 0x01E1, [IntPtr]::Zero, [IntPtr]::Zero)
    $rect = New-Object MenuNative+MRECT
    if (-not [MenuNative]::GetMenuItemRect([IntPtr]::Zero, $hmenu, [uint32]$menuIndex, [ref]$rect)) {
        throw "GetMenuItemRect failed for [$tag]"
    }
    $ix = [int](($rect.Left + $rect.Right) / 2)
    $iy = [int](($rect.Top + $rect.Bottom) / 2)
    "menu click [$tag] item $menuIndex at $ix,$iy"
    Mouse-Click $ix $iy
    Start-Sleep -Milliseconds 900
    if ((Get-Process FloatPrototype -ErrorAction SilentlyContinue) -eq $null) { throw "app exited during menu item [$tag]" }
}

$float = Get-FloatWindow
"float=$float"
$r0 = Get-WindowRect $float
"initial rect: $($r0.Left),$($r0.Top) $($r0.Width)x$($r0.Height)"

# --- 通过右键菜单把背景与边框调到 100%
Activate $float | Out-Null
Invoke-MenuItem $float 4 '背景100%'
Invoke-MenuItem $float 8 '边框100%'
$rect = Get-WindowRect $float
Save-Screenshot "$repo\artifacts\proto-menu-all100.png" $rect.Left $rect.Top $rect.Width $rect.Height

# --- 菜单把背景与边框都调到 0%
Invoke-MenuItem $float 2 '背景0%'
Invoke-MenuItem $float 6 '边框0%'
$rect = Get-WindowRect $float
Save-Screenshot "$repo\artifacts\proto-menu-all0.png" $rect.Left $rect.Top $rect.Width $rect.Height

# --- Ctrl+左键拖动移动浮窗
Activate $float | Out-Null
$r = Get-WindowRect $float
$cx = $r.Left + [int]($r.Width / 2); $cy = $r.Top + [int]($r.Height / 2)
Key-Down 0x11
Mouse-Down $cx $cy
Start-Sleep -Milliseconds 100
foreach ($i in 1..6) { Mouse-MoveTo ($cx + $i * 20) ($cy + $i * 15) }
Start-Sleep -Milliseconds 150
Mouse-Up
Key-Up 0x11
Start-Sleep -Milliseconds 500
$r2 = Get-WindowRect $float
"after ctrl+drag: $($r2.Left),$($r2.Top) (expect ~$($r.Left + 120),$($r.Top + 90))"

# --- 拖动选字不应翻页
Activate $float | Out-Null
$anchorBefore = (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
Mouse-Down ($r2.Left + 60) ($r2.Top + 100)
foreach ($i in 1..8) { Mouse-MoveTo ($r2.Left + 60 + $i * 30) ($r2.Top + 100) }
Mouse-Up
Start-Sleep -Milliseconds 500
$anchorAfter = (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
"selection before: $anchorBefore"
"selection after : $anchorAfter"
Save-Screenshot "$repo\artifacts\proto-selection.png" $r2.Left $r2.Top $r2.Width $r2.Height

# --- 0% 边框时从右缘缩放
Activate $float | Out-Null
$edgeX = $r2.Right - 2; $edgeY = $r2.Top + [int]($r2.Height / 2)
Mouse-Down $edgeX $edgeY
foreach ($i in 1..8) { Mouse-MoveTo ($edgeX + $i * 10) $edgeY }
Mouse-Up
Start-Sleep -Milliseconds 700
$r3 = Get-WindowRect $float
"after right-edge resize: $($r3.Width)x$($r3.Height) (was $($r2.Width)x$($r2.Height))"

# --- 角落缩放
Activate $float | Out-Null
$cx2 = $r3.Right - 2; $cy2 = $r3.Bottom - 2
Mouse-Down $cx2 $cy2
foreach ($i in 1..6) { Mouse-MoveTo ($cx2 + $i * 10) ($cy2 + $i * 8) }
Mouse-Up
Start-Sleep -Milliseconds 700
$r4 = Get-WindowRect $float
"after corner resize: $($r4.Left),$($r4.Top) $($r4.Width)x$($r4.Height)"
Save-Screenshot "$repo\artifacts\proto-resized.png" $r4.Left $r4.Top $r4.Width $r4.Height

# --- 置顶行为：先确保置顶开
Activate $float | Out-Null
Key-Press 0x76  # F7 -> topmost on
Start-Sleep -Milliseconds 500
$ide = (Get-Process | Where-Object { $_.MainWindowTitle -and $_.ProcessName -ne 'FloatPrototype' } |
    Sort-Object { $_.MainWindowHandle } | Select-Object -Last 1)
"foreground test against: $($ide.ProcessName) $($ide.MainWindowTitle)"
if ($ide) {
    [GuiNative]::SetForegroundWindow($ide.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 800
    $fg = [GuiNative]::GetForegroundWindow()
    "foreground now: $fg (ide=$($ide.MainWindowHandle))"
    $r5 = Get-WindowRect $float
    Save-Screenshot "$repo\artifacts\proto-topmost-ide-focused.png" $r5.Left $r5.Top $r5.Width $r5.Height
    # 方向键此时应交给 IDE：浮窗锚点不应变化
    Key-Press 0x25; Key-Press 0x27
    Start-Sleep -Milliseconds 500
}

# --- 关闭置顶后应让出层级
Invoke-MenuItem $float 0 '置顶off'
Start-Sleep -Milliseconds 500
$rf = Get-WindowRect $float
$pt = New-Object GuiNative+POINT
$pt.X = $rf.Left + 30; $pt.Y = $rf.Top + 30
$hit = [GuiNative]::WindowFromPoint($pt)
"window at float corner (topmost off): $hit float=$float"
Invoke-MenuItem $float 0 '置顶on'
Start-Sleep -Milliseconds 400
$hit2 = [GuiNative]::WindowFromPoint($pt)
"window at float corner (topmost on): $hit2 float=$float"

# --- 模式切换：Esc 返回普通模式
Activate $float | Out-Null
Key-Press 0x1B
Start-Sleep -Milliseconds 900
$p = Get-Process FloatPrototype
$p.Refresh()
$shell = [IntPtr]$p.MainWindowHandle
"shell hwnd=$shell title=$($p.MainWindowTitle)"
$sr = Get-WindowRect $shell
Save-Screenshot "$repo\artifacts\proto-back-to-shell.png" $sr.Left $sr.Top $sr.Width $sr.Height

# --- 再进入浮窗（点击按钮），位置应保留
Activate $shell | Out-Null
Mouse-Click ($sr.Left + 371) ($sr.Top + 42)
Start-Sleep -Milliseconds 1200
$float2 = Get-FloatWindow
$r6 = Get-WindowRect $float2
"float again: $($r6.Left),$($r6.Top) $($r6.Width)x$($r6.Height)"
Save-Screenshot "$repo\artifacts\proto-float-again.png" $r6.Left $r6.Top $r6.Width $r6.Height

"LOG TAIL:"
Log-Tail 30
