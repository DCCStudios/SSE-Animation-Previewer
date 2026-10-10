using FO4_Base_Library;
using NiflySharp.Blocks;
using M4=OpenTK.Mathematics.Matrix4;
using V3=OpenTK.Mathematics.Vector3;

namespace SSEPreview;
public sealed partial class Scene
{
    readonly Assets assets;readonly Viewport view;readonly Records? records;
    readonly List<NifAnimation> attachmentAnimations=[];
    readonly Dictionary<NifAnimation,Func<double,bool>> inheritedVisibility=[];
    int addonLoads;
    public SkeletonInstance Skeleton {get;}=new();
    readonly SkeletonInstance effectSkeleton=new();
    public HkxPoseImportSession? Session;
    // A queue binds one pose session per clip.
    readonly List<HkxPoseImportSession?> segmentSessions=[];
    bool HasPose=>Session!=null||segmentSessions.Any(s=>s!=null);
    // The actor pose at a clip or queue time. In a queue, the first BlendTime seconds of each clip after the first
    // blend in from the clip before it, which keeps playing past its hand-over (holding its last frame at its end).
    internal Poses_class? PoseAt(double time,string label)
    {
        if(Clip?.Segments is not {} segments||segmentSessions.Count!=segments.Count)return Session?.BuildPoseAtTime((float)time,label,false).Pose;
        int index=Clip.SegmentIndex(time);var segment=segments[index];
        var pose=segmentSessions[index]?.BuildPoseAtTime((float)segment.Local(time),label,false).Pose;
        double blend=Math.Min(Clip.BlendTime,segment.Length),into=time-segment.Start;
        if(index==0||blend<=1e-6||into>=blend||pose==null)return pose;
        var previous=segmentSessions[index-1]?.BuildPoseAtTime((float)segments[index-1].Continued(time),label,false).Pose;
        if(previous==null)return pose;
        double u=Math.Clamp(into/blend,0,1);
        // Smooth ease in and out, the default curve of a Havok blending transition.
        return BlendPoses(previous,pose,(float)(u*u*(3-2*u)));
    }
    internal static Poses_class BlendPoses(Poses_class from,Poses_class to,float weight)
    {
        if(from.Source!=to.Source)return to;
        var result=new Poses_class{Name=to.Name,Skeleton=to.Skeleton,Version=to.Version,Source=to.Source,Transforms=new(StringComparer.OrdinalIgnoreCase)};
        static System.Numerics.Quaternion Rotation(Transform_Class t){var r=t.Rotation;return System.Numerics.Quaternion.Normalize(System.Numerics.Quaternion.CreateFromRotationMatrix(new System.Numerics.Matrix4x4(r.M11,r.M12,r.M13,0,r.M21,r.M22,r.M23,0,r.M31,r.M32,r.M33,0,0,0,0,1)));}
        var rest=new PoseTransformData();
        foreach(string bone in from.Transforms.Keys.Concat(to.Transforms.Keys).Distinct(StringComparer.OrdinalIgnoreCase)){
            var a=from.Transforms.GetValueOrDefault(bone)??rest;var b=to.Transforms.GetValueOrDefault(bone)??rest;
            var m=System.Numerics.Matrix4x4.CreateFromQuaternion(System.Numerics.Quaternion.Slerp(Rotation(new Transform_Class(a,from.Source)),Rotation(new Transform_Class(b,to.Source)),weight));
            var rotation=Transform_Class.Matrix33ToBSRotation(new NiflySharp.Structs.Matrix33{M11=m.M11,M12=m.M12,M13=m.M13,M21=m.M21,M22=m.M22,M23=m.M23,M31=m.M31,M32=m.M32,M33=m.M33});
            float Mix(float x,float y)=>x+(y-x)*weight;
            result.Transforms[bone]=new PoseTransformData{Yaw=rotation.X,Pitch=rotation.Y,Roll=rotation.Z,X=Mix(a.X,b.X),Y=Mix(a.Y,b.Y),Z=Mix(a.Z,b.Z),Scale=Mix(a.Scale,b.Scale),ScaleX=Mix(a.ScaleX,b.ScaleX),ScaleY=Mix(a.ScaleY,b.ScaleY),ScaleZ=Mix(a.ScaleZ,b.ScaleZ)};
        }
        return result;
    }
    public AnimationClip? Clip;
    public string SkeletonHkx=@"meshes\actors\character\character assets\skeleton.hkx";
    readonly List<IRenderableShape> shapes=[];
    readonly Dictionary<IRenderableShape,(NifAnimation Animation,EffectCue Cue)> effectShapes=[];
    readonly Dictionary<IRenderableShape,M4> bases=[];
    readonly Dictionary<IRenderableShape,Color> hairTints=[];
    Dictionary<string,Nifcontent_Class_Manolo> preparedModels=new(StringComparer.OrdinalIgnoreCase);
    public async Task PrepareModelsAsync(IEnumerable<string> paths){
        var files=paths.Where(p=>!string.IsNullOrWhiteSpace(p)).Select(p=>Path.IsPathRooted(p)?p:ProjectileVisuals.ModelPath(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        preparedModels=await Task.Run(()=>{
            var result=new Dictionary<string,Nifcontent_Class_Manolo>(StringComparer.OrdinalIgnoreCase);
            foreach(string file in files){if(!assets.Exists(file))continue;try{var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(file));result[Assets.Normalize(file)]=nif;}catch{ /* AddModel reports parse errors with its source path. */ }}
            return result;
        });
    }
    internal double LastUpdateMs,LastSimulationMs,LastEffectDrawMs;
    long evaluationFrame;
    bool committing;
    internal M4[] WeaponTransforms=>view.Model.meshes.Where(m=>weaponShapes.Contains(m.MeshData.Shape)).Select(m=>m.MeshData.Transform).ToArray();
    readonly Dictionary<IRenderableShape,M4> effectBindInverse=[];
    readonly HashSet<IRenderableShape> weaponShapes=[];
    readonly Dictionary<int,List<IRenderableShape>> bipedShapes=[];
    readonly Dictionary<EffectCue,System.Numerics.Matrix4x4?> attachmentFrames=[];
    readonly Dictionary<(int Biped,double Time),System.Numerics.Matrix4x4> attachmentNodes=[];
    readonly Dictionary<(string Node,double Time),System.Numerics.Matrix4x4> namedAttachmentNodes=[];
    readonly List<(ParticleNif System,EffectCue? Cue)> particles=[];
    readonly ParticleRenderer particleRenderer;
    readonly GridFloor floor=new();
    public bool ShowFloor{get=>floor.Enabled;set{
        floor.Enabled=value;
        var rig=Config_App.Current.ActiveLights();rig.ShadowOnGround=value;Config_App.Current.SetActiveLights(rig);
        view.Model.FloorOffset=0;view.UpdateRequired=true;view.Invalidate();
    }}
    public int FloorDrawn=>floor.LastDrawn;
    public ParticleNif.MeshEmitterSource[] MeshEmitters=>particles.SelectMany(p=>p.System.MeshSourceBindings).ToArray();
    public void RefreshEmitterDiagnostics()
    {
        static bool BindingMessage(string d)=>d.Contains(": mesh emitter")||d.Contains(": emitter mesh ")||d.Contains(": mesh emission ")||d.Contains(": skinned mesh emitter")||d.Contains(": preview emitter override");
        Diagnostics.RemoveAll(BindingMessage);
        Diagnostics.AddRange(MeshEmitters.SelectMany(item=>item.Diagnostics).Where(BindingMessage).Distinct());
        foreach(var item in MeshEmitters){
            if(item.HasEmitterOverride)Diagnostics.Add(item.Name+": preview emitter override; authored NIF remains unchanged");
        }
    }
    float normalizedLodDistance;
    public void SetParticleLodDistance(float distance)
    {
        normalizedLodDistance=Math.Max(0,distance);
        foreach(var item in particles)item.System.NormalizedLodDistance=normalizedLodDistance;
        ResetParticles();SetTime(Time);
    }
    internal IEnumerable<ParticleNif> ParticleInstances=>particles.Select(p=>p.System);
    public int ParticleCount=>particles.Sum(p=>p.System.Simulation.Particles.Count);
    public long BudgetDrops=>particles.Sum(p=>p.System.Simulation.CapacityDrops);
    public IEnumerable<string> BudgetDiagnostics=>particles.Where(p=>p.System.Simulation.CapacityDrops>0).Select(p=>$"{p.System.Name}: {p.System.Simulation.CapacityDrops:N0} births dropped by the {(ParticleNif.PreviewBudget>0&&ParticleNif.PreviewBudget<p.System.AuthoredCapacity?"preview particle budget":"authored particle limit")} ({p.System.Simulation.Config.Capacity:N0}, authored {p.System.AuthoredCapacity:N0})");
    // Diagnostic only: particle system names whose draw is skipped (attribution runs), set from the profiler.
    public readonly HashSet<string> HiddenSystems=new(StringComparer.OrdinalIgnoreCase);
    public int ParticleSystems=>particles.Count;
    public int DrawnParticles=>particleRenderer.LastDrawn;public string ParticleDebug=>particleRenderer.DebugState;
    IEnumerable<NifAnimation> EffectAnimations=>effectShapes.Values.Select(e=>e.Animation).Concat(particles.Select(p=>p.System.Animation)).Concat(attachmentAnimations).Distinct();
    public double EffectDuration=>EffectAnimations.Select(a=>a.AuthoredDuration).DefaultIfEmpty(0).Max();
    public int BehaviorGraphCount=>EffectAnimations.Count(a=>a.Behavior!=null);
    public string[] SequenceNames=>EffectAnimations.SelectMany(a=>a.SequenceNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public void SelectSequence(string? sequence)
    {
        foreach(var animation in EffectAnimations)animation.SelectedSequence=sequence;
        ResetParticles();SetTime(Time);
    }
    public void ResetParticles(){attachmentFrames.Clear();attachmentNodes.Clear();namedAttachmentNodes.Clear();foreach(var animation in EffectAnimations)animation.ResetMotion();foreach(var p in particles)p.System.Simulation.Reset();}
    public void Dispose(){DisposePhysicsWorld();smpDraw.Dispose();precisionDraw.Dispose();rootArrow.Dispose();skeletonDraw.Dispose();boneLabels.Dispose();particleRenderer.Dispose();refractionRenderer.Dispose();floor.Dispose();}
    // Runs inside the engine model pass, after its own floor and before any mesh, so blended effect
    // meshes and particles compose over the ground.
    void DrawFloor(M4 projection)
    {
        floor.Draw(projection,view.camera);
        if(floor.Diagnostic is {} issue&&!Diagnostics.Contains(issue))Diagnostics.Add(issue);
    }
    readonly RefractionRenderer refractionRenderer;
    readonly Dictionary<IRenderableShape,Nifcontent_Class_Manolo> modelOwners=[];
    readonly WeightBlend weightBlend=new();
    public int WeightedShapes=>weightBlend.Count;
    internal bool IsWeighted(IRenderableShape shape)=>weightBlend.Shapes.Contains(shape);
    internal bool MeshIsWeapon(IRenderableShape shape)=>weaponShapes.Contains(shape);
    // Body weight 0..100 across every _0/_1 mesh pair in the scene; reapplied through the morph pass without a reload.
    public float Weight{get=>weightBlend.Weight;set{if(weightBlend.Weight==value)return;weightBlend.Weight=value;weightChangedAt=physicsClock.Elapsed.TotalSeconds;if(weightBlend.Count>0){view.Intent.MarkDirty(RenderDirtyFlags.Morphs,weightBlend.Shapes);view.InvalidateRender();}view.UpdateRequired=true;view.Invalidate();}}
    readonly List<PreviewModel.RenderableMesh> refractionMeshes=[];
    internal bool SubmittedForDrawing(PreviewModel.RenderableMesh mesh)=>!mesh.MeshData.Shape.RenderHide||refractionMeshes.Contains(mesh);
    internal bool RefractionEnabled=true;
    public int DrawnRefractionLayers=>refractionRenderer.LastDrawn;
    public List<string> Diagnostics {get;}=[];
    public enum FrameSubject { Everything,Character,Effects }
    public FrameSubject FramingSubject;
    public bool RootMotion=true,ShowEffects=true,FollowScene;
    // Hides the equipped weapon meshes only; particle spawn placement and weapon-node following still use them.
    bool showWeapon=true;public bool ShowWeapon{get=>showWeapon;set{showWeapon=value;view.UpdateRequired=true;view.Invalidate();}}
    // Sheathed placement: the game parents the weapon to its type's holster node with an identity local
    // transform, so the same mesh base transform applies under a different bone. Weapon-attached particle
    // spawns and following use the same bone, so effects stay on the blade wherever it hangs.
    bool sheathed;string? holsterNode;
    public bool Sheathed{get=>sheathed;set{sheathed=value;attachmentFrames.Clear();attachmentNodes.Clear();view.UpdateRequired=true;view.Invalidate();}}
    public string? HolsterNode{get=>holsterNode;set{holsterNode=value;attachmentFrames.Clear();attachmentNodes.Clear();}}
    // The bone the weapon currently hangs from: the hand, or the holster node when sheathed and present.
    // A clip with weaponDraw / weaponSheathe annotations decides the state per time and overrides the manual toggle.
    public bool ClipDrivesWeapon=>Clip!=null&&Clip.WeaponEvents.Count>0;
    public bool SheathedAt(double time)=>Clip?.WeaponSheathedAt(time)??sheathed;
    public string WeaponBoneNameAt(double time)=>SheathedAt(time)&&holsterNode!=null&&Skeleton.SkeletonDictionary.ContainsKey(holsterNode)?holsterNode:"Weapon";
    public string WeaponBoneName=>WeaponBoneNameAt(Time);
    public bool HolsterNodeMissing=>(sheathed||ClipDrivesWeapon)&&holsterNode!=null&&!Skeleton.SkeletonDictionary.ContainsKey(holsterNode);
    bool WeaponBone(double time,out HierarchiBone_class bone)=>Skeleton.SkeletonDictionary.TryGetValue(WeaponBoneNameAt(time),out bone!);
    public int VisibleWeaponMeshes=>view.Model.meshes.Count(m=>weaponShapes.Contains(m.MeshData.Shape)&&!m.MeshData.Shape.RenderHide);
    public int VisibleEffectMeshes=>view.Model.meshes.Count(m=>effectShapes.ContainsKey(m.MeshData.Shape)&&!m.MeshData.Shape.RenderHide);
    internal (V3 Grip,V3 Tip) WeaponBladeSegment()
    {
        if(!WeaponBone(Time,out var bone))throw new InvalidDataException("Weapon attachment is unavailable.");
        var motion=RootMotion&&Clip!=null?Clip.Motion(Time):(System.Numerics.Vector3.Zero,0f);
        var actor=M4.CreateRotationZ(motion.Item2)*M4.CreateTranslation(motion.Item1.X,motion.Item1.Y,motion.Item1.Z);
        V3 grip=V3.TransformPosition(V3.Zero,bone.GetGlobalTransform.ToMatrix4()*actor);
        V3 tip=grip;float farthest=0;
        foreach(var mesh in view.Model.meshes.Where(m=>weaponShapes.Contains(m.MeshData.Shape))){
            foreach(var vertex in mesh.MeshData.Meshgeometry.Vertices){
                var point=V3.TransformPosition(new V3((float)vertex.X,(float)vertex.Y,(float)vertex.Z),mesh.MeshData.Transform);
                float distance=(point-grip).LengthSquared;
                if(distance>farthest){farthest=distance;tip=point;}
            }
        }
        if(farthest<100)throw new InvalidDataException("Buster Sword geometry did not produce a blade tip.");
        return(grip,tip);
    }

    public double Time;
    public Scene(Assets a,Viewport v,Records? records=null){assets=a;view=v;this.records=records;view.BeforeSceneFrame+=BeforeFrame;particleRenderer=new(a);refractionRenderer=new(a);view.AfterSceneFrame+=AfterFrame;view.BeforeSceneMeshes+=DrawFloor;}
    public void Clear()
    {
        hairTints.Clear();ClearPhysics();weaponMeshLength=null;
        attachmentAnimations.Clear();inheritedVisibility.Clear();addonLoads=0;modelOwners.Clear();weightBlend.Clear();pooledCues.Clear();bipedShapes.Clear();attachmentFrames.Clear();attachmentNodes.Clear();namedAttachmentNodes.Clear();particleRenderer.InvalidateTextures();refractionRenderer.InvalidateTextures();refractionMeshes.Clear();particles.Clear();shapes.Clear();effectShapes.Clear();effectBindInverse.Clear();bases.Clear();weaponShapes.Clear();Session=null;segmentSessions.Clear();Skeleton.Reset();Diagnostics.Clear();
    }
    public void LoadSkeleton(string path)
    {
        if(!assets.Exists(path)){Diagnostics.Add("Missing skeleton NIF: "+path);return;}
        Skeleton.LoadFromBytes(assets.Read(path));
        ReadBoneCapsules(path);
        string hkx=Path.ChangeExtension(path,"hkx");
        SkeletonHkx=assets.Exists(hkx)?hkx:SkeletonHkx;
        if(assets.Exists(SkeletonHkx))Skeleton.MergeHkxSkeleton(assets.Read(SkeletonHkx));
    }
    public void AddModel(string path,bool weapon=false,EffectCue? cue=null,string[]? textureOverride=null,Color? hairColor=null,uint bipedSlots=0,int weaponIndex=-1,string[]? skinTextureOverride=null,Func<double,System.Numerics.Matrix4x4>? placementOverride=null,Func<double,bool>? visibilityOverride=null,HashSet<string>? addonChain=null,Func<double,float>? addonScale=null,Color? skinColor=null,IReadOnlyList<EffectCue>? pool=null)
    {
        poolAccepted=false;
        path=Assets.Normalize(path);if(!path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase)&&!Path.IsPathRooted(path))path=@"meshes\"+path;
        if(!assets.Exists(path)&&!Path.IsPathRooted(path)){Diagnostics.Add("Missing model: "+path);return;}
        try {
            if(!preparedModels.Remove(path,out var nif)){nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(path));}
            MaterialOverrides.Apply(nif,textureOverride,skinTextureOverride);
            if(hairColor is {} tint)foreach(var shader in nif.Blocks.OfType<BSLightingShaderProperty>())
                if(shader.IsTypeHairTint)shader.HairTintColor=new NiflySharp.Structs.Color3{R=tint.R/255f,G=tint.G/255f,B=tint.B/255f};
            if(skinColor is {} skinTint)foreach(var shader in nif.Blocks.OfType<BSLightingShaderProperty>())
                if(shader.IsTypeSkinTint)shader.SkinTintColor=new NiflySharp.Structs.Color3{R=skinTint.R/255f,G=skinTint.G/255f,B=skinTint.B/255f};
            var parsed=NifRenderableShape.FromNif(nif);
            if(cue==null)RegisterPhysicsModel(path,nif,parsed,weapon);
            // A stateless mesh effect (no particle systems, addon nodes or behavior graph) looks the same whichever
            // trigger shows it, so non-overlapping repeats of one cue can share this loaded instance.
            if(pool!=null&&cue!=null&&!nif.Blocks.OfType<NiParticleSystem>().Any()&&!nif.Blocks.OfType<BSValueNode>().Any()&&!nif.Blocks.OfType<BSBehaviorGraphExtraData>().Any()){
                pooledCues[cue]=pool.OrderBy(c=>c.Time).ToArray();poolAccepted=true;
            }
            if(cue==null&&!weapon&&WeightBlend.CounterpartPath(path,out bool loadedIsHeavy) is {} counterpartPath){
                if(assets.Exists(counterpartPath)){
                    try{var other=new Nifcontent_Class_Manolo();other.Load_Manolo(assets.Read(counterpartPath));weightBlend.Register(parsed,other,loadedIsHeavy,Diagnostics,path);}
                    catch(Exception ex){Diagnostics.Add(path+": weight counterpart "+counterpartPath+" failed to load: "+ex.Message);}
                }else Diagnostics.Add(path+": weight counterpart "+counterpartPath+" is missing; the mesh stays at its loaded weight.");
            }
            EffectSequenceGraph? behavior=null;
            string graphPath=nif.Blocks.OfType<BSBehaviorGraphExtraData>().FirstOrDefault()?.BehaviourGraphFile.ToString()??"";
            if(graphPath.Length>0){
                try{
                    behavior=EffectSequenceGraph.Load(assets,graphPath);
                    if(behavior!=null)foreach(var name in behavior.Sequences.Except(nif.Blocks.OfType<NiControllerSequence>().Select(s=>s.Name.ToString())))
                        Diagnostics.Add(path+": behavior requests missing NIF sequence "+name+"; a stopped art effect remains subject to the native three-second timeout.");
                }
                catch(Exception ex){Diagnostics.Add(path+": behavior graph "+graphPath+" unavailable/unsupported: "+ex.Message);}
            }
            Func<double,System.Numerics.Matrix4x4> modelPlacement=placementOverride??(t=>cue==null?ActorPlacement(t):EffectPlacement(Live(cue),t));
            string? namedTarget=NamedNodeAttachment.Read(nif,out var attachIssue);
            if(attachIssue!=null)Diagnostics.Add(path+": "+attachIssue);
            // A cue may request a skeleton node (casting art on the magic node); the NIF's own AttachT wins.
            if(cue is {BladeSpace:true}&&placementOverride==null){
                // PIE.@FF7VFX weapon rows: authored in blade space, kept on the WEAPON node as the plugin does each frame.
                string? weaponNode=Skeleton.SkeletonDictionary.Keys.FirstOrDefault(k=>k.Equals("WEAPON",StringComparison.OrdinalIgnoreCase));
                if(weaponNode!=null)modelPlacement=t=>{var live=Live(cue);return live.LocalPlacement(t)*BladeMatrix()*NamedAttachmentNode(weaponNode,live.Time+t)*ActorPlacement(live.Time+t);};
                else Diagnostics.Add(path+": the skeleton has no WEAPON node; the weapon effect is placed at the actor root.");
                namedTarget=null;
            }
            else if(namedTarget==null&&cue?.AttachNode!=null&&placementOverride==null){namedTarget=cue.AttachNode;Diagnostics.Add(path+": casting art follows "+cue.AttachNode+" as the game parents it to the casting hand; left-hand, dual and staff casts are not distinguished.");}
            if(namedTarget!=null){
                string? bone=Skeleton.SkeletonDictionary.Keys.FirstOrDefault(k=>k.Equals(namedTarget,StringComparison.OrdinalIgnoreCase));
                if(placementOverride==null&&cue is {FollowActor:true,Biped:<0,WorldSpace:false}&&bone!=null){
                    modelPlacement=t=>{var live=Live(cue);return live.LocalPlacement(t)*NamedAttachmentNode(bone,live.Time+t)*ActorPlacement(live.Time+t);};
                    Diagnostics.Add(path+$": AttachT NamedNode {namedTarget} follows animated skeleton node {bone}; node aliases and additional attachment techniques remain unsupported.");
                }else Diagnostics.Add(path+$": AttachT NamedNode {namedTarget} uses root placement: "+(bone==null?"named skeleton node is unavailable.":"this preview has no supported actor attachment context."));
            }
            void Configure(NifAnimation a){a.Placement=modelPlacement;if(visibilityOverride!=null)inheritedVisibility[a]=visibilityOverride;a.Behavior=behavior;if(cue!=null&&(cue.FollowActor||cue.ArtLifecycle))a.StopAt=Math.Min(cue.Lifetime,cue.RemovedAt-cue.Time);}
            var animation=cue==null?null:new NifAnimation(nif);
            if(animation!=null)Configure(animation);
            if(animation!=null)Diagnostics.AddRange(animation.Diagnostics.Select(d=>path+": "+d));
            var emitterSources=nif.Blocks.OfType<NiPSysMeshEmitter>().SelectMany(e=>NifAnimation.Items(e.EmitterMeshes)).OfType<NiflySharp.INiRef>().Select(r=>nif.GetBlock(r)).ToHashSet();
            var meshTemplates=ParticleMeshArtwork.TemplateObjects(nif);
            foreach(var shape in parsed) {
                if(meshTemplates.Contains((NiAVObject)shape.NifShape))continue;
                // A source mesh with no shader describes an emitter volume, not visible effect artwork.
                if(shape.NifShader==null&&emitterSources.Contains(shape.NifShape))continue;
                // Skyrim draws nothing for geometry without a shader property. On armor, hair and weapons such
                // shapes are HDT-SMP collision proxies (VirtualGround, body colliders); they stay available to physics.
                if(shape.NifShader==null&&cue==null){Diagnostics.Add($"{path}: shape {shape.NifShape?.Name} has no shader property and is not drawn (collision proxy).");continue;}
                modelOwners[shape]=nif;shapes.Add(shape);if(hairColor is {} resolvedHair&&shape.NifShader?.IsTypeHairTint==true)hairTints[shape]=resolvedHair;if(weapon)weaponShapes.Add(shape);
                void Register(int index){if(!bipedShapes.TryGetValue(index,out var group))bipedShapes[index]=group=[];group.Add(shape);}
                for(int index=0;index<32;index++)if((bipedSlots&(1u<<index))!=0)Register(index);
                if(weapon&&weaponIndex>=32&&weaponIndex<42)Register(weaponIndex);
                if(cue!=null){effectShapes[shape]=(animation!,cue);effectBindInverse[shape]=animation!.World(shape.NifShape).Inverted();}

            }
            foreach(var ps in nif.Blocks.OfType<NiParticleSystem>()){
                if(meshTemplates.Contains(ps)){Diagnostics.Add(path+": nested mesh-template particles are unsupported and suppressed");continue;}
                var system=new ParticleNif(nif,ps,(uint)(particles.Count+1));
                Configure(system.Animation);
                system.NormalizedLodDistance=normalizedLodDistance;
                if(placementOverride!=null){system.AddonEmitterTarget=placementOverride;system.AddonEmitterScale=addonScale;}
                system.Placement=modelPlacement;
                if(cue!=null){var active=system.Simulation.EmissionEnabled;system.Simulation.EmissionEnabled=t=>(system.Animation.Behavior!=null&&(cue.FollowActor||cue.ArtLifecycle)?!system.Animation.Finished(t):t<cue.Lifetime&&cue.Time+t<cue.RemovedAt)&&(cue.Biped<0||AttachmentFrame(cue)!=null)&&(visibilityOverride?.Invoke(t)??true)&&active(t);}
                particles.Add((system,cue));Diagnostics.AddRange(system.Diagnostics.Select(d=>path+": "+d));
            }
            bool linked=false;
            foreach(var node in nif.Blocks.OfType<BSValueNode>()){
                var addon=AddonArtwork.Resolve(records,node.Value,out var issue);
                if(addon==null){Diagnostics.Add($"{path}: {node.Name}: {issue}");continue;}
                string child=AddonArtwork.Model(addon);
                if(child.Length==0){Diagnostics.Add($"{path}: {addon.EditorId} has no ADDN mesh.");continue;}
                var chain=addonChain==null?new HashSet<string>(StringComparer.OrdinalIgnoreCase):new HashSet<string>(addonChain,StringComparer.OrdinalIgnoreCase);
                chain.Add(path);
                if(chain.Contains(child)||chain.Count>=16||addonLoads>=256){Diagnostics.Add($"{path}: ADDN artwork recursion/instance limit reached for {child}.");continue;}
                addonLoads++;
                var parent=animation??new NifAnimation(nif);if(animation==null)Configure(parent);
                if(!attachmentAnimations.Contains(parent))attachmentAnimations.Add(parent);
                long cachedFrame=-1;
                var samples=new Dictionary<double,(System.Numerics.Matrix4x4 Transform,float Scale,bool Visible)>();
                (System.Numerics.Matrix4x4 Transform,float Scale,bool Visible) SampleAttachment(double age){
                    if(cachedFrame!=evaluationFrame){samples.Clear();cachedFrame=evaluationFrame;}
                    if(samples.TryGetValue(age,out var saved))return saved;
                    parent.Evaluate(age);
                    var transform=ParticleNif.Matrix(Transform_Class.GetGlobalTransform(node,nif).ToMatrix4())*parent.Placement(age);
                    float scale=node.Scale;bool visible=parent.VisibleNode(node)&&(visibilityOverride?.Invoke(age)??true);
                    return samples[age]=(transform,scale,visible);
                }
                System.Numerics.Matrix4x4 Attached(double age)=>SampleAttachment(age).Transform;
                float LocalScale(double age)=>SampleAttachment(age).Scale;
                bool Visible(double age)=>SampleAttachment(age).Visible;
                var childCue=cue??new EffectCue(0,child,0,0,double.PositiveInfinity,1,System.Numerics.Vector3.Zero,"ADDN artwork",FollowActor:true);
                AddModel(child,cue:childCue,placementOverride:Attached,visibilityOverride:Visible,addonChain:chain,addonScale:LocalScale);
                linked=true;
                Diagnostics.Add($"{path}: {node.Name} (index {node.Value}) -> {addon.EditorId} [{addon.Key}]: {child}. Animated node placement is previewed; native master-particle caps, value-node flags, sound and load scheduling remain unverified.");
            }
            if(!linked&&parsed.Count==0&&!nif.Blocks.OfType<NiParticleSystem>().Any())Diagnostics.Add((nif.Blocks.OfType<NiflySharp.INiShape>().Any()?"Geometry exists but could not be loaded: ":"No visual geometry is authored in this NIF; linked scripts/records may supply separate effects: ")+path);
        }catch(Exception ex){Diagnostics.Add(path+": "+ex.Message);}
    }
    long textureRevision=-1;
    public void Commit(bool resetCamera=true)
    {
        MergeModelBones();
        Skeleton.PrepareForShapes(shapes.Where(s=>!effectShapes.ContainsKey(s)));
        // RenderShapes may draw synchronously. Capture local matrices before any
        // attachment callback can replace them with animated world transforms.
        committing=true;cameraWasReset=resetCamera&&!view.PlayingAnimation;poseShapes=shapes.Where(s=>!effectShapes.ContainsKey(s)).ToArray();
        // A playback or physics tick may have marked shapes of the previous scene that no render has consumed yet. The
        // engine restricts the reload's morph and base-geometry pass to marked shapes, so stale entries would leave every
        // new mesh at its loaded _1 weight. A full reload covers all shapes.
        view.Intent.DirtyShapes.Clear();
        try {view.RenderShapes(new RenderRequest {Shapes=shapes.ToArray(),SkeletonResolver=new SingleInstanceSkeletonResolver(Skeleton),BaseGeometryProvider=weightBlend,ResetCamera=resetCamera,PreserveTextureCache=textureRevision==assets.Revision});}
        finally {committing=false;}
        foreach(var mesh in view.Model.meshes)if(hairTints.TryGetValue(mesh.MeshData.Shape,out var tint)&&mesh.MeshData.Material.MaterialBase is {} material){material.HairTintColor=tint;material.TintColorScale=2;}
        preparedModels.Clear();
        textureRevision=assets.Revision;
        bases.Clear();foreach(var m in view.Model.meshes)bases[m.MeshData.Shape]=m.MeshData.Transform;
        RebuildPhysics();
    }
    public void BindClip(AnimationClip clip)
    {
        attachmentFrames.Clear();attachmentNodes.Clear();namedAttachmentNodes.Clear();Clip=clip;Session=null;segmentSessions.Clear();physicsJump=true;
        if(!Skeleton.HasSkeleton){Diagnostics.Add("Animation decoded; load a race or skeleton NIF to display its pose.");return;}
        byte[]? hkx=assets.Exists(SkeletonHkx)?assets.Read(SkeletonHkx):null;
        if(clip.Segments is {} segments){
            foreach(var segment in segments){
                try{segmentSessions.Add(HkxPoseImportSession.Create(hkx,segment.Clip.Bytes,Skeleton,segment.Name,SkeletonHkx));}
                catch(Exception ex){segmentSessions.Add(null);Diagnostics.Add($"Animation binding ({segment.Name}): {ex.Message}");}
            }
            return;
        }
        try {Session=HkxPoseImportSession.Create(hkx,clip.Bytes,Skeleton,"Selected animation",SkeletonHkx);}
        catch(Exception ex){Diagnostics.Add("Animation binding: "+ex.Message);}
    }
    public void SetTime(double t)
    {
        NotePhysicsTime(t);Time=t;
        if(HasPose) {Skeleton.ApplyPose(PoseAt(t,"Preview"));
            // Only actor shapes follow the actor pose. Effect meshes are placed by their own controllers in BeforeFrame,
            // and the engine's pose step costs tens of microseconds per dirty mesh.
            if(poseShapes.Length>0&&Environment.GetEnvironmentVariable("SSE_POSE_ALL_SHAPES")!="1")view.Intent.MarkDirty(RenderDirtyFlags.Pose,poseShapes);else view.Intent.MarkDirty(RenderDirtyFlags.Pose);}
        view.UpdateRequired=true;view.Invalidate();
    }
    System.Numerics.Matrix4x4 ActorPlacement(double time)
    {
        var motion=RootMotion&&Clip!=null?Clip.Motion(time):(System.Numerics.Vector3.Zero,0f);
        return System.Numerics.Matrix4x4.CreateRotationZ(motion.Item2)*System.Numerics.Matrix4x4.CreateTranslation(motion.Item1);
    }
    public System.Numerics.Matrix4x4? AttachmentFrame(EffectCue cue)
    {
        if(attachmentFrames.TryGetValue(cue,out var cached))return cached;
        if(!bipedShapes.TryGetValue(cue.Biped,out var parts)||parts.Count==0){
            Diagnostics.Add($"Particle biped {cue.Biped}: no equipped part; effect suppressed.");
            return attachmentFrames[cue]=null;
        }
        var oldPose=Skeleton.Pose;
        try{
            if(HasPose)Skeleton.ApplyPose(PoseAt(cue.Time,"Effect spawn"));
            var actor=ActorPlacement(cue.Time);
            var rotation=actor;rotation.Translation=System.Numerics.Vector3.Zero;
            var low=new System.Numerics.Vector3(float.MaxValue);var high=new System.Numerics.Vector3(float.MinValue);bool any=false;
            foreach(var shape in parts){
                var geometry=SkinningHelper.ExtractSkinnedGeometry(shape,false,false,Skeleton);
                var placement=actor;
                if(weaponShapes.Contains(shape)&&WeaponBone(cue.Time,out var bone)){
                    placement=ParticleNif.Matrix(bone.GetGlobalTransform.ToMatrix4())*actor;
                    rotation=placement;rotation.Translation=System.Numerics.Vector3.Zero;
                }
                foreach(var vertex in SkinningHelper.GetWorldVertices(ref geometry)){
                    var world=System.Numerics.Vector3.Transform(new((float)vertex.X,(float)vertex.Y,(float)vertex.Z),placement);
                    if(!float.IsFinite(world.X)||!float.IsFinite(world.Y)||!float.IsFinite(world.Z))continue;
                    low=System.Numerics.Vector3.Min(low,world);high=System.Numerics.Vector3.Max(high,world);any=true;
                }
            }
            if(!any){Diagnostics.Add($"Particle biped {cue.Biped}: no usable geometry bounds; effect suppressed.");return attachmentFrames[cue]=null;}
            // Reconstructed displayed-geometry bounds; Skyrim's hierarchical NiBound center can differ.
            if(System.Numerics.Matrix4x4.Decompose(rotation,out _,out var orientation,out _))
                rotation=System.Numerics.Matrix4x4.CreateFromQuaternion(orientation);
            rotation.Translation=(low+high)*.5f;
            Diagnostics.Add($"Particle biped {cue.Biped}: spawn uses animated geometry bounds at {cue.Time:F3}s; target following uses the weapon node or actor root; exact partClone node and NiBound require comparison.");
            return attachmentFrames[cue]=rotation;
        }finally{Skeleton.ApplyPose(oldPose);}
    }
    internal System.Numerics.Matrix4x4 NamedAttachmentNode(string node,double time)
    {
        if(namedAttachmentNodes.TryGetValue((node,time),out var cached))return cached;
        var oldPose=Skeleton.Pose;
        try{
            if(HasPose)Skeleton.ApplyPose(PoseAt(time,"Named effect attachment"));
            var result=ParticleNif.Matrix(Skeleton.SkeletonDictionary[node].GetGlobalTransform.ToMatrix4());
            if(namedAttachmentNodes.Count>4096)namedAttachmentNodes.Clear();return namedAttachmentNodes[(node,time)]=result;
        }finally{Skeleton.ApplyPose(oldPose);}
    }
    System.Numerics.Matrix4x4 AttachmentNode(int biped,double time)
    {
        if(attachmentNodes.TryGetValue((biped,time),out var result))return result;
        var oldPose=Skeleton.Pose;
        try{
            if(HasPose)Skeleton.ApplyPose(PoseAt(time,"Particle attachment"));
            result=ActorPlacement(time);
            if(bipedShapes.TryGetValue(biped,out var parts)&&parts.Any(weaponShapes.Contains)&&WeaponBone(time,out var bone))
                result=ParticleNif.Matrix(bone.GetGlobalTransform.ToMatrix4())*result;
            // Nonweapon equipment currently uses actor-root motion; its exact partClone node is not reconstructed.
            if(attachmentNodes.Count>4096)attachmentNodes.Clear();
            return attachmentNodes[(biped,time)]=result;
        }finally{Skeleton.ApplyPose(oldPose);}
    }
    public System.Numerics.Matrix4x4? FollowingAttachmentFrame(EffectCue cue,double age)
    {
        var initial=AttachmentFrame(cue);if(initial==null||age<=0)return initial;
        if(!System.Numerics.Matrix4x4.Invert(AttachmentNode(cue.Biped,cue.Time),out var inverse))return initial;
        // AE temporary particle update composes current target world transform with
        // the effect's stored target-relative transform. No spawn-flag gate is used.
        return initial.Value*inverse*AttachmentNode(cue.Biped,cue.Time+Math.Max(0,age));
    }
    // Where a loaded effect instance sits at an age, from the placement it was given when it was loaded.
    internal System.Numerics.Matrix4x4? CuePlacement(EffectCue cue,double age)
    {
        foreach(var (animation,owner) in effectShapes.Values)if(ReferenceEquals(owner,cue))return animation.Placement(age);
        foreach(var (system,owner) in particles)if(ReferenceEquals(owner,cue))return system.Animation.Placement(age);
        return null;
    }
    static M4 RenderMatrix(System.Numerics.Matrix4x4 m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
    internal System.Numerics.Matrix4x4 EffectPlacement(EffectCue cue,double age)
    {
        return cue.LocalPlacement(age)*(cue.WorldSpace?System.Numerics.Matrix4x4.Identity:cue.Biped>=0?FollowingAttachmentFrame(cue,age)??System.Numerics.Matrix4x4.Identity:ActorPlacement(cue.FollowActor?cue.Time+age:cue.PlacementTime??cue.Time));
    }
    bool CueVisible(EffectCue? cue,NifAnimation animation,double age)=>
        age>=0&&(inheritedVisibility.GetValueOrDefault(animation)?.Invoke(age)??true)&&(animation.Behavior!=null&&cue!=null&&(cue.FollowActor||cue.ArtLifecycle)?!animation.Finished(age):cue?.ParticlesVisible(age)??true);
    long engineStart;
    void AfterFrame(M4 projection)
    {
        if(engineStart!=0)ParticleSimulation.Bucket("engine:render",engineStart);
        long profileStart=System.Diagnostics.Stopwatch.GetTimestamp();
        var visible=particles.Where(p=>ShowEffects&&Time>=(p.Cue?.Time??0)&&CueVisible(p.Cue,p.System.Animation,Time-(p.Cue?.Time??0))&&!HiddenSystems.Contains(p.System.Name)).Select(p=>(p.System,Time-(p.Cue?.Time??0))).ToArray();
        if(visible.Length>0)particleRenderer.Draw(visible,projection,view.camera);else particleRenderer.ClearDrawStatistics();
        var fadeDistances=new Dictionary<Nifcontent_Class_Manolo,float>();
        float FadeDistance(PreviewModel.RenderableMesh target){
            var owner=modelOwners[target.MeshData.Shape];if(fadeDistances.TryGetValue(owner,out var cached))return cached;
            var minimum=new V3(float.MaxValue);var maximum=new V3(float.MinValue);bool any=false;
            void Include(V3 point,float radius=0){if(!float.IsFinite(point.X)||!float.IsFinite(point.Y)||!float.IsFinite(point.Z))return;minimum=V3.ComponentMin(minimum,point-new V3(radius));maximum=V3.ComponentMax(maximum,point+new V3(radius));any=true;}
            foreach(var mesh in view.Model.meshes.Where(m=>modelOwners.GetValueOrDefault(m.MeshData.Shape)==owner)){
                var geo=mesh.MeshData.Meshgeometry;var vertices=geo.PerVertexSkinMatrix!=null?SkinningHelper.GetWorldVertices(ref geo):geo.Vertices;mesh.MeshData.Meshgeometry=geo;
                foreach(var p in vertices)Include(V3.TransformPosition(new((float)p.X,(float)p.Y,(float)p.Z),mesh.MeshData.Transform));
            }
            foreach(var p in particles.Where(p=>p.System.Nif==owner)){
                var transform=p.System.DisplayTransform(Time-(p.Cue?.Time??0),view.camera);
                float scale=p.System.WorldSpace?1:new System.Numerics.Vector3(transform.M11,transform.M12,transform.M13).Length();
                foreach(var particle in p.System.Simulation.Particles){
                    if(particle.Strip is {} strip){
                        foreach(var vertex in ParticleStripGeometry.BoundsVertices(strip)){
                            var world=System.Numerics.Vector3.Transform(vertex,transform);Include(new(world.X,world.Y,world.Z));
                        }
                        continue;
                    }
                    var c=System.Numerics.Vector3.Transform(particle.Position,transform);Include(new(c.X,c.Y,c.Z),Math.Max(0,particle.Radius*scale));
                }
            }
            var center=any?(minimum+maximum)*.5f:V3.Zero;
            return fadeDistances[owner]=RefractionFade.NormalizedDistance((view.camera.GetEyePosition()-center).Length);
        }
        refractionRenderer.Draw(RefractionEnabled?refractionMeshes:[],projection,view.camera,FadeDistance);
        DrawPhysicsOverlay(projection);
        DrawPrecisionOverlay(projection);
        DrawRootMotionArrow(projection);
        DrawSkeletonOverlay(projection);
        foreach(var d in particleRenderer.Diagnostics.Concat(refractionRenderer.Diagnostics))if(!Diagnostics.Contains(d))Diagnostics.Add(d);
        LastEffectDrawMs=System.Diagnostics.Stopwatch.GetElapsedTime(profileStart).TotalMilliseconds;
    }
    internal IEnumerable<string> FramingDiagnostics()
    {
        foreach(var mesh in view.Model.meshes)yield return $"Mesh {mesh.MeshData.Shape.NifShape.Name}: hidden={mesh.MeshData.Shape.RenderHide}; bounds {mesh.BoundsMin} .. {mesh.BoundsMax}; alpha {mesh.MeshData.Material.MaterialBase?.Alpha}; shader {mesh.MeshData.Shape.NifShader?.GetType().Name}; refraction {mesh.MeshData.Material.MaterialBase?.Refraction}; textures {string.Join(";",mesh.MeshData.Material.Textures_Path_List)}";
        foreach(var p in particles){var live=p.System.Simulation.Particles;yield return $"Particles {p.System.Name}: seek {p.System.LastSeekMs:F2} ms; count {live.Count}; max radius {live.Select(x=>x.Radius).DefaultIfEmpty().Max()}; alpha {live.Select(x=>x.Color.W).DefaultIfEmpty().Min()}..{live.Select(x=>x.Color.W).DefaultIfEmpty().Max()}; min aspect {p.System.MinimumAspect}";}
    }
    static readonly V3 Parked=new(1e30f);
    IRenderableShape[] poseShapes=[];
    readonly Dictionary<NifAnimation,(EffectCue Cue,double Age,bool Hidden,System.Numerics.Matrix4x4 Placement)> instanceStates=new(ReferenceEqualityComparer.Instance);
    // Pooled effect instances: lead cue -> every cue that instance serves, by start time.
    readonly Dictionary<EffectCue,EffectCue[]> pooledCues=new(ReferenceEqualityComparer.Instance);
    bool poolAccepted;
    public int PooledEffectInstances=>pooledCues.Count;
    public int PooledEffectCues=>pooledCues.Values.Sum(v=>v.Length);
    // The cue a pooled instance is showing at the scene time: the latest one that has started.
    EffectCue Live(EffectCue lead)
    {
        if(pooledCues.Count==0||!pooledCues.TryGetValue(lead,out var members))return lead;
        var live=members[0];
        foreach(var member in members){if(member.Time<=Time)live=member;else break;}
        return live;
    }
    // Adds animation cues. Repeats of one stateless mesh effect whose lifetimes do not overlap share an instance;
    // a looping clip that retriggers a 3,000-shape baked effect every two seconds otherwise loads it once per trigger.
    public void AddEffects(IReadOnlyList<EffectCue> cues)
    {
        var handled=new HashSet<EffectCue>(ReferenceEqualityComparer.Instance);
        // Verification switch: SSE_NO_EFFECT_POOL=1 loads one instance per trigger so renders can be compared.
        IEnumerable<EffectCue> candidates=Environment.GetEnvironmentVariable("SSE_NO_EFFECT_POOL")=="1"?[]:cues;
        foreach(var group in candidates.Where(c=>double.IsFinite(c.Lifetime)&&c.Lifetime>0&&double.IsPositiveInfinity(c.RemovedAt)&&c.PlacementTime==null&&!c.ArtLifecycle)
            .GroupBy(c=>c with{Time=0,Origin=""}).Where(g=>g.Count()>1)){
            // Greedy interval colouring: each instance takes the next cue that starts after its previous one ended.
            var instances=new List<List<EffectCue>>();
            foreach(var cue in group.OrderBy(c=>c.Time)){
                var free=instances.FirstOrDefault(i=>i[^1].Time+i[^1].Lifetime+1e-6<cue.Time);
                if(free==null)instances.Add(free=[]);
                free.Add(cue);
            }
            if(instances.Count==group.Count())continue;
            bool first=true,accepted=false;
            foreach(var instance in instances){
                if(first){AddModel(instance[0].Model,false,instance[0],pool:instance);accepted=poolAccepted;first=false;if(!accepted)handled.Add(instance[0]);}
                else if(accepted)AddModel(instance[0].Model,false,instance[0],pool:instance);
                if(accepted)foreach(var cue in instance)handled.Add(cue);
            }
            if(accepted)Diagnostics.Add($"{group.First().Model}: {group.Count()} identical triggers share {instances.Count} loaded instance(s); the effect has no particle systems, addon nodes or behavior graph, so reuse does not change what is drawn.");
        }
        foreach(var cue in cues)if(!handled.Contains(cue))AddModel(cue.Model,false,cue);
    }
    // Set when the user moves the camera by hand: an effect preview then stops re-fitting each frame, so the chosen
    // view holds while Follow camera stays ticked. Framing the scene again clears it.
    public bool HoldFraming;
    public float FloorSize=>floor.Size;
    public void Frame(){HoldFraming=false;BeforeFrame();FitCamera(false);}
    // The engine reset frames every mesh at bind pose, including effect shapes that are not visible yet and
    // backdrop geometry far larger than the actor. After a rebuild the host asks for the selected subject instead.
    bool cameraWasReset;
    public void FrameAfterRebuild(){if(!cameraWasReset)return;cameraWasReset=false;Frame();}
    public int OversizedEffectMeshes{get;private set;}
    void FitCamera(bool growOnly,FrameSubject? fallback=null){
        var subject=fallback??FramingSubject;
        var min=new V3(float.MaxValue);var max=new V3(float.MinValue);bool any=false;var oversized=new List<(V3 Min,V3 Max)>();
        void Include(V3 lo,V3 hi){if(!float.IsFinite(lo.X)||!float.IsFinite(lo.Y)||!float.IsFinite(lo.Z)||!float.IsFinite(hi.X)||!float.IsFinite(hi.Y)||!float.IsFinite(hi.Z))return;min=V3.ComponentMin(min,lo);max=V3.ComponentMax(max,hi);any=true;}
        foreach(var mesh in view.Model.meshes){
            if(mesh.MeshData.Shape.RenderHide)continue;
            bool isEffect=effectShapes.ContainsKey(mesh.MeshData.Shape);
            if(subject==FrameSubject.Character&&isEffect||subject==FrameSubject.Effects&&!isEffect)continue;
            var lo=mesh.MeshData.Meshgeometry.Minv;var hi=mesh.MeshData.Meshgeometry.Maxv;
            var boxMin=new V3(float.MaxValue);var boxMax=new V3(float.MinValue);
            for(int i=0;i<8;i++){
                var point=V3.TransformPosition(new V3((float)((i&1)==0?lo.X:hi.X),(float)((i&2)==0?lo.Y:hi.Y),(float)((i&4)==0?lo.Z:hi.Z)),mesh.MeshData.Transform);
                boxMin=V3.ComponentMin(boxMin,point);boxMax=V3.ComponentMax(boxMax,point);
            }
            // Backdrop geometry (sky domes, screen-filling flashes) larger than the floor disc would push the camera
            // out until the actor is a dot. It is still drawn; framing uses it only when nothing else is visible.
            if(isEffect&&(boxMax-boxMin).Length*.5f>floor.Size*.5f){oversized.Add((boxMin,boxMax));continue;}
            Include(boxMin,boxMax);
        }
        foreach(var p in particles){
            if(subject==FrameSubject.Character)continue;
            double age=Time-(p.Cue?.Time??0);if(age<0||!ShowEffects||!CueVisible(p.Cue,p.System.Animation,age)||!p.System.Animation.Visible(p.System.System))continue;
            var transform=p.System.DisplayTransform(age,view.camera);
            foreach(var part in p.System.Simulation.Particles){
                if(p.System.MeshArtwork is {} artwork){
                    foreach(var meshPart in artwork.Parts.Where(m=>ParticleMeshTransform.TemplateIndex(part.Generation,m.TemplateCount)==m.Generation))
                        foreach(var vertex in ParticleMeshArtwork.Vertices(meshPart,part,artwork.HasRotation,transform)){
                            var position=vertex.Position;var point=new V3(position.X,position.Y,position.Z);Include(point,point);
                        }
                    continue;
                }

                if(part.Strip is {} strip){
                    foreach(var position in ParticleStripGeometry.BoundsVertices(strip)){
                        var vertex=System.Numerics.Vector3.Transform(position,transform);var bound=new V3(vertex.X,vertex.Y,vertex.Z);Include(bound,bound);
                    }
                    continue;
                }

                if(!(part.Color.W>0))continue;
                var pos=System.Numerics.Vector3.Transform(part.Position,transform);
                float scale=new System.Numerics.Vector3(transform.M11,transform.M12,transform.M13).Length();
                var velocity=System.Numerics.Vector3.TransformNormal(part.Velocity,transform);
                float vx=System.Numerics.Vector3.Dot(velocity,new(view.camera.right.X,view.camera.right.Y,view.camera.right.Z));
                float vy=System.Numerics.Vector3.Dot(velocity,new(view.camera.upPlane.X,view.camera.upPlane.Y,view.camera.upPlane.Z));
                float aspect=p.System.AspectAtSpeed(MathF.Sqrt(vx*vx+vy*vy));
                if(!float.IsFinite(aspect)||aspect<=0)aspect=1;
                float extent=ParticleNif.BillboardRadius(part)*MathF.Sqrt(1+1/(aspect*aspect));
                if(!float.IsFinite(extent)||extent<=0)continue;
                var center=new V3(pos.X,pos.Y,pos.Z);var radius=new V3(extent);
                Include(center-radius,center+radius);
            }
        }
        OversizedEffectMeshes=oversized.Count;
        const string oversizedNote="Camera framing ignores effect meshes larger than the floor disc (backdrop spheres, screen-filling flashes); they are still drawn. With nothing else visible, whole-scene framing uses them.";
        if(oversized.Count>0&&!Diagnostics.Contains(oversizedNote))Diagnostics.Add(oversizedNote);
        if(!any&&subject==FrameSubject.Everything)foreach(var box in oversized)Include(box.Min,box.Max);
        if(!any){if(subject!=FrameSubject.Everything)FitCamera(growOnly,FrameSubject.Everything);return;}
        var focus=(min+max)*.5f;
        float radiusScene=Math.Max(5,(max-min).Length*.5f);
        float halfAngle=Math.Min(MathF.PI/8,MathF.Atan(MathF.Tan(MathF.PI/8)*Math.Max(.01f,view.Width/(float)Math.Max(1,view.Height))));
        float distance=radiusScene/MathF.Sin(halfAngle)*1.15f;
        if(growOnly)distance=Math.Max(distance,view.camera.distance);
        if(!float.IsFinite(distance))return;
        bool changed=(view.camera.FocusPosition-focus).LengthSquared>1e-8f||Math.Abs(view.camera.distance-distance)>.0001f;
        view.camera.FocusPosition=focus;view.camera.distance=distance;
        view.camera.Optimaldistance=distance;
        if(changed||!growOnly){view.UpdateProjection(true);view.UpdateRequired=true;if(!growOnly)view.Invalidate();}
    }
    void BeforeFrame()
    {
        if(committing)return;
        long profileStart=System.Diagnostics.Stopwatch.GetTimestamp();evaluationFrame++;
        // The engine's far plane hugs the mesh bounds; keep the whole floor disc inside it.
        view.FarPlaneExtension=floor.Enabled?view.camera.FocusPosition.Length+floor.Size*.75f:0;view.UpdateProjection();
        var motion=RootMotion && Clip!=null?Clip.Motion(Time):(System.Numerics.Vector3.Zero,0f);
        var actor=M4.CreateRotationZ(motion.Item2)*M4.CreateTranslation(motion.Item1.X,motion.Item1.Y,motion.Item1.Z);
        foreach(var p in particles){double age=Time-(p.Cue?.Time??0);if(age>=0&&ShowEffects&&CueVisible(p.Cue,p.System.Animation,age)){long seekStart=System.Diagnostics.Stopwatch.GetTimestamp();p.System.Seek(age);p.System.LastSeekMs=System.Diagnostics.Stopwatch.GetElapsedTime(seekStart).TotalMilliseconds;}else{p.System.Simulation.Reset();p.System.LastSeekMs=0;}}
        LastSimulationMs=System.Diagnostics.Stopwatch.GetElapsedTime(profileStart).TotalMilliseconds;
        refractionMeshes.Clear();
        instanceStates.Clear();long tLoop=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var mesh in view.Model.meshes) {
            var shape=mesh.MeshData.Shape;if(mesh.MeshData.Material.MaterialBase?.Refraction==true)shape.RenderHide=false;
            if(effectShapes.TryGetValue(shape,out var effect)) {
                // The cue-level decision is identical for every shape of one effect instance; a baked effect has
                // thousands of shapes, so it is made once per instance per frame (cue lookups hash the whole record).
                if(!instanceStates.TryGetValue(effect.Animation,out var state)){
                    var liveCue=Live(effect.Cue);double liveAge=Time-liveCue.Time;
                    bool hidden=!ShowEffects||!CueVisible(liveCue,effect.Animation,liveAge)||(effect.Animation.Behavior==null||!(liveCue.FollowActor||liveCue.ArtLifecycle))&&liveAge>liveCue.Lifetime||(liveCue.Biped>=0&&AttachmentFrame(liveCue)==null);
                    if(!hidden){long tEval=System.Diagnostics.Stopwatch.GetTimestamp();effect.Animation.Evaluate(liveAge);ParticleSimulation.Bucket("scene:evaluate",tEval);}
                    instanceStates[effect.Animation]=state=(liveCue,liveAge,hidden,hidden?System.Numerics.Matrix4x4.Identity:effect.Animation.Placement(liveAge));
                }
                var cue=state.Cue;double age=state.Age;
                shape.RenderHide=state.Hidden;
                if(shape.RenderHide)continue;
                long tv=System.Diagnostics.Stopwatch.GetTimestamp();shape.RenderHide=!effect.Animation.Visible(shape.NifShape);ParticleSimulation.Bucket("loop:visible-check",tv);if(shape.RenderHide)continue;
                long tx=System.Diagnostics.Stopwatch.GetTimestamp();
                if(shape.IsSkinned){
                    // Effect controllers run after the shared actor upload pass. Refresh
                    // this NIF's own bone palette here, before drawing or refraction.
                    var geometry=mesh.MeshData.Meshgeometry;
                    SkinningHelper.RecomputeGPUBoneMatrices(shape,ref geometry,false,effectSkeleton,true,true);
                    mesh.MeshData.Meshgeometry=geometry;mesh.ComputeBounds();mesh.UpdateBoneMatricesSSBO();
                    // Skin matrices already output NIF world coordinates.
                    mesh.MeshData.Transform=RenderMatrix(state.Placement);
                }else mesh.MeshData.Transform=effectBindInverse[shape]*RenderMatrix(effect.Animation.RenderWorld(shape.NifShape,state.Placement,view.camera));
                ParticleSimulation.Bucket("loop:transform",tx);tx=System.Diagnostics.Stopwatch.GetTimestamp();
                if(!float.IsFinite(mesh.MeshData.Transform.Determinant)||Math.Abs(mesh.MeshData.Transform.Determinant)<1e-12f){shape.RenderHide=true;continue;}
                if(shape.NifShader is BSEffectShaderProperty bs && mesh.MeshData.Material.MaterialBase is {} material) {

                    if(shape.NifShape.AlphaPropertyRef is {} alphaRef&&effect.Animation.Block(alphaRef) is NiAlphaProperty alpha)material.AlphaTestRef=alpha.Threshold;
                    material.Alpha=bs.BaseColor.A;material.UOffset=bs.UVOffset.U;material.VOffset=bs.UVOffset.V;
                    material.UScale=bs.UVScale.U;material.VScale=bs.UVScale.V;material.EmittanceMult=bs.BaseColorScale;
                    material.EmittanceColor=Color.FromArgb(255,(int)Math.Clamp(bs.BaseColor.R*255,0,255),(int)Math.Clamp(bs.BaseColor.G*255,0,255),(int)Math.Clamp(bs.BaseColor.B*255,0,255));
                }else if(shape.NifShader is BSLightingShaderProperty lit && mesh.MeshData.Material.MaterialBase is {} litMaterial){
                    if(effect.Animation.Block(shape.NifShape.AlphaPropertyRef) is NiAlphaProperty alpha)litMaterial.AlphaTestRef=alpha.Threshold;
                    litMaterial.Alpha=lit.Alpha;litMaterial.UOffset=lit.UVOffset.U;litMaterial.VOffset=lit.UVOffset.V;
                    litMaterial.UScale=lit.UVScale.U;litMaterial.VScale=lit.UVScale.V;litMaterial.EmittanceMult=lit.EmissiveMultiple;
                }
                ParticleSimulation.Bucket("loop:material",tx);
            }else if(weaponShapes.Contains(shape)){
                shape.RenderHide=!showWeapon;
                mesh.MeshData.Transform=WeaponBone(Time,out var bone)?bases.GetValueOrDefault(shape,M4.Identity)*bone.GetGlobalTransform.ToMatrix4()*actor:bases.GetValueOrDefault(shape,M4.Identity)*actor;
                if(shape.RenderHide)continue;
            }
            else mesh.MeshData.Transform=bases.GetValueOrDefault(shape,M4.Identity)*actor;
            if(mesh.MeshData.Material.MaterialBase?.Refraction==true){refractionMeshes.Add(mesh);shape.RenderHide=true;}
        }
        ParticleSimulation.Bucket("scene:mesh-loop (incl. evaluate)",tLoop);tLoop=System.Diagnostics.Stopwatch.GetTimestamp();
        // Frustum culling must use the same animated placement as the vertex shader.
        foreach(var mesh in view.Model.meshes){
            if(mesh.MeshData.Shape.RenderHide){
                // The engine frustum-tests every mesh before its hidden check and depth-sorts every blended one;
                // park hidden effect meshes far outside the frustum so thousands of idle flipbook shapes cost nothing.
                if(effectShapes.ContainsKey(mesh.MeshData.Shape)){mesh.BoundsMin=Parked;mesh.BoundsMax=Parked;}
                continue;
            }
            var lo=mesh.MeshData.Meshgeometry.Minv;var hi=mesh.MeshData.Meshgeometry.Maxv;
            var min=new V3(float.MaxValue);var max=new V3(float.MinValue);
            for(int i=0;i<8;i++){
                var point=V3.TransformPosition(new V3((float)((i&1)==0?lo.X:hi.X),(float)((i&2)==0?lo.Y:hi.Y),(float)((i&4)==0?lo.Z:hi.Z)),mesh.MeshData.Transform);
                min=V3.ComponentMin(min,point);max=V3.ComponentMax(max,point);
            }
            mesh.BoundsMin=min;mesh.BoundsMax=max;
        }
        ParticleSimulation.Bucket("scene:bounds",tLoop);
        // With a clip, following tracks the actor root (the point root motion moves); an effect preview has no actor,
        // so it keeps fitting the scene.
        if(FollowScene&&Clip!=null)TrackRoot();
        else{followOffset=null;if(FollowScene&&!HoldFraming)FitCamera(true);}
        LastUpdateMs=System.Diagnostics.Stopwatch.GetElapsedTime(profileStart).TotalMilliseconds;engineStart=System.Diagnostics.Stopwatch.GetTimestamp();
    }
}
