using System.Runtime.InteropServices;
using System.Text;

namespace SSEPreview;

/// <summary>Keeps a background run from taking the foreground from whatever the user is doing (a game, an editor).
/// <para>Measured 2026-11-03 with a foreground poller on the real desktop: every background start put OpenTK's GLFW window
/// (class GLFW30, "OpenTK Window") in the foreground for about 2 s. A stack taken in this hook showed where:
/// GLControl.NonportableReparent calls SetParent while that window is still a top-level popup and makes it a child only
/// afterwards, and reparenting a top-level window activates it. Refusing the activation alone was not enough: the poller then
/// saw the foreground drop to no window at all, so the switch away from the user's window happens before the activation.</para>
/// <para>So, on the UI thread of a background run, a thread-local CBT hook:</para>
/// <list type="bullet">
/// <item>takes the system foreground lock (LockSetForegroundWindow) when the GLFW window is created and releases it once that
/// window's activation has been refused, so no foreground switch can happen in between. Windows also releases the lock on any
/// key press or click, so the user is never locked out.</item>
/// <item>answers every HCBT_ACTIVATE with "no", so no window of this thread (the form, the GLFW window, a dialog) can be
/// activated later either.</item>
/// </list>
/// <para>Each run appends one line to %TEMP%\sse-previewer-focus-guard.log (lock taken or refused, activations refused), and
/// SSE_PREVIEW_FOCUS_LOG (with SSE_PREVIEW_FOCUS_TRACE=1, the stack) records each refusal.</para></summary>
static class ActivationGuard
{
    delegate IntPtr HookProc(int code,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll",SetLastError=true,EntryPoint="SetWindowsHookExW")]static extern IntPtr SetWindowsHookEx(int idHook,HookProc proc,IntPtr module,uint threadId);
    [DllImport("user32.dll")]static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    [DllImport("user32.dll",SetLastError=true)]static extern bool LockSetForegroundWindow(uint code);
    [DllImport("user32.dll")]static extern IntPtr GetAncestor(IntPtr window,uint flags);
    [DllImport("user32.dll")]static extern IntPtr GetDesktopWindow();
    const uint GA_PARENT=1;
    [DllImport("user32.dll")]static extern IntPtr GetWindowLongPtr(IntPtr window,int index);
    [DllImport("user32.dll")]static extern IntPtr SetWindowLongPtr(IntPtr window,int index,IntPtr value);
    const int WH_CBT=5,WH_CALLWNDPROC=4,HCBT_ACTIVATE=5,HCBT_CREATEWND=3,GWL_STYLE=-16,GWL_EXSTYLE=-20;
    const uint WM_CREATE=0x0001,WM_WINDOWPOSCHANGING=0x0046,WM_WINDOWPOSCHANGED=0x0047;
    const long WS_POPUP=0x80000000,WS_CHILD=0x40000000,WS_EX_NOACTIVATE=0x08000000,WS_EX_APPWINDOW=0x00040000;
    const uint LSFW_LOCK=1,LSFW_UNLOCK=2;
    // Kept in fields: the hooks call through these delegates for the life of the process.
    static HookProc? proc,messages;
    // GLControl's GLFW window while it is being created, and whether WM_CREATE has gone by (then its styles may change).
    static IntPtr surface;static bool surfaceCreated,surfaceIsChild;
    static readonly System.Diagnostics.Stopwatch since=System.Diagnostics.Stopwatch.StartNew();
    static bool locked;static string lockResult="no GLFW window seen";
    public static int Blocked{get;private set;}

    public static void Install()
    {
        if(proc!=null)return;
        proc=(code,wParam,lParam)=>{
            if(code==HCBT_CREATEWND&&!locked&&ClassOf(wParam)=="GLFW30"&&CreatedTitle(lParam)=="OpenTK Window"){
                surface=wParam;
                locked=LockSetForegroundWindow(LSFW_LOCK);
                lockResult=locked?"lock taken":"lock refused, error "+Marshal.GetLastWin32Error();
                Note($"GLFW window {wParam:X} created: foreground {lockResult}");
            }
            if(code!=HCBT_ACTIVATE)return CallNextHookEx(IntPtr.Zero,code,wParam,lParam);
            Blocked++;Log(wParam);
            if(locked&&ClassOf(wParam)=="GLFW30")Release("after the GLFW window's activation was refused");
            return 1;   // nonzero: the activation does not happen
        };
        if(SetWindowsHookEx(WH_CBT,proc,IntPtr.Zero,GetCurrentThreadId())==IntPtr.Zero)
            Console.Error.WriteLine("Activation guard not installed: error "+Marshal.GetLastWin32Error());
        // The lock does not stop the switch SetParent makes (it is not a SetForegroundWindow call): the poller still saw the
        // foreground drop to no window on about one start in eight. A child window cannot be activated, so the GLFW window is
        // made one before GLControl reparents it, the order SetParent's documentation asks for. That cannot happen at
        // HCBT_CREATEWND (CreateWindowEx then fails and GLFW retries); it happens at the first move or size message after its
        // WM_CREATE, which GLFW sends from glfwCreateWindow and OpenTK from NativeWindow's constructor, both before the reparent.
        messages=(code,wParam,lParam)=>{
            // Once GLControl has parented the window (its parent is no longer the desktop), the lock has nothing left to guard.
            if(code>=0&&locked&&surfaceIsChild&&GetAncestor(surface,GA_PARENT)!=GetDesktopWindow())Release("once the GLFW window was parented into the form");
            if(code>=0&&surface!=IntPtr.Zero&&!surfaceIsChild){
                // CWPSTRUCT: lParam, wParam, message (padded to pointer size), hwnd.
                IntPtr window=Marshal.ReadIntPtr(lParam,3*IntPtr.Size);uint message=(uint)Marshal.ReadInt32(lParam,2*IntPtr.Size);
                if(window==surface){
                    if(message==WM_CREATE)surfaceCreated=true;
                    else if(surfaceCreated&&message is WM_WINDOWPOSCHANGING or WM_WINDOWPOSCHANGED){
                        surfaceIsChild=true;
                        long style=(long)GetWindowLongPtr(window,GWL_STYLE),ex=(long)GetWindowLongPtr(window,GWL_EXSTYLE);
                        SetWindowLongPtr(window,GWL_STYLE,(IntPtr)((style&~WS_POPUP)|WS_CHILD));
                        SetWindowLongPtr(window,GWL_EXSTYLE,(IntPtr)((ex|WS_EX_NOACTIVATE)&~WS_EX_APPWINDOW));
                        Note($"GLFW window {window:X} made a child before reparenting (style {style:X8} -> {(long)GetWindowLongPtr(window,GWL_STYLE):X8})");
                    }
                }
            }
            return CallNextHookEx(IntPtr.Zero,code,wParam,lParam);
        };
        if(SetWindowsHookEx(WH_CALLWNDPROC,messages,IntPtr.Zero,GetCurrentThreadId())==IntPtr.Zero)
            Console.Error.WriteLine("Activation guard message hook not installed: error "+Marshal.GetLastWin32Error());
    }

    /// <summary>Releases the foreground lock if the GLFW window never asked to be activated (MainWindow calls it once shown),
    /// and writes this run's line to the guard log.</summary>
    public static void Release(string when)
    {
        if(locked){LockSetForegroundWindow(LSFW_UNLOCK);locked=false;Note("foreground lock released "+when);}
        if(proc==null||summaryWritten)return;
        summaryWritten=true;
        try{
            string file=Path.Combine(Path.GetTempPath(),"sse-previewer-focus-guard.log");
            if(File.Exists(file)&&new FileInfo(file).Length>1_000_000)File.Delete(file);
            File.AppendAllText(file,$"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid {Environment.ProcessId}: {lockResult}, released {when} at {since.ElapsedMilliseconds} ms, activations refused {Blocked}, {Environment.CommandLine[..Math.Min(160,Environment.CommandLine.Length)]}{Environment.NewLine}");
        }catch(IOException){}catch(UnauthorizedAccessException){}
    }
    static bool summaryWritten;

    static string ClassOf(IntPtr window){var name=new StringBuilder(64);GetClassName(window,name,64);return name.ToString();}
    /// <summary>The window name passed to CreateWindowEx: HCBT_CREATEWND's lParam is a CBT_CREATEWND whose first field points
    /// to the CREATESTRUCT, which holds lpszName after lpCreateParams, hInstance, hMenu, hwndParent, four ints and the style
    /// (padded to pointer size: offset 56 on x64, 36 on x86). The hook is the Unicode one, so the name is UTF-16.</summary>
    static string? CreatedTitle(IntPtr cbtCreate)
    {
        IntPtr create=Marshal.ReadIntPtr(cbtCreate);if(create==IntPtr.Zero)return null;
        IntPtr name=Marshal.ReadIntPtr(create,4*IntPtr.Size+16+IntPtr.Size);
        return name==IntPtr.Zero||(long)name<0x10000?null:Marshal.PtrToStringUni(name);
    }
    static void Note(string text)
    {
        if(Environment.GetEnvironmentVariable("SSE_PREVIEW_FOCUS_LOG") is not {Length:>0} file)return;
        try{File.AppendAllText(file,$"{since.ElapsedMilliseconds} ms {text}{Environment.NewLine}");}catch(IOException){}catch(UnauthorizedAccessException){}
    }

    static void Log(IntPtr window)
    {
        if(Environment.GetEnvironmentVariable("SSE_PREVIEW_FOCUS_LOG") is not {Length:>0} file)return;
        var name=new StringBuilder(256);GetClassName(window,name,256);
        string trace=Environment.GetEnvironmentVariable("SSE_PREVIEW_FOCUS_TRACE")=="1"?string.Concat(Environment.StackTrace.Split('\n').Skip(3).Take(30).Select(l=>"        "+l.Trim()+Environment.NewLine)):"";
        try{File.AppendAllText(file,$"{since.ElapsedMilliseconds} ms activation refused: window {window:X} class {name}{Environment.NewLine}{trace}");}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
