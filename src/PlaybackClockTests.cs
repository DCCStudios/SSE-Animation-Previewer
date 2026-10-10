namespace SSEPreview;
internal static class PlaybackClockTests
{
    internal static void Run(Action<bool,string> check)
    {
        double wall=0;var clock=new PlaybackClock(()=>wall){Duration=2};clock.Play();
        wall=.5;clock.SourceFps=60;check(clock.Time==.5,"Changing stepping FPS preserves elapsed playback time");
        wall=5.25;check(clock.Time==1.25,"A slow frame crossing multiple loops retains the full elapsed time");
        clock.Speed=2;wall=5.5;check(clock.Time==1.75,"Speed changes preserve the current anchor before applying the new rate");
        clock.Pause();wall=20;check(clock.Time==1.75,"Paused playback does not advance with wall time");
        clock.SourceFps=30;clock.Step(1);check(Math.Abs(clock.Time-(1.75+1d/30))<1e-9,"Default frame stepping advances one thirtieth second independently of rendering");
        clock.Seek(.25);clock.Loop=false;clock.Play();wall=30;check(clock.Time==2,"Non-looping playback clamps after a delayed frame");
    }
}
