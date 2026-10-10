using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SSEPreview;

// BFCO (the BFCO attack behavior framework). The facts here come from its Nemesis patch (1hm_behavior), the names in
// brackets being the patch objects that show them:
//   BFCO_NextIsAttackN / BFCO_NextIsPowerAttackN  open the input buffer and name the next clip by its number (#103).
//   BFCO_NextWinStart / BFCO_NextPowerWinStart    the earliest moment that transition may begin (#103).
//   BFCO_DIY_EndLoop                              closes the buffer and both windows; it is optional, and without it
//                                                 they stay open to the end of the clip (#103).
//   BFCO_DIY_recovery, MCO_Recovery               from here movement cancels the attack (#4802).
//   Clip speed is BFCO_AttackSpeedManual = BFCO_AttackSpeed, or MCO_AttackSpeed while BFCO_AttackSpeed is exactly 1
//   (#991). BFCO_AttackSpeed goes back to 1 on entering every attack state (#999), so it does not carry from one
//   clip of a combo to the next; MCO_AttackSpeed does. The hold loops BFCO_PowerAttackLoopN are not bound to it.
// BFCO still runs MCO's own annotations (MCO_WinOpen and the rest) for clips that carry them.
public static class Bfco
{
    static readonly Regex Selector=new(@"^BFCO_NextIs(Power)?Attack(\d+)$",RegexOptions.Compiled|RegexOptions.IgnoreCase);
    static bool Named(Annotation e,string name)=>e.Text.Trim().Equals(name,StringComparison.OrdinalIgnoreCase);
    static string File(string name)=>Path.GetFileName(name.Replace('\\','/'));
    // A clip the BFCO graph plays: named BFCO_*, or carrying one of BFCO's own chain annotations.
    public static bool IsClip(string name,IEnumerable<Annotation> events)=>File(name).StartsWith("BFCO_",StringComparison.OrdinalIgnoreCase)
        ||events.Any(e=>e.Text.TrimStart().StartsWith("BFCO_Next",StringComparison.OrdinalIgnoreCase)||e.Text.TrimStart().StartsWith("BFCO_DIY_",StringComparison.OrdinalIgnoreCase)||e.Text.TrimStart().StartsWith("BFCO_DefAttackStage",StringComparison.OrdinalIgnoreCase));
    public static bool IsHoldLoop(string name)=>File(name).StartsWith("BFCO_PowerAttackLoop",StringComparison.OrdinalIgnoreCase);
    // The clip number a selector names, with the time it fires. Null when the clip has none of that kind.
    public static (int Number,double Time)? Next(IEnumerable<Annotation> events,bool power)
    {
        foreach(var e in events.OrderBy(e=>e.Time))
            if(Selector.Match(e.Text.Trim()) is {Success:true} m&&m.Groups[1].Success==power&&int.TryParse(m.Groups[2].Value,NumberStyles.None,CultureInfo.InvariantCulture,out int number))return (number,e.Time);
        return null;
    }
    // The clip's speed over its own time as the BFCO graph computes it. carriedMco is MCO_AttackSpeed when the clip
    // starts; BFCO_AttackSpeed starts at 1. A hold loop plays at 1 throughout.
    public static List<(double Time,double Value)> SpeedChanges(AnimationClip clip,double carriedMco)
    {
        var merged=clip.SpeedChanges.Select(c=>(c.Time,c.Value,Bfco:false)).Concat(clip.BfcoSpeedChanges.Select(c=>(c.Time,c.Value,Bfco:true))).OrderBy(c=>c.Time).ToList();
        var result=new List<(double Time,double Value)>();double mco=carriedMco,bfco=1;
        for(int i=0;i<merged.Count;){
            double time=merged[i].Time;
            for(;i<merged.Count&&merged[i].Time-time<=1e-9;i++){if(merged[i].Bfco)bfco=merged[i].Value;else mco=merged[i].Value;}
            result.Add((time,Math.Abs(bfco-1)<1e-9?mco:bfco));
        }
        return result;
    }
    // MCO_AttackSpeed at a clip time, which is what the next clip of a chain inherits.
    public static double McoSpeedAt(AnimationClip clip,double carriedMco,double time)
    {
        double value=carriedMco;foreach(var c in clip.SpeedChanges){if(c.Time<=time+1e-9)value=c.Value;else break;}
        return value;
    }
    // The hand-over annotation for a window choice ("MCO_WinOpen" for the light window, "MCO_PowerWinOpen" for the
    // power one). A BFCO clip uses BFCO's window when it has one and MCO's otherwise; any other clip the reverse.
    public static (string? Annotation,List<double> Times) Window(string name,IReadOnlyList<Annotation> events,string window)
    {
        bool power=window.Equals("MCO_PowerWinOpen",StringComparison.OrdinalIgnoreCase)||window.Equals("BFCO_NextPowerWinStart",StringComparison.OrdinalIgnoreCase);
        bool light=window.Equals("MCO_WinOpen",StringComparison.OrdinalIgnoreCase)||window.Equals("BFCO_NextWinStart",StringComparison.OrdinalIgnoreCase);
        if(!power&&!light){var own=AnimationClip.WindowTimes(events,window);return (own.Count>0?window:null,own);}
        string mco=power?"MCO_PowerWinOpen":"MCO_WinOpen",bfco=power?"BFCO_NextPowerWinStart":"BFCO_NextWinStart";
        foreach(string candidate in IsClip(name,events)?new[]{bfco,mco}:new[]{mco,bfco}){
            var times=AnimationClip.WindowTimes(events,candidate);if(times.Count>0)return (candidate,times);
        }
        return (null,[]);
    }
    // What the Annotations tab says about a BFCO clip; empty for any other clip.
    public static string Describe(string name,AnimationClip clip)
    {
        var events=clip.Events;if(!IsClip(name,events))return "";
        var text=new StringBuilder("\n\nBFCO\n");
        string At(double time)=>time.ToString("0.000",CultureInfo.InvariantCulture)+" s";
        double? First(string annotation)=>events.Where(e=>Named(e,annotation)).Select(e=>(double?)e.Time).FirstOrDefault();
        text.AppendLine(File(name).StartsWith("BFCO_",StringComparison.OrdinalIgnoreCase)?"  Read as a BFCO clip, by its file name.":"  Read as a BFCO clip, by its annotations.");
        bool chains=false;
        foreach(bool power in new[]{false,true}){
            string kind=power?"power":"light",target=power?"BFCO_PowerAttack":"BFCO_Attack",windowName=power?"BFCO_NextPowerWinStart":"BFCO_NextWinStart";
            var next=Next(events,power);double? window=First(windowName);
            if(next is {} n){
                chains=true;
                text.AppendLine($"  Next {kind} attack: {target}{n.Number}.hkx. The input buffer opens at {At(n.Time)}"+(window is {} w?$" and the transition can begin at {windowName}, {At(w)}.":$". There is no {windowName}, so the transition can begin as soon as the attack is pressed."));
            }else if(window is {} w)text.AppendLine($"  Warning: {windowName} at {At(w)} has no BFCO_NextIs{(power?"Power":"")}AttackN before it, so that window leads to no clip.");
        }
        if(events.Any(e=>e.Text.TrimStart().StartsWith("BFCO_DefAttackStage",StringComparison.OrdinalIgnoreCase))){chains=true;text.AppendLine("  BFCO_DefAttackStageN: the graph's default chain picks the next clip, with a window the graph times itself.");}
        bool mcoChain=events.Any(e=>Named(e,"MCO_WinOpen")||Named(e,"MCO_PowerWinOpen"));
        if(mcoChain)text.AppendLine("  The clip also carries MCO windows, which BFCO still runs.");
        if(!chains&&!mcoChain&&!IsHoldLoop(name))text.AppendLine("  No next-attack annotation and no window: this clip cannot chain into another.");
        if(chains)text.AppendLine(First("BFCO_DIY_EndLoop") is {} close?$"  The buffer and both windows close at BFCO_DIY_EndLoop, {At(close)}.":"  No BFCO_DIY_EndLoop: the buffer and windows stay open to the end of the clip.");
        var recovery=new[]{"BFCO_DIY_recovery","MCO_Recovery"}.Select(r=>(Name:r,Time:First(r))).Where(r=>r.Time!=null).OrderBy(r=>r.Time).FirstOrDefault();
        if(recovery.Time is {} cancel)text.AppendLine($"  Movement cancels the attack from {recovery.Name}, {At(cancel)}.");
        if(First("BFCO_DIY_Rcovery") is {} typo)text.AppendLine($"  Warning: BFCO_DIY_Rcovery at {At(typo)} is not an event BFCO registers (BFCO_DIY_recovery is), so it does nothing.");
        if(IsHoldLoop(name))text.AppendLine("  A hold loop: it is not bound to the attack speed and plays at 1.00x.");
        else if(clip.BfcoSpeedChanges.Count>0||clip.SpeedChanges.Count>0)text.AppendLine("  Speed: BFCO_AttackSpeed, or MCO_AttackSpeed while BFCO_AttackSpeed is exactly 1. BFCO_AttackSpeed returns to 1 at the start of every clip.");
        text.Append("  In game the graph itself sends attackStop 0.06 s before the clip ends.");
        return text.ToString();
    }
}
