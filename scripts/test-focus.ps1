# Checks that the previewer opens without taking the focus. It starts the exe on a separate, hidden Windows desktop,
# the way Start-Process starts a program (with a start-up show command), and reads the previewer's own focus log
# (SSE_PREVIEW_FOCUS_LOG). Programs on another desktop cannot take the keyboard or mouse from the one in use, so this
# is safe to run while working. Expected: the plain start is activated once; every other case, never.
param(
    [string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'bin/Release/net8.0-windows/win-x64/SSE Animation Previewer.exe'),
    [string]$LogDir = (Join-Path ([IO.Path]::GetTempPath()) 'sse-previewer-focus-test')
)
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
Add-Type @"
using System;using System.Runtime.InteropServices;
public static class HiddenDesktop {
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags; public short wShowWindow,cbReserved2; public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError; }
  [StructLayout(LayoutKind.Sequential)] public struct PROCESS_INFORMATION { public IntPtr hProcess,hThread; public int dwProcessId,dwThreadId; }
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr devmode,int flags,uint access,IntPtr security);
  [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern bool CreateProcess(string app,System.Text.StringBuilder command,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string dir,ref STARTUPINFO si,out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] public static extern bool TerminateProcess(IntPtr process,uint code);
  [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr handle,uint ms);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
  public static string Run(string exe,string args,string desktop,bool showCommand,int seconds){
    var si=new STARTUPINFO();si.cb=Marshal.SizeOf(typeof(STARTUPINFO));si.lpDesktop="winsta0\\"+desktop;
    if(showCommand){si.dwFlags=1;si.wShowWindow=1;}
    PROCESS_INFORMATION pi;var command=new System.Text.StringBuilder("\""+exe+"\" "+args);
    if(!CreateProcess(null,command,IntPtr.Zero,IntPtr.Zero,false,0,IntPtr.Zero,System.IO.Path.GetDirectoryName(exe),ref si,out pi))return "CreateProcess failed "+Marshal.GetLastWin32Error();
    uint wait=WaitForSingleObject(pi.hProcess,(uint)(seconds*1000));
    if(wait!=0)TerminateProcess(pi.hProcess,0);
    CloseHandle(pi.hThread);CloseHandle(pi.hProcess);return wait==0?"exited on its own":"stopped after "+seconds+" s";
  }
}
"@
$desktopName = "SsePreviewerFocusTest"
$desktop = [HiddenDesktop]::CreateDesktop($desktopName, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x10000000, [IntPtr]::Zero)
if ($desktop -eq [IntPtr]::Zero) { "could not create the hidden desktop"; exit 1 }
try {
  foreach ($case in @(@('plain start', ''), @('no-activate', '--no-activate'), @('no-activate env', ''), @('tool switch', '--camera-smoke'))) {
    $log = Join-Path $LogDir ("focus-" + ($case[0] -replace ' ', '-') + ".txt"); Remove-Item -LiteralPath $log -ErrorAction SilentlyContinue
    $env:SSE_PREVIEW_FOCUS_LOG = $log
    if ($case[0] -eq 'no-activate env') { $env:SSE_PREVIEW_NO_ACTIVATE = '1' } else { Remove-Item Env:SSE_PREVIEW_NO_ACTIVATE -ErrorAction SilentlyContinue }
    $result = [HiddenDesktop]::Run($Exe, $case[1], $desktopName, $true, 9)
    $lines = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() }
    $activated = @($lines | Where-Object { $_ -match ' activated;' }).Count
    "{0,-16} {1}; shown: {2}; activations: {3}" -f $case[0], $result, (@($lines | Where-Object { $_ -match ' shown;' }).Count -gt 0), $activated
    $lines | Select-Object -First 3 | ForEach-Object { "      $_" }
  }
} finally { Remove-Item Env:SSE_PREVIEW_FOCUS_LOG -ErrorAction SilentlyContinue; Remove-Item Env:SSE_PREVIEW_NO_ACTIVATE -ErrorAction SilentlyContinue; [void][HiddenDesktop]::CloseDesktop($desktop) }
