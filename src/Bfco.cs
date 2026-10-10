using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSEPreview;

// BFCO (the BFCO attack behavior framework). The facts here come from its Nemesis patch (1hm_behavior), the names in
// brackets being the patch objects that show them:
//   BFCO_NextIsAttackN / BFCO_NextIsPowerAttackN  open the input buffer and name the next clip by its number (#103).
//   BFCO_NextWinStart / BFCO_NextPowerWinStart    the earliest moment that transition may begin (#103). Without it
//                                                 the transition never begins.
//   BFCO_DIY_EndLoop                              closes the buffer and both windows. A clip need not carry it: the
//                                                 graph sends it on entering the next clip (#103).
//   BFCO_DIY_recovery, MCO_Recovery               from here movement cancels the attack (#4802).
//   Clip speed is BFCO_AttackSpeedManual = BFCO_AttackSpeed, or MCO_AttackSpeed while BFCO_AttackSpeed is exactly 1
//   (#991). BFCO_AttackSpeed goes back to 1 on entering the numbered attack states (#999) but not the hold loop, its
//   outro or the charge clips, so it carries into those; MCO_AttackSpeed carries everywhere. The hold loops
//   BFCO_PowerAttackLoopN and the NPC clips BFCO_NpcPowerAttackN are not bound to either.
// BFCO still runs MCO's own annotations (MCO_WinOpen and the rest) for clips that carry them.
public static class Bfco
{
    static readonly Regex Selector=new(@"^BFCO_NextIs(Power)?Attack(\d+)$",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    static readonly Regex Numbered=new(@"^BFCO_(Attack|PowerAttack|RangeAttack|PowerAttackOutro)\d+\.",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    static bool Named(Annotation e,string name)=>e.Text.Trim().Equals(name,StringComparison.OrdinalIgnoreCase);
    static string File(string name)=>Path.GetFileName(name.Replace('\\','/'));
    static bool Starts(string name,string prefix)=>File(name).StartsWith(prefix,StringComparison.OrdinalIgnoreCase);
    // A clip the BFCO graph plays. The file name decides first: BFCO's generators play BFCO_* files and MCO's play
    // mco_* files, and many MCO clips carry BFCO annotations as well. For any other name, BFCO's chain annotations do.
    public static bool IsClip(string name,IEnumerable<Annotation> events)
    {
        if(Starts(name,"BFCO_"))return true;
        if(Starts(name,"mco_"))return false;
        return events.Any(e=>e.Text.TrimStart().StartsWith("BFCO_Next",StringComparison.OrdinalIgnoreCase)||e.Text.TrimStart().StartsWith("BFCO_DIY_",StringComparison.OrdinalIgnoreCase)||e.Text.TrimStart().StartsWith("BFCO_DefAttackStage",StringComparison.OrdinalIgnoreCase));
    }
    public static bool IsHoldLoop(string name)=>Starts(name,"BFCO_PowerAttackLoop");
    // Clips whose generators have no speed binding: they play at 1x whatever the variables hold.
    public static bool IsUnbound(string name)=>IsHoldLoop(name)||Starts(name,"BFCO_NpcPowerAttack");
    // Clips entered without the BFCO_AttackSpeed reset, so the value the clip before left carries into them.
    public static bool KeepsBfcoSpeed(string name)=>IsHoldLoop(name)||Starts(name,"BFCO_PowerAttackOutro")||Starts(name,"BFCO_PowerAttack_Charge");
    // The clip number a selector names, with the time it fires: the first of that kind. Null when there is none.
    public static (int Number,double Time)? Next(IEnumerable<Annotation> events,bool power)=>Selectors(events,power).Select(s=>((int,double)?)s).FirstOrDefault();
    static IEnumerable<(int Number,double Time)> Selectors(IEnumerable<Annotation> events,bool power)
    {
        foreach(var e in events.OrderBy(e=>e.Time))
            if(Selector.Match(e.Text.Trim()) is {Success:true} m&&m.Groups[1].Success==power&&int.TryParse(m.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int number))yield return (number,e.Time);
    }
    // The clip's speed over its own time as the BFCO graph computes it, from MCO_AttackSpeed and BFCO_AttackSpeed as
    // they stand when the clip starts.
    public static List<(double Time,double Value)> SpeedChanges(AnimationClip clip,double startMco,double startBfco=1)
    {
        var merged=clip.SpeedChanges.Select(c=>(c.Time,c.Value,Bfco:false)).Concat(clip.BfcoSpeedChanges.Select(c=>(c.Time,c.Value,Bfco:true))).OrderBy(c=>c.Time).ToList();
        var result=new List<(double Time,double Value)>();double mco=startMco,bfco=startBfco;
        for(int i=0;i<merged.Count;){
            double time=merged[i].Time;
            for(;i<merged.Count&&merged[i].Time-time<=1e-9;i++){if(merged[i].Bfco)bfco=merged[i].Value;else mco=merged[i].Value;}
            result.Add((time,Effective(mco,bfco)));
        }
        return result;
    }
    public static double Effective(double mco,double bfco)=>Math.Abs(bfco-1)<1e-9?mco:bfco;
    // A variable's value at a clip time, given its value when the clip starts. The next clip of a chain inherits it.
    public static double McoSpeedAt(AnimationClip clip,double start,double time)=>ValueAt(clip.SpeedChanges,start,time);
    public static double BfcoSpeedAt(AnimationClip clip,double start,double time)=>ValueAt(clip.BfcoSpeedChanges,start,time);
    static double ValueAt(List<(double Time,double Value)> changes,double start,double time)
    {
        double value=start;foreach(var c in changes){if(c.Time<=time+1e-9)value=c.Value;else break;}
        return value;
    }
    // The hand-over annotation for a window choice ("MCO_WinOpen" for the light window, "MCO_PowerWinOpen" for the
    // power one). A BFCO clip uses BFCO's window when it also carries the next-attack annotation that arms it, and
    // MCO's otherwise; any other clip uses MCO's. Either falls back to the other framework's window.
    public static (string? Annotation,List<double> Times) Window(string name,IReadOnlyList<Annotation> events,string window)
    {
        bool power=window.Equals("MCO_PowerWinOpen",StringComparison.OrdinalIgnoreCase)||window.Equals("BFCO_NextPowerWinStart",StringComparison.OrdinalIgnoreCase);
        bool light=window.Equals("MCO_WinOpen",StringComparison.OrdinalIgnoreCase)||window.Equals("BFCO_NextWinStart",StringComparison.OrdinalIgnoreCase);
        if(!power&&!light){var own=AnimationClip.WindowTimes(events,window);return (own.Count>0?window:null,own);}
        string mco=power?"MCO_PowerWinOpen":"MCO_WinOpen",bfco=power?"BFCO_NextPowerWinStart":"BFCO_NextWinStart";
        bool armed=IsClip(name,events)&&(Next(events,power)!=null||!Numbered.IsMatch(File(name)));
        foreach(string candidate in armed?new[]{bfco,mco}:new[]{mco,bfco}){
            var times=AnimationClip.WindowTimes(events,candidate);if(times.Count>0)return (candidate,times);
        }
        return (null,[]);
    }
    // What the Annotations tab says about a BFCO clip; empty for any other clip.
    public static string Describe(string name,AnimationClip clip)
    {
        var events=clip.Events.OrderBy(e=>e.Time).ToList();if(!IsClip(name,events))return "";
        var text=new StringBuilder("\n\nBFCO\n");
        string At(double time)=>time.ToString("0.000",CultureInfo.InvariantCulture)+" s";
        List<double> Times(string annotation)=>events.Where(e=>Named(e,annotation)).Select(e=>e.Time).ToList();
        text.AppendLine(Starts(name,"BFCO_")?"  Read as a BFCO clip, by its file name.":"  Read as a BFCO clip, by its annotations.");
        bool numbered=Numbered.IsMatch(File(name)),range=Starts(name,"BFCO_RangeAttack"),chains=false;double opened=double.NegativeInfinity;
        var closers=Times("BFCO_DIY_EndLoop");
        foreach(bool power in new[]{false,true}){
            string kind=power?"power":"light",target=power?"BFCO_PowerAttack":range?"BFCO_RangeAttack":"BFCO_Attack",windowName=power?"BFCO_NextPowerWinStart":"BFCO_NextWinStart";
            var selectors=Selectors(events,power).ToList();var windows=Times(windowName);
            if(selectors.Count>0){
                // The pair that can chain: the first window, and the last selector at or before it that no
                // BFCO_DIY_EndLoop has closed again; failing that, the first selector.
                var pick=selectors[0];double? window=windows.Count>0?windows[0]:null;
                foreach(double w in windows){
                    var armed=selectors.Where(s=>s.Time<=w+1e-6&&!closers.Any(c=>c>s.Time+1e-6&&c<w-1e-6)).ToList();
                    if(armed.Count>0){pick=armed[^1];window=w;break;}
                }
                if(pick.Number is <1 or >20)text.AppendLine($"  Warning: BFCO_NextIs{(power?"Power":"")}Attack{pick.Number} is not an event BFCO registers (it has 1 to 20), so it does nothing.");
                else if(window is not {} at)text.AppendLine($"  Next {kind} attack: {target}{pick.Number}.hkx, but the clip has no {windowName}, and BFCO starts that transition only from one. This clip cannot chain into it.");
                else if(pick.Time>at+1e-6){chains=true;opened=Math.Max(opened,pick.Time);text.AppendLine($"  Next {kind} attack: {target}{pick.Number}.hkx. The input buffer opens at {At(pick.Time)}, after {windowName} ({At(at)}), so the transition can begin from {At(pick.Time)}.");}
                else{chains=true;opened=Math.Max(opened,at);text.AppendLine($"  Next {kind} attack: {target}{pick.Number}.hkx. The input buffer opens at {At(pick.Time)} and the transition can begin at {windowName}, {At(at)}.");}
                if(selectors.Select(s=>s.Number).Distinct().Count()>1)text.AppendLine($"  The clip has more than one BFCO_NextIs{(power?"Power":"")}AttackN; the one named above is the one in force when the window opens.");
            }else if(windows.Count>0&&numbered)text.AppendLine($"  Warning: {windowName} at {At(windows[0])}, but the clip has no BFCO_NextIs{(power?"Power":"")}AttackN, so that window leads to no numbered clip.");
        }
        if(events.Any(e=>e.Text.TrimStart().StartsWith("BFCO_DefAttackStage",StringComparison.OrdinalIgnoreCase))){chains=true;text.AppendLine("  BFCO_DefAttackStageN: the graph's default chain picks the next clip, with a window the graph times itself.");}
        bool mcoChain=events.Any(e=>Named(e,"MCO_WinOpen")||Named(e,"MCO_PowerWinOpen"));
        if(mcoChain)text.AppendLine("  The clip also carries MCO windows, which BFCO still runs.");
        if(!chains&&!mcoChain&&numbered)text.AppendLine("  No next-attack annotation with a window: this clip names no numbered light or power follow-up. The graph's own exits (dual-wield, sprint and directional attacks) still apply.");
        if(chains&&!double.IsNegativeInfinity(opened)){
            var close=closers.Where(c=>c>opened+1e-6).Select(c=>(double?)c).FirstOrDefault();
            text.AppendLine(close is {} closed?$"  The buffer and both windows close at BFCO_DIY_EndLoop, {At(closed)}.":"  No BFCO_DIY_EndLoop after that: the buffer and windows stay open to the end of the clip.");
        }
        var recovery=new[]{"BFCO_DIY_recovery","MCO_Recovery"}.Select(r=>(Name:r,Times:Times(r))).Where(r=>r.Times.Count>0).OrderBy(r=>r.Times[0]).FirstOrDefault();
        if(recovery.Times is {Count:>0} cancel)text.AppendLine($"  Movement cancels the attack from {recovery.Name}, {At(cancel[0])}.");
        if(Times("BFCO_DIY_Rcovery") is {Count:>0} typo)text.AppendLine($"  Warning: BFCO_DIY_Rcovery at {At(typo[0])} is not an event BFCO registers (BFCO_DIY_recovery is), so it does nothing.");
        if(IsUnbound(name))text.AppendLine(IsHoldLoop(name)?"  A hold loop: it is not bound to the attack speed and plays at 1.00x.":"  An NPC power attack: it is not bound to the attack speed and plays at 1.00x.");
        else if(clip.BfcoSpeedChanges.Count>0||clip.SpeedChanges.Count>0)text.AppendLine("  Speed: BFCO_AttackSpeed, or MCO_AttackSpeed while BFCO_AttackSpeed is exactly 1."+(KeepsBfcoSpeed(name)?" This clip keeps the BFCO_AttackSpeed the clip before it left.":" BFCO_AttackSpeed returns to 1 when this clip starts."));
        if(!Starts(name,"BFCO_SwimAttack")&&!IsHoldLoop(name))text.Append($"  In game the graph itself sends attackStop {(numbered?"0.06":"0.01")} s before the clip ends.");
        return text.ToString().TrimEnd();
    }
}
