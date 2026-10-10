using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using FO4_Base_Library;

namespace SSEPreview;
public sealed record Annotation(double Time,string Text,int Track);
// Clip time against playback time for one clip. MCO binds every attack clip's playbackSpeed to the MCO_AttackSpeed
// graph variable (checked in the MCO behavior files), and each PIE.@SGVF|MCO_AttackSpeed|x annotation sets it, so
// the clip runs at x from that annotation until the next one. The value at the start is whatever the previous clip
// left (MCO resets it to 1 only outside attacks).
public sealed class SpeedMap
{
    // Breakpoints: clip time, the playback time it is reached at, and the speed from there on.
    public readonly List<(double Clip,double Play,double Speed)> Points=[];
    public SpeedMap(IEnumerable<(double Time,double Value)> changes,double startSpeed=1,double duration=double.PositiveInfinity)
    {
        Points.Add((0,0,startSpeed));
        foreach(var (time,value) in changes.OrderBy(c=>c.Time)){
            if(time>duration+1e-9)break;
            var last=Points[^1];double at=Math.Max(time,last.Clip);
            if(at-last.Clip<1e-9)Points[^1]=(last.Clip,last.Play,value);
            else Points.Add((at,last.Play+(at-last.Clip)/last.Speed,value));
        }
    }
    public double StartSpeed=>Points[0].Speed;
    public bool Uniform=>Points.All(p=>Math.Abs(p.Speed-1)<1e-9);
    public double ToPlay(double clip){var p=Points[0];foreach(var q in Points){if(q.Clip<=clip+1e-12)p=q;else break;}return p.Play+(clip-p.Clip)/p.Speed;}
    public double ToClip(double play){var p=Points[0];foreach(var q in Points){if(q.Play<=play+1e-12)p=q;else break;}return p.Clip+(play-p.Play)*p.Speed;}
    public double SpeedAt(double clip){double speed=Points[0].Speed;foreach(var q in Points){if(q.Clip<=clip+1e-12)speed=q.Speed;else break;}return speed;}
    public string Describe()=>string.Join(", ",Points.Select(p=>FormattableString.Invariant($"{p.Speed:0.00}x from {p.Clip:0.000} s")));
}
// One clip on a timeline. Start and Length are in playback time; Speed maps the clip's own time to playback time.
// BasePosition and BaseYaw are where the actor stands when the clip starts: the root motion accumulated by every clip
// before it. HandOver is the clip time at which the next clip takes over (an MCO window annotation, or the clip's
// end); Window names the annotation used, null when the clip plays to its end.
public sealed record ClipSegment(string Name,AnimationClip Clip,double Start,double Length,SpeedMap Speed,Vector3 BasePosition,float BaseYaw,double HandOver,string? Window=null)
{
    public double Local(double queueTime){double u=Math.Clamp(queueTime-Start,0,Length);return Math.Min(Clip.Duration,Speed.ToClip(u));}
    // Clip time after the hand-over, while this clip keeps playing under the next one's blend; holds the last frame.
    public double Continued(double queueTime){double u=Math.Max(0,queueTime-Start);return Math.Min(Clip.Duration,Speed.ToClip(u));}
    public double Global(double clipTime)=>Start+Speed.ToPlay(clipTime);
}
public sealed class AnimationClip
{
    public byte[] Bytes=[];
    public double Duration;
    public int Frames,Tracks;
    public List<Annotation> Events=[];
    public List<(double Time,Vector3 Value)> Translation=[];
    public List<(double Time,float Value)> Rotation=[];
    // Every PIE.@SGVF|MCO_AttackSpeed|x annotation, in clip time (1.0 included: it changes speed back).
    public List<(double Time,double Value)> SpeedChanges=[];
    // BFCO's own variable. Only the BFCO behavior reads it, and none is installed in the mod lists checked; MCO ignores it.
    public List<(double Time,double Value)> BfcoSpeedChanges=[];
    // weaponDraw moves the weapon from its holster node to the hand; weaponSheathe moves it back. Each entry is
    // the state after that annotation fires.
    public List<(double Time,bool Sheathed)> WeaponEvents=[];
    // Set on a timeline: the clips it plays in order. Events, weapon events and Duration are then in playback time.
    public List<ClipSegment>? Segments;
    // True for a queue the user built; false for a single clip played through its speed map.
    public bool Queued;
    // Seconds over which a queued clip's pose blends in from the last pose of the clip before it.
    public double BlendTime;
    public int SegmentIndex(double time){var list=Segments!;int index=0;for(int i=1;i<list.Count;i++){if(list[i].Start<=time+1e-9)index=i;else break;}return index;}
    // Clip time of the first annotation with this name (case and padding ignored), or null.
    public double? AnnotationTime(string name)=>Events.FirstOrDefault(e=>e.Text.Trim().Equals(name,StringComparison.OrdinalIgnoreCase))?.Time;
    // Every distinct time an annotation with this name sits at, earliest first.
    public List<double> AnnotationTimes(string name)=>WindowTimes(Events,name);
    public static List<double> WindowTimes(IEnumerable<Annotation> events,string name)
    {
        var times=new List<double>();
        foreach(var e in events.Where(e=>e.Text.Trim().Equals(name,StringComparison.OrdinalIgnoreCase)).OrderBy(e=>e.Time))
            if(times.Count==0||e.Time-times[^1]>1e-6)times.Add(e.Time);
        return times;
    }
    // Plays the clips one after another. Each clip's root motion starts from where the previous clip left the
    // actor, turned by the yaw accumulated so far, so travel and turns carry through the whole queue. With a window
    // annotation (MCO_WinOpen, MCO_PowerWinOpen) every clip but the last hands over at that annotation, as an MCO
    // combo does when the next attack is pressed in time; the clip's later annotations then never fire.
    // Each clip starts at the speed the previous clip had reached at its hand-over: MCO only resets MCO_AttackSpeed
    // to 1 outside the attack states, so it carries from one combo attack into the next until the clip sets its own.
    // windowPicks chooses, per clip, which occurrence of the window annotation hands over (0 = the first); a clip with
    // several MCO_WinOpen annotations can then hand over at a later one.
    public static AnimationClip Queue(IReadOnlyList<(string Name,AnimationClip Clip)> items,double blendTime,string? window=null,bool queued=true,IReadOnlyList<int>? windowPicks=null)
    {
        var queue=new AnimationClip{Segments=[],BlendTime=Math.Max(0,blendTime),Queued=queued};
        double start=0,carried=1;Vector3 position=Vector3.Zero;float yaw=0;
        for(int index=0;index<items.Count;index++){
            var (name,clip)=items[index];
            var speed=new SpeedMap(clip.SpeedChanges,carried,clip.Duration);
            double? opens=null;
            if(window!=null&&index<items.Count-1&&clip.AnnotationTimes(window) is {Count:>0} times)opens=times[Math.Clamp(windowPicks!=null&&index<windowPicks.Count?windowPicks[index]:0,0,times.Count-1)];
            double handOver=Math.Clamp(opens??clip.Duration,0,clip.Duration);
            double length=speed.ToPlay(handOver);carried=speed.SpeedAt(handOver);
            var segment=new ClipSegment(name,clip,start,length,speed,position,yaw,handOver,opens!=null?window:null);queue.Segments.Add(segment);
            foreach(var e in clip.Events)if(opens==null||e.Time<=handOver+1e-9)queue.Events.Add(new(segment.Global(e.Time),e.Text,e.Track));
            foreach(var e in clip.WeaponEvents)if(opens==null||e.Time<=handOver+1e-9)queue.WeaponEvents.Add((segment.Global(e.Time),e.Sheathed));
            var end=clip.Motion(handOver);
            position+=Vector3.Transform(end.Position,Matrix4x4.CreateRotationZ(yaw));yaw+=end.Yaw;
            start+=segment.Length;queue.Frames+=clip.Frames;queue.Tracks=Math.Max(queue.Tracks,clip.Tracks);
        }
        queue.Duration=start;queue.Events=queue.Events.OrderBy(e=>e.Time).ToList();queue.WeaponEvents=queue.WeaponEvents.OrderBy(e=>e.Time).ToList();
        if(items.Count>0)queue.Bytes=items[0].Clip.Bytes;
        foreach(var segment in queue.Segments)queue.SpeedChanges.AddRange(segment.Clip.SpeedChanges.Where(c=>c.Time<=segment.HandOver+1e-9).Select(c=>(segment.Global(c.Time),c.Value)));
        return queue;
    }
    // A single clip on its own: the clip itself when it never changes speed, otherwise a one-clip timeline so that
    // pose, root motion, annotations and effects all run on playback time.
    public static AnimationClip Timeline(string name,AnimationClip clip)=>clip.SpeedChanges.All(c=>Math.Abs(c.Value-1)<1e-9)?clip:Queue([(name,clip)],0,null,queued:false);
    public static List<(double Time,bool Sheathed)> ReadWeaponEvents(IEnumerable<Annotation> events)=>events.OrderBy(e=>e.Time)
        .Select(e=>(e.Time,Name:e.Text.Trim())).Where(e=>e.Name.Equals("weaponDraw",StringComparison.OrdinalIgnoreCase)||e.Name.Equals("weaponSheathe",StringComparison.OrdinalIgnoreCase))
        .Select(e=>(e.Time,e.Name.Equals("weaponSheathe",StringComparison.OrdinalIgnoreCase))).ToList();
    // Null when the clip has neither annotation. Before the first one the weapon is in the opposite state:
    // an equip clip starts sheathed, an unequip clip starts drawn.
    public bool? WeaponSheathedAt(double time)
    {
        if(WeaponEvents.Count==0)return null;
        bool state=!WeaponEvents[0].Sheathed;
        foreach(var e in WeaponEvents){if(e.Time<=time)state=e.Sheathed;else break;}
        return state;
    }
    // Every PIE.@SGVF|<variable>|value annotation with a positive value, in time order.
    public static List<(double Time,double Value)> ReadSpeedChanges(IEnumerable<Annotation> events,string variable="MCO_AttackSpeed")
    {
        var list=new List<(double Time,double Value)>();
        foreach(var e in events.OrderBy(e=>e.Time)){
            var parts=e.Text.Split('|');
            if(parts.Length<3||!parts[0].Trim().EndsWith("@SGVF",StringComparison.OrdinalIgnoreCase)||!parts[1].Trim().Equals(variable,StringComparison.OrdinalIgnoreCase))continue;
            if(!double.TryParse(parts[2].Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out var value)||!double.IsFinite(value)||value<=0)continue;
            list.Add((e.Time,value));
        }
        return list;
    }
    public static AnimationClip Load(byte[] bytes)
    {
        // Binary tagfiles are converted to an equivalent packfile first; Bytes keeps the converted form so pose import sees one format.
        bytes=HkxTagfile.EnsurePackfile(bytes);
        var pack=HkxPackfileParser_Class.Parse(bytes);
        var graph=HkxObjectGraphParser_Class.BuildGraph(pack);
        var anim=graph.Animaciones().FirstOrDefault() ?? throw new InvalidDataException("No supported animation in this HKX. Select a spline or lossless animation, not a behavior graph.");
        var clip=new AnimationClip {Bytes=bytes,Duration=anim.Animacion.Duration,Frames=anim.NumFrames,Tracks=anim.Animacion.NumberOfTransformTracks};
        int track=0, motionTrack=-1;
        foreach(var t in anim.Animacion.AnnotationTracks) {
            foreach(var e in t.Annotations) {
                string text=e.Text ?? "";clip.Events.Add(new(e.Time,text,track));
                var v=text.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(v.Length==4 && v[0]=="animmotion" && Try(v[1],out var x)&&Try(v[2],out var y)&&Try(v[3],out var z)) {
                    if(motionTrack<0)motionTrack=track;
                    if(track==motionTrack)clip.Translation.Add((e.Time,new(x,y,z)));
                }
                if(v.Length==2 && v[0]=="animrotation" && Try(v[1],out var yaw)) {
                    if(motionTrack<0)motionTrack=track;
                    if(track==motionTrack)clip.Rotation.Add((e.Time,yaw*MathF.PI/180));
                }
            }
            track++;
        }
        clip.Events=clip.Events.OrderBy(e=>e.Time).ToList();clip.SpeedChanges=ReadSpeedChanges(clip.Events);clip.BfcoSpeedChanges=ReadSpeedChanges(clip.Events,"BFCO_AttackSpeed");clip.WeaponEvents=ReadWeaponEvents(clip.Events);clip.Translation.Sort((a,b)=>a.Time.CompareTo(b.Time));clip.Rotation.Sort((a,b)=>a.Time.CompareTo(b.Time));
        return clip;
    }
    static bool Try(string s,out float v) => float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out v) && float.IsFinite(v);
    public (Vector3 Position,float Yaw) Motion(double time)
    {
        if(Segments is {Count:>0} list){
            var segment=list[SegmentIndex(time)];var local=segment.Clip.Motion(segment.Local(time));
            return (segment.BasePosition+Vector3.Transform(local.Position,Matrix4x4.CreateRotationZ(segment.BaseYaw)),segment.BaseYaw+local.Yaw);
        }
        Vector3 pos=Vector3.Zero;float yaw=0;
        for(int i=0;i<Translation.Count;i++) {var next=Translation[i];var prev=i==0?(0d,Vector3.Zero):Translation[i-1];if(time>=next.Time) {pos=next.Value;continue;}pos=Vector3.Lerp(prev.Item2,next.Value,(float)Math.Clamp((time-prev.Item1)/Math.Max(1e-9,next.Time-prev.Item1),0,1));break;}
        for(int i=0;i<Rotation.Count;i++) {var next=Rotation[i];var prev=i==0?(0d,0f):Rotation[i-1];if(time>=next.Time) {yaw=next.Value;continue;}var q=Quaternion.Slerp(Quaternion.CreateFromAxisAngle(Vector3.UnitZ,prev.Item2),Quaternion.CreateFromAxisAngle(Vector3.UnitZ,next.Value),(float)Math.Clamp((time-prev.Item1)/Math.Max(1e-9,next.Time-prev.Item1),0,1));yaw=2*MathF.Atan2(q.Z,q.W);break;}
        return(pos,yaw);
    }
}

// Source-frame stepping, playback time, and presentation frequency are separate quantities.
public sealed class PlaybackClock
{
    readonly Func<double> wallTime;double anchorTime,anchorWall;double speed=1;
    public PlaybackClock(Func<double>? timeSource=null){var watch=Stopwatch.StartNew();wallTime=timeSource??(()=>watch.Elapsed.TotalSeconds);}
    public bool Playing {get;private set;}
    public bool Loop {get;set;}=true;
    public double Duration {get;set;}=1;
    public double SourceFps {get;set;}=30;
    public double Speed {get=>speed;set {var t=Absolute;anchorTime=t;anchorWall=wallTime();speed=Math.Clamp(value,0.05,4);}}
    public double Absolute => anchorTime+(Playing?(wallTime()-anchorWall)*speed:0);
    public double Time => !Playing&&anchorTime==Duration?Duration:Normalize(Absolute,Duration,Loop);
    public static double Normalize(double absolute,double duration,bool loop) => duration<=0?0:loop?((absolute%duration)+duration)%duration:Math.Clamp(absolute,0,duration);
    public void Play() {if(Playing)return;anchorWall=wallTime();Playing=true;}
    public void Pause() {anchorTime=Absolute;Playing=false;}
    public void Seek(double seconds) {anchorTime=Math.Clamp(seconds,0,Duration);anchorWall=wallTime();}
    public void Step(int frames) {Pause();Seek(Time+frames/SourceFps);}
}
