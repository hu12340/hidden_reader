Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class DeskCheck {
    [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);
}
'@
$input = [DeskCheck]::OpenInputDesktop(0, $false, 0x0001) # DESKTOP_READOBJECTS
"openInputDesktop=$input (non-zero = unlocked)"
$tid = [DeskCheck]::GetCurrentThreadId()
"threadDesktop=$([DeskCheck]::GetThreadDesktop($tid))"
$fg = [DeskCheck]::GetForegroundWindow()
$t = New-Object System.Text.StringBuilder 256
[DeskCheck]::GetWindowTextW($fg, $t, 256) | Out-Null
"foreground='$t' hwnd=$fg"
