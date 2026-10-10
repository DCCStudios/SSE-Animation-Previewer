using System.Runtime.InteropServices;
using System.Text;
namespace SSEPreview;
// Exercises the real Windows common dialogs, cancelling only the test window's owned popup.
internal static class SceneDialogProbe
{
    [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr window);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wparam,IntPtr lparam);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr window,int id);
    delegate bool WindowCallback(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowCallback callback,IntPtr parameter);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    static IntPtr FindTestWindow(string expected){
        IntPtr found=IntPtr.Zero;
        EnumWindows((window,_)=>{GetWindowThreadProcessId(window,out uint process);if(process!=Environment.ProcessId)return true;var title=new StringBuilder(256);GetWindowText(window,title,title.Capacity);if(title.ToString()==expected)found=window;return true;},IntPtr.Zero);
        return found;
    }
    public static bool SaveAndAnswerOverwrite(Form owner,ToolStripItem item,bool accept){
        bool prompted=false;int ticks=0,stage=0;
        using var watchdog=new CancellationTokenSource();
        _=Task.Run(async()=>{try{await Task.Delay(15000,watchdog.Token);File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"scene-save-dialog-timeout.txt"),$"stage={stage}, accept={accept}");Environment.Exit(1);}catch(OperationCanceledException){}});
        using var timer=new System.Windows.Forms.Timer{Interval=150};
        timer.Tick+=(_,_)=>{
            var browser=FindTestWindow("Save preview scene");var confirm=FindTestWindow("Confirm Save As");
            if(stage==0&&browser!=IntPtr.Zero){stage=1;PostMessage(browser,0x111,(IntPtr)1,IntPtr.Zero);}
            else if(stage==1&&confirm!=IntPtr.Zero){
                prompted=true;stage=2;int id=accept?6:7;var button=GetDlgItem(confirm,id);
                if(button!=IntPtr.Zero)PostMessage(button,0xF5,IntPtr.Zero,IntPtr.Zero);
                else PostMessage(confirm,0x466,(IntPtr)id,IntPtr.Zero); // TaskDialog's TDM_CLICK_BUTTON.
            }else if(stage==2&&!accept&&confirm==IntPtr.Zero&&browser!=IntPtr.Zero){stage=3;PostMessage(browser,0x10,IntPtr.Zero,IntPtr.Zero);}
            if(++ticks>60){if(confirm!=IntPtr.Zero)PostMessage(confirm,0x10,IntPtr.Zero,IntPtr.Zero);if(browser!=IntPtr.Zero)PostMessage(browser,0x10,IntPtr.Zero,IntPtr.Zero);}
        };
        try{timer.Start();item.PerformClick();return prompted;}finally{timer.Stop();watchdog.Cancel();}
    }
    public static bool ClickAndCancel(Form owner,ToolStripItem item,string expected){
        bool found=false;int ticks=0;
        using var timer=new System.Windows.Forms.Timer{Interval=100};
        timer.Tick+=(_,_)=>{
            var popup=GetLastActivePopup(owner.Handle);var title=new StringBuilder(256);GetWindowText(popup,title,title.Capacity);
            if(popup!=owner.Handle&&title.ToString()==expected){found=true;PostMessage(popup,0x10,IntPtr.Zero,IntPtr.Zero);}
            if(++ticks>100){timer.Stop();if(popup!=owner.Handle)PostMessage(popup,0x10,IntPtr.Zero,IntPtr.Zero);}
        };
        timer.Start();item.PerformClick();timer.Stop();return found;
    }
}
