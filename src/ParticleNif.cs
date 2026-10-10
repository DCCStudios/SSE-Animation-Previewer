using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using static SSEPreview.NifAnimation;

namespace SSEPreview;

public sealed class ParticleNif
{
    public readonly Nifcontent_Class_Manolo Nif;
    public readonly NiParticleSystem System;
    public readonly NifAnimation Animation;
    public readonly ParticleSimulation Simulation;
    public readonly List<string> Diagnostics=[];
    public readonly object[] Modifiers;
    public readonly object? Data,Shader,Alpha;
    public double LastSeekMs;
    public readonly string Name,Texture,Palette;
    public Func<double,Matrix4x4> Placement = _=>Matrix4x4.Identity;
    // A single ADDN instance supplies one native-style emission target. Shared
    // master pooling and distance-selected target caps are not simulated yet.
    // Assigning either binding invalidates the per-step emitter caches (see Memo in ConfigureEmitter).
    Func<double,Matrix4x4>? addonEmitterTarget;Func<double,float>? addonEmitterScale;int memoVersion;
    public Func<double,Matrix4x4>? AddonEmitterTarget{get=>addonEmitterTarget;set{addonEmitterTarget=value;memoVersion++;}}
    public Func<double,float>? AddonEmitterScale{get=>addonEmitterScale;set{addonEmitterScale=value;memoVersion++;}}
    public readonly bool WorldSpace;
    internal readonly ParticleMeshArtwork? MeshArtwork;
    float normalizedLodDistance;
    public float NormalizedLodDistance{get=>normalizedLodDistance;set{normalizedLodDistance=value;lodTime=double.NaN;memoVersion++;}}
    // Preview budget per system (0 = authored NiPSysData limit only). Dense mod effects author 9,999
    // births per second against a 9,999 cap; the native update is SIMD C++, this simulation is not.
    public static int PreviewBudget=2000;
    public int AuthoredCapacity=>Math.Clamp(Convert.ToInt32(typeof(NiGeometryData).GetField("_bSMaxVertices",global::System.Reflection.BindingFlags.NonPublic|global::System.Reflection.BindingFlags.Instance)?.GetValue(Data??throw new InvalidDataException("Particle system has no data block"))??1024),1,65535);
    double lodTime=double.NaN;(float Emission,float Size) lodValue;
    (float Emission,float Size) Lod(double time)
    {
        if(time==lodTime)return lodValue;
        var result=(Emission:1f,Size:1f);
        foreach(var modifier in Modifiers.OfType<BSPSysLODModifier>())
            if(Active(modifier,time))result=ParticleLod.Evaluate(normalizedLodDistance,F(modifier,"LODBeginDistance"),F(modifier,"LODEndDistance"),F(modifier,"EndEmitScale"),F(modifier,"EndSize"));
        lodTime=time;lodValue=result;
        return result;
    }
    readonly object emitter;
    readonly BSPSysInheritVelocityModifier[] inheritanceModifiers;
    readonly Dictionary<BSPSysInheritVelocityModifier,(double Time,Vector3 Position,Vector3 Velocity)> inheritanceCache=[];
    double evaluatedTime=double.NaN;
    readonly Dictionary<object,Matrix4x4> worldCache=new();
    readonly Dictionary<object,(double Time,bool Value)> activeCache=new();
    readonly Dictionary<(object,string),(double Time,float Value)> animatedCache=new();
    readonly Dictionary<double,Matrix4x4> placementCache=[];
    bool cachePlacement;
    Matrix4x4 PlacementAt(double time){
        if(!cachePlacement)return Placement(time);
        if(placementCache.TryGetValue(time,out var transform))return transform;
        return placementCache[time]=Placement(time);
    }
    void Evaluate(double time){if(time==evaluatedTime)return;Animation.Evaluate(time);evaluatedTime=time;worldCache.Clear();}
    readonly List<(NiAVObject Node,ParticleMesh Surface)> emitterMeshes=[];
    public sealed class MeshEmitterSource(ParticleNif owner,NiPSysMeshEmitter source,ParticleSimulation.EmitterChannel channel,List<(NiAVObject Node,ParticleMesh Surface)> meshes)
    {
        public ParticleNif Owner=>owner;
        public NiPSysMeshEmitter Source=>source;
        public Nifcontent_Class_Manolo Nif=>owner.Nif;
        public string Name=>$"{owner.Name} � {channel.Name} (#{Nif.Blocks.IndexOf(source)})";
        public List<string> Diagnostics=>owner.Diagnostics;
        public bool HasEmitterOverride {get;private set;}
        public NiAVObject[] AvailableEmitterMeshes=>owner.AvailableEmitterMeshes;
        public NiAVObject[] BoundEmitterMeshes=>meshes.Select(m=>m.Node).ToArray();
        public string AuthoredEmitterSources=>"Saved source links: "+(source.EmitterMeshes.Indices.Count()==0?"(empty)":string.Join(", ",source.EmitterMeshes.Indices.Select(index=>index<0?"null":index>=Nif.Blocks.Count?$"#{index} (missing)":$"#{index} {Nif.Blocks[index].GetType().Name} {Get(Nif.Blocks[index],"Name")}")));
        public string EmitterSourceDetails=>AuthoredEmitterSources+Environment.NewLine+
            (HasEmitterOverride?"Temporary preview override is active. Saved links are unchanged.":"Using saved source links.")+Environment.NewLine+
            (meshes.Count==0?"The previewer suppresses births without usable geometry. This is a preview limitation; it does not establish that Skyrim suppresses this emitter.":$"{meshes.Count} usable source mesh(es).");
        public void BindEmitterMeshes(IEnumerable<NiAVObject>? sources){
            var selected=sources?.Distinct().ToArray();
            if(selected?.Any(n=>!AvailableEmitterMeshes.Contains(n))==true)throw new ArgumentException("Select geometry from this effect NIF.");
            owner.ConfigureMeshEmitterCore(source,meshes,channel,selected);HasEmitterOverride=selected!=null;owner.Simulation.Reset();
        }
    }
    readonly List<MeshEmitterSource> meshSourceBindings=[];
    public IReadOnlyList<MeshEmitterSource> MeshSourceBindings=>meshSourceBindings;
    MeshEmitterSource? PrimaryMeshSource=>meshSourceBindings.FirstOrDefault(s=>ReferenceEquals(s.Source,emitter));
    public bool IsMeshEmitter=>emitter is NiPSysMeshEmitter;
    public bool HasEmitterOverride=>PrimaryMeshSource?.HasEmitterOverride??false;
    public string AuthoredEmitterSources=>PrimaryMeshSource?.AuthoredEmitterSources??"This emitter does not use source meshes.";
    public string EmitterSourceDetails=>PrimaryMeshSource?.EmitterSourceDetails??AuthoredEmitterSources;
    public NiAVObject[] BoundEmitterMeshes=>PrimaryMeshSource?.BoundEmitterMeshes??[];
    public NiAVObject[] AvailableEmitterMeshes=>Nif.Blocks.OfType<NiAVObject>().Where(n=>n is INiShape&&n is not NiParticleSystem).ToArray();
    public void BindEmitterMeshes(IEnumerable<NiAVObject>? sources)=>(PrimaryMeshSource??throw new InvalidOperationException("The first emitter does not use source meshes.")).BindEmitterMeshes(sources);
    readonly NiTimeController[] controllers;
    readonly Dictionary<string,object> byName;
    public object? Block(object? r)=>Animation.Block(r);
    static string S(object? o,string p)=>Get(o,p) is NiString4 str?str.Content:Get(o,p)?.ToString()??"";
    static float F(object? o,string p,float d=0)=>Number(Get(o,p),d);
    static Vector3 V(object? o,string p,Vector3 d=default)=>Get(o,p) is Vector3 v?v:d;
    static bool B(object? o,string p,bool d=false)=>Get(o,p) is NiBool nb?nb.GetValueOrDefault(d):Get(o,p) is bool b?b:d;
    public static Vector4 Color(object? o)=>o is Color4 c?new(c.R,c.G,c.B,c.A):Vector4.One;
    public static Matrix4x4 Matrix(OpenTK.Mathematics.Matrix4 m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
    Matrix4x4 World(object? node)
    {
        if(node is not NiAVObject av)return Matrix4x4.Identity;
        if(worldCache.TryGetValue(av,out var cached))return cached;
        var local=Matrix(new Transform_Class{Rotation=av.Rotation,Translation=av.Translation,Scale=av.Scale}.ToMatrix4());
        int index=Nif.Blocks.IndexOf(av);
        var parent=Nif.Blocks.OfType<NiNode>().FirstOrDefault(n=>n.Children.Indices.Contains(index));
        return worldCache[av]=parent==null?local:local*Matrix(Transform_Class.GetGlobalTransform(parent,Nif).ToMatrix4());
    }
    public ParticleNif(Nifcontent_Class_Manolo nif,NiParticleSystem system,uint seed=1)
    {
        Nif=nif;System=system;Animation=new(nif);Animation.Placement=t=>PlacementAt(t);Name=system.Name.ToString();
        Data=Block(system.Data_NiBlockRef_NPSD)??Block(system.DataRef);Shader=Block(system.ShaderPropertyRef);Alpha=Block(system.AlphaPropertyRef)??Items(Get(system,"Properties")).Select(Block).OfType<NiAlphaProperty>().FirstOrDefault();
        if(ParticleOrientation.SpeedAspect(Convert.ToInt32(Get(Data,"AspectFlags")??0))){
            float first=F(Data,"SpeedtoAspectSpeed1"),second=F(Data,"SpeedtoAspectSpeed2"),aspect2=F(Data,"SpeedtoAspectAspect2",1);
            if(!float.IsFinite(first)||!float.IsFinite(second)||first==second||!float.IsFinite(aspect2)||aspect2<=0)
                Diagnostics.Add(Name+": invalid speed-to-aspect limits/ratio; renderer uses the base aspect instead of nonfinite geometry");
        }
        if(Alpha==null)Diagnostics.Add(Name+": no alpha property is authored; preview assumes the vanilla particle default (additive SrcAlpha/One, flags 4109, used by 287 of 313 vanilla magic systems) because opaque rendering shows black quads; Skyrim's handling of the missing property is unverified");
        Texture=Assets.GamePath(S(Shader,"SourceTexture"));Palette=Assets.GamePath(S(Shader,"GreyscaleTexture"));
        if(string.IsNullOrWhiteSpace(Palette)&&(Get(Shader,"HasGreyscaleToPaletteColor") is true||Get(Shader,"HasGreyscaleToPaletteAlpha") is true))Diagnostics.Add(Name+": palette shader flags are set without a palette texture; source texture is shown and runtime shader overrides may be required");
        if(Shader is BSEffectShaderProperty lit&&((uint)lit.ShaderFlags_SSPF2&0x40000000u)!=0)Diagnostics.Add(Name+": effect lighting uses the configured studio radiance and authored influence; Skyrim weather/cell lighting, point lights, shadows and exposure are not reproduced");
        Modifiers=Items(Get(system,"Modifiers")).Select(Block).Where(x=>x!=null).Cast<object>().OrderBy(x=>F(x,"Order")).ToArray();
        byName=Modifiers.GroupBy(m=>S(m,"Name")).ToDictionary(g=>g.Key,g=>g.First());
        controllers=nif.Blocks.OfType<NiTimeController>().Where(c=>ReferenceEquals(Block(c.Target),system)).ToArray();
        inheritanceModifiers=Modifiers.OfType<BSPSysInheritVelocityModifier>().ToArray();
        emitter=Modifiers.FirstOrDefault(m=>m is NiPSysEmitter)??new object();
        WorldSpace=B(system,"WorldSpace",true);
        var cfg=Settings(0);
        Simulation=new(cfg,seed){AnimatedSettings=t=>Settings(t)};
        ConfigureEmitter(emitter,Simulation.PrimaryEmitter,emitterMeshes);
        foreach(var extra in Modifiers.OfType<NiPSysEmitter>().Skip(1)){
            var channel=new ParticleSimulation.EmitterChannel();Simulation.AdditionalEmitters.Add(channel);
            ConfigureEmitter(extra,channel,[]);
        }
        Simulation.Update=Update;
        if(inheritanceModifiers.Length>0){
            Simulation.Initialize=InitializeParticle;
            Simulation.BeforeStep=UpdateInheritance;
            Simulation.ResetState=inheritanceCache.Clear;
        }
        var deathModifiers=Modifiers.OfType<NiPSysAgeDeathModifier>().ToArray();
        // Queried for every live particle each step; cache the answer per step time.
        var rotationModifiers=Modifiers.OfType<NiPSysRotationModifier>().ToArray();
        double ageAt=double.NaN,spinAt=double.NaN;bool ageValue=false,spinValue=false;
        Simulation.AgeEnabled=t=>{if(t!=ageAt){ageValue=deathModifiers.Any(m=>Active(m,t));ageAt=t;}return ageValue;};
        Simulation.SpinEnabled=t=>{if(t!=spinAt){spinValue=rotationModifiers.Any(m=>Active(m,t));spinAt=t;}return spinValue;};
        Simulation.DeathSpawn=(particle,time)=>{
            var death=deathModifiers.FirstOrDefault(m=>Active(m,time)&&B(m,"SpawnonDeath"));
            var spawn=Block(Get(death,"SpawnModifier"));
            return SpawnSettings(spawn,time);
        };
        if(Data is BSStripPSysData stripData){
            int count=stripData.MaxPointCount;
            if(count<=65532)Simulation.Initialize+=(particle,time)=>particle.Strip=new ParticleStripHistory(count);
            Diagnostics.Add(Name+": strip history and crossed surfaces follow inspected AE routines; cap geometry, depth prepass, transport edge cases and render-driven scheduling remain unfinished; retained-strip child-spawn timing uses preview ticks");
        }
        if(system is NiMeshParticleSystem)MeshArtwork=new(this);
        ConfigureColliders();
        foreach(var m in Modifiers) {
            string type=m.GetType().Name;
            if(type=="BSPSysLODModifier")Diagnostics.Add(Name+": fixed normalized LOD distance controls emission and birth size; automatic fade-node/camera mapping and changing-distance scheduling are not yet implemented");
            if(!Supported.Contains(type))Diagnostics.Add(Name+": unsupported modifier "+type);
            if(type=="BSPSysInheritVelocityModifier"){
                Diagnostics.Add(Name+": source-origin velocity and relative variation follow AE initialization; update cadence and child-spawn scheduling still require comparison");
                if(Block(Get(m,"InheritObject")) is not NiAVObject)
                    Diagnostics.Add(Name+": velocity inheritance has no source object; this modifier is disabled");
            }
            if(type=="NiPSysBombModifier"){
                Diagnostics.Add(Name+": bomb symmetry, signed acceleration and bounded falloff follow AE code; scene transforms and update cadence still require visual comparison");
                if((int)F(m,"SymmetryType") is <0 or >2 || (int)F(m,"DecayType") is <0 or >2)
                    Diagnostics.Add(Name+": unknown bomb symmetry/decay type; this bomb force is disabled");
                if((int)F(m,"DecayType")!=0&&F(m,"Decay")<=0)
                    Diagnostics.Add(Name+": nonpositive bomb falloff distance; this bomb force is disabled");
            }
            if(type=="NiPSysDragModifier"){
                Diagnostics.Add(Name+": directional drag uses AE capped linear damping and outer-distance falloff; transform and update-cadence parity remain unverified");
                if(F(m,"Range")<0||F(m,"RangeFalloff")<0)Diagnostics.Add(Name+": negative drag range is not defined by NifXML; this drag modifier is disabled");
            }
            if(type=="NiPSysSpawnModifier")Diagnostics.Add(Name+": child particles currently inherit parent birth color/size and current angle/spin; native child initialization and modifier ordering remain unverified");
            if(type=="NiPSysSpawnModifier"&&(F(m,"SpawnDirVariation")!=0||F(m,"SpawnSpeedVariation")!=0))Diagnostics.Add(Name+": spawn speed/direction variation distribution is not calibrated against Skyrim");
            if(type=="NiPSysGravityModifier"&&F(m,"Turbulence")!=0)Diagnostics.Add(Name+": gravity turbulence uses native amplitude with deterministic 120 Hz sampling; Skyrim RNG sequence and update cadence remain unverified");
        }
        if(emitter is not NiPSysBoxEmitter and not NiPSysSphereEmitter and not NiPSysCylinderEmitter and not NiPSysMeshEmitter)
            Diagnostics.Add(Name+": unsupported emitter "+emitter.GetType().Name);
        if(Simulation.AdditionalEmitters.Count>0)Diagnostics.Add(Name+" ("+string.Join(", ",Simulation.Emitters.Select(e=>e.Name))+"): multiple emitters share particle capacity and random state with independent controller timing; native cross-emitter controller scheduling remains unverified.");
        if(!controllers.Any(c=>c is NiPSysEmitterCtlr))Diagnostics.Add(Name+": no emitter controller; emission is disabled");
        if(F(Data,"NumActive")>0)Diagnostics.Add(Name+": nonzero saved active count has no serialized per-particle state in Skyrim NIFs; preview starts with emitter births");
        if(system.GetType()!=typeof(NiParticleSystem)&&system is not NiMeshParticleSystem&&!(system is BSStripParticleSystem&&Data is BSStripPSysData))Diagnostics.Add(Name+": specialized particle geometry "+system.GetType().Name+" not supported");
    }
    void UpdateInheritance(double time)
    {
        double restoreTime=evaluatedTime;
        try{
            Evaluate(time);
            foreach(var modifier in inheritanceModifiers){
                if(!Active(modifier,time)||Block(Get(modifier,"InheritObject")) is not NiAVObject source)continue;
                var position=(World(source)*PlacementAt(time)).Translation;
                Vector3 velocity=Vector3.Zero;
                if(inheritanceCache.TryGetValue(modifier,out var previous)){
                    if(previous.Time==time)continue;
                    velocity=(position-previous.Position)/(float)(time-previous.Time);
                }
                inheritanceCache[modifier]=(time,position,velocity);
            }
        }finally{Evaluate(double.IsFinite(restoreTime)?restoreTime:time);}
    }
    void InitializeParticle(ParticleSimulation.Particle particle,double time)
    {
        foreach(var modifier in inheritanceModifiers){
            // Native initialization is unconditional; Active gates cache updates instead.
            if(!inheritanceCache.TryGetValue(modifier,out var cached))continue;
            float chance=F(modifier,"ChanceToInherit",100);
            if(!float.IsFinite(chance)||chance<=0||Simulation.Random01()>=Math.Clamp(chance/100,0,1))continue;
            float multiplier=F(modifier,"VelocityMultiplier",.5f),variation=F(modifier,"VelocityVariation");
            if(!float.IsFinite(multiplier)||!float.IsFinite(variation))continue;
            var inherited=cached.Velocity*ParticleForces.InheritanceFactor(multiplier,variation,Simulation.Random01());
            if(float.IsFinite(inherited.X)&&float.IsFinite(inherited.Y)&&float.IsFinite(inherited.Z))particle.Velocity+=inherited;
        }
    }
    void ConfigureEmitter(object source,ParticleSimulation.EmitterChannel channel,List<(NiAVObject Node,ParticleMesh Surface)> meshes)
    {
        channel.RotateVelocityOnly=source is NiPSysBoxEmitter or NiPSysSphereEmitter or NiPSysCylinderEmitter;
        channel.Name=S(source,"Name");
        // Births within one step share the step time, so a last-value cache removes the per-birth reflection cost.
        Func<double,T> Memo<T>(Func<double,T> inner){double at=double.NaN;int version=-1;T value=default!;return t=>{if(t==at&&version==memoVersion)return value;value=inner(t);at=t;version=memoVersion;return value;};}
        channel.SettingsAt=t=>Settings(t,source);
        var controller=controllers.FirstOrDefault(c=>c is NiPSysEmitterCtlr&&S(c,"ModifierName")==channel.Name);
        bool BoundMulti()=>controller is BSPSysMultiTargetEmitterCtlr&&AddonEmitterTarget!=null;
        channel.SettingsAt=Memo<ParticleSimulation.Settings>(t=>{
            var settings=Settings(t,source);if(!BoundMulti())return settings;
            float scale=AddonEmitterScale?.Invoke(t)??1;if(!float.IsFinite(scale))scale=0;
            scale=Math.Max(0,scale);
            return settings with{Radius=settings.Radius*scale,RadiusVariation=settings.RadiusVariation*scale};
        });
        float MultiRate(){var interp=Block(Get(controller,"Interpolator"));var data=Block(Get(interp,"Data"));float rate=Number(Sample(Get(data,"Data"),0)??Get(interp,"Value"),0);return float.IsFinite(rate)&&rate>0?rate:0;}
        // Rate and activity are read once per step (per-birth checks are gated in the simulation), so they stay live.
        channel.BirthRate=t=>controller==null?0:(BoundMulti()?MultiRate():Value(controller,"Interpolator",t,0))*Lod(t).Emission;
        channel.Active=t=>Active(source,t)&&(source is not NiPSysMeshEmitter||meshes.Count>0)&&controller!=null&&NifAnimation.Started(controller.Flags.Value)&&
            (BoundMulti()?F(controller,"MaxEmitters")>0:Value(controller,"VisibilityInterpolator",t,1)>.5f);
        channel.Transform=Memo<Matrix4x4>(t=>{Evaluate(t);
            if(BoundMulti()){
                var target=AddonEmitterTarget!(t);if(WorldSpace)return target;
                Matrix4x4.Invert(World(System)*PlacementAt(t),out var localInverse);return target*localInverse;
            }
            var transform=World(Block(Get(source,"EmitterObject"))??System);if(WorldSpace)return transform*PlacementAt(t);Matrix4x4.Invert(World(System),out var inverse);return transform*inverse;});
        if(controller is BSPSysMultiTargetEmitterCtlr&&source is NiPSysMeshEmitter)Diagnostics.Add(Name+": multi-target mesh source sampling retains authored source geometry; target-source replacement is not yet reproduced.");
        if(controller is BSPSysMultiTargetEmitterCtlr)Diagnostics.Add(Name+": multi-target controller previews a bound ADDN target with rate sampled at time zero and target-node placement; shared master pooling, native per-frame birth caps, target selection remain unverified; radius uses the bound node local scale. Unbound direct NIF preview uses ordinary emitter fallback.");
        if(source is NiPSysMeshEmitter mesh){meshSourceBindings.Add(new(this,mesh,channel,meshes));ConfigureMeshEmitterCore(source,meshes,channel);}
        else if(source is not NiPSysBoxEmitter and not NiPSysSphereEmitter and not NiPSysCylinderEmitter){channel.Active=_=>false;Diagnostics.Add(Name+": unsupported emitter "+source.GetType().Name+" "+channel.Name);}
        if(controller==null)Diagnostics.Add(Name+": emitter "+channel.Name+" has no matching controller; this emitter is disabled");
    }
    void ConfigureMeshEmitterCore(object emitter,List<(NiAVObject Node,ParticleMesh Surface)> emitterMeshes,ParticleSimulation.EmitterChannel channel,IEnumerable<NiAVObject>? sources=null)
    {
        Diagnostics.RemoveAll(d=>d.StartsWith(Name+" ["+channel.Name+"]: ",StringComparison.Ordinal));
        int mode=(int)F(emitter,"EmissionType");
        emitterMeshes.Clear();

        foreach(var node in sources??Items(Get(emitter,"EmitterMeshes")).Select(Block).OfType<NiAVObject>()){
            var geometry=node is INiShape shape?shape.GeometryData:null;
            var positions=node is BSTriShape bs?bs.VertexPositions?.ToArray():Items(Get(geometry,"Vertices")).OfType<Vector3>().ToArray();
            var normals=node is BSTriShape bs2?bs2.Normals?.ToArray():Items(Get(geometry,"Normals")).OfType<Vector3>().ToArray();
            var triangles=node is INiShape s?s.Triangles?.ToArray():null;
            var surface=new ParticleMesh(positions??[],normals??[],triangles??[]);
            if(node is INiShape skinned&&skinned.IsSkinned){
                try{ParticleSkin.Bind(Nif,skinned,surface,World);Diagnostics.Add(Name+" ["+channel.Name+"]: mesh source "+node.Name+" samples NIF bone deformation at birth time; external actor skeleton bindings and native skinning parity remain unverified");}
                catch(InvalidDataException ex){Diagnostics.Add(Name+" ["+channel.Name+"]: skinned emitter source "+node.Name+" disabled: "+ex.Message);continue;}
            }
            if(surface.Valid(mode))emitterMeshes.Add((node,surface));
            else Diagnostics.Add(Name+" ["+channel.Name+"]: emitter mesh "+node.Name+" has no usable surface");
        }
        if(emitterMeshes.Count==0){channel.MeshSample=null;Diagnostics.Add(Name+" ["+channel.Name+"]: mesh emitter has no usable source geometry; the previewer suppresses births. Skyrim null-source behavior is unresolved. "+"Saved source links: "+string.Join(", ",((NiPSysMeshEmitter)emitter).EmitterMeshes.Indices)+". Assign sources explicitly only for preview testing.");return;}
        Diagnostics.Add(Name+" ["+channel.Name+"]: mesh emission: AE face sampling follows native triangle selection and surface weighting; other emission modes and runtime source binding remain unverified");
        channel.Transform=_=>Matrix4x4.Identity;
        channel.MeshSample=time=>{
            if(!double.IsNaN(Simulation.StepTime))time=Simulation.StepTime;Evaluate(time);
            var mesh=emitterMeshes[Math.Min(emitterMeshes.Count-1,(int)(ParticleMesh.Random15(Simulation.Random01)/32767f*emitterMeshes.Count))];
            var sample=mesh.Surface.Sample(mode,Simulation.Random01);
            var sourceTransform=World(mesh.Node);
            var transform=mesh.Surface.SamplesWorldSpace?Matrix4x4.Identity:sourceTransform;
            if(WorldSpace)transform*=PlacementAt(time);
            else {Matrix4x4.Invert(World(System),out var inverse);transform*=inverse;}
            var axisTransform=WorldSpace?sourceTransform*PlacementAt(time):sourceTransform*(Matrix4x4.Invert(World(System),out var inverseAxis)?inverseAxis:Matrix4x4.Identity);
            Vector3 direction;
            int velocityType=(int)F(emitter,"InitialVelocityType");
            if(velocityType==1){
                direction=ParticleMesh.RandomVelocityDirection(Simulation.Random01);
            }else if(velocityType==2)direction=V(emitter,"EmissionAxis",Vector3.UnitX);
            else direction=sample.Normal;
            // Random velocity is authored in particle-system space, not emitter-mesh space.
            var directionTransform=velocityType==1?(WorldSpace?World(System)*PlacementAt(time):Matrix4x4.Identity):velocityType==2?axisTransform:transform;
            direction=Vector3.TransformNormal(direction,directionTransform);
            if(direction.LengthSquared()>1e-12f)direction=Vector3.Normalize(direction);
            return(Vector3.Transform(sample.Position,transform),direction);
        };
    }
    static readonly HashSet<string> Supported=[
        "BSPSysStripUpdateModifier","BSPSysLODModifier","BSPSysInheritVelocityModifier","NiPSysBombModifier","NiPSysColliderManager","NiPSysSpawnModifier","NiPSysMeshEmitter","NiPSysBoxEmitter","NiPSysSphereEmitter","NiPSysCylinderEmitter","NiPSysPositionModifier","NiPSysAgeDeathModifier",
        "NiPSysBoundUpdateModifier","NiPSysGravityModifier","NiPSysDragModifier","NiPSysGrowFadeModifier","NiPSysColorModifier",
        "NiPSysMeshUpdateModifier","NiPSysRotationModifier","BSPSysScaleModifier","BSPSysSimpleColorModifier","BSPSysSubTexModifier"];
    float Value(NiTimeController c,string property,double time,float fallback)
    {
        var interp=Animation.ResolveInterpolator(c,property,time,out double sampleTime);var data=Block(Get(interp,"Data"));
        float value=Number(Sample(Get(data,"Data"),sampleTime)??Get(interp,"Value"),fallback);return float.IsFinite(value)&&value>-1e30f?value:fallback;
    }
    bool Active(object m,double t)
    {
        if(activeCache.TryGetValue(m,out var cached)&&cached.Time==t)return cached.Value;
        var c=controllers.FirstOrDefault(c=>c is NiPSysModifierActiveCtlr&&S(c,"ModifierName")==S(m,"Name"));
        bool value=c==null?B(m,"Active",true):Value(c,"Interpolator",t,1)>.5f;activeCache[m]=(t,value);return value;
    }
    float Animated(object m,string property,double t)
    {
        if(animatedCache.TryGetValue((m,property),out var cached)&&cached.Time==t)return cached.Value;
        string controller=property switch{
            "Speed"=>"NiPSysEmitterSpeedCtlr","LifeSpan"=>"NiPSysEmitterLifeSpanCtlr","InitialRadius"=>"NiPSysEmitterInitialRadiusCtlr",
            "Declination"=>"NiPSysEmitterDeclinationCtlr","DeclinationVariation"=>"NiPSysEmitterDeclinationVarCtlr",
            "PlanarAngle"=>"NiPSysEmitterPlanarAngleCtlr","PlanarAngleVariation"=>"NiPSysEmitterPlanarAngleVarCtlr",
            "Strength"=>"NiPSysGravityStrengthCtlr","RotationSpeed"=>"NiPSysInitialRotSpeedCtlr",
            "RotationSpeedVariation"=>"NiPSysInitialRotSpeedVarCtlr","RotationAngle"=>"NiPSysInitialRotAngleCtlr","RotationAngleVariation"=>"NiPSysInitialRotAngleVarCtlr",_=>""};
        var c=controllers.FirstOrDefault(c=>c.GetType().Name==controller&&S(c,"ModifierName")==S(m,"Name"));
        float value=c==null?F(m,property):Value(c,"Interpolator",t,F(m,property));animatedCache[(m,property)]=(t,value);return value;
    }
    ParticleSimulation.Settings Settings(double t,object? source=null)
    {
        var emitter=source??this.emitter;
        var rotation=Modifiers.FirstOrDefault(m=>m is NiPSysRotationModifier);
        return new(){
            Capacity=PreviewBudget>0?Math.Min(AuthoredCapacity,PreviewBudget):AuthoredCapacity,WorldSpace=WorldSpace,
            Shape=emitter is NiPSysMeshEmitter?"Mesh":emitter is NiPSysSphereEmitter?"Sphere":emitter is NiPSysCylinderEmitter?"Cylinder":"Box",
            Dimensions=emitter is NiPSysBoxEmitter?new(F(emitter,"Width"),F(emitter,"Height"),F(emitter,"Depth")):new(F(emitter,"Radius"),0,F(emitter,"Height")),
            Speed=Animated(emitter,"Speed",t),SpeedVariation=F(emitter,"SpeedVariation"),
            Declination=Animated(emitter,"Declination",t),DeclinationVariation=Animated(emitter,"DeclinationVariation",t),
            PlanarAngle=Animated(emitter,"PlanarAngle",t),PlanarVariation=Animated(emitter,"PlanarAngleVariation",t),
            Radius=Animated(emitter,"InitialRadius",t)*Lod(t).Size,RadiusVariation=F(emitter,"RadiusVariation")*Lod(t).Size,Life=Animated(emitter,"LifeSpan",t),LifeVariation=F(emitter,"LifeSpanVariation"),
            Color=Color(Get(emitter,"InitialColor")),Rotation=rotation==null?0:Animated(rotation,"RotationAngle",t),
            RotationVariation=rotation==null?0:Animated(rotation,"RotationAngleVariation",t),Spin=rotation==null?0:Animated(rotation,"RotationSpeed",t),
            SpinVariation=rotation==null?0:Animated(rotation,"RotationSpeedVariation",t),RandomSpinSign=B(rotation,"RandomRotSpeedSign"),
            HasRotationAxis=rotation!=null&&B(Data,"HasRotationAxes"),RandomRotationAxis=B(rotation,"RandomAxis"),RotationAxis=V(rotation!,"Axis",Vector3.UnitX)
        };
    }
    public void Seek(double time){
        placementCache.Clear();collisionChainTime=double.NaN;evaluatedTime=double.NaN;preparedTime=double.NaN;memoVersion++;worldCache.Clear();activeCache.Clear();animatedCache.Clear();
        cachePlacement=true;try{Simulation.Seek(time);Evaluate(time);}finally{cachePlacement=false;placementCache.Clear();}
    }
    // AE billboard generation (E51320) transforms centers by world scale but
    // builds its camera-facing axes from rotation and radius * size only.
    internal static float BillboardRadius(ParticleSimulation.Particle particle)=>particle.Radius;
    public float AspectAtSpeed(float speed)=>ParticleAspect.AtSpeed(F(Data,"AspectRatio",1),F(Data,"SpeedtoAspectAspect2",1),F(Data,"SpeedtoAspectSpeed1"),F(Data,"SpeedtoAspectSpeed2"),speed,ParticleOrientation.SpeedAspect(Convert.ToInt32(Get(Data,"AspectFlags")??0)));
    public float MinimumAspect=>Math.Min(AspectAtSpeed(0),Math.Min(AspectAtSpeed(float.MaxValue),AspectAtSpeed(F(Data,"SpeedtoAspectSpeed1"))));
    public Matrix4x4 DisplayTransform(double time,OrbitCamera? camera=null)=>WorldSpace?Matrix4x4.Identity:camera==null?World(System)*PlacementAt(time):Animation.RenderWorld(System,PlacementAt(time),camera);
    // Per-step resolved modifier parameters. The generic reflection helpers cost microseconds per call and
    // ran for every modifier of every live particle each 120 Hz step; dense authored systems (9,999
    // births per second) made a frame take hundreds of milliseconds. Parameters only depend on the step
    // time, so they are resolved once per (system, time) and the per-particle loop is plain arithmetic.
    sealed class Prepared
    {
        public string Kind="";public object Modifier=null!;public bool Active;
        public Matrix4x4 Transform;public Vector3 Axis,Origin,UnitAxis;public bool Radial,HasObject,AxisValid;
        public float Strength,Decay,Turbulence,TurbulenceScale,Percentage,Range,RangeFalloff,DeltaV;public int DecayType,Symmetry;
        public float GrowTime,FadeTime,BaseScale;public int GrowGeneration,FadeGeneration;
        public float[] Scales=[];public Vector4[] Colors=[];public float A,B,C,D,FadeIn,FadeOut;
        public object? ColorData;
    }
    double preparedTime=double.NaN;Prepared[] prepared=[];
    Prepared[] Prepare(double time)
    {
        if(time==preparedTime)return prepared;
        Evaluate(time);
        var list=new Prepared[Modifiers.Length];
        for(int i=0;i<Modifiers.Length;i++){
            var m=Modifiers[i];var r=new Prepared{Kind=m.GetType().Name,Modifier=m,Active=Active(m,time)};list[i]=r;
            if(!r.Active)continue;
            Matrix4x4 ObjectTransform(string property,out bool has){
                var node=Block(Get(m,property));has=node!=null;if(node==null)return Matrix4x4.Identity;
                var transform=World(node);
                if(WorldSpace)transform*=PlacementAt(time);else{Matrix4x4.Invert(World(System),out var inverse);transform*=inverse;}
                return transform;
            }
            switch(r.Kind){
                case "NiPSysGravityModifier":{
                    r.Transform=ObjectTransform("GravityObject",out r.HasObject);
                    if(r.HasObject&&B(m,"WorldAligned")&&Block(Get(m,"GravityObject")) is NiAVObject gravityNode){
                        // AE DD7875 uses GravityObject.local.rotation directly for the axis.
                        // Keep the previously computed origin for radial distance and decay.
                        var origin=r.Transform.Translation;
                        r.Transform=Matrix(new Transform_Class{Rotation=gravityNode.Rotation,Scale=1}.ToMatrix4());
                        r.Transform.Translation=origin;
                    }
                    r.Radial=Get(m,"ForceType")?.ToString()?.Contains("Spherical",StringComparison.OrdinalIgnoreCase)==true;
                    r.Axis=V(m,"GravityAxis",Vector3.UnitZ);r.Strength=Animated(m,"Strength",time);r.Decay=F(m,"Decay");
                    {var axis=Vector3.TransformNormal(r.Axis,r.Transform);float length=axis.Length();r.AxisValid=float.IsFinite(length)&&length>1e-6f;r.UnitAxis=r.AxisValid?axis/length:Vector3.Zero;r.Origin=r.Transform.Translation;}
                    r.Turbulence=F(m,"Turbulence");r.TurbulenceScale=F(m,"TurbulenceScale",1);break;
                }
                case "NiPSysBombModifier":
                    r.Transform=ObjectTransform("BombObject",out r.HasObject);r.Axis=V(m,"BombAxis",Vector3.UnitZ);
                    r.DeltaV=F(m,"DeltaV");r.Decay=F(m,"Decay");r.DecayType=(int)F(m,"DecayType");r.Symmetry=(int)F(m,"SymmetryType");break;
                case "NiPSysDragModifier":
                    r.Transform=ObjectTransform("DragObject",out r.HasObject);r.Axis=V(m,"DragAxis",Vector3.UnitX);
                    r.Percentage=F(m,"Percentage");r.Range=F(m,"Range",float.MaxValue);r.RangeFalloff=F(m,"RangeFalloff",float.MaxValue);
                    {var axis=Vector3.TransformNormal(r.Axis,r.Transform);float length=axis.Length();r.AxisValid=float.IsFinite(length)&&length>=1e-7f;r.UnitAxis=r.AxisValid?axis/length:Vector3.Zero;r.Origin=Vector3.Transform(Vector3.Zero,r.Transform);}break;
                case "NiPSysGrowFadeModifier":
                    r.GrowTime=F(m,"GrowTime");r.FadeTime=F(m,"FadeTime");r.BaseScale=F(m,"BaseScale",1);r.GrowGeneration=(int)F(m,"GrowGeneration");r.FadeGeneration=(int)F(m,"FadeGeneration");break;
                case "BSPSysScaleModifier":r.Scales=Items(Get(m,"Scales")).Select(x=>Number(x)).ToArray();break;
                case "NiPSysColorModifier":r.ColorData=Get(Block(Get(m,"Data")),"Data");break;
                case "BSPSysSimpleColorModifier":
                    r.Colors=Items(Get(m,"Colors")).Select(Color).ToArray();
                    r.A=F(m,"Color1EndPercent");r.B=F(m,"Color1StartPercent");r.C=F(m,"Color2EndPercent");r.D=F(m,"Color2StartPercent",1);
                    r.FadeIn=F(m,"FadeInPercent");r.FadeOut=F(m,"FadeOutPercent",1);break;
            }
        }
        prepared=list;preparedTime=time;return list;
    }
    void Update(ParticleSimulation.Particle p,float dt,double time)
    {
        var steps=Prepare(double.IsNaN(Simulation.StepTime)?time:Simulation.StepTime);
        bool collisionMoved=false,collisionChecked=false;Vector3? integratedFrom=null;
        float age=p.Age,u=Math.Clamp(age/p.Life,0,1);
        foreach(var r in steps){
            if(!r.Active)continue;
            switch(r.Kind){
                case "BSPSysStripUpdateModifier":
                    p.Strip?.Sample((float)time,p.Position,p.Color,p.Radius,p.Age<=p.Life);break;
                case "NiPSysPositionModifier":if(!collisionMoved){integratedFrom=p.Position;p.Position+=p.Velocity*dt;}break;
                case "NiPSysColliderManager":
                    if(!collisionChecked){
                        var integratedEnd=p.Position;if(integratedFrom is {} start)p.Position=start;
                        collisionMoved=Collide(p,dt,time);
                        if(!collisionMoved&&integratedFrom.HasValue)p.Position=integratedEnd;
                        collisionChecked=true;
                    }
                    if(p.Dead)return;break;
                case "NiPSysGravityModifier":{
                    if(!r.HasObject)break;
                    // Native rand() maps 15-bit integer draws onto the closed [-1,1] interval.
                    float Noise()=>((int)(Simulation.Random01()*32768))*(2f/32767)-1;
                    var noise=r.Turbulence==0?Vector3.Zero:new Vector3(Noise(),Noise(),Noise());
                    p.Velocity+=ParticleForces.Gravity(p.Position,r.Origin,r.UnitAxis,r.Strength,r.Decay,r.Radial,r.Turbulence,r.TurbulenceScale,noise)*dt;
                    break;
                }
                case "NiPSysBombModifier":
                    p.Velocity+=ParticleForces.Bomb(p.Position,r.Transform,r.Axis,r.DeltaV,r.Decay,r.DecayType,r.Symmetry)*dt;break;
                case "NiPSysDragModifier":
                    if(!r.HasObject||!r.AxisValid)break;
                    p.Velocity=ParticleForces.Drag(p.Velocity,p.Position,r.Origin,r.UnitAxis,r.Percentage,r.Range,r.RangeFalloff,dt);break;
                case "NiPSysGrowFadeModifier":{
                    float scale=r.BaseScale;
                    if(r.GrowTime>0&&p.Generation==r.GrowGeneration)scale*=Math.Clamp(age/r.GrowTime,0,1);
                    if(r.FadeTime>0&&p.Generation==r.FadeGeneration)scale*=Math.Clamp((p.Life-age)/r.FadeTime,0,1);
                    p.Radius*=scale;break;
                }
                case "BSPSysScaleModifier":{
                    var values=r.Scales;
                    if(values.Length>0){float at=u*(values.Length-1);int a=(int)at,b=Math.Min(a+1,values.Length-1);p.Radius*=values[a]+(values[b]-values[a])*(at-a);}break;
                }
                case "NiPSysColorModifier":{
                    var value=Sample(r.ColorData,age);if(value!=null)p.Color=Color(value);break;
                }
                case "BSPSysSimpleColorModifier":{
                    var cols=r.Colors;if(cols.Length<3)break;
                    float a=r.A,b=r.B,c=r.C,d=r.D;
                    p.Color=u<a?cols[0]:u<b?Vector4.Lerp(cols[0],cols[1],Fraction(u,a,b)):u<c?cols[1]:u<d?Vector4.Lerp(cols[1],cols[2],Fraction(u,c,d)):cols[2];
                    if(r.FadeIn>0)p.Color.W*=Math.Clamp(u/r.FadeIn,0,1);
                    if(r.FadeOut<1)p.Color.W*=Math.Clamp((1-u)/(1-r.FadeOut),0,1);
                    break;
                }
            }
        }
    }
    readonly Dictionary<object,object[]> colliders=[];
    double collisionChainTime=double.NaN;object[] collisionChain=[];
    void ConfigureColliders()
    {
        foreach(var manager in Modifiers.OfType<NiPSysColliderManager>()){
            var chain=new List<object>();var visited=new HashSet<object>();
            object? collider=Block(Get(manager,"Collider"));
            while(collider!=null){
                if(!visited.Add(collider)){Diagnostics.Add(Name+": collider chain contains a cycle; repeated links are ignored");break;}
                if(collider is NiPSysPlanarCollider or NiPSysSphericalCollider)chain.Add(collider);
                else Diagnostics.Add(Name+": unsupported particle collider "+collider.GetType().Name);
                collider=Block(Get(collider,"NextCollider"));
            }
            colliders[manager]=chain.ToArray();
            if(chain.Count>0)Diagnostics.Add(Name+": swept point colliders supported; moving-surface velocity, particle-radius contacts and exact Skyrim collision response remain unverified");
        }
    }
    ParticleSimulation.SpawnSettings? SpawnSettings(object? spawn,double time)
    {
        if(spawn is not NiPSysSpawnModifier||!Active(spawn,time))return null;
        return new(){
            Generations=(int)F(spawn,"NumSpawnGenerations"),Probability=F(spawn,"PercentageSpawned",1),
            Minimum=(int)F(spawn,"MinNumtoSpawn",1),Maximum=(int)F(spawn,"MaxNumtoSpawn",1),
            SpeedVariation=F(spawn,"SpawnSpeedVariation"),DirectionVariation=F(spawn,"SpawnDirVariation"),
            Life=F(spawn,"LifeSpan",1),LifeVariation=F(spawn,"LifeSpanVariation")
        };
    }
    bool Collide(ParticleSimulation.Particle p,float dt,double time)
    {
        if(dt<=0)return false;
        if(collisionChainTime!=time){
            collisionChain=colliders.Where(pair=>Active(pair.Key,time)).SelectMany(pair=>pair.Value).Distinct().ToArray();
            collisionChainTime=time;
        }
        var chain=collisionChain;if(chain.Length==0)return false;
        float remaining=dt;bool moved=false;
        for(int iteration=0;iteration<8&&remaining>0;iteration++){
            object? selected=null;ParticleCollision.Hit contact=default;
            var end=p.Position+p.Velocity*remaining;
            foreach(var collider in chain){
                var frame=World(Block(Get(collider,"ColliderObject")));
                if(WorldSpace)frame*=PlacementAt(time);
                else {Matrix4x4.Invert(World(System),out var inv);frame*=inv;}
                var hit=ParticleCollision.Sweep(p.Position,end,frame,collider is NiPSysSphericalCollider,
                    F(collider,"Radius"),F(collider,"Width"),F(collider,"Height"),V(collider,"XAxis"),V(collider,"YAxis"));
                if(hit is {} h&&(selected==null||h.Fraction<contact.Fraction)){selected=collider;contact=h;}
            }
            if(selected==null){if(moved)p.Position=end;return moved;}
            moved=true;p.Position=Vector3.Lerp(p.Position,end,contact.Fraction);
            remaining*=1-contact.Fraction;
            p.Dead=B(selected,"DieonCollide");
            if(B(selected,"SpawnonCollide"))
                Simulation.SpawnChildren(p,SpawnSettings(Block(Get(selected,"SpawnModifier")),time-remaining),remaining+(float)Math.Max(0,(Simulation.Tick+1)*ParticleSimulation.Step-time));
            if(p.Dead)return true;
            p.Velocity=ParticleCollision.Reflect(p.Velocity,contact.Normal,F(selected,"Bounce",1));
            // Keep the next sweep off the exact contact plane to avoid a zero-time re-hit.
            p.Position+=contact.Normal*1e-4f;
        }
        return moved;
    }
    static float Fraction(float v,float a,float b)=>Math.Clamp((v-a)/Math.Max(b-a,1e-7f),0,1);
    public Vector4 UV(ParticleSimulation.Particle p)
    {
        var offsets=Items(Get(Data,"SubtextureOffsets"));if(offsets.Length==0)return new(0,0,1,1);
        var mod=Modifiers.FirstOrDefault(m=>m.GetType().Name=="BSPSysSubTexModifier");
        float frame=mod==null?p.FrameSeed*offsets.Length:F(mod,"StartFrame")+p.FrameSeed*F(mod,"StartFrameFudge")+p.Age*F(mod,"FrameCount",30);
        if(mod!=null){float end=F(mod,"EndFrame",offsets.Length-1),loop=F(mod,"LoopStartFrame");if(frame>end)frame=loop+(frame-loop)%Math.Max(1,end-loop+1);}
        int index=Math.Clamp((int)frame,0,offsets.Length-1);
        return offsets[index] is Vector4 cell?CellRect(cell):new(0,0,1,1);
    }
    // A NiPSysData sub-texture cell is stored as (u, width, v, height), as the game reads it (checked against the vanilla
    // 8x8 sheet in fxdirtgiantexplosion.nif: every cell has width and height 0.125). Read as corner pairs, every cell after
    // the first was inside out or grew to cover the sheet. The renderer wants (u0, v0, u1, v1).
    // The sheet cells as stored, for tests.
    public Vector4[] SheetCells=>Items(Get(Data,"SubtextureOffsets")).OfType<Vector4>().ToArray();
    public static Vector4 CellRect(Vector4 cell)=>new(cell.X,cell.Z,cell.X+cell.Y,cell.Z+cell.W);
}

