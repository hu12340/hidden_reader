# 浮窗原型阶段 C：方向键翻页（浮窗有焦点）+ 置顶行为。
$ErrorActionPreference = 'Stop'
$repo = 'D:\MyProjects\HiddenReader'
. "$repo\tools\gui.ps1"
$log = "$env:TEMP\hiddenreader-proto.log"
Get-Process FloatPrototype -ErrorAction SilentlyContinue | Stop-Process -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600
Remove-Item $log -ErrorAction SilentlyContinue
$env:HR_PROTO_LOG_DIR = $env:TEMP
$p = Start-Process "$repo\prototype\FloatPrototype\bin\Debug\net10.0-windows\FloatPrototype.exe" -ArgumentList '--float' -PassThru
Start-Sleep -Seconds 4
$p.Refresh()
$float = [IntPtr]$p.MainWindowHandle

function Anchor-Last {
    (Select-String -Path $log -Pattern 'float render' | Select-Object -Last 1).Line
}

# --- 1. 浮窗有焦点时方向键翻页
"focus float: $(Activate-Window $float)"
"before arrows: $(Anchor-Last)"
Key-Press 0x27  # -> 下一页
Start-Sleep -Milliseconds 500
"after Right: $(Anchor-Last)"
Key-Press 0x28  # Down 下一页
Start-Sleep -Milliseconds 500
"after Down: $(Anchor-Last)"
Key-Press 0x25  # <- 上一页
Start-Sleep -Milliseconds 500
"after Left: $(Anchor-Last)"
Key-Press 0x26  # Up 上一页
Start-Sleep -Milliseconds 500
"after Up: $(Anchor-Last)"

# --- 2. 置顶开启（默认开启），切到其他应用后浮窗保持最上层、方向键归对方
$notes = Get-Process 'Microsoft.Notes' -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $notes) { $notes = Get-Process ApplicationFrameHost | Where-Object { $_.MainWindowTitle -and $_.MainWindowTitle -ne '' } | Select-Object -First 1 }
"target app: $($notes.ProcessName) [$($notes.MainWindowTitle)]"
"focus target: $(Activate-Window ([IntPtr]$notes.MainWindowHandle))"
Start-Sleep -Milliseconds 400
$fg = [GuiNative]::GetForegroundWindow()
"foreground is target: $($fg -eq [IntPtr]$notes.MainWindowHandle)"
$rf = Get-WindowRect $float
Save-Screenshot "$repo\artifacts\proto-topmost-on.png" $rf.Left $rf.Top $rf.Width $rf.Height
$before = Anchor-Last
Key-Press 0x27
Key-Press 0x25
Start-Sleep -Milliseconds 600
$after = Anchor-Last
"anchor before arrows in notes: $before"
"anchor after arrows in notes : $after"
# 便笺窗口在浮窗中心点之上？（置顶时应命中浮窗）
$pt = New-Object GuiNative+POINT
$pt.X = $rf.Left + 30; $pt.Y = $rf.Top + 30
"window at float corner: $([GuiNative]::WindowFromPoint($pt)) float=$float"

"LOG:"
Get-Content $log | Select-Object -Last 8
