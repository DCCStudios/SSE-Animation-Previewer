using FO4_Base_Library;
using NiflySharp.Blocks;
using M4=OpenTK.Mathematics.Matrix4;
using M4d=OpenTK.Mathematics.Matrix4d;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// HDT-SMP in the scene: which loaded models carry physics, where their bones live, and the per-frame step.
// Armor, clothing and head-part NIFs name their XML in a "HDT Skinned Mesh Physics Object" string on the root.
// FSMP only reads that link from armor and head parts, so a weapon's physics comes from a carrier armor NIF
// whose XML names bones that exist in the weapon NIF.
public sealed partial class Scene
{
    sealed record SmpModel(string Path,Nifcontent_Class_Manolo Nif,IReadOnlyList<IRenderableShape> Shapes,bool Weapon);
    readonly List<SmpModel> smpModels=[];
    readonly List<HierarchiBone_class> mergedBones=[];
    readonly SmpDebugDraw smpDraw=new();
    (SmpWorld?,long,bool,bool,V3d,V3d) overlayKey;
    public int PhysicsOverlayBuilds=>smpDraw.Builds;
    SmpWorld? smp;
    bool physicsEnabled,physicsJump;
    M4d physicsActor=M4d.Identity,physicsWeapon=M4d.Identity,lastPhysicsActor=M4d.Identity;
    string lastWeaponBone="";
    readonly System.Diagnostics.Stopwatch physicsClock=System.Diagnostics.Stopwatch.StartNew();double lastPhysicsTime;
    const string PhysicsPrefix="HDT-SMP: ";

    public bool PhysicsColliders,PhysicsBones;
    // Test hook: a fixed physics interval per StepPhysics call instead of wall-clock time, for repeatable runs.
    public double? FixedPhysicsInterval;
    // HDT-SMP collision meshes take their vertices at the actor's weight, as the game builds them from the
    // weight-blended model. A weight change rebuilds the simulation once the slider has been still for a moment.
    float physicsWeight=float.NaN;double weightChangedAt;
    public float PhysicsWeight=>physicsWeight;
    // Largest kinematic bone displacement measured at the last seek or loop wrap.
    public double LastPoseJump {get;private set;}
    public bool PhysicsEnabled{get=>physicsEnabled;set{if(physicsEnabled==value)return;physicsEnabled=value;RebuildPhysics();}}
    public bool PhysicsActive=>smp!=null;
    public int PhysicsSystems=>smp?.Systems.Count??0;
    public int PhysicsBoneCount=>smp?.Systems.Sum(s=>s.Bones.Count)??0;
    public int PhysicsDynamicBones=>smp?.Systems.Sum(s=>s.DynamicBones)??0;
    public int PhysicsBodies=>smp?.Systems.Sum(s=>s.Bodies.Count)??0;
    public int PhysicsConstraints=>smp?.Systems.Sum(s=>s.Links.Count)??0;
    public int PhysicsContacts=>smp?.LastContacts??0;
    public int PhysicsPeakContacts=>smp?.PeakContacts??0;
    public long PhysicsSteps=>smp?.Steps??0;
    public int PhysicsResets=>smp?.Resets??0;
    public double PhysicsStepMs=>smp?.LastStepMs??0;
    public double PhysicsCollisionMs=>smp?.LastCollisionMs??0;
    public int PhysicsSubSteps=>smp?.LastSubSteps??0;
    public int PhysicsOverlayLines=>smpDraw.LastLines;
    public int PhysicsOverlaySpheres=>smpDraw.LastSpheres;
    public int PhysicsOverlayPlates=>smpDraw.LastPlates;
    internal SmpWorld? PhysicsWorld=>smp;
    public string PhysicsStatus=>smp==null
        ?physicsEnabled?"HDT-SMP on · nothing in this scene carries a physics XML":"HDT-SMP off"
        :$"HDT-SMP · {PhysicsSystems} system(s) · {PhysicsDynamicBones} simulated / {PhysicsBoneCount} bones · {PhysicsConstraints} constraints · {PhysicsBodies} collision shapes · {PhysicsContacts} contacts · {PhysicsStepMs:0.00} ms";

    void RegisterPhysicsModel(string path,Nifcontent_Class_Manolo nif,IReadOnlyList<IRenderableShape> shapes,bool weapon)=>smpModels.Add(new(path,nif,shapes,weapon));

    void ClearPhysics()
    {
        DisposePhysicsWorld();smpModels.Clear();
        foreach(var bone in mergedBones){
            if(bone.Parent==null)Skeleton.SkeletonStructure.Remove(bone);else bone.Parent.Childrens.Remove(bone);
            if(Skeleton.SkeletonDictionary.TryGetValue(bone.BoneName,out var current)&&ReferenceEquals(current,bone))Skeleton.SkeletonDictionary.Remove(bone.BoneName);
        }
        mergedBones.Clear();
    }
    void DisposePhysicsWorld()
    {
        if(smp==null)return;
        foreach(var system in smp.Systems)foreach(var bone in system.Bones)bone.Node.Clear();
        smp.Dispose();smp=null;
    }

    // An armor or head-part NIF can bring its own bones (cloth chains, hair strands). The game clones them into
    // the actor skeleton under the matching parent when the item is attached; without that they would stay at
    // their bind position while the body moves.
    void MergeModelBones()
    {
        if(!Skeleton.HasSkeleton)return;
        Skeleton.SkeletonDictionary.TryGetValue("NPC",out var npc);
        // The game looks a bone up across the whole actor, equipped weapon included, before cloning it. A carrier
        // armor that repeats the weapon's strap bones must bind to the weapon's, not to copies at the actor root.
        var weaponNames=smpModels.Where(m=>m.Weapon).SelectMany(m=>m.Nif.Blocks.OfType<NiNode>()).Select(n=>n.Name?.String??"").Where(n=>n.Length>0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var model in smpModels){
            if(model.Weapon||model.Nif.GetRootNode() is not NiNode root)continue;
            int before=mergedBones.Count;
            MergeChildren(model.Nif,root,npc,weaponNames);
            if(mergedBones.Count>before)Diagnostics.Add($"{model.Path}: {mergedBones.Count-before} extra bone(s) merged into the skeleton ({string.Join(", ",mergedBones.Skip(before).Take(4).Select(b=>b.BoneName))}{(mergedBones.Count-before>4?", …":"")}).");
        }
    }
    void MergeChildren(Nifcontent_Class_Manolo nif,NiNode node,HierarchiBone_class? anchor,HashSet<string> weaponNames)
    {
        foreach(int index in node.Children.Indices){
            if(index<0||index>=nif.Blocks.Count||nif.Blocks[index] is not NiNode child)continue;
            string name=child.Name?.String??"";
            if(name.Length==0||weaponNames.Contains(name)){MergeChildren(nif,child,anchor,weaponNames);continue;}
            if(Skeleton.SkeletonDictionary.TryGetValue(name,out var existing)){MergeChildren(nif,child,existing,weaponNames);continue;}
            var bone=new HierarchiBone_class{BoneName=name,Parent=anchor,OriginalLocaLTransform=new Transform_Class(child)};
            if(anchor==null)Skeleton.SkeletonStructure.Add(bone);else anchor.Childrens.Add(bone);
            Skeleton.SkeletonDictionary[name]=bone;mergedBones.Add(bone);
            MergeChildren(nif,child,bone,weaponNames);
        }
    }

    M4d ActorWorld(double time)
    {
        var motion=RootMotion&&Clip!=null?Clip.Motion(time):(System.Numerics.Vector3.Zero,0f);
        return M4d.CreateRotationZ(motion.Item2)*M4d.CreateTranslation(motion.Item1.X,motion.Item1.Y,motion.Item1.Z);
    }
    M4d WeaponWorld(double time)=>(WeaponBone(time,out var bone)?bone.GetGlobalTransform.ToMatrix4d():M4d.Identity)*physicsActor;

    // Skeleton-space bone matrices for the current physics frame. The engine recomposes a bone's whole parent
    // chain on every read; with a few hundred physics bones that is thousands of compositions per frame.
    readonly Dictionary<HierarchiBone_class,M4d> physicsGlobals=new(ReferenceEqualityComparer.Instance);
    // Inverse world matrix of a parent bone (null key: the actor root), shared by all of its simulated children in one
    // write-back. A written bone drops its own entry, so its children invert its new world.
    readonly Dictionary<object,M4d> parentInverses=new(ReferenceEqualityComparer.Instance);
    static readonly object RootKey=new();
    void ClearPhysicsCaches(){physicsGlobals.Clear();parentInverses.Clear();}
    M4d ParentInverse(HierarchiBone_class? parent)
    {
        var key=(object?)parent??RootKey;
        if(parentInverses.TryGetValue(key,out var cached))return cached;
        var world=parent==null?physicsActor:PhysicsGlobal(parent)*physicsActor;
        return parentInverses[key]=world.Inverted();
    }
    M4d PhysicsGlobal(HierarchiBone_class bone)
    {
        if(physicsGlobals.TryGetValue(bone,out var cached))return cached;
        var local=bone.LocaLTransform.ToMatrix4d();
        return physicsGlobals[bone]=bone.Parent==null?local:local*PhysicsGlobal(bone.Parent);
    }
    sealed class SkeletonNode(Scene scene,HierarchiBone_class bone):ISmpNode
    {
        public string Name=>bone.BoneName;
        public int Depth{get{int depth=0;for(var b=bone.Parent;b!=null;b=b.Parent)depth++;return depth;}}
        public M4d World=>scene.PhysicsGlobal(bone)*scene.physicsActor;
        public HierarchiBone_class Bone=>bone;
        public void Write(M4d world)
        {
            // world = local · parentWorld and local = delta · base, so the physics layer holds delta.
            // Physics sets rotation and translation only, as FSMP's writeTransform does; the bone keeps its animated
            // local scale, so the delta carries no scale.
            var basis=bone.LocaLTransformWithoutPhysics.ToMatrix4d();
            var local=Scene.KeepScale(world*scene.ParentInverse(bone.Parent),basis.Row0.Xyz.Length);
            bone.PhysicsDeltaTransform=new Transform_Class(local*basis.Inverted());
            scene.physicsGlobals[bone]=local*(bone.Parent==null?M4d.Identity:scene.PhysicsGlobal(bone.Parent));
            scene.parentInverses.Remove(bone);
        }
        public void Clear(){bone.PhysicsDeltaTransform=null;scene.ClearPhysicsCaches();}
    }
    // A node inside the weapon NIF. The weapon hangs from the hand or holster node, so its nodes are not part of
    // the skeleton; simulated ones are written back into the NIF, where the weapon's skin reads them.
    sealed class WeaponNode:ISmpNode
    {
        readonly Scene scene;readonly NiNode node;readonly NiNode[] ancestors;
        readonly NiflySharp.Structs.Matrix33 restRotation;readonly System.Numerics.Vector3 restTranslation;readonly float restScale;
        public WeaponNode(Scene scene,NiNode node,NiNode[] ancestors){this.scene=scene;this.node=node;this.ancestors=ancestors;restRotation=node.Rotation;restTranslation=node.Translation;restScale=node.Scale;}
        public string Name=>node.Name?.String??"";
        public int Depth=>ancestors.Length+64;
        M4d ParentWorld{get{var m=M4d.Identity;foreach(var ancestor in ancestors)m*=new Transform_Class(ancestor).ToMatrix4d();return m*scene.physicsWeapon;}}
        public M4d World=>new Transform_Class(node).ToMatrix4d()*ParentWorld;
        public void Write(M4d world)
        {
            // Rotation and translation only; the node keeps its own scale (see SkeletonNode.Write).
            var local=new Transform_Class(Scene.KeepScale(world*ParentWorld.Inverted(),restScale));
            node.Translation=local.Translation;node.Rotation=local.Rotation;node.Scale=restScale;
        }
        public void Clear(){node.Translation=restTranslation;node.Rotation=restRotation;node.Scale=restScale;}
    }
    // A skin bone that exists in neither the skeleton nor the weapon: pinned where its own NIF places it.
    sealed class PinnedNode(Scene scene,string name,M4d nifWorld):ISmpNode
    {
        public string Name=>name;
        public int Depth=>0;
        public M4d World=>nifWorld*scene.physicsActor;
        public void Write(M4d world){}
        public void Clear(){}
    }

    // A local matrix with its rotation and translation and the given uniform scale. Physics writes a bone's rotation
    // and translation only. Before this, the previewer also wrote the scale it had read, and read it back next frame;
    // along constraint chains the small mismatches compounded and a bone (and the mesh skinned to it, and its mass)
    // could grow thousands of times over within seconds of fast motion.
    internal static M4d KeepScale(M4d local,double scale)
    {
        var rotation=local.ExtractRotation();
        return M4d.CreateScale(scale)*M4d.CreateFromQuaternion(rotation)*M4d.CreateTranslation(local.Row3.Xyz);
    }
    static NiNode[] Ancestors(Nifcontent_Class_Manolo nif,Dictionary<NiNode,NiNode> parents,NiNode node)
    {
        var chain=new List<NiNode>();
        for(var current=node;parents.TryGetValue(current,out var parent)&&chain.Count<256;current=parent)chain.Add(parent);
        return chain.ToArray();
    }
    static Dictionary<NiNode,NiNode> ParentMap(Nifcontent_Class_Manolo nif)
    {
        var parents=new Dictionary<NiNode,NiNode>(ReferenceEqualityComparer.Instance);
        foreach(var node in nif.Blocks.OfType<NiNode>())foreach(int index in node.Children.Indices)
            if(index>=0&&index<nif.Blocks.Count&&nif.Blocks[index] is NiNode child)parents.TryAdd(child,node);
        return parents;
    }

    // Collision mesh data of one or more skinned shapes, concatenated the way FSMP builds a body from a name set.
    SmpMeshSource? MeshSource(Nifcontent_Class_Manolo nif,IReadOnlyList<IRenderableShape> shapes,string name,Dictionary<NiNode,NiNode> parents)
    {
        var boneNames=new List<string>();var skinToBone=new List<SmpTransform>();var bounds=new List<(V3d,double)>();var fallback=new List<ISmpNode?>();
        var positions=new List<V3d>();var weights=new List<float>();var indices=new List<int>();var triangles=new List<int>();
        foreach(var shape in shapes){
            if(!shape.IsSkinned)continue;
            var bones=shape.ShapeBones;var transforms=shape.ShapeBoneTransforms;
            if(bones.Count==0||transforms.Count!=bones.Count)continue;
            int boneStart=boneNames.Count,vertexStart=positions.Count;
            var data=shape.NifSkin is NiSkinInstance instance?nif.GetBlock(instance.Data):null;
            for(int k=0;k<bones.Count;k++){
                boneNames.Add(bones[k].Name?.String??"");skinToBone.Add(SmpTransform.From(transforms[k].ToMatrix4d()));
                var sphere=data!=null&&k<data.BoneList.Count?data.BoneList[k].BoundingSphere:default;
                bounds.Add((new V3d(sphere.Center.X,sphere.Center.Y,sphere.Center.Z),sphere.Radius));
                var nifWorld=new Transform_Class(bones[k]).ToMatrix4d();foreach(var ancestor in Ancestors(nif,parents,bones[k]))nifWorld*=new Transform_Class(ancestor).ToMatrix4d();
                fallback.Add(new PinnedNode(this,boneNames[^1],nifWorld));
            }
            var geometry=shape.Geometry;var skin=geometry.GetSkinning();var vertexList=geometry.GetVertexPositions();var blended=weightBlend.Blended(shape);
            int perVertex=skin.WeightsPerVertex,count=vertexList.Count;
            for(int v=0;v<count;v++){
                positions.Add(blended!=null&&v<blended.Length?blended[v]:new V3d(vertexList[v].X,vertexList[v].Y,vertexList[v].Z));
                for(int k=0;k<4;k++){
                    bool present=k<perVertex&&skin.BoneWeights!=null&&skin.BoneIndices!=null&&v*perVertex+k<skin.BoneWeights.Length;
                    weights.Add(present?(float)skin.BoneWeights![v*perVertex+k]:0);
                    indices.Add(present?skin.BoneIndices![v*perVertex+k]+boneStart:boneStart);
                }
            }
            foreach(var triangle in geometry.GetTriangles()){triangles.Add(triangle.V1+vertexStart);triangles.Add(triangle.V2+vertexStart);triangles.Add(triangle.V3+vertexStart);}
        }
        return positions.Count==0?null:new SmpMeshSource(name,boneNames.ToArray(),skinToBone.ToArray(),bounds.ToArray(),positions.ToArray(),weights.ToArray(),indices.ToArray(),triangles.ToArray(),fallback.ToArray());
    }

    // FSMP's body-physics table: SKSE\Plugins\hdtSkinnedMeshConfigs\defaultBBPs.xml maps a shape name to an XML
    // for models that carry no physics link of their own, and can remap several shape names onto one.
    sealed class DefaultBbp
    {
        public readonly List<(string Shape,string File)> Files=[];
        public readonly List<(string Target,List<(int Priority,string Source)> Entries,List<string> Required)> Remaps=[];
        public static DefaultBbp? Load(Assets assets,List<string> log)
        {
            const string path=@"SKSE\Plugins\hdtSkinnedMeshConfigs\defaultBBPs.xml";
            if(!assets.Exists(path))return null;
            try{
                var root=System.Xml.Linq.XDocument.Parse(assets.Text(path)).Root;
                if(root==null||root.Name.LocalName!="default-bbps")return null;
                var table=new DefaultBbp();
                foreach(var e in root.Elements()){
                    if(e.Name.LocalName=="map"&&e.Attribute("shape") is {} shape&&e.Attribute("file") is {} file)table.Files.Add((shape.Value,file.Value));
                    else if(e.Name.LocalName=="remap"&&e.Attribute("target") is {} target){
                        var entries=new List<(int,string)>();var required=new List<string>();
                        foreach(var c in e.Elements()){
                            if(c.Name.LocalName=="source")entries.Add((int.TryParse(c.Attribute("priority")?.Value,out int p)?p:0,c.Value.Trim()));
                            else if(c.Name.LocalName=="requires")required.Add(c.Value.Trim());
                        }
                        table.Remaps.Add((target.Value,entries,required));
                    }
                }
                return table;
            }catch(Exception ex){log.Add(PhysicsPrefix+path+" could not be read: "+ex.Message);return null;}
        }
        // Shape name -> shapes that stand for it, after remaps.
        public Dictionary<string,HashSet<string>> NameMap(IEnumerable<string> shapeNames)
        {
            var map=shapeNames.Where(n=>n.Length>0).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(n=>n,n=>new HashSet<string>(StringComparer.OrdinalIgnoreCase){n},StringComparer.OrdinalIgnoreCase);
            foreach(var (target,entries,required) in Remaps){
                if(required.Any(r=>!map.ContainsKey(r)))continue;
                // Highest priority source present in the model wins, together with every source of that priority.
                var present=entries.Where(e=>map.ContainsKey(e.Source)).OrderByDescending(e=>e.Priority).ToList();
                if(present.Count==0)continue;
                if(!map.TryGetValue(target,out var set))map[target]=set=new(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in present.Where(e=>e.Priority==present[0].Priority))foreach(string shape in map[entry.Source].ToArray())set.Add(shape);
            }
            return map;
        }
    }

    string? ReadPhysicsXml(string link,string modelPath,out string resolved)
    {
        string relative=SmpPhysicsXml.SinPrefijoData(link);resolved=relative;
        if(assets.Exists(relative)&&!Path.IsPathRooted(relative))return assets.Text(relative);
        // A mesh added from disk outside the mounted folders: look beside its own Data root, then beside the mesh.
        if(Path.IsPathRooted(modelPath)){
            int at=modelPath.IndexOf(@"\meshes\",StringComparison.OrdinalIgnoreCase);
            foreach(string candidate in new[]{at>=0?Path.Combine(modelPath[..at],relative):"",Path.Combine(Path.GetDirectoryName(modelPath)??"",Path.GetFileName(relative))}){
                if(candidate.Length>0&&File.Exists(candidate)){resolved=candidate;return File.ReadAllText(candidate).TrimStart('﻿');}
            }
        }
        return null;
    }

    // Builds every physics system the loaded models declare. Called after a scene rebuild and by the toggle.
    public void RebuildPhysics()
    {
        DisposePhysicsWorld();physicsWeight=Weight;
        Diagnostics.RemoveAll(d=>d.StartsWith(PhysicsPrefix,StringComparison.Ordinal));
        if(!physicsEnabled||!Skeleton.HasSkeleton||smpModels.Count==0){MarkPhysicsDirty();return;}
        var log=new List<string>();
        var settings=new SmpSolverSettings();
        const string configPath=@"SKSE\Plugins\hdtSkinnedMeshConfigs\configs.json";
        if(assets.Exists(configPath)){
            try{settings=SmpSolverSettings.FromConfig(assets.Text(configPath),configPath);}
            catch(Exception ex){log.Add("configs.json could not be read, FSMP defaults used: "+ex.Message);}
        }
        var world=new SmpWorld(settings);
        // Constraint frames and spring rest lengths are measured in the reference pose, as FSMP forces it while building.
        var oldPose=Skeleton.Pose;Skeleton.ApplyPose(null);
        ClearPhysicsCaches();physicsActor=M4d.Identity;physicsWeapon=WeaponWorld(Time);
        try{
            var skeletonNodes=new Dictionary<HierarchiBone_class,ISmpNode>(ReferenceEqualityComparer.Instance);
            var weaponNodes=new Dictionary<string,ISmpNode>(StringComparer.OrdinalIgnoreCase);
            foreach(var model in smpModels.Where(m=>m.Weapon)){
                var parents=ParentMap(model.Nif);var root=model.Nif.GetRootNode();
                foreach(var node in model.Nif.Blocks.OfType<NiNode>()){
                    string name=node.Name?.String??"";
                    if(name.Length==0||ReferenceEquals(node,root)||Skeleton.SkeletonDictionary.ContainsKey(name))continue;
                    weaponNodes.TryAdd(name,new WeaponNode(this,node,Ancestors(model.Nif,parents,node)));
                }
            }
            ISmpNode? Resolve(string name)
            {
                if(!Skeleton.SkeletonDictionary.TryGetValue(name,out var bone))bone=Skeleton.SkeletonDictionary.FirstOrDefault(p=>p.Key.Equals(name,StringComparison.OrdinalIgnoreCase)).Value;
                if(bone!=null){if(!skeletonNodes.TryGetValue(bone,out var node))skeletonNodes[bone]=node=new SkeletonNode(this,bone);return node;}
                return weaponNodes.GetValueOrDefault(name);
            }
            var bbp=DefaultBbp.Load(assets,log);var usedXml=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void BuildFrom(string modelPath,Nifcontent_Class_Manolo nif,IReadOnlyList<IRenderableShape> shapes,string link,string origin)
            {
                string? xml=ReadPhysicsXml(link,modelPath,out string resolved);
                if(xml==null){log.Add($"{modelPath}: physics XML {link} is not in the mounted folders; no physics for this item.");return;}
                // One system per XML and model, as an item equipped once.
                if(!usedXml.Add(resolved+"|"+modelPath))return;
                var parents=ParentMap(nif);
                var names=(bbp??new DefaultBbp()).NameMap(shapes.Select(ShapeName));
                SmpMeshSource? Mesh(string name)
                {
                    var wanted=names.TryGetValue(name,out var set)?set:new HashSet<string>(StringComparer.OrdinalIgnoreCase){name};
                    return MeshSource(nif,shapes.Where(s=>wanted.Contains(ShapeName(s))).ToList(),name,parents);
                }
                var messages=new List<string>();
                var system=SmpSystemBuilder.Build(Path.GetFileName(resolved),xml,Resolve,Mesh,world.EmptyShape,messages);
                log.AddRange(messages.Take(40));if(messages.Count>40)log.Add($"{Path.GetFileName(resolved)}: {messages.Count-40} more message(s) not shown.");
                if(system==null)return;
                world.Add(system);
                log.Add($"{Path.GetFileName(modelPath)} -> {resolved} ({origin}): {system.Bones.Count} bones, {system.DynamicBones} simulated, {system.Links.Count} constraints, {system.Bodies.Count} collision shape(s){(system.Bodies.Count>0?" ["+string.Join(", ",system.Bodies.Select(b=>$"{b.Name}: {b.Shape.Colliders.Length} {(b.Shape.Triangles?"triangles":"spheres")}"))+"]":"")}.");
                foreach(var body in system.Bodies.Where(b=>b.Shape.Colliders.Length>10&&b.BoneBounds.Length>0&&b.BoneBounds.All(s=>s.Radius<=0)))
                    log.Add($"{resolved}: shape {body.Name} has more than ten colliders and every bone bound in its NiSkinData has radius 0. FSMP sizes its broadphase box from those bounds, so this shape may never collide in game.");
            }
            foreach(var model in smpModels){
                string? link=model.Nif.TryGetSmpPhysicsXmlPath();
                if(model.Weapon){
                    if(link!=null)log.Add($"{model.Path}: the weapon NIF carries a physics link ({link}). FSMP reads that link from armor and head parts only, so in game it does nothing; the previewer follows the game.");
                    if(weaponNodes.Count==0)continue;
                    foreach(var (path,nif,carrierLink) in WeaponCarriers(model.Path,weaponNodes)){
                        var carrierShapes=NifRenderableShape.FromNif(nif);
                        BuildFrom(path,nif,carrierShapes,carrierLink,"carrier armor found beside the weapon");
                    }
                    continue;
                }
                if(link!=null){BuildFrom(model.Path,model.Nif,model.Shapes,link,"physics link in the NIF");continue;}
                if(bbp==null)continue;
                var names=bbp.NameMap(model.Shapes.Select(ShapeName));
                var match=bbp.Files.FirstOrDefault(f=>names.ContainsKey(f.Shape));
                if(match.File!=null)BuildFrom(model.Path,model.Nif,model.Shapes,match.File,"defaultBBPs.xml, shape "+match.Shape);
            }
        }
        catch(Exception ex){log.Add("physics build failed: "+ex.Message);}
        finally{Skeleton.ApplyPose(oldPose);ClearPhysicsCaches();}
        foreach(string line in log)Diagnostics.Add(PhysicsPrefix+line);
        if(world.Systems.Count==0){world.Dispose();MarkPhysicsDirty();return;}
        Diagnostics.Add(PhysicsPrefix+"solver "+settings+".");
        // Bones are written parents first; values cached while reading are stale once an ancestor has moved.
        world.BeforeWrite=ClearPhysicsCaches;
        smp=world;FindPhysicsShapes();physicsJump=false;lastPhysicsTime=physicsClock.Elapsed.TotalSeconds;lastPhysicsActor=ActorWorld(Time);lastWeaponBone=WeaponBoneName;
        MarkPhysicsDirty();
    }
    static string ShapeName(IRenderableShape shape)=>shape.NifShape?.Name?.String??shape.ShapeName??"";

    // NIFs in the weapon's folder that link an XML naming at least one bone of the weapon.
    IEnumerable<(string Path,Nifcontent_Class_Manolo Nif,string Link)> WeaponCarriers(string weaponPath,Dictionary<string,ISmpNode> weaponNodes)
    {
        string directory=Path.GetDirectoryName(weaponPath)??"";
        IEnumerable<string> siblings=Path.IsPathRooted(weaponPath)
            ?(Directory.Exists(directory)?Directory.EnumerateFiles(directory,"*.nif"):[])
            :assets.Paths.Where(p=>p.EndsWith(".nif",StringComparison.OrdinalIgnoreCase)&&string.Equals(Path.GetDirectoryName(p),directory,StringComparison.OrdinalIgnoreCase));
        var marker=System.Text.Encoding.ASCII.GetBytes("HDT Skinned Mesh Physics Object");
        foreach(string path in siblings.Where(p=>!p.Equals(weaponPath,StringComparison.OrdinalIgnoreCase)).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).Take(256).ToList()){
            Nifcontent_Class_Manolo nif;string? link;string? xml;
            try{
                byte[] bytes=assets.Read(path);
                if(bytes.AsSpan().IndexOf(marker)<0)continue;
                nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(bytes);link=nif.TryGetSmpPhysicsXmlPath();
                if(link==null)continue;
                xml=ReadPhysicsXml(link,path,out _);
                if(xml==null)continue;
            }catch(Exception){continue;}
            bool names;
            try{names=System.Xml.Linq.XDocument.Parse(xml.TrimStart('﻿')).Descendants().Any(e=>e.Name.LocalName=="bone"&&e.Attribute("name") is {} a&&weaponNodes.ContainsKey(a.Value));}
            catch(System.Xml.XmlException){names=false;}
            if(names)yield return (path,nif,link);
        }
    }

    void NotePhysicsTime(double time)
    {
        if(smp==null)return;
        double delta=time-Time;
        // Playback only moves forward in small steps; anything else is a seek or a loop wrap.
        if(delta<-1e-6||delta>.25)physicsJump=true;
    }
    // Actor meshes skinned to a simulated bone, to a bone below one, or (for the weapon) to a simulated weapon node.
    // Every other mesh has the same bone matrices whatever the simulation does, so a physics step re-skins only these.
    IRenderableShape[] physicsShapes=[];
    public int PhysicsMovedShapes=>physicsShapes.Length;
    void FindPhysicsShapes()
    {
        var moved=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool weaponMoved=false;
        void Below(HierarchiBone_class bone){if(!moved.Add(bone.BoneName))return;foreach(var child in bone.Childrens)Below(child);}
        foreach(var system in smp?.Systems??[])foreach(var bone in system.Bones){
            if(bone.Kinematic)continue;
            if(bone.Node is SkeletonNode node)Below(node.Bone);else weaponMoved=true;
        }
        physicsShapes=poseShapes.Where(shape=>weaponMoved&&weaponShapes.Contains(shape)||shape.ShapeBones.Any(b=>moved.Contains(b.Name?.String??""))).ToArray();
    }
    void MarkPhysicsDirty(bool movedOnly=false)
    {
        if(committing)return;
        var shapes=movedOnly&&smp!=null&&!SmpWorld.Unoptimized?physicsShapes:poseShapes;
        if(shapes.Length>0)view.Intent.MarkDirty(RenderDirtyFlags.Pose,shapes);
        view.UpdateRequired=true;view.Invalidate();
    }
    // Advances the simulation by the wall-clock time since the last call. Physics keeps running while playback is
    // paused, as it does in game while an actor stands still. Returns true when bones were written.
    public bool StepPhysics()
    {
        if(smp==null||committing)return false;
        double now=physicsClock.Elapsed.TotalSeconds,interval=FixedPhysicsInterval??now-lastPhysicsTime;lastPhysicsTime=now;
        if(physicsWeight!=Weight&&now-weightChangedAt>.3){RebuildPhysics();return smp!=null;}
        ClearPhysicsCaches();physicsActor=ActorWorld(Time);physicsWeapon=WeaponWorld(Time);
        string weaponBone=WeaponBoneName;
        bool jump=physicsJump;
        if(interval>.5||weaponBone!=lastWeaponBone){smp.RequestReset();jump=false;}
        // A seek or loop wrap: carry the simulation with the actor. The update then resets it if the pose itself jumped.
        else if(jump&&lastPhysicsActor.Determinant!=0)smp.Carry(lastPhysicsActor.Inverted()*physicsActor);
        physicsJump=false;lastPhysicsActor=physicsActor;lastWeaponBone=weaponBone;
        smp.UpdateAllBodies=PhysicsColliders;
        bool stepped=smp.Update(Math.Min(interval,.25),jump);ClearPhysicsCaches();
        if(!stepped){if(jump)physicsJump=true;return false;}
        if(jump)LastPoseJump=smp.LastKinematicGap;
        if(smp.Diagnostics.Count>0){foreach(string line in smp.Diagnostics)if(!Diagnostics.Contains(line))Diagnostics.Add(line);smp.Diagnostics.Clear();}
        // Once every simulated bone hangs still there is nothing new to draw; a paused scene then stops redrawing.
        if(smp.LastMotion>1e-3||PhysicsColliders||PhysicsBones)MarkPhysicsDirty(movedOnly:true);
        return true;
    }
    public void ResetPhysics(){smp?.RequestReset();}
    internal V3d ActorOrigin=>ActorWorld(Time).ExtractTranslation();

    void DrawPhysicsOverlay(M4 projection)
    {
        if(smp==null||!(PhysicsColliders||PhysicsBones))return;
        var inverse=view.camera.GetViewMatrix().Inverted();
        var right=new V3d(inverse.Row0.X,inverse.Row0.Y,inverse.Row0.Z);var up=new V3d(inverse.Row1.X,inverse.Row1.Y,inverse.Row1.Z);
        // Camera-facing rings depend on the view, everything else on the simulation state, which only a step changes.
        var key=(smp,smp.Steps,PhysicsColliders,PhysicsBones,right,up);
        if(!key.Equals(overlayKey)||SmpWorld.Unoptimized){smpDraw.Build(smp,PhysicsColliders,PhysicsBones,right,up);overlayKey=key;}
        smpDraw.Draw(projection,view.camera);
    }
}
