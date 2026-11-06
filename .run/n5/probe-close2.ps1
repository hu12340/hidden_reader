. 'D:\MyProjects\HiddenReader\tools\gui.ps1'
Add-Type -AssemblyName UIAutomationClient
$root='D:\MyProjects\HiddenReader'
$env:HR_DATA_DIR="$root\.run\n5\data"; $env:HR_LOG_DIR="$root\.run\n5\log"
$exe="$root\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$p=Start-Process $exe -PassThru
Start-Sleep -Milliseconds 3000
$rootEl=[System.Windows.Automation.AutomationElement]::RootElement
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$m=$rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)
$hwnd=[IntPtr]$m.Current.NativeWindowHandle
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class Q2{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]public static extern bool IsHungAppWindow(IntPtr h);}'
[Q2]::PostMessage($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null
Start-Sleep -Milliseconds 2000
Write-Host "hung=$([Q2]::IsHungAppWindow($hwnd)) visible=$([Q2]::IsWindowVisible($hwnd)) exited=$($p.HasExited)"
# 该进程的窗口列表
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;using System.Text;public class EW{[DllImport("user32.dll")]public static extern bool EnumWindows(Func<IntPtr,IntPtr,bool> cb,IntPtr l);[DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetWindowText(IntPtr h,StringBuilder sb,int n);[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern int GetClassName(IntPtr h,StringBuilder sb,int n);}'
$found=@()
$cb=[Func[IntPtr,IntPtr,bool]]{param($h,$l) [uint32]$pid=0; [EW]::GetWindowThreadProcessId($h,[ref]$pid)|Out-Null; if($pid -eq $p.Id){$sb=New-Object Text.StringBuilder 256;[EW]::GetWindowText($h,$sb,256)|Out-Null;$sb2=New-Object Text.StringBuilder 256;[EW]::GetClassName($h,$sb2,256)|Out-Null;$script:found+="hwnd=$h class='$($sb2)' title='$($sb)' vis=$([Q2]::IsWindowVisible($h))"}; return $true}
[EW]::EnumWindows($cb,[IntPtr]::Zero)|Out-Null
$found | ForEach-Object { Write-Host $_ }
Save-Screenshot "$root\.run\n5\shots\close-block.png" 0 0 800 600
if(-not $p.HasExited){Stop-Process -Id $p.Id -Force}