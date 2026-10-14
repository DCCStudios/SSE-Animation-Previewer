using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SSEPreview;

// Raises Tick on the UI thread at a steady rate, for playback. A WinForms timer rounds every interval up to the
// system tick (15.6 ms unless something raised the timer resolution), so a 60 FPS loop on one presented a frame every
// other tick, about 32 FPS. This waits on a high-resolution waitable timer on its own thread and posts one tick at a
// time: a tick that has not run yet is not queued again, so a slow frame skips ahead instead of piling up.
// Same Tick / Start / Stop shape as System.Windows.Forms.Timer, so the window code drives it the same way.
public sealed class FramePacer(Control owner,double hz):IDisposable
{
    public event EventHandler? Tick;
    public double Hz=hz;
    volatile bool running;int pending,generation;

    public void Start()
    {
        if(running)return;running=true;
        // A thread left from a Stop just before this sees the new generation and ends.
        int mine=Interlocked.Increment(ref generation);
        new Thread(()=>Run(mine)){IsBackground=true,Name="Frame pacer",Priority=ThreadPriority.AboveNormal}.Start();
    }
    public void Stop()=>running=false;
    public void Dispose()=>Stop();

    void Run(int mine)
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION needs Windows 10 1803; without it the plain timer is used and the
        // process asks for 1 ms timer resolution instead.
        IntPtr timer=CreateWaitableTimerExW(IntPtr.Zero,null,2,0x1F0003);
        bool raised=false;
        if(timer==IntPtr.Zero){timer=CreateWaitableTimerExW(IntPtr.Zero,null,0,0x1F0003);raised=timeBeginPeriod(1)==0;}
        try{
            long period=(long)(Stopwatch.Frequency/Math.Max(1,Hz)),next=Stopwatch.GetTimestamp()+period;
            while(running&&Volatile.Read(ref generation)==mine){
                long wait=next-Stopwatch.GetTimestamp();
                if(wait>0){
                    if(timer!=IntPtr.Zero){long due=-(wait*10_000_000/Stopwatch.Frequency);if(SetWaitableTimerEx(timer,ref due,0,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,0))WaitForSingleObject(timer,1000);else Thread.Sleep(1);}
                    else Thread.Sleep(1);
                    continue;
                }
                period=(long)(Stopwatch.Frequency/Math.Max(1,Hz));next+=period;
                // More than a frame behind (a long load, a breakpoint): start the schedule again from now.
                long now=Stopwatch.GetTimestamp();if(now-next>period)next=now+period;
                if(Interlocked.CompareExchange(ref pending,1,0)!=0)continue;
                try{owner.BeginInvoke(Fire);}catch(InvalidOperationException){pending=0;if(owner.IsDisposed)break;}
            }
        }finally{if(timer!=IntPtr.Zero)CloseHandle(timer);if(raised)timeEndPeriod(1);}
    }
    void Fire()
    {
        try{if(running)Tick?.Invoke(this,EventArgs.Empty);}
        finally{Volatile.Write(ref pending,0);}
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWaitableTimerExW(IntPtr attributes,string? name,uint flags,uint access);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetWaitableTimerEx(IntPtr timer,ref long dueTime,int period,IntPtr completion,IntPtr argument,IntPtr wakeContext,uint tolerableDelay);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
}
