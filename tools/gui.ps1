# GUI 验收辅助脚本：窗口查找、截图与鼠标键盘注入。
Add-Type -AssemblyName System.Drawing

if (-not ('GuiNative' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class GuiNative {
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(ref POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
}
'@
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MenuNative {
    [StructLayout(LayoutKind.Sequential)] public struct MRECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr menu, uint index, out MRECT rect);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
}
'@
}

function Get-WindowByClass([string]$className) { [GuiNative]::FindWindow($className, $null) }

# 让测试脚本的坐标与窗口矩形使用物理像素（PerMonitorV2）。
[GuiNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

function Activate-Window([IntPtr]$hwnd) {
    for ($i = 0; $i -lt 6; $i++) {
        $fg = [GuiNative]::GetForegroundWindow()
        if ($fg -eq $hwnd) { return $true }
        $fgThread = [GuiNative]::GetWindowThreadProcessId($fg, [IntPtr]::Zero)
        $myThread = [GuiNative]::GetCurrentThreadId()
        $attached = $false
        try {
            if ($fgThread -ne 0 -and $fgThread -ne $myThread) { $attached = [GuiNative]::AttachThreadInput($myThread, $fgThread, $true) }
            [GuiNative]::BringWindowToTop($hwnd) | Out-Null
            [GuiNative]::SetForegroundWindow($hwnd) | Out-Null
        } finally {
            if ($attached) { [GuiNative]::AttachThreadInput($myThread, $fgThread, $false) | Out-Null }
        }
        Start-Sleep -Milliseconds 200
        if ([GuiNative]::GetForegroundWindow() -eq $hwnd) { return $true }
    }
    return $false
}

function Get-WindowRect([IntPtr]$hwnd) {
    $r = New-Object GuiNative+RECT
    [GuiNative]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    [pscustomobject]@{ Left=$r.Left; Top=$r.Top; Right=$r.Right; Bottom=$r.Bottom; Width=$r.Right-$r.Left; Height=$r.Bottom-$r.Top }
}

function Get-ClientRect([IntPtr]$hwnd) {
    $r = New-Object GuiNative+RECT
    [GuiNative]::GetClientRect($hwnd, [ref]$r) | Out-Null
    [pscustomobject]@{ Width=$r.Right-$r.Left; Height=$r.Bottom-$r.Top }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class MonitorWake {
    [DllImport("user32.dll")] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowW(string cls, string title);
}
'@

function Wake-Screen {
    # 关闭/休眠的显示器重新点亮（SC_MONITORPOWER = 0xF170, -1 = 开）
    $hwnd = [MonitorWake]::FindWindowW('Progman', $null)
    [MonitorWake]::SendMessageW($hwnd, 0x0112, [IntPtr]0xF170, [IntPtr](-1)) | Out-Null
    Start-Sleep -Milliseconds 800
}

function Save-Screenshot([string]$path, [int]$x, [int]$y, [int]$w, [int]$h) {
    Wake-Screen
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    } catch {
        $bmp.Dispose()
        Diagnostics2-Log "screenshot failed: $_"
        return
    }
    $g.Dispose()
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

function Diagnostics2-Log([string]$msg) {
    try { [System.IO.File]::AppendAllText("$env:TEMP\gui-helper.log", "$(Get-Date -Format HH:mm:ss.fff) $msg`r`n") } catch { }
}

# ---------- Win32 对话框自动化 ----------

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class DlgNative {
    public delegate bool EnumChildProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr lparam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lparam);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr wp, string lp);
    [DllImport("user32.dll", EntryPoint="SendMessageW")] public static extern IntPtr SendMessageIntPtr(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
'@
$script:WM_SETTEXT = 0x000C
$script:BM_CLICK = 0x00F5

function Get-ChildWindows([IntPtr]$parent) {
    $list = New-Object System.Collections.ArrayList
    $cb = {
        param($h, $l)
        [void]$list.Add($h)
        return $true
    }
    [DlgNative]::EnumChildWindows($parent, $cb, [IntPtr]::Zero) | Out-Null
    return $list
}

function Get-WindowClass([IntPtr]$h) {
    $c = New-Object System.Text.StringBuilder 256
    [DlgNative]::GetClassNameW($h, $c, 256) | Out-Null
    return $c.ToString()
}

function Get-WindowTitle([IntPtr]$h) {
    $t = New-Object System.Text.StringBuilder 256
    [DlgNative]::GetWindowTextW($h, $t, 256) | Out-Null
    return $t.ToString()
}

function Set-DialogFileName([IntPtr]$dlg, [string]$path) {
    # 文件名编辑框：#32770 -> ComboBoxEx32 -> ComboBox -> Edit
    $comboEx = [DlgNative]::FindWindowExW($dlg, [IntPtr]::Zero, 'ComboBoxEx32', $null)
    $combo = [DlgNative]::FindWindowExW($comboEx, [IntPtr]::Zero, 'ComboBox', $null)
    $edit = [DlgNative]::FindWindowExW($combo, [IntPtr]::Zero, 'Edit', $null)
    if ($edit -eq [IntPtr]::Zero) {
        foreach ($c in Get-ChildWindows $dlg) {
            if ((Get-WindowClass $c) -eq 'Edit') { $edit = $c; break }
        }
    }
    if ($edit -eq [IntPtr]::Zero) { throw 'filename edit not found' }
    [DlgNative]::SendMessageW($edit, $script:WM_SETTEXT, [IntPtr]::Zero, $path) | Out-Null
    Start-Sleep -Milliseconds 400
}

function Click-DialogButton([IntPtr]$dlg, [string]$match) {
    foreach ($c in (Get-ChildWindows $dlg)) {
        if ((Get-WindowClass $c) -eq 'Button' -and (Get-WindowTitle $c) -like "*$match*") {
            [DlgNative]::SendMessageIntPtr($c, $script:BM_CLICK, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
            return $true
        }
    }
    return $false
}

function Get-ProcessWindows([int]$ownerPid) {
    $list = New-Object System.Collections.ArrayList
    $cb = {
        param($h, $l)
        [uint32]$wpid = 0
        [DlgNative]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
        if ($wpid -eq $ownerPid) { [void]$list.Add($h) }
        return $true
    }
    [DlgNative]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    return $list
}

function Wait-VisibleDialog([int]$ownerPid, [int]$timeoutMs = 8000) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        foreach ($h in (Get-ProcessWindows $ownerPid)) {
            if ((Get-WindowClass $h) -eq '#32770' -and [DlgNative]::IsWindowVisible($h)) { return $h }
        }
        Start-Sleep -Milliseconds 300
    }
    return [IntPtr]::Zero
}

function Move-CursorVerified([int]$x, [int]$y) {
    for ($i = 0; $i -lt 4; $i++) {
        [GuiNative]::SetCursorPos($x, $y) | Out-Null
        Start-Sleep -Milliseconds 50
        $p2 = New-Object GuiNative+POINT
        [GuiNative]::GetCursorPos([ref]$p2) | Out-Null
        if ($p2.X -eq $x -and $p2.Y -eq $y) { return $true }
        Start-Sleep -Milliseconds 80
    }
    return $false
}

function Mouse-Click([int]$x, [int]$y, [string]$button = 'left') {
    [void](Move-CursorVerified $x $y)
    Start-Sleep -Milliseconds 60
    if ($button -eq 'right') {
        [GuiNative]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
        [GuiNative]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
    } else {
        [GuiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        [GuiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    }
}

function Key-Down([byte]$vk) {
    [GuiNative]::keybd_event($vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

function Key-Up([byte]$vk) {
    [GuiNative]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

function Mouse-DownAt([int]$x, [int]$y) {
    [GuiNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [GuiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
}

function Mouse-UpAt([int]$x, [int]$y) {
    [GuiNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [GuiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
}

function Mouse-Down([int]$x, [int]$y) {
    [GuiNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 40
    [GuiNative]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
}

function Mouse-Up() {
    [GuiNative]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Mouse-MoveTo([int]$x, [int]$y) {
    [GuiNative]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 30
}

function Key-Press([int]$vk) {
    [GuiNative]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 30
    [GuiNative]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
}

function Key-Down([int]$vk) { [GuiNative]::keybd_event([byte]$vk, 0, 0, [UIntPtr]::Zero) }
function Key-Up([int]$vk) { [GuiNative]::keybd_event([byte]$vk, 0, 2, [UIntPtr]::Zero) }

function Get-ForegroundTitle {
    $h = [GuiNative]::GetForegroundWindow()
    $sb = New-Object System.Text.StringBuilder 256
    Add-Type -AssemblyName System.Windows.Forms
    return $h
}
