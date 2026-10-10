using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Bitfields;
using NiflySharp.Enums;

namespace SSEPreview;
public static class ParticleFixture
{
    public static string CreateMeshParticles(string directory)
    {
        var n=new NifFile();if(n.Load(CreateRotation(directory,true))!=0)throw new InvalidDataException("Cannot reload mesh particle source");
        var old=n.Blocks.OfType<NiParticleSystem>().Single();var ps=new NiMeshParticleSystem();
        static void CopyFields(object source,object destination,Type type){for(Type? current=type;current!=null;current=current.BaseType)foreach(var field in current.GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.DeclaredOnly))field.SetValue(destination,field.GetValue(source));}
        CopyFields(old,ps,typeof(NiParticleSystem));
        n.ReplaceBlock(old,ps);ps.Name=new("Original tetrahedron debris");
        var oldData=n.Blocks.OfType<NiPSysData>().Single();var data=new NiMeshPSysData();
        CopyFields(oldData,data,typeof(NiPSysData));
        typeof(NiGeometryData).GetField("_bSMaxVertices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(data,(ushort)128);
        n.ReplaceBlock(oldData,data);data.HasRotationAxes=true;data.HasRotationAngles=true;
        data.ParticleMeshes=new(n.AddBlock(new NiNode{Name=new("Runtime particle pool"),Scale=1}));
        var mesh=new BSTriShape(NiVersion.GetSSE(),[new(-2,-1,-1),new(2,-1,-1),new(0,2,-1),new(0,0,3)],
            [new Triangle(0,2,1),new Triangle(0,1,3),new Triangle(1,2,3),new Triangle(2,0,3)],
            [new(0,0),new(1,0),new(.5f,1),new(.5f,.5f)],[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ])
            {Name=new("Debris template"),Scale=1,IsSkinned=false,ShaderPropertyRef=ps.ShaderPropertyRef,AlphaPropertyRef=ps.AlphaPropertyRef};
        int meshIndex=n.AddBlock(mesh);
        var modifier=new NiPSysMeshUpdateModifier{Name=new("Geometry instances"),Target=new(n.Blocks.IndexOf(ps)),Active=true,Order=(NiPSysModifierOrder)6500,NumMeshes=1,Meshes=new()};modifier.Meshes.AddBlockRef(meshIndex);ps.Modifiers.AddBlockRef(n.AddBlock(modifier));
        var rotation=n.Blocks.OfType<NiPSysRotationModifier>().Single();rotation.RandomAxis=true;rotation.RotationSpeed=3;rotation.RotationSpeedVariation=1;
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.InitialRadius=3.5f;emitter.RadiusVariation=.5f;emitter.LifeSpan=3;emitter.LifeSpanVariation=0;emitter.Declination=.7f;
        n.Blocks.OfType<NiFloatInterpolator>().Single().Value=12;
        string path=Path.Combine(directory,@"meshes\previewer\mesh-debris.nif");if(n.Save(path)!=0)throw new InvalidDataException("Mesh particle fixture save failed");return path;
    }
    public static string CreateAnimatedMeshParticles(string directory)
    {
        var n=new NifFile();if(n.Load(CreateMeshParticles(directory))!=0)throw new InvalidDataException("Cannot reload mesh fixture");
        var mesh=n.Blocks.OfType<BSTriShape>().Single();int meshId=n.Blocks.IndexOf(mesh);
        var root=new NiNode{Name=new("Animated debris template"),Scale=1};root.Children.AddBlockRef(meshId);
        var modifier=n.Blocks.OfType<NiPSysMeshUpdateModifier>().Single();modifier.Meshes.Clear();modifier.Meshes.AddBlockRef(n.AddBlock(root));
        var data=new NiTransformData{Translations=new KeyGroup<Vector3>{NumKeys=2,Interpolation=(KeyType)1,
            Keys=[new Key<Vector3>{Time=0,Value=Vector3.Zero},new Key<Vector3>{Time=2,Value=new(0,0,10)}]}};
        var interp=new NiTransformInterpolator{Data=new(n.AddBlock(data)),Transform=new NiQuatTransform{Scale=1,Translation=Vector3.Zero,Rotation=Quaternion.Identity}};
        mesh.Controller=new(n.AddBlock(new NiTransformController{Target=new(meshId),Frequency=1,StartTime=0,StopTime=2,
            Flags=new TimeControllerFlags{Value=76},Interpolator=new(n.AddBlock(interp))}));
        string path=Path.Combine(directory,@"meshes\previewer\mesh-animated.nif");if(n.Save(path)!=0)throw new InvalidDataException("Animated mesh fixture save failed");return path;
    }
    public static string CreateMeshVisibility(string directory)
    {
        var n=new NifFile();if(n.Load(CreateAnimatedMeshParticles(directory))!=0)throw new InvalidDataException("Cannot reload animated mesh fixture");
        var modifier=n.Blocks.OfType<NiPSysMeshUpdateModifier>().Single();var root=(NiNode)n.GetBlock(modifier.Meshes.References.Single());
        var keys=new NiBoolData{Data=new KeyGroup<byte>{NumKeys=3,Interpolation=(KeyType)5,
            Keys=[new(){Time=0,Value=0},new(){Time=.25f,Value=1},new(){Time=.75f,Value=0}]}};
        root.Controller=new(n.AddBlock(new NiVisController{Target=new(n.Blocks.IndexOf(root)),Frequency=1,StartTime=0,StopTime=3,
            Flags=new TimeControllerFlags{Value=76},Interpolator=new(n.AddBlock(new NiBoolInterpolator{Value=true,Data=new(n.AddBlock(keys))}))}));
        string path=Path.Combine(directory,@"meshes\previewer\mesh-visibility.nif");if(n.Save(path)!=0)throw new InvalidDataException("Mesh visibility fixture save failed");return path;
    }
    public static string CreateBillboardScale(string directory,float scale)
    {
        var n=new NifFile();if(n.Load(CreateColorPacking(directory))!=0)throw new InvalidDataException("Cannot reload scale fixture");
        var system=n.Blocks.OfType<NiParticleSystem>().Single();system.WorldSpace=false;
        var root=n.Blocks.OfType<NiNode>().First();root.Scale=scale;
        string path=Path.Combine(directory,@"meshes\previewer\billboard-scale-"+scale.ToString("g",System.Globalization.CultureInfo.InvariantCulture)+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save scale fixture");return path;
    }
    public static string CreateDistanceFade(string directory)
    {
        var n=new NifFile();if(n.Load(CreateColorPacking(directory))!=0)throw new InvalidDataException("Cannot reload fade fixture");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Camera distance fade");ps.FarBegin=300;ps.FarEnd=200;ps.NearBegin=100;ps.NearEnd=50;
        typeof(NiGeometryData).GetField("_bSMaxVertices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(n.Blocks.OfType<NiPSysData>().Single(),(ushort)1);
        n.Blocks.OfType<NiPSysBoxEmitter>().Single().InitialColor=new Color4{R=1,G=1,B=1,A=1};
        n.Blocks.OfType<NiAlphaProperty>().Single().Flags=new AlphaFlags{Value=237};
        string path=Path.Combine(directory,@"meshes\previewer\distance-fade.nif");if(n.Save(path)!=0)throw new InvalidDataException("Cannot save fade fixture");return path;
    }
    public static string CreateColorPacking(string directory)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload color fixture");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Native packed particle color");
        var keep=ps.Modifiers.Indices.Where(i=>n.Blocks[i] is NiPSysBoxEmitter or NiPSysAgeDeathModifier or NiPSysPositionModifier or NiPSysBoundUpdateModifier).ToArray();
        ps.Modifiers.Clear();foreach(int i in keep)ps.Modifiers.AddBlockRef(i);
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.Speed=0;emitter.SpeedVariation=0;emitter.Width=0;emitter.Depth=0;emitter.Height=0;
        emitter.InitialColor=new Color4{R=.5f,G=1.5f,B=-.5f,A=.5f};emitter.InitialRadius=25;emitter.RadiusVariation=0;
        var shader=n.Blocks.OfType<BSEffectShaderProperty>().Single();shader.SourceTexture=new("");shader.BaseColor=new Color4{R=1,G=1,B=1,A=1};
        n.Blocks.OfType<NiAlphaProperty>().Single().Flags=new AlphaFlags{Value=0};
        string path=Path.Combine(directory,@"meshes\previewer\color-packing.nif");if(n.Save(path)!=0)throw new InvalidDataException("Cannot save color fixture");return path;
    }
    public static string CreateLag(string directory,bool enabled)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload lag source");
        var root=n.Blocks.OfType<NiNode>().First();var ps=n.Blocks.OfType<NiParticleSystem>().Single();
        root.Children.Clear();
        var lag=new NiNode{Name=new("Lag bone"),Scale=1,Translation=new(20,0,0)};
        int index=n.AddBlock(lag);root.Children.AddBlockRef(index);lag.Children.AddBlockRef(n.Blocks.IndexOf(ps));
        lag.Controller=new(n.AddBlock(new BSLagBoneController{Target=new(index),Frequency=1,StartTime=0,StopTime=10,
            Flags=new TimeControllerFlags{Value=(ushort)(enabled?76:68)},LinearVelocity=3,LinearRotation=1,MaximumDistance=40}));
        ps.WorldSpace=false;n.Blocks.OfType<NiPSysBoxEmitter>().Single().EmitterObject=new(index);
        var data=new NiTransformData{Translations=new KeyGroup<Vector3>{NumKeys=2,Interpolation=(KeyType)1,
            Keys=[new Key<Vector3>{Time=0,Value=Vector3.Zero},new Key<Vector3>{Time=4,Value=new(800,0,0)}]}};
        var interpolator=new NiTransformInterpolator{Data=new(n.AddBlock(data)),Transform=new NiQuatTransform{Scale=1,Translation=Vector3.Zero,Rotation=Quaternion.Identity}};
        root.Controller=new(n.AddBlock(new NiTransformController{Target=new(n.Blocks.IndexOf(root)),Frequency=1,StartTime=0,StopTime=4,
            Flags=new TimeControllerFlags{Value=76},Interpolator=new(n.AddBlock(interpolator))}));
        string path=Path.Combine(directory,@"meshes\previewer\lag-"+(enabled?"on":"off")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save lag fixture");return path;
    }
    public static string CreateAspect(string directory,bool enabled)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload aspect source");
        var data=n.Blocks.OfType<NiPSysData>().Single();data.AspectRatio=1;data.AspectFlags=(AspectFlags)(enabled?256:0);
        data.SpeedtoAspectAspect2=.25f;data.SpeedtoAspectSpeed1=0;data.SpeedtoAspectSpeed2=100;
        string path=Path.Combine(directory,@"meshes\previewer\aspect-"+(enabled?"on":"off")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save aspect fixture");return path;
    }
    public static string CreateOrientation(string directory,int flags)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload orientation source");
        var data=n.Blocks.OfType<NiPSysData>().Single();data.AspectRatio=.2f;data.AspectFlags=(AspectFlags)flags;
        data.SpeedtoAspectAspect2=.8f;data.SpeedtoAspectSpeed1=0;data.SpeedtoAspectSpeed2=100;
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.Declination=1;emitter.DeclinationVariation=.2f;
        string path=Path.Combine(directory,@"meshes\previewer\orientation-"+flags+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save orientation fixture");return path;
    }
    public static string CreateRotation(string directory,bool enabled)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload rotation source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Rotation activity fixture");
        var data=n.Blocks.OfType<NiPSysData>().Single();data.HasRotationAngles=true;data.HasRotationSpeeds=true;
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysRotationModifier{Name=new("Spin"),Target=new(n.Blocks.IndexOf(ps)),Active=enabled,
            Order=(NiPSysModifierOrder)4000,RotationAngle=.3f,RotationSpeed=2,RandomAxis=false,Axis=Vector3.UnitZ}));
        string path=Path.Combine(directory,@"meshes\previewer\rotation-"+(enabled?"on":"off")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save rotation fixture");return path;
    }
    public static string CreateGravityDecay(string directory)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload gravity source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Planar gravity decay fountain");
        var gravity=n.Blocks.OfType<NiPSysGravityModifier>().Single();gravity.Decay=.05f;
        string path=Path.Combine(directory,@"meshes\previewer\gravity-decay.nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save gravity fixture");return path;
    }
    public static string CreateGravityTurbulence(string directory,bool enabled=true)
    {
        var n=new NifFile();if(n.Load(CreateGravityDecay(directory))!=0)throw new InvalidDataException("Cannot reload gravity source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Gravity turbulence fountain");
        var gravity=n.Blocks.OfType<NiPSysGravityModifier>().Single();gravity.Turbulence=enabled?2:0;gravity.TurbulenceScale=1;
        string path=Path.Combine(directory,@"meshes\previewer\gravity-turbulence-"+(enabled?"on":"off")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save turbulence fixture");return path;
    }
    public static string CreateWorldAlignedGravity(string directory,bool aligned)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload gravity source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("World aligned gravity "+aligned);
        var gravity=n.Blocks.OfType<NiPSysGravityModifier>().Single();gravity.WorldAligned=aligned;
        var parent=new NiNode{Name=new("Rotated gravity parent"),Scale=1,Children=new()};
        int parentId=n.AddBlock(parent);n.Blocks.OfType<NiNode>().First().Children.AddBlockRef(parentId);
        var source=new NiNode{Name=new("Gravity source"),Scale=1};int sourceId=n.AddBlock(source);parent.Children.AddBlockRef(sourceId);
        gravity.GravityObject=new(sourceId);
        var interpolator=new NiTransformInterpolator{Transform=new NiQuatTransform{Scale=1,Translation=Vector3.Zero,Rotation=Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2)}};
        parent.Controller=new(n.AddBlock(new NiTransformController{Target=new(parentId),Flags=new TimeControllerFlags{Value=76},Frequency=1,StartTime=0,StopTime=10,Interpolator=new(n.AddBlock(interpolator))}));
        string path=Path.Combine(directory,@"meshes\previewer\gravity-world-aligned-"+(aligned?"on":"off")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save world aligned gravity fixture");return path;
    }
    public static string CreateLod(string directory)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload LOD source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("LOD fountain");
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.InitialRadius=4;emitter.RadiusVariation=0;emitter.LifeSpan=5;emitter.LifeSpanVariation=0;
        ps.Modifiers.AddBlockRef(n.AddBlock(new BSPSysLODModifier{Name=new("Distance LOD"),Target=new(n.Blocks.IndexOf(ps)),Active=true,Order=(NiPSysModifierOrder)1,
            LODBeginDistance=.1f,LODEndDistance=.5f,EndEmitScale=.25f,EndSize=2}));
        string path=Path.Combine(directory,@"meshes\previewer\particle-lod.nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save LOD fixture");return path;
    }
    public static void CreateRenderStates(string directory)
    {
        string source=Create(directory);
        foreach(string mode in new[]{"always","never","greater","uv-repeat","uv-clamp","palette-zero","palette-quarter","palette-full"}){
            var n=new NifFile();if(n.Load(source)!=0)throw new InvalidDataException("Cannot reload render-state source");
            var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Render state "+mode);
            int comparison=mode=="never"?7:mode=="always"?0:4;
            var alpha=n.Blocks.OfType<NiAlphaProperty>().Single();
            alpha.Flags=new AlphaFlags{Value=(ushort)(237|512|(comparison<<10))};alpha.Threshold=64;
            var shader=n.Blocks.OfType<BSEffectShaderProperty>().Single();
            if(mode.StartsWith("palette-")){
                shader.HasGreyscaleToPaletteColor=true;
                shader.GreyscaleTexture=new(@"textures\previewer\particle.dds");
                shader.BaseColor=new Color4{R=.5f,G=1,B=1,A=1};
                shader.BaseColorScale=mode=="palette-zero"?0:mode=="palette-quarter"?.25f:1;
            }
            if(mode.StartsWith("uv-"))shader.UVOffset=new(){U=1,V=1};
            shader.TextureClampMode=(byte)(mode=="uv-clamp"?0:3);
            if(n.Save(Path.Combine(directory,@"meshes\previewer\render-"+mode+".nif"))!=0)
                throw new InvalidDataException("Cannot save render-state fixture");
        }
    }
    public static string CreateInheritance(string directory,bool enabled=true)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload inheritance source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Inherited motion sparks");
        int target=n.Blocks.IndexOf(ps);
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Width=emitter.Depth=emitter.Height=0;emitter.Speed=25;emitter.SpeedVariation=0;
        emitter.Declination=emitter.DeclinationVariation=emitter.PlanarAngleVariation=0;
        emitter.LifeSpan=2;emitter.LifeSpanVariation=0;emitter.InitialRadius=1;emitter.RadiusVariation=0;
        n.Blocks.OfType<NiPSysGravityModifier>().Single().Strength=0;
        var origin=new NiNode{Name=new("Moving source"),Scale=1,Children=new()};
        int originId=n.AddBlock(origin);n.Blocks.OfType<NiNode>().First().Children.AddBlockRef(originId);
        emitter.EmitterObject=new(originId);
        var interpolator=new NiTransformInterpolator{Transform=new NiQuatTransform{Scale=1,Translation=Vector3.Zero,Rotation=Quaternion.Identity}};
        var data=new NiTransformData{Translations=new KeyGroup<Vector3>{NumKeys=2,Interpolation=(KeyType)1,
            Keys=[new Key<Vector3>{Time=0,Value=Vector3.Zero},new Key<Vector3>{Time=2,Value=new(120,0,0)}]}};
        interpolator.Data=new(n.AddBlock(data));
        var controller=new NiTransformController{Target=new(originId),Flags=new TimeControllerFlags{Value=76},Frequency=1,StartTime=0,StopTime=2,Interpolator=new(n.AddBlock(interpolator))};
        origin.Controller=new(n.AddBlock(controller));
        ps.Modifiers.AddBlockRef(n.AddBlock(new BSPSysInheritVelocityModifier{Name=new("Source motion"),Target=new(target),Active=enabled,
            Order=(NiPSysModifierOrder)6000,InheritObject=new(originId),ChanceToInherit=100,VelocityMultiplier=.5f,VelocityVariation=0}));
        string path=Path.Combine(directory,@"meshes\previewer\inherit-"+(enabled?"motion":"disabled")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save inheritance fixture");return path;
    }
    public static string CreateBomb(string directory,bool enabled=true)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload bomb source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Cylindrical inward force");
        int target=n.Blocks.IndexOf(ps);
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Width=emitter.Depth=40;emitter.Height=2;emitter.Speed=35;emitter.SpeedVariation=0;
        emitter.Declination=emitter.DeclinationVariation=emitter.PlanarAngleVariation=0;
        emitter.LifeSpan=2;emitter.LifeSpanVariation=0;emitter.InitialRadius=1;emitter.RadiusVariation=0;
        n.Blocks.OfType<NiPSysGravityModifier>().Single().Strength=0;
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysBombModifier{Name=new("Inward cylinder"),Target=new(target),Active=enabled,
            Order=(NiPSysModifierOrder)4000,BombObject=new(0),BombAxis=Vector3.UnitZ,DeltaV=-120,Decay=40,
            DecayType=(DecayType)1,SymmetryType=(SymmetryType)1}));
        string path=Path.Combine(directory,@"meshes\previewer\bomb-"+(enabled?"inward":"disabled")+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save bomb fixture");return path;
    }
    public static string CreateCollider(string directory,bool sphere=false,bool die=false,bool spawn=false)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload collision source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Collision fixture");
        int target=n.Blocks.IndexOf(ps);
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();
        var origin=new NiNode{Name=new("Emission origin"),Translation=new(0,0,20),Scale=1,Children=new()};
        int originIndex=n.AddBlock(origin);n.Blocks.OfType<NiNode>().First().Children.AddBlockRef(originIndex);
        emitter.EmitterObject=new(originIndex);emitter.Width=emitter.Depth=16;emitter.Height=0;
        emitter.Speed=100;emitter.SpeedVariation=0;emitter.Declination=MathF.PI;
        emitter.DeclinationVariation=.2f;emitter.PlanarAngleVariation=MathF.PI;
        emitter.LifeSpan=1.3f;emitter.LifeSpanVariation=0;
        n.Blocks.OfType<NiPSysGravityModifier>().Single().Strength=0;
        NiPSysCollider collider=sphere?new NiPSysSphericalCollider{Radius=8}:
            new NiPSysPlanarCollider{Width=100,Height=100,XAxis=Vector3.UnitX,YAxis=Vector3.UnitY};
        collider.ColliderObject=new(0);collider.Bounce=.65f;collider.DieonCollide=die;collider.SpawnonCollide=spawn;
        if(spawn){
            var spawner=new NiPSysSpawnModifier{Name=new("Impact children"),Target=new(target),Active=true,Order=(NiPSysModifierOrder)1000,
                NumSpawnGenerations=1,PercentageSpawned=1,MinNumtoSpawn=2,MaxNumtoSpawn=2,LifeSpan=.5f};
            int si=n.AddBlock(spawner);ps.Modifiers.AddBlockRef(si);collider.SpawnModifier=new(si);
        }
        var manager=new NiPSysColliderManager{Name=new("Colliders"),Target=new(target),Active=true,Order=(NiPSysModifierOrder)5000};
        int managerId=n.AddBlock(manager);collider.Parent=new(managerId);manager.Collider=new(n.AddBlock(collider));ps.Modifiers.AddBlockRef(managerId);
        string name=sphere?"sphere":spawn?"spawn":die?"die":"plane";
        string path=Path.Combine(directory,@"meshes\previewer\collision-"+name+".nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save collision fixture");return path;
    }
    public static string CreateDrag(string directory)
    {
        var n=new NifFile();if(n.Load(Create(directory))!=0)throw new InvalidDataException("Cannot reload drag source");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Directional drag fixture");
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Width=emitter.Depth=emitter.Height=0;emitter.Speed=100;emitter.SpeedVariation=0;
        emitter.Declination=MathF.PI/4;emitter.DeclinationVariation=emitter.PlanarAngleVariation=0;
        emitter.LifeSpan=3;emitter.LifeSpanVariation=0;emitter.InitialRadius=2;emitter.RadiusVariation=0;
        n.Blocks.OfType<NiPSysGravityModifier>().Single().Strength=0;
        n.Blocks.OfType<NiFloatInterpolator>().Single().Value=20;
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysDragModifier{Name=new("X drag"),Target=new(n.Blocks.IndexOf(ps)),
            Active=true,Order=(NiPSysModifierOrder)4000,DragObject=new(0),DragAxis=Vector3.UnitX,
            Percentage=2,Range=float.MaxValue,RangeFalloff=float.MaxValue}));
        string path=Path.Combine(directory,@"meshes\previewer\directional-drag.nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save drag fixture");return path;
    }
    public static string CreateSpawn(string directory)
    {
        string fountain=Create(directory);
        var n=new NifFile();if(n.Load(fountain)!=0)throw new InvalidDataException("Cannot reload source fixture");
        var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Spawn on death fixture");
        int target=n.Blocks.IndexOf(ps);
        var emitter=n.Blocks.OfType<NiPSysBoxEmitter>().Single();
        emitter.Width=emitter.Depth=emitter.Height=0;emitter.Speed=20;emitter.SpeedVariation=0;
        emitter.Declination=emitter.DeclinationVariation=emitter.PlanarAngleVariation=0;
        emitter.LifeSpan=.05f;emitter.LifeSpanVariation=0;emitter.InitialRadius=2;emitter.RadiusVariation=0;
        n.Blocks.OfType<NiPSysGravityModifier>().Single().Strength=0;
        var growth=n.Blocks.OfType<NiPSysGrowFadeModifier>().Single();growth.GrowTime=growth.FadeTime=0;
        var spawn=new NiPSysSpawnModifier{Name=new("Child generation"),Target=new(target),Active=true,Order=(NiPSysModifierOrder)1000,
            NumSpawnGenerations=2,PercentageSpawned=1,MinNumtoSpawn=2,MaxNumtoSpawn=2,LifeSpan=.1f,LifeSpanVariation=0};
        int spawnIndex=n.AddBlock(spawn);ps.Modifiers.AddBlockRef(spawnIndex);
        var death=n.Blocks.OfType<NiPSysAgeDeathModifier>().Single();death.SpawnonDeath=true;death.SpawnModifier=new(spawnIndex);
        n.Blocks.OfType<NiFloatInterpolator>().Single().Value=120;
        var visibility=n.Blocks.OfType<NiBoolInterpolator>().Single();
        visibility.Data=new(n.AddBlock(new NiBoolData{Data=new KeyGroup<byte>{NumKeys=2,Interpolation=(KeyType)5,
            Keys=[new Key<byte>{Time=0,Value=1},new Key<byte>{Time=(float)ParticleSimulation.Step,Value=0}]}}));
        string path=Path.Combine(directory,@"meshes\previewer\spawn-on-death.nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save spawn fixture");return path;
    }
    // Original CC0 conformance assets; no game or mod files copied into this fixture.
    public static string Create(string directory)
    {
        string texture=Path.Combine(directory,@"textures\previewer\particle.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
        using(var file=File.Create(texture))using(var w=new BinaryWriter(file)){
            w.Write(0x20534444u);w.Write(124u);w.Write(0x100Fu);w.Write(64);w.Write(64);w.Write(256);w.Write(0);w.Write(0);
            for(int i=0;i<11;i++)w.Write(0);
            w.Write(32);w.Write(0x41);w.Write(0);w.Write(32);w.Write(0x000000FFu);w.Write(0x0000FF00u);w.Write(0x00FF0000u);w.Write(0xFF000000u);
            w.Write(0x1000);for(int i=0;i<4;i++)w.Write(0);
            for(int y=0;y<64;y++)for(int x=0;x<64;x++){float r=Vector2.Distance(new((x+.5f)/32-1,(y+.5f)/32-1),Vector2.Zero);w.Write((byte)255);w.Write((byte)255);w.Write((byte)255);w.Write((byte)(Math.Clamp(1-r,0,1)*255));}
        }
        var n=new NifFile(NiVersion.GetSSE(),true);var root=n.Blocks.OfType<NiNode>().First();root.Name=new("Particle fixture");
        var ps=new NiParticleSystem{Name=new("Authored fountain"),WorldSpace=true,Modifiers=new(),Scale=1,Translation=new(0,0,0)};
        int psi=n.AddBlock(ps);root.Children.AddBlockRef(psi);
        var data=new NiPSysData{AspectRatio=1,HasRadii=true,HasSizes=true,HasRotationAngles=true};
        typeof(NiGeometryData).GetField("_bSMaxVertices",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(data,(ushort)512);
        ps.Data_NiBlockRef_NPSD=new(n.AddBlock(data));
        var shader=new BSEffectShaderProperty{SourceTexture=new(@"textures\previewer\particle.dds"),BaseColor=new Color4{R=1,G=.55f,B=.15f,A=1},BaseColorScale=1,UVScale=new(){U=1,V=1},TextureClampMode=3};
        shader.ShaderFlags_SSPF1=(SkyrimShaderPropertyFlags1)0x80000008u;
        shader.ShaderFlags_SSPF2=(SkyrimShaderPropertyFlags2)32u;
        ps.ShaderPropertyRef=new(n.AddBlock(shader));
        ps.AlphaPropertyRef=new(n.AddBlock(new NiAlphaProperty{Flags=new AlphaFlags{Value=237}}));
        var emitter=new NiPSysBoxEmitter{Name=new("Emitter"),Target=new(psi),Active=true,Order=(NiPSysModifierOrder)1000,EmitterObject=new(0),Width=12,Depth=12,Height=1,
            Speed=65,SpeedVariation=12,Declination=.2f,DeclinationVariation=.3f,PlanarAngleVariation=MathF.PI,
            InitialColor=new Color4{R=1,G=1,B=1,A=1},InitialRadius=2.2f,RadiusVariation=.7f,LifeSpan=1.8f,LifeSpanVariation=.3f};
        ps.Modifiers.AddBlockRef(n.AddBlock(emitter));
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysAgeDeathModifier{Name=new("Death"),Target=new(psi),Active=true,Order=(NiPSysModifierOrder)0}));
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysGravityModifier{Name=new("Gravity"),Target=new(psi),Active=true,Order=(NiPSysModifierOrder)4000,GravityObject=new(0),GravityAxis=new(0,0,-1),Strength=40}));
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysPositionModifier{Name=new("Position"),Target=new(psi),Active=true,Order=(NiPSysModifierOrder)6000}));
        ps.Modifiers.AddBlockRef(n.AddBlock(new NiPSysGrowFadeModifier{Name=new("Grow fade"),Target=new(psi),Active=true,Order=(NiPSysModifierOrder)10000,GrowTime=.12f,FadeTime=.5f,BaseScale=1}));
        int rate=n.AddBlock(new NiFloatInterpolator{Value=90});
        int visible=n.AddBlock(new NiBoolInterpolator{Value=true});
        var ctlr=new NiPSysEmitterCtlr{ModifierName=new("Emitter"),Target=new(psi),Frequency=1,StartTime=0,StopTime=10,Flags=new TimeControllerFlags{Value=76},Interpolator=new(rate),VisibilityInterpolator=new(visible)};
        ps.Controller=new(n.AddBlock(ctlr));
        string path=Path.Combine(directory,@"meshes\previewer\fountain.nif");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if(n.Save(path)!=0)throw new InvalidDataException("Fixture NIF save failed");
        return path;
    }
}
