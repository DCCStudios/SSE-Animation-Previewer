using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SSEPreview;
public sealed record EffectCue(double Time,string Model,float Speed,float Gravity,double Lifetime,float Scale,Vector3 Offset,string Origin,int Biped=-1,float FadeDuration=0,int ProjectileType=0,bool FollowActor=false,uint SpawnFlags=0,bool TemporaryParticle=false,string? AppliedSpell=null,double RemovedAt=double.PositiveInfinity,float Elevation=0,float Heading=0,bool WorldSpace=false,bool ArtLifecycle=false,double? PlacementTime=null,string? AttachNode=null,Matrix4x4? Rotation=null,bool BladeSpace=false)
{
    public Matrix4x4 LocalPlacement(double age)
    {
        // PIE.@FF7VFX: a fixed rotation and offset in the actor root's (or the blade's) space.
        if(Rotation is {} rotation)return Matrix4x4.CreateScale(Scale)*rotation*Matrix4x4.CreateTranslation(Offset);
        float t=(float)Math.Max(0,Math.Min(age,Lifetime));
        var yaw=Matrix4x4.CreateRotationZ(Heading);
        return Matrix4x4.CreateScale(Scale)*Matrix4x4.CreateRotationX(ProjectileType!=0?ProjectileMotion.ModelElevation(Speed,Gravity,ProjectileType,t,Elevation):0)*yaw*
            Matrix4x4.CreateTranslation(Offset+Vector3.Transform(ProjectileMotion.Displacement(Speed,Gravity,ProjectileType,t,Elevation),yaw));
    }
    public bool ParticlesVisible(double age)=>age>=0&&Time+age<RemovedAt&&(!TemporaryParticle||age<=Lifetime);
}
public sealed record Trigger(string Name,string Instruction,string Source,string Conditions="",string[]? AdditionalInstructions=null)
{
    public IEnumerable<string> Instructions=>new[]{Instruction}.Concat(AdditionalInstructions??[]);
}
public sealed record TimedTrigger(Trigger Trigger,double Time);

public sealed class Effects
{
    readonly Assets assets;readonly Records records;
    sealed record Application(string Key,double Time,bool Remove,List<EffectCue> Cues,double Duration=double.PositiveInfinity);
    readonly List<Application> applications=[];
    public List<Trigger> Triggers {get;}=[];
    public float ProjectileElevation {get;set;}
    public List<string> Diagnostics {get;}=[];
    readonly List<string> configDiagnostics=[];
    readonly Dictionary<string,List<string>> aliases=new(StringComparer.Ordinal);
    readonly Dictionary<string,List<Trigger>> animationTriggers=new(StringComparer.Ordinal);
    // PIE.@FF7VFXEND annotations of the clip being built: when a weapon effect stops being spawned again.
    readonly List<(double Time,string Model)> vfxEnds=[];
    // The FF7 Limit Gauge plugin keeps a weapon effect going for at most this long without an END.
    public const double Ff7VfxMaximum=12;
    static string VfxModel(string field){var path=Assets.Normalize(field.Trim());return path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase)?path:@"meshes\"+path;}
    public Effects(Assets a,Records r) {assets=a;records=r;}
    public void ReadConfigs()
    {
        Triggers.Clear();Diagnostics.Clear();configDiagnostics.Clear();aliases.Clear();animationTriggers.Clear();
        var keyBindings=new Dictionary<(string Section,string Key),List<(string Form,string Plugin,string Source,string Text)>>();
        foreach(var path in assets.Paths.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)) {
            if(path.Contains(@"PayloadInterpreter\Config\",StringComparison.OrdinalIgnoreCase)&&path.EndsWith(".ini",StringComparison.OrdinalIgnoreCase)) {
                foreach(var line in assets.Text(path).Split('\n')) {
                    var l=line.Trim();if(!l.StartsWith('$'))continue;int eq=l.IndexOf('=');if(eq<0)continue;
                    var key=l[..eq].Trim();var value=l[(eq+1)..].Trim();
                    if(!aliases.TryGetValue(key,out var list))aliases[key]=list=[];
                    list.Add(value);Triggers.Add(new(key,value,path));
                }
            }
            if(path.Contains(@"dtryKeyUtil\config\custom\",StringComparison.OrdinalIgnoreCase)&&path.EndsWith(".ini",StringComparison.OrdinalIgnoreCase)) {
                string section="";
                foreach(var line in assets.Text(path).Split('\n')) {
                    var l=line.Trim();if(l.StartsWith('[')){int close=l.IndexOf(']');section=close>0?l[1..close].Trim():"";continue;}
                    if(l.StartsWith(';')||l.StartsWith('#'))continue;int eq=l.IndexOf('=');if(eq<0)continue;
                    var v=l[(eq+1)..].Trim().Split('|');if(v.Length<3)continue;
                                        if(!new[]{"KeyTrace_userEvent","KeyTrace_EventID_Keyboard","KeyTrace_EventID_Mouse","KeyTrace_EventID_GamePad"}.Contains(section,StringComparer.OrdinalIgnoreCase))continue;
                    string input=v[0].Trim();
                    if(!section.Equals("KeyTrace_userEvent",StringComparison.OrdinalIgnoreCase)){
                        if(!uint.TryParse(input,NumberStyles.Integer,CultureInfo.InvariantCulture,out uint id)){
                            Diagnostics.Add($"{path}: invalid dtry input ID {input}");continue;
                        }
                        input=id.ToString(CultureInfo.InvariantCulture);
                    }
                    var groupKey=(section.ToUpperInvariant(),input);
                    if(!keyBindings.TryGetValue(groupKey,out var bindings))keyBindings[groupKey]=bindings=[];
                    bindings.Add((v[1].Trim(),v[2].Trim(),path,$"{path}\n[{section}]\n{l}"));
                    for(int action=0;action<2;action++){
                        string state=action==0?"Press":"Release",op=action==0?"@APPLYSPELL":"@UNAPPLYSPELL";
                        Triggers.Add(new($"Key {state} · {l[..eq].Trim()} ({section}: {v[0]})",$"{op}|{v[1].Trim()}|{v[2].Trim()}",path,
                            $"[{section}]\n{l}\n\n{state} {(action==0?"adds":"removes")} this spell. Preview press and release at selected timeline times; no system key is sent."));
                    }
                }
            }
            if(path.EndsWith(".toml",StringComparison.OrdinalIgnoreCase)&&path.Contains("DynamicAnimationCasting",StringComparison.OrdinalIgnoreCase)) ReadDac(path);
        }
        AddKeyGroups(keyBindings);
        configDiagnostics.AddRange(Diagnostics.Distinct());
    }
    void AddKeyGroups(Dictionary<(string Section,string Key),List<(string Form,string Plugin,string Source,string Text)>> groups)
    {
        foreach(var (key,bindings) in groups.Where(g=>g.Value.Count>1)){
            string device=key.Section=="KEYTRACE_USEREVENT"?"User event":key.Section["KEYTRACE_EVENTID_".Length..].ToLowerInvariant();
            for(int action=0;action<2;action++){
                string state=action==0?"Press":"Release",op=action==0?"@APPLYSPELL":"@UNAPPLYSPELL";
                var commands=bindings.Select(b=>$"{op}|{b.Form}|{b.Plugin}").ToArray();
                Triggers.Insert(action,new Trigger($"Key {state} · {device} {key.Key} · all {bindings.Count} bindings",commands[0],
                    string.Join("\n",bindings.Select(b=>b.Source).Distinct(StringComparer.OrdinalIgnoreCase)),
                    $"{state} all bindings for this device/input together. User events and device IDs remain separate; the previewer does not infer your game control mappings.\n\n"+string.Join("\n\n",bindings.Select(b=>b.Text)),commands.Skip(1).ToArray()));
            }
        }
    }
    void ReadDac(string path)
    {
        // Preserve complete conditions for inspection. Never claim to evaluate a Skyrim actor's state.
        foreach(var block in Regex.Split(assets.Text(path),@"(?m)^\s*\[\[event\]\]\s*$").Skip(1)) {
            string Value(string name)=>Regex.Match(block,@"(?m)^\s*"+name+@"\s*=\s*[""']([^""']*)[""']").Groups[1].Value;
            var ev=Value("AnimationEvent");var esp=Value("SpellEspName");
            var ids=Regex.Match(block,@"SpellFormIDs\s*=\s*\[([^\]]*)\]",RegexOptions.Singleline).Groups[1].Value;
            foreach(Match m in Regex.Matches(ids,@"[""']([^""']+)[""']|(0x[0-9a-fA-F]+|\d+)")) {
                string id=m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Value,plugin=esp;
                if(id.Contains('|')) {var sp=id.Split('|');plugin=sp[0];id=sp[1];}
                if(id.StartsWith('@')) {Diagnostics.Add($"{path}: dynamic equipped/favorite spell {id} requires actor context.");continue;}
                if(!TryId(id,out var form,false))continue;
                var t=new Trigger(ev,$"@CAST|0x{form:X}|{plugin}|1|1|0|0|0|0|0|0|0",path,block.Trim());
                Triggers.Add(t);if(!animationTriggers.TryGetValue(ev,out var list))animationTriggers[ev]=list=[];list.Add(t);
            }
        }
    }
    void BeginPreview()
    {
        Diagnostics.Clear();Diagnostics.AddRange(configDiagnostics);applications.Clear();vfxEnds.Clear();
    }
    public List<EffectCue> Build(AnimationClip clip,bool includeConditionalDac,IEnumerable<TimedTrigger>? manual=null,IEnumerable<TimedReferenceEffect>? references=null)
    {
        var result=new List<EffectCue>();BeginPreview();
        foreach(var ev in clip.Events)if(ev.Text.StartsWith("PIE.@FF7VFXEND|",StringComparison.Ordinal))vfxEnds.Add((ev.Time,VfxModel(ev.Text.Split('|')[1])));
        foreach(var ev in clip.Events) {
            if(ev.Text.StartsWith("PIE.",StringComparison.Ordinal)) Expand(ev.Text[4..],ev.Time,result,new(),0);
            if(includeConditionalDac && animationTriggers.TryGetValue(ev.Text,out var triggers))
                foreach(var trigger in triggers)Expand(trigger.Instruction,ev.Time,result,new(),0);
        }
        if(manual!=null)foreach(var item in manual)foreach(var instruction in item.Trigger.Instructions)Expand(instruction,item.Time,result,new(),0);
        var referenceTimeline=references??[];
        result.AddRange(ReferenceEffectTimeline.Build(assets,records,referenceTimeline,Diagnostics));
        return ApplyApplications(result);
    }
    public List<EffectCue> Preview(Trigger trigger,double time) {var cues=new List<EffectCue>();BeginPreview();foreach(var instruction in trigger.Instructions)Expand(instruction,time,cues,new(),0);return ApplyApplications(cues);}
    List<EffectCue> ApplyApplications(List<EffectCue> cues)
    {
        var active=new Dictionary<string,(double Until,List<int> Indices)>(StringComparer.OrdinalIgnoreCase);
        // Stable ordering keeps equal-time events in their declared order, including aliases.
        foreach(var action in applications.OrderBy(a=>a.Time)){
            if(active.TryGetValue(action.Key,out var prior)&&prior.Until<=action.Time)active.Remove(action.Key);
            if(action.Remove){
                if(active.Remove(action.Key,out prior))foreach(int index in prior.Indices)cues[index]=cues[index] with{RemovedAt=action.Time};
                continue;
            }
            if(active.ContainsKey(action.Key))continue;
            List<int> indices=[];
            foreach(var cue in action.Cues){indices.Add(cues.Count);cues.Add(cue with{AppliedSpell=action.Key});}
            double until=action.Time+action.Duration;
            active[action.Key]=(until,indices);
        }
        return cues.OrderBy(c=>c.Time).ToList();
    }    void Expand(string instruction,double time,List<EffectCue> cues,HashSet<string> chain,int depth)
    {
        if(depth>32){Diagnostics.Add("Payload expansion exceeded nesting limit.");return;}
        instruction=instruction.Trim();
        if(instruction.StartsWith('$')) {
            if(!chain.Add(instruction)){Diagnostics.Add("Recursive payload alias: "+instruction);return;}
            if(aliases.TryGetValue(instruction,out var list))foreach(var x in list)Expand(x,time,cues,chain,depth+1);
            else Diagnostics.Add("Unresolved payload: "+instruction);
            chain.Remove(instruction);return;
        }
        if(instruction.StartsWith('!')) {
            var m=Regex.Match(instruction,@"^!\[([^\]]+)\](.*)$");
            if(m.Success && double.TryParse(m.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var delay)&&double.IsFinite(delay))Expand(m.Groups[2].Value,time+Math.Max(0,delay),cues,chain,depth+1);
            return;
        }
        var v=instruction.Split('|');if(v.Length<2)return;
        if(v[0] is "@CAST" or "@CASTSPELL" or "@APPLYSPELL" or "@UNAPPLYSPELL") {
            if(v.Length>=3 && TryId(v[1],out var id)) {
                var spell=records.Resolve(v[2],id);
                if(spell==null) {Diagnostics.Add($"Missing spell {v[2]}|{id:X6}");return;}
                if(v[0]=="@UNAPPLYSPELL"){applications.Add(new(spell.Key,time,true,[]));return;}
                if(v[0]=="@APPLYSPELL"){
                    uint type=spell.First("SPIT")?.UInt(8)??uint.MaxValue;
                    List<EffectCue> applied=[];
                    if(type is 1 or 4 or 10)Spell(spell,time,applied,new(),true);
                    else Diagnostics.Add(spell.EditorId+": AddSpell learns this spell; it does not cast it. Use a cast trigger to preview its effects.");
                    applications.Add(new(spell.Key,time,false,applied,ApplicationDuration(spell)));
                }else Spell(spell,time,cues,new());
            }
        }
        else if(v[0]=="@FF7VFXEND")return;
        else if(v[0]=="@FF7VFX") {
            // FF7 Limit Gauge's own instruction (Payload Interpreter ignores it):
            //   @FF7VFX|<nif under meshes>|root, follow or weapon|<seconds>|<x>|<y>|<z>[|nine rotation numbers, by rows]
            // root: one spawn at the actor root's position at that moment, x right, y forward, z up; a NIF point p
            // goes to R*p + offset. weapon: the NIF follows the blade and is spawned again every <seconds> until
            // @FF7VFXEND with the same NIF, or 12 s.
            if(v.Length<7){Diagnostics.Add("@FF7VFX needs nif|attach|seconds|x|y|z: "+instruction);return;}
            string path=VfxModel(v[1]);string attach=v[2].Trim().ToLowerInvariant();double seconds=F(v[3],0);
            if(!(seconds>0)){Diagnostics.Add("@FF7VFX with no play time is never shown: "+instruction);return;}
            var offset=new Vector3(F(v[4]),F(v[5]),F(v[6]));var rotation=Matrix4x4.Identity;
            if(v.Length>=16){
                // Rows of R act on column vectors; System.Numerics multiplies row vectors, so the matrix is transposed.
                float[] m=v.Skip(7).Take(9).Select(x=>F(x,float.NaN)).ToArray();
                if(m.Any(float.IsNaN)){Diagnostics.Add("@FF7VFX rotation could not be read: "+instruction);return;}
                rotation=new Matrix4x4(m[0],m[3],m[6],0,m[1],m[4],m[7],0,m[2],m[5],m[8],0,0,0,0,1);
            }else if(v.Length>7)Diagnostics.Add("@FF7VFX rotation needs nine numbers; identity used: "+instruction);
            if(attach=="root")Add(new(time,path,0,0,seconds,1,offset,instruction,TemporaryParticle:true,Rotation:rotation),cues);
            // follow: root kept on the actor. Same offset and rotation, re-placed at the actor's position and heading as it moves; plays once.
            else if(attach=="follow")Add(new(time,path,0,0,seconds,1,offset,instruction,FollowActor:true,TemporaryParticle:true,Rotation:rotation),cues);
            else if(attach=="weapon"){
                double end=vfxEnds.Where(e=>e.Time>=time-1e-9&&e.Model.Equals(path,StringComparison.OrdinalIgnoreCase)).Select(e=>e.Time).DefaultIfEmpty(time+Ff7VfxMaximum).Min();
                end=Math.Min(end,time+Ff7VfxMaximum);
                for(double start=time;start<end-1e-6;start+=seconds)
                    Add(new(start,path,0,0,seconds,1,offset,instruction,FollowActor:true,TemporaryParticle:true,RemovedAt:end,AttachNode:"WEAPON",Rotation:rotation,BladeSpace:true),cues);
            }else Diagnostics.Add("@FF7VFX attach must be root, follow or weapon: "+instruction);
        }
        else if(v[0]=="@PLAYPARTICLE" && v.Length>=9) {
            if(!int.TryParse(v[2],out int biped)||biped<0||biped>=42){Diagnostics.Add("Invalid particle biped index: "+v[2]);return;}
            if(!TryId(v[5].TrimEnd('u','U'),out uint flags,false)){Diagnostics.Add("Invalid particle spawn flags: "+v[5]);return;}
            var path=Assets.Normalize(v[1]);if(!path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))path=@"meshes\"+path;
            Add(new(time,path,0,0,F(v[4],1),F(v[3],1),new(F(v[6]),F(v[7]),F(v[8])),instruction,biped,SpawnFlags:flags,TemporaryParticle:true),cues);
            if(flags!=0)Diagnostics.Add($"Particle spawn flags 0x{flags:X} retained; target following is independent of these flags; other native flag effects remain unverified.");
            if(F(v[4],1)<=0)Diagnostics.Add("Nonpositive temporary-particle lifetime expires on the first positive-time update; it is not an infinite effect.");
        }
    }
    double ApplicationDuration(GameRecord spell)
    {
        if(spell.First("SPIT")?.UInt(16)==0)return double.PositiveInfinity;
        double duration=0;
        foreach(var field in spell.Fields){
            if(field.Tag=="EFID" && records.Resolve(spell,field.UInt())?.First("DATA") is {} data && (data.UInt()&0x200)!=0)return double.PositiveInfinity;
            if(field.Tag=="EFIT"){if(field.UInt(8)==0)return double.PositiveInfinity;duration=Math.Max(duration,field.UInt(8));}
        }
        return duration>0?duration:double.PositiveInfinity;
    }    void Spell(GameRecord spell,double time,List<EffectCue> cues,HashSet<string> seen,bool passive=false,RemoteCastFrame? remote=null)
    {
        if(seen.Count>=32||!seen.Add(spell.Key)){Diagnostics.Add(spell.EditorId+": recursive spell chain stopped.");return;}
        for(int index=0;index<spell.Fields.Count;index++) {
            var e=spell.Fields[index];if(e.Tag!="EFID")continue;
            Field? parameters=null;
            for(int next=index+1;next<spell.Fields.Count&&spell.Fields[next].Tag!="EFID";next++)
                if(spell.Fields[next].Tag=="EFIT"){parameters=spell.Fields[next];break;}
            var effect=records.Resolve(spell,e.UInt());if(effect==null){Diagnostics.Add("Missing magic effect: "+spell.Reference(e.UInt()));continue;}
            var handled=PreviewScriptedCasts(effect,time,cues,seen,passive,remote);
            Diagnostics.AddRange(ScriptAttachments.Describe(effect,records,handled));
            var data=effect.First("DATA");if(data==null)continue;
            // TES5 MGEF DATA: projectile at 72, casting art at 92, hit art at 96.
            var projectile=records.Resolve(effect,data.UInt(72));
            if(!passive&&projectile!=null && projectile.Kind=="PROJ") {
                Diagnostics.AddRange(ProjectileVisuals.Describe(projectile,records,assets));
                var model=ProjectileVisuals.ModelPath(projectile.First("MODL")?.Text);var pd=projectile.First("DATA");float speed=pd?.Float(8)??0,range=pd?.Float(12)??0;
                float life=pd?.Float(76)??0,fade=pd?.Float(48)??0;
                int type=pd is {Bytes.Length:>=4}?BitConverter.ToUInt16(pd.Bytes,2):0;
                float gravity=pd?.Float(4)??0;float launchElevation=remote?.Elevation??ProjectileElevation;
                if(ProjectileVisuals.Muzzle(projectile,time,remote?.Start??new(0,0,100),launchElevation,remote?.Heading??0) is {} muzzle)Add(muzzle,cues);
                double rangeTime=ProjectileMotion.IsBallistic(type)?ProjectileMotion.RangeTime(speed,gravity,type,range,launchElevation):
                    speed>0&&range>0?range/speed:double.PositiveInfinity;
                if(!ProjectileMotion.IsBallistic(type))Diagnostics.Add(projectile.EditorId+": projectile type "+type+" uses a provisional travelling-model preview; native beam/flame/cone/barrier behavior is not implemented.");
                if(ProjectileMotion.GravityFactor(gravity,type)!=0)Diagnostics.Add(projectile.EditorId+": ballistic preview uses AE gravity units and a continuous trajectory; pitch follows continuous unsigned gravity turning with the AE pitch clamp; native frame stepping, degenerate/reversed launches, and collisions are not yet reproduced.");
                double duration=life>0?Math.Min(life,rangeTime):rangeTime;
                if(!double.IsFinite(duration)){duration=3;Diagnostics.Add(projectile.EditorId+": no finite record lifetime/range; preview limit is 3 seconds.");}
                double? detonation=ProjectileVisuals.DetonationTime(projectile,records,duration);
                var launch=new EffectCue(time,model,speed,gravity,duration,1,remote?.Start??new(0,0,100),
                    projectile.EditorId,ProjectileType:type,Elevation:launchElevation,Heading:remote?.Heading??0);
                if(detonation is {} delay){
                    duration=delay;
                    if(ProjectileVisuals.Explosion(projectile,records,launch,delay) is {} explosion)Add(explosion,cues);
                    Diagnostics.Add(projectile.EditorId+": alternate timer detonates at "+delay.ToString("g",CultureInfo.InvariantCulture)+"s using the continuous preview trajectory. Proximity, collisions, lights, damage and secondary spawning are not simulated; explosion emission preview limit is 10 seconds.");
                }
                if(!string.IsNullOrEmpty(model))Add(new(time,model,speed,gravity,duration,1,remote?.Start??new(0,0,100),
                    $"{spell.EditorId} → {effect.EditorId} → {projectile.EditorId} [type={type}, speed={speed}, range={range}, life={life}, fade={fade}]", -1,fade,type,RemovedAt:detonation is {} stop?time+stop:double.PositiveInfinity,Elevation:launchElevation,Heading:remote?.Heading??0),cues);
            }
            void Art(int offset,double duration,string purpose,string? attachNode=null) {
                var art=records.Resolve(effect,data.UInt(offset));var model=art?.Kind=="ARTO"?art.First("MODL")?.Text:null;
                if(!string.IsNullOrEmpty(model))Add(new(time,@"meshes\"+model,0,0,duration,1,remote?.Start??Vector3.Zero,
                    $"{spell.EditorId} → {effect.EditorId} → {art!.EditorId} [{purpose}, {duration:g} seconds]",FollowActor:remote==null,Heading:remote?.Heading??0,AttachNode:remote==null?attachNode:null),cues);
            }
            // The game parents casting art to the casting hand's magic node; animation-driven casts here are
            // previewed on the right hand. Left-hand, dual and staff casts are not distinguished.
            if(!passive)Art(92,3,"casting art on NPC R MagicNode; preview limit","NPC R MagicNode [RMag]");
            if(data.UInt(96)!=0) {
                if(data.UInt(84)==0 && (data.UInt()&0x08000000)==0) {
                    double duration=parameters?.UInt(8)??0;
                    if(passive&&(spell.First("SPIT")?.UInt(16)==0||duration<=0||(data.UInt()&0x200)!=0))duration=double.PositiveInfinity;
                    else if(duration<=0 || (data.UInt()&0x200)!=0) {
                        duration=3;
                        Diagnostics.Add(effect.EditorId+": self effect has no finite EFIT duration; art preview limit is 3 seconds.");
                    }
                    Art(96,duration,"self-target hit art");
                } else if((data.UInt()&0x08000000)==0)
                    Diagnostics.Add(effect.EditorId+": targeted hit art requires an explicit impact event.");
            }
            if(data.UInt(32)!=0 || data.UInt(36)!=0)
                Diagnostics.Add(effect.EditorId+": effect-shader records are present; actor-surface shader effects are not yet rendered.");
            if(effect.All("CTDA").Any())Diagnostics.Add(effect.EditorId+": magic-effect conditions need an in-game actor; visual preview assumes they pass.");
        }
    }
    HashSet<string> PreviewScriptedCasts(GameRecord effect,double time,List<EffectCue> cues,HashSet<string> seen,bool passive,RemoteCastFrame? remote)
    {
        HashSet<string> handled=new(StringComparer.OrdinalIgnoreCase);
        if(passive||remote!=null||effect.First("VMAD") is not {} vmad)return handled;
        ScriptAttachment[] scripts;
        try{scripts=ScriptAttachments.Read(vmad.Bytes);}catch(Exception ex) when(ex is IOException or ArgumentException){return handled;}
        foreach(var script in scripts){
            if(!script.Name.Equals("FenrirBladeBeam",StringComparison.OrdinalIgnoreCase))continue;
            const string path=@"scripts\FenrirBladeBeam.pex";
            if(!assets.Exists(path)||!ScriptedCasts.VerifiedFenrir(assets.Read(path))){Diagnostics.Add(effect.EditorId+": Fenrir remote-cast adapter requires the inspected script version; missing or modified PEX remains unsupported.");continue;}
            var data=effect.First("DATA");
            if(data==null||data.UInt(84)!=0){Diagnostics.Add(effect.EditorId+": Fenrir remote casts require the supported self-target context.");continue;}
            var property=script.Properties.FirstOrDefault(p=>p.Name.Equals("BeamPRSpell",StringComparison.OrdinalIgnoreCase));
            var activatorProperty=script.Properties.FirstOrDefault(p=>p.Name.Equals("FXEmptyActivator",StringComparison.OrdinalIgnoreCase));
            var spell=property?.Value is ScriptObject {Alias:-1} reference?records.Resolve(effect,reference.FormId):null;
            var activator=activatorProperty?.Value is ScriptObject {Alias:-1} objectReference?records.Resolve(effect,objectReference.FormId):null;
            if(spell?.Kind!="SPEL"||activator?.Kind!="ACTI") {Diagnostics.Add(effect.EditorId+": Fenrir remote-cast spell or activator property is missing or unresolved.");continue;}
            foreach(var frame in ScriptedCasts.FenrirFrames())Spell(spell,time,cues,new(seen),remote:frame);
            handled.Add(script.Name);
            Diagnostics.Add(effect.EditorId+": verified FenrirBladeBeam adapter creates three aimed remote casts at the script's offsets. Papyrus scheduling, actor collision and targeted hit art remain unverified; temporary helper activators are not rendered.");
        }
        return handled;
    }
    void Add(EffectCue cue,List<EffectCue> list) {if(assets.Exists(cue.Model))list.Add(cue);else Diagnostics.Add("Missing effect mesh: "+cue.Model);}
    static float F(string s,float fallback=0)=>float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out var f)&&float.IsFinite(f)?f:fallback;
    static bool TryId(string s,out uint id,bool hex=true) {s=s.Trim();if(s.StartsWith("0x",StringComparison.OrdinalIgnoreCase)){s=s[2..];hex=true;}return uint.TryParse(s,hex?NumberStyles.HexNumber:NumberStyles.Integer,CultureInfo.InvariantCulture,out id);}
}
