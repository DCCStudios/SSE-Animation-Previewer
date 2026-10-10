using System.Globalization;
using System.Text.RegularExpressions;
using V3d=OpenTK.Mathematics.Vector3d;
using M3d=OpenTK.Mathematics.Matrix3d;

namespace SSEPreview;

// Precision (2.0.4) attack collisions rebuilt from a clip's Collision_* annotations: which capsules exist when, and
// where each one sits relative to its bone. The rules follow Precision's PrecisionHandler.cpp and AttackCollision.cpp.

// Precision's MCM settings that size and time the capsules. Defaults are Settings.h; a mounted Precision install
// overrides them from MCM\Config\Precision\settings.ini, then MCM\Settings\Precision.ini, as the plugin reads them.
public sealed class PrecisionSettings
{
    public float WeaponLengthMult=1.2f,WeaponCapsuleRadius=12f,MinWeaponLength=40f,MinWeaponRadius=1f;
    public float DefaultCollisionLifetime=.3f,DefaultCollisionLifetimePowerAttackMult=1.8f;
    public float PlayerAttackLengthMult=1f,PlayerAttackRadiusMult=1.5f;
    public bool RecoilPlayer=true,RecoilNpc=true,RemoveRecoilOnHitframe=true;
    public float RecoilCollisionLength=25f;
    public List<string> Sources {get;}=[];
    public const string DefaultsPath=@"MCM\Config\Precision\settings.ini",UserPath=@"MCM\Settings\Precision.ini";
    public static PrecisionSettings Load(Assets assets)
    {
        var settings=new PrecisionSettings();
        foreach(string path in new[]{DefaultsPath,UserPath}){
            if(!assets.Exists(path))continue;
            try{settings.Apply(assets.Text(path));settings.Sources.Add(assets.Source(path));}catch(Exception){ }
        }
        return settings;
    }
    public void Apply(string ini)
    {
        string section="";
        foreach(string raw in ini.Split('\n')){
            string line=raw.Trim();
            if(line.Length==0||line[0]==';'||line[0]=='#')continue;
            if(line[0]=='['){section=line.Trim('[',']').Trim();continue;}
            int equals=line.IndexOf('=');if(equals<0)continue;
            string key=line[..equals].Trim();string value=line[(equals+1)..].Split(';')[0].Trim();
            bool isFloat=float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out float f);
            bool flag=value=="1"||value.Equals("true",StringComparison.OrdinalIgnoreCase);
            switch(section.ToLowerInvariant()+"/"+key.ToLowerInvariant()){
                case "attackcollisions/fweaponlengthmult" when isFloat:WeaponLengthMult=f;break;
                case "attackcollisions/fweaponcapsuleradius" when isFloat:WeaponCapsuleRadius=f;break;
                case "attackcollisions/fminweaponlength" when isFloat:MinWeaponLength=f;break;
                case "attackcollisions/fdefaultcollisionlifetime" when isFloat:DefaultCollisionLifetime=f;break;
                case "attackcollisions/fdefaultcollisionlifetimepowerattackmult" when isFloat:DefaultCollisionLifetimePowerAttackMult=f;break;
                case "attackcollisions/fplayerattacklengthmult" when isFloat:PlayerAttackLengthMult=f;break;
                case "attackcollisions/fplayerattackradiusmult" when isFloat:PlayerAttackRadiusMult=f;break;
                case "recoil/brecoilplayer":RecoilPlayer=flag;break;
                case "recoil/brecoilnpc":RecoilNpc=flag;break;
                case "recoil/bremoverecoilonhitframe":RemoveRecoilOnHitframe=flag;break;
                case "recoil/frecoilcollisionlength" when isFloat:RecoilCollisionLength=f;break;
            }
        }
    }
    public string Describe(bool player)=>FormattableString.Invariant(
        $"fWeaponLengthMult {WeaponLengthMult:0.###}, fWeaponCapsuleRadius {WeaponCapsuleRadius:0.###}, fMinWeaponLength {MinWeaponLength:0.###}, ")
        +(player?FormattableString.Invariant($"fPlayerAttackLengthMult {PlayerAttackLengthMult:0.###}, fPlayerAttackRadiusMult {PlayerAttackRadiusMult:0.###}, "):"")
        +FormattableString.Invariant($"fDefaultCollisionLifetime {DefaultCollisionLifetime:0.###} s, recoil {((player?RecoilPlayer:RecoilNpc)?FormattableString.Invariant($"on ({RecoilCollisionLength:0.###} units)"):"off")}")
        +(Sources.Count==0?" (Precision defaults; no MCM ini mounted)":" from "+string.Join(" + ",Sources));
}

// One parsed Collision_Add / Collision_Remove payload.
public sealed class PrecisionDefinition
{
    public string Node="";public byte? Id;
    public bool NoRecoil,NoTrail,TrailUseTrueLength,WeaponTip;
    public float? DamageMult,Duration,Radius,RadiusMult,Length,LengthMult,Scale;
    public V3d? Rotation,Translation,GroundShake;
    public bool HasTransform=>Rotation!=null||Translation!=null||Scale!=null;
    public bool IsWeapon=>Node is "WEAPON" or "SHIELD";
    public List<string> Ignored {get;}=[];

    static readonly Regex Number=new(@"^-?(\d+(\.\d*)?|\.\d+)([eE][-+]?\d+)?",RegexOptions.Compiled);
    // std::from_chars: a number at the very start of the text, no sign other than '-', no leading space.
    static bool Float(string text,out float value)
    {
        var m=Number.Match(text);value=0;
        return m.Success&&float.TryParse(m.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out value);
    }
    static string Inside(string parameter)
    {
        int start=parameter.IndexOf('(')+1;int end=parameter.IndexOf(')');
        if(end<0)end=parameter.Length;
        return end>start?parameter[start..end]:"";
    }
    static string After(string parameter){int start=parameter.IndexOf('(');return start<0?parameter:parameter[(start+1)..];}
    static bool Point(string parameter,out V3d point)
    {
        point=default;var parts=Inside(parameter).Split(',');
        if(parts.Length<3)return false;
        if(!Float(parts[0].TrimStart(' '),out float x)||!Float(parts[1].TrimStart(' '),out float y)||!Float(parts[2].TrimStart(' '),out float z))return false;
        point=new V3d(x,y,z);return true;
    }
    // Precision tokenizes the payload on '|' and matches each parameter name (before '(') without regard to case.
    // A parameter that fails to parse rejects the whole event, as the plugin does.
    public static PrecisionDefinition? Parse(string payload,out string? error)
    {
        var d=new PrecisionDefinition();error=null;
        var parameters=payload.Split('|',StringSplitOptions.RemoveEmptyEntries);
        if(parameters.Length==0){error="empty payload";return null;}
        foreach(string parameter in parameters){
            int nameEnd=parameter.IndexOf('(');string name=(nameEnd<0?parameter:parameter[..nameEnd]).ToLowerInvariant();
            bool ok=true;float f;V3d p;
            switch(name){
                case "node":d.Node=Inside(parameter);ok=d.Node.Length>0;break;
                case "id":ok=Float(After(parameter),out f)&&Regex.IsMatch(After(parameter),@"^\d")&&f>=0&&f<=255;if(ok)d.Id=(byte)Math.Floor(f);break;
                case "norecoil":d.NoRecoil=true;break;
                case "notrail":d.NoTrail=true;break;
                case "trailusetruelength":d.TrailUseTrueLength=true;break;
                case "weapontip":d.WeaponTip=true;break;
                case "damagemult":ok=Float(After(parameter),out f);d.DamageMult=f;break;
                case "duration":ok=Float(After(parameter),out f);d.Duration=f;break;
                case "radius":ok=Float(After(parameter),out f);d.Radius=f;break;
                case "radiusmult":ok=Float(After(parameter),out f);d.RadiusMult=f;break;
                case "length":ok=Float(After(parameter),out f);d.Length=f;break;
                case "lengthmult":ok=Float(After(parameter),out f);d.LengthMult=f;break;
                case "rotation":ok=Point(parameter,out p);d.Rotation=p;break;
                case "translation":ok=Point(parameter,out p);d.Translation=p;break;
                case "scale":ok=Float(After(parameter),out f);d.Scale=f;break;
                case "groundshake":ok=Point(parameter,out p);d.GroundShake=p;break;
                case "traillifetimemult" or "trailbasecoloroverride" or "trailbasecolorscalemult" or "trailmeshoverride":d.Ignored.Add(parameter);break;
                default:d.Ignored.Add(parameter+" (not a Precision parameter)");break;
            }
            if(!ok){error=$"{parameter} could not be parsed";return null;}
        }
        return d;
    }
    public string Summary()
    {
        var parts=new List<string>{Node.Length>0?Node:"(no node)"};
        if(Id!=null)parts.Add($"ID {Id}");
        string F(float v)=>v.ToString("0.###",CultureInfo.InvariantCulture);
        if(Duration!=null)parts.Add($"Duration {F(Duration.Value)}");
        if(Length!=null)parts.Add($"Length {F(Length.Value)}");if(LengthMult!=null)parts.Add($"LengthMult {F(LengthMult.Value)}");
        if(Radius!=null)parts.Add($"Radius {F(Radius.Value)}");if(RadiusMult!=null)parts.Add($"RadiusMult {F(RadiusMult.Value)}");
        if(Scale!=null)parts.Add($"Scale {F(Scale.Value)}");
        if(Rotation is {} r)parts.Add(FormattableString.Invariant($"Rotation ({r.X:0.##}, {r.Y:0.##}, {r.Z:0.##})"));
        if(Translation is {} t)parts.Add(FormattableString.Invariant($"Translation ({t.X:0.##}, {t.Y:0.##}, {t.Z:0.##})"));
        if(WeaponTip)parts.Add("WeaponTip");if(NoRecoil)parts.Add("NoRecoil");
        return string.Join(", ",parts);
    }
}

public sealed class PrecisionCollision(PrecisionDefinition definition,double start,string source)
{
    public PrecisionDefinition Definition {get;}=definition;
    public double Start {get;}=start;
    public string Source {get;}=source;
    public double End=double.PositiveInfinity;
    public string EndReason="still active when the clip ends";
    // The recoil capsule Precision adds beside a weapon collision; it goes with the collision or at the first hitFrame.
    public bool Recoil;public double RecoilEnd=double.PositiveInfinity;
    public bool ActiveAt(double time)=>time>=Start-1e-9&&time<End-1e-9;
    public bool RecoilAt(double time)=>Recoil&&ActiveAt(time)&&time<RecoilEnd-1e-9;
}

public sealed class PrecisionTimeline
{
    public List<PrecisionCollision> Collisions {get;}=[];
    public List<string> Notes {get;}=[];
    public bool HasAnnotations;
    static readonly string[] EndTags=["collision_attackend","attackstop","staggerstart","collision_cancel"];
    public static bool IsPrecisionEvent(string text)=>text.TrimStart().StartsWith("Collision_",StringComparison.OrdinalIgnoreCase);

    // Replays the annotations through Precision's event handler. nodeExists says whether the actor has the bone
    // (Precision drops a collision whose node is missing); powerAttack says whether the attack playing at a time is a
    // power attack, which lengthens the Duration(0) default.
    public static PrecisionTimeline Build(IEnumerable<Annotation> events,PrecisionSettings settings,bool player,Func<double,bool> powerAttack,Func<string,bool> nodeExists)
    {
        var timeline=new PrecisionTimeline();
        var active=new List<PrecisionCollision?>();bool started=false;
        // Why a Collision_Add or Remove is ignored: the attack was never opened, or an earlier event closed it.
        string? closedBy=null;
        string NotStarted()=>closedBy==null?"ignored, no Collision_AttackStart before it":$"ignored: {closedBy} closed the attack and no Collision_AttackStart follows it (Precision only adds collisions between an AttackStart and the next AttackEnd, Cancel, attackStop or staggerStart)";
        string T(double t)=>t.ToString("0.0000",CultureInfo.InvariantCulture);
        void Expire(double time){
            for(int i=active.Count-1;i>=0;i--)if(active[i] is {} c&&c.End<=time+1e-9)active.RemoveAt(i);
        }
        void Finish(PrecisionCollision c,double time,string reason){if(time<c.End){c.End=time;c.EndReason=reason;}}
        foreach(var e in events.OrderBy(e=>e.Time)){
            string text=e.Text.Trim();int dot=text.IndexOf('.');
            string tag=(dot<0?text:text[..dot]).ToLowerInvariant();string payload=dot<0?"":text[(dot+1)..];
            Expire(e.Time);
            if(tag.StartsWith("collision_"))timeline.HasAnnotations=true;
            switch(tag){
                case "collision_attackstart" or "collision_start":started=true;closedBy=null;break;
                case "collision_add":{
                    if(!started){timeline.Notes.Add($"{T(e.Time)} {text}: {NotStarted()}");break;}
                    var d=PrecisionDefinition.Parse(payload,out string? error);
                    if(d==null){timeline.Notes.Add($"{T(e.Time)} {text}: rejected by Precision, {error}");break;}
                    if(d.Node.Length==0){timeline.Notes.Add($"{T(e.Time)} {text}: rejected by Precision, Node() is required");break;}
                    if(!nodeExists(d.Node)){timeline.Notes.Add($"{T(e.Time)} {text}: the skeleton has no node {d.Node}, so Precision adds nothing");break;}
                    foreach(string ignored in d.Ignored)timeline.Notes.Add($"{T(e.Time)} {ignored}: no visual in this preview");
                    if(d.GroundShake!=null)timeline.Notes.Add($"{T(e.Time)} GroundShake: camera shake on hit, not drawn");
                    if(d.DamageMult!=null)timeline.Notes.Add($"{T(e.Time)} DamageMult: damage only, not drawn");
                    var c=new PrecisionCollision(d,e.Time,text);
                    if(d.Duration is float duration){
                        // Duration(0) means the MCM default lifetime; weaponSpeedMult is taken as 1.
                        bool power=powerAttack(e.Time);
                        double life=duration==0?settings.DefaultCollisionLifetime*(power?settings.DefaultCollisionLifetimePowerAttackMult:1):duration;
                        c.End=e.Time+life;c.EndReason=FormattableString.Invariant($"Duration({duration:0.###}) ran out")+(duration==0?FormattableString.Invariant($" (default {life:0.###} s{(power?", power attack":"")})"):"");
                    }
                    c.Recoil=d.IsWeapon&&!d.WeaponTip&&!d.NoRecoil&&(player?settings.RecoilPlayer:settings.RecoilNpc);
                    timeline.Collisions.Add(c);active.Add(c);
                    break;
                }
                case "collision_remove":{
                    if(!started){timeline.Notes.Add($"{T(e.Time)} {text}: {NotStarted()}");break;}
                    var d=PrecisionDefinition.Parse(payload,out string? error);
                    if(d==null){timeline.Notes.Add($"{T(e.Time)} {text}: rejected by Precision, {error}");break;}
                    if(d.Node.Length==0&&d.Id==null){timeline.Notes.Add($"{T(e.Time)} {text}: rejected by Precision, needs Node() or ID()");break;}
                    if(active.Count==0){timeline.Notes.Add($"{T(e.Time)} {text}: nothing to remove");break;}
                    if(d.Id is byte id){
                        // std::vector::erase(std::remove_if(...)) with one iterator: remove_if packs the survivors to the
                        // front, then a single element is erased. Only one matching collision is guaranteed to go.
                        bool Match(PrecisionCollision? x)=>x==null||x.Definition.Id==id;
                        int first=active.FindIndex(x=>Match(x));
                        if(first<0){timeline.Notes.Add($"{T(e.Time)} {text}: no collision with ID {id}; Precision then erases at end(), which is undefined behaviour");break;}
                        var removed=new List<PrecisionCollision>();if(active[first] is {} overwritten)removed.Add(overwritten);
                        for(int i=first+1;i<active.Count;i++){
                            if(Match(active[i]))continue;
                            if(first!=i&&active[first] is {} lost&&!removed.Contains(lost))removed.Add(lost);
                            active[first]=active[i];active[i]=null;first++;
                        }
                        if(first<active.Count){if(active[first] is {} erased&&!removed.Contains(erased))removed.Add(erased);active.RemoveAt(first);}
                        // Collisions moved over by remove_if are gone; the erased slot is gone. Matching ones left behind stay.
                        var survivors=active.Where(x=>x!=null).ToHashSet();
                        removed.RemoveAll(survivors.Contains);
                        foreach(var x in removed)Finish(x,e.Time,$"removed by {text}");
                        active.RemoveAll(x=>x==null);
                        int left=active.Count(x=>x!.Definition.Id==id);
                        if(left>0)timeline.Notes.Add($"{T(e.Time)} {text}: Precision 2.0.4 erases a single entry here (vector::erase with one iterator), so {left} other collision(s) with ID {id} stay active");
                    }else{
                        // The first collision whose node name matches exactly (std::string, case sensitive).
                        var hit=active.FirstOrDefault(x=>x!.Definition.Node==d.Node);
                        if(hit==null){timeline.Notes.Add($"{T(e.Time)} {text}: no active collision on {d.Node} (node names are case sensitive)");break;}
                        Finish(hit,e.Time,$"removed by {text}");active.Remove(hit);
                    }
                    break;
                }
                case "collision_cleartargets":
                    if(started)timeline.Notes.Add($"{T(e.Time)} {text}: clears the hit list so targets can be hit again; nothing to draw");
                    break;
                case "hitframe":{
                    // Precision removes the recoil capsule of the first collision that still has one.
                    if(!settings.RemoveRecoilOnHitframe||!(player?settings.RecoilPlayer:settings.RecoilNpc))break;
                    var withRecoil=active.FirstOrDefault(x=>x!.Recoil&&x.RecoilEnd>e.Time);
                    if(withRecoil!=null)withRecoil.RecoilEnd=e.Time;
                    break;
                }
                default:
                    if(EndTags.Contains(tag)){
                        foreach(var x in active)Finish(x!,e.Time,$"removed by {text}");
                        if(started)closedBy=$"{text} at {T(e.Time)} s";
                        active.Clear();started=false;
                    }else if(tag.StartsWith("collision_"))timeline.Notes.Add($"{T(e.Time)} {text}: not a Precision event");
                    break;
            }
        }
        foreach(var c in timeline.Collisions)if(c.RecoilEnd>c.End)c.RecoilEnd=c.End;
        return timeline;
    }
}

// Capsule geometry. Precision builds a Havok capsule (vertex A, vertex B, radius) in a new node parented to the bone,
// with the payload's Rotation and Translation as that node's local transform. Everything here is in game units in the
// attack node's frame; NiMatrix3 entries are [row, column] and act on column vectors, as in the game.
public static class PrecisionGeometry
{
    public const double HavokWorldScale=0.0142875;
    // Hand capsule Precision falls back to when the bone has no capsule collision of its own (Havok units).
    public static readonly V3d FallbackA=new(.002939,.003175,.054681),FallbackB=new(.001875,.003175,.054681);
    public const double FallbackRadius=.068565;
    // NiMatrix3::SetEulerAnglesXYZ.
    public static M3d EulerXYZ(double x,double y,double z)
    {
        double cx=Math.Cos(x),sx=Math.Sin(x),cy=Math.Cos(y),sy=Math.Sin(y),cz=Math.Cos(z),sz=Math.Sin(z);
        return new M3d(cy*cz,cy*sz,-sy,
                       sx*sy*cz-cx*sz,sx*sy*sz+cx*cz,sx*cy,
                       cx*sy*cz+sx*sz,cx*sy*sz-sx*cz,cx*cy);
    }
    // Weapon capsules lie along the attack node's X axis; this turns X onto the weapon bone's Y (the blade).
    public static readonly M3d WeaponRotation=EulerXYZ(0,Math.PI/2,-Math.PI/2);
    public static V3d Apply(M3d m,V3d v)=>new(V3d.Dot(m.Row0,v),V3d.Dot(m.Row1,v),V3d.Dot(m.Row2,v));

    public readonly record struct Capsule(V3d A,V3d B,double Radius,V3d Translate,M3d Rotate,string Detail);

    public static double WeaponAttackLength(PrecisionSettings s,PrecisionDefinition d,double meshLength,bool player,out string detail)
    {
        double lengthMult=(d.LengthMult??1)*(d.Scale??1);
        double mult=s.WeaponLengthMult*(player?s.PlayerAttackLengthMult:1)*lengthMult;
        double length=d.Length??meshLength;
        detail=FormattableString.Invariant($"{(d.Length!=null?"Length()":meshLength>0?"weapon mesh":"no weapon mesh")} {length:0.#} x {mult:0.###}");
        return length>0?Math.Max(length*mult,s.MinWeaponLength*mult):s.MinWeaponLength*mult;
    }
    public static double WeaponAttackRadius(PrecisionSettings s,PrecisionDefinition d,bool player)
    {
        double radiusMult=(d.RadiusMult??1)*(d.Scale??1);
        double radius=d.Radius??s.WeaponCapsuleRadius;
        return Math.Max(radius*(player?s.PlayerAttackRadiusMult:1)*radiusMult,s.MinWeaponRadius);
    }
    // WEAPON / SHIELD: a capsule from the node origin along the blade, its length from the weapon mesh bounds.
    public static Capsule Weapon(PrecisionSettings s,PrecisionDefinition d,double meshLength,bool player)
    {
        double length=WeaponAttackLength(s,d,meshLength,player,out string how),radius=WeaponAttackRadius(s,d,player);
        var translate=d.Translation??V3d.Zero;
        // WeaponTip moves the attack node out along the bone's Y axis by length minus radius.
        if(d.WeaponTip)translate+=new V3d(0,length-radius,0);
        var rotate=WeaponRotation*Rotation(d);
        return new(new V3d(length,0,0),V3d.Zero,radius,translate,rotate,FormattableString.Invariant($"length {length:0.#} ({how}), radius {radius:0.#}"));
    }
    public static Capsule Recoil(PrecisionSettings s,PrecisionDefinition d,double radius)
    {
        return new(new V3d(s.RecoilCollisionLength,0,0),V3d.Zero,radius,d.Translation??V3d.Zero,WeaponRotation*Rotation(d),FormattableString.Invariant($"recoil length {s.RecoilCollisionLength:0.#}"));
    }
    // Any other node: the bone's own capsule collision (or the hand fallback), stretched by the length settings.
    // As in GetNodeAttackDimensions, Radius() only rescales the length; the drawn radius stays the bone's.
    public static Capsule Node(PrecisionSettings s,PrecisionDefinition d,(V3d A,V3d B,double Radius)? boneCapsule,bool player)
    {
        var (a,b,r)=boneCapsule??(FallbackA,FallbackB,FallbackRadius);
        double lengthMult=(d.LengthMult??1)*(d.Scale??1),radiusMult=(d.RadiusMult??1)*(d.Scale??1);
        double radius=d.Radius is float overrideRadius?overrideRadius*HavokWorldScale:r;
        double originalLength=Math.Max(radius*2,0);
        double length=d.Length is float overrideLength?overrideLength*HavokWorldScale:originalLength;
        if(length>0&&originalLength>0)lengthMult*=length/originalLength;
        lengthMult*=s.WeaponLengthMult*(player?s.PlayerAttackLengthMult:1);
        radiusMult*=player?s.PlayerAttackRadiusMult:1;
        double inverse=1/HavokWorldScale;
        return new(a*lengthMult*inverse,b*inverse,r*radiusMult*inverse,d.Translation??V3d.Zero,Rotation(d),
            (boneCapsule==null?"hand fallback capsule":"bone capsule")+FormattableString.Invariant($", radius {r*radiusMult*inverse:0.#}, length x {lengthMult:0.###}"));
    }
    static M3d Rotation(PrecisionDefinition d)=>d.Rotation is {} r?EulerXYZ(r.X*Math.PI/180,r.Y*Math.PI/180,r.Z*Math.PI/180):M3d.Identity;

    // PrecisionHandler::GetWeaponMeshLength: the merged bounding sphere of the weapon's visible geometry, in the
    // weapon node's frame; length = radius + |center.y|.
    public static double MeshLength(IEnumerable<(V3d Center,double Radius)> spheres){var bound=MergedBound(spheres);return bound.Any?bound.Radius+Math.Abs(bound.Center.Y):0;}
    public static (bool Any,V3d Center,double Radius) MergedBound(IEnumerable<(V3d Center,double Radius)> spheres)
    {
        bool any=false;V3d center=default;double radius=0;
        foreach(var (c,r) in spheres){
            if(!any){center=c;radius=r;any=true;continue;}
            // NiBound::Combine: the smallest sphere around both.
            double distance=(c-center).Length;
            if(distance+r<=radius)continue;
            if(distance+radius<=r){center=c;radius=r;continue;}
            double merged=(distance+radius+r)/2;
            center+=(c-center)*((merged-radius)/distance);radius=merged;
        }
        return (any,center,radius);
    }
}
