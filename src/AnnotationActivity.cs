using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SSEPreview;

// One annotation line of the Annotations tab: when it fires in playback time (Fires is false for a queued clip's
// annotations after its hand-over, which never fire) and its text.
// End is the playback time at which the row's clip hands over to the next queued clip: states that belong to a clip
// (windows, the BFCO chain, recovery) end there, as the graph closes them on entering the next clip.
public sealed record AnnotationRow(double Time,string Text,bool Fires,string Line,double End=double.PositiveInfinity);

// Which annotations are "active" at a playback time.
//
// An ordinary annotation is active from the moment it fires until the next annotation fires. Some annotations set a
// state that lasts, so they stay active until that state changes:
//   MCO_nextattack / MCO_nextpowerattack                until the next one of the same kind
//   MCO_WinOpen / MCO_WinClose, MCO_PowerWinOpen / Close until the window is closed or opened again
//   MCO_AttackSpeed / BFCO_AttackSpeed                   until the next one for the same variable
//   BFCO_NextIsAttackN / BFCO_NextIsPowerAttackN,
//   BFCO_NextWinStart / BFCO_NextPowerWinStart            until BFCO_DIY_EndLoop (or the end of the clip)
//   BFCO_DIY_recovery                                     to the end of the clip
//   BFCO_ChargeStageStart and BFCO_ChargeStageN           until the next stage
//   BFCO_ForbidRotation, BFCO_0Gravity, BFCO_bsprinting,
//   AMF_ForceMoveMagstim, AMF_ForbidMoveMagstim           from their Start until their Close, End or Stop
//   Collision_Add                                        while Precision keeps that collision alive
//   Collision_AttackStart                                until Collision_AttackEnd, Collision_Cancel, attackStop or staggerStart
//   other Collision_* annotations                        until the next Collision_* annotation
// AMR's animmotion / animrotation keys are root motion data written on almost every frame. They do not count as the
// "next annotation" for the others; the latest key of each is the active one.
public static class AnnotationActivity
{
    const double Epsilon=1e-6;
    static readonly Regex Variable=new(@"@SGV[FI]\|\s*([A-Za-z0-9_]+)\s*\|",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    // The lasting state an annotation belongs to, or null for an ordinary annotation.
    public static string? Channel(string text)
    {
        string t=text.Trim();string lower=t.ToLowerInvariant();
        if(lower.StartsWith("animmotion "))return "root-translation";
        if(lower.StartsWith("animrotation "))return "root-rotation";
        if(Variable.Match(t) is {Success:true} m){
            string name=m.Groups[1].Value.ToLowerInvariant();
            if(name is "mco_nextattack" or "mco_nextpowerattack" or "mco_attackspeed" or "bfco_attackspeed")return "var:"+name;
            return null;
        }
        if(lower is "mco_winopen" or "mco_winclose")return "window:mco";
        if(lower is "mco_powerwinopen" or "mco_powerwinclose")return "window:mco-power";
        // BFCO: the selectors (buffer open) and the two windows last until BFCO_DIY_EndLoop closes them all; there is
        // no separate window-close event. The other pairs are a start and its own close.
        if(lower is "bfco_nextwinstart")return "bfco-chain:window";
        if(lower is "bfco_nextpowerwinstart")return "bfco-chain:power-window";
        if(lower.StartsWith("bfco_nextispowerattack"))return "bfco-chain:next-power";
        if(lower.StartsWith("bfco_nextisattack"))return "bfco-chain:next";
        if(lower is "bfco_diy_recovery")return "recovery";
        if(lower.StartsWith("bfco_chargestage"))return "bfco:charge";
        if(lower is "bfco_forbidrotationstart" or "bfco_forbidrotationclose")return "bfco:forbid-rotation";
        if(lower is "amf_forcemovemagstimstart" or "amf_forcemovemagstimclose")return "amf:force-move";
        if(lower is "amf_forbidmovemagstimstart" or "amf_forbidmovemagstimclose")return "amf:forbid-move";
        if(lower is "bfco_0gravitystart" or "bfco_0gravityend")return "bfco:0gravity";
        if(lower is "bfco_bsprintingstart" or "bfco_bsprintingstop")return "bfco:sprinting";
        if(lower.StartsWith("collision_")){
            string tag=lower.Split('.')[0];
            if(tag=="collision_add")return "precision:add";
            if(tag is "collision_attackstart" or "collision_start" or "collision_attackend" or "collision_cancel")return "precision:attack";
            return "precision:other";
        }
        return null;
    }
    static bool EndsPrecisionAttack(string text){string t=text.Trim();return t.Equals("attackStop",StringComparison.OrdinalIgnoreCase)||t.Equals("staggerStart",StringComparison.OrdinalIgnoreCase);}
    static bool IsRootMotion(string? channel)=>channel is "root-translation" or "root-rotation";
    // Indices into rows of the annotations active at this time.
    public static HashSet<int> ActiveAt(IReadOnlyList<AnnotationRow> rows,double time,PrecisionTimeline? precision)
    {
        var active=new HashSet<int>();
        var fired=new List<(int Index,AnnotationRow Row,string? Channel)>();
        for(int i=0;i<rows.Count;i++){
            if(!rows[i].Fires||rows[i].Time>time+Epsilon)continue;
            string? channel=Channel(rows[i].Text);
            // A clip's own states do not outlive the clip. Variables do, and Precision has its own timeline.
            if(channel!=null&&time>=rows[i].End-Epsilon&&!channel.StartsWith("var:")&&!channel.StartsWith("precision:")&&!IsRootMotion(channel))continue;
            fired.Add((i,rows[i],channel));
        }
        if(fired.Count==0)return active;
        // Ordinary annotations: the group that fired last, root motion keys aside.
        var events=fired.Where(f=>!IsRootMotion(f.Channel)).ToList();
        if(events.Count>0){
            double latest=events.Max(f=>f.Row.Time);
            foreach(var f in events)if(f.Channel==null&&Math.Abs(f.Row.Time-latest)<=Epsilon)active.Add(f.Index);
        }
        // Lasting states: the latest annotation of each one.
        foreach(var group in fired.Where(f=>f.Channel!=null&&f.Channel!="precision:add"&&f.Channel!="precision:other").GroupBy(f=>f.Channel)){
            double latest=group.Max(f=>f.Row.Time);
            foreach(var f in group){
                if(Math.Abs(f.Row.Time-latest)>Epsilon)continue;
                if(group.Key!.StartsWith("bfco-chain:")&&events.Any(e=>e.Row.Time>latest+Epsilon&&e.Row.Text.Trim().Equals("BFCO_DIY_EndLoop",StringComparison.OrdinalIgnoreCase)))continue;
                if(group.Key=="precision:attack"&&f.Row.Text.Trim().ToLowerInvariant() is var tag&&(tag.StartsWith("collision_attackstart")||tag.StartsWith("collision_start"))
                    &&events.Any(e=>e.Row.Time>latest+Epsilon&&EndsPrecisionAttack(e.Row.Text)))continue;
                active.Add(f.Index);
            }
        }
        // Precision: an Add is active while its collision lives; Remove, ClearTargets and the rest until the next Collision_*.
        var collisions=fired.Where(f=>f.Channel!=null&&f.Channel.StartsWith("precision:")).ToList();
        if(collisions.Count>0){
            double latest=collisions.Max(f=>f.Row.Time);
            foreach(var f in collisions){
                if(f.Channel=="precision:other"){if(Math.Abs(f.Row.Time-latest)<=Epsilon)active.Add(f.Index);continue;}
                if(f.Channel!="precision:add")continue;
                var collision=precision?.Collisions.FirstOrDefault(c=>Math.Abs(c.Start-f.Row.Time)<=1e-4&&c.Source==f.Row.Text.Trim());
                if(collision!=null){if(collision.ActiveAt(time))active.Add(f.Index);}
                else if(Math.Abs(f.Row.Time-latest)<=Epsilon)active.Add(f.Index);
            }
        }
        return active;
    }
}

// Paints the active annotation lines of a RichTextBox without disturbing the user's selection or scroll position.
public sealed class AnnotationHighlighter(RichTextBox box)
{
    public static readonly Color ActiveBack=Color.FromArgb(28,96,66),ActiveFore=Color.White;
    readonly List<AnnotationRow> rows=[];readonly List<(int Start,int Length)> spans=[];
    HashSet<int> shown=[];int textLength=-1;
    public IReadOnlyList<AnnotationRow> Rows=>rows;
    public IReadOnlySet<int> Active=>shown;
    public (int Start,int Length) Span(int row)=>spans[row];
    // Call after the box's text is set: finds each annotation line, in order, in that text.
    public void Index(IEnumerable<AnnotationRow> annotationRows)
    {
        rows.Clear();spans.Clear();shown=[];
        string text=box.Text;int cursor=0;
        foreach(var row in annotationRows){
            int at=text.IndexOf(row.Line,cursor,StringComparison.Ordinal);
            if(at<0)continue;
            rows.Add(row);spans.Add((at,row.Line.Length));cursor=at+row.Line.Length;
        }
        textLength=box.TextLength;
    }
    public void Clear(){rows.Clear();spans.Clear();shown=[];textLength=-1;}
    // follow: scroll so the annotation that fired last stays in view (used during playback; a paused view is left alone).
    public void Update(double time,PrecisionTimeline? precision,bool follow=false)
    {
        // Something else replaced the text (an effect preview, a reset): the stored lines no longer apply.
        if(rows.Count==0||box.TextLength!=textLength||box.IsDisposed)return;
        var active=AnnotationActivity.ActiveAt(rows,time,precision);
        if(active.SetEquals(shown))return;
        var off=shown.Where(i=>!active.Contains(i)).ToList();var on=active.Where(i=>!shown.Contains(i)).ToList();
        // A box on a tab that was never opened has no window yet; it is painted the first time it has one.
        if(!box.IsHandleCreated)return;
        shown=active;
        int selectionStart=box.SelectionStart,selectionLength=box.SelectionLength;var scroll=new Point();
        SendMessage(box.Handle,EM_GETSCROLLPOS,IntPtr.Zero,ref scroll);
        SendMessage(box.Handle,WM_SETREDRAW,IntPtr.Zero,IntPtr.Zero);
        try{
            foreach(int i in off){box.Select(spans[i].Start,spans[i].Length);box.SelectionBackColor=box.BackColor;box.SelectionColor=box.ForeColor;}
            foreach(int i in on){box.Select(spans[i].Start,spans[i].Length);box.SelectionBackColor=ActiveBack;box.SelectionColor=ActiveFore;}
            box.Select(selectionStart,selectionLength);
            if(follow){
                int target=-1;
                foreach(int i in active){string? channel=AnnotationActivity.Channel(rows[i].Text);if(channel is "root-translation" or "root-rotation")continue;if(target<0||rows[i].Time>rows[target].Time||rows[i].Time==rows[target].Time&&i<target)target=i;}
                if(target>=0){
                    // Positions are relative to the current scroll, which SetRedraw has not moved yet.
                    SendMessage(box.Handle,EM_SETSCROLLPOS,IntPtr.Zero,ref scroll);
                    int y=box.GetPositionFromCharIndex(spans[target].Start).Y,line=box.Font.Height;
                    if(y<0||y>box.ClientSize.Height-line*2)scroll.Y=Math.Max(0,scroll.Y+y-line*2);
                }
            }
            SendMessage(box.Handle,EM_SETSCROLLPOS,IntPtr.Zero,ref scroll);
        }finally{
            SendMessage(box.Handle,WM_SETREDRAW,new IntPtr(1),IntPtr.Zero);box.Invalidate();
        }
    }
    const int WM_SETREDRAW=0x000B,EM_GETSCROLLPOS=0x04DD,EM_SETSCROLLPOS=0x04DE;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,ref Point point);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr handle,int message,IntPtr wParam,IntPtr lParam);
}
