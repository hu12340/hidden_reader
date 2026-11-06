Add-Type -AssemblyName UIAutomationClient
$root='D:\MyProjects\HiddenReader'
$env:HR_DATA_DIR="$root\.run\n5\emptydata2"; $env:HR_LOG_DIR="$root\.run\n5\log-base"
New-Item -ItemType Directory -Force -Path "$root\.run\n5\emptydata2" | Out-Null
$exe="$root\.run\baseline\src\HiddenReader.App\bin\Debug\net10.0-windows\HiddenReader.exe"
$p=Start-Process $exe -PassThru
Start-Sleep -Milliseconds 3000
$rootEl=[System.Windows.Automation.AutomationElement]::RootElement
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$m=$rootEl.FindFirst([System.Windows.Automation.TreeScope]::Children,$cond)
$hwnd=[IntPtr]$m.Current.NativeWindowHandle
Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class Q5{[DllImport("user32.dll")]public static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);}'
[Q5]::PostMessage($hwnd,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)|Out-Null
for($i=0;$i -lt 16 -and -not $p.HasExited;$i++){Start-Sleep -Milliseconds 500}
Write-Host "baseline empty exited=$($p.HasExited) after=$($i*0.5)s"
if(-not $p.HasExited){Stop-Process -Id $p.Id -Force}