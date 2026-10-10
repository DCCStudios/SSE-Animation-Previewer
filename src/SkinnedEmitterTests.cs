using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Bitfields;
using NiflySharp.Enums;
namespace SSEPreview;
internal static class SkinnedEmitterTests
{
    internal static string Fixture(string directory)
    {
        var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(MultiEmitterTests.Fixture(directory)));
        var root=n.Blocks.OfType<NiNode>().First();var ps=n.Blocks.OfType<NiParticleSystem>().Single();ps.Name=new("Animated skin emitter");
        var mesh=n.Blocks.OfType<BSTriShape>().Single();mesh.IsSkinned=true;mesh.Translation=new(100,0,0);
        var a=new NiNode{Name=new("Fixed bone"),Scale=1,Translation=new(-10,0,0)};int aId=n.AddBlock(a);root.Children.AddBlockRef(aId);
        var b=new NiNode{Name=new("Rising bone"),Scale=1,Translation=new(10,0,0)};int bId=n.AddBlock(b);root.Children.AddBlockRef(bId);
        NiTransform Bind(Vector3 translation)=>new(){Rotation=a.Rotation,Translation=translation,Scale=1};
        var data=new NiSkinData{NumBones=2,HasVertexWeights=true,SkinTransform=Bind(Vector3.Zero),BoneList=[new BoneData{SkinTransform=Bind(new(10,0,0)),VertexWeights=[]},new BoneData{SkinTransform=Bind(new(-10,0,0)),VertexWeights=[]}]};
        var skin=new NiSkinInstance{Data=new(n.AddBlock(data)),SkinPartition=new(n.AddBlock(new NiSkinPartition{NumPartitions=1,Partitions=[new SkinPartition{TrianglesCopy=[new Triangle(0,1,2)]}]})),SkeletonRoot=new(n.Blocks.IndexOf(root)),NumBones=2,Bones=new()};skin.Bones.AddBlockRef(aId);skin.Bones.AddBlockRef(bId);mesh.SkinInstanceRef=new(n.AddBlock(skin));
        var renderable=new NifRenderableShape(n,mesh,n.Blocks.IndexOf(mesh));renderable.Geometry.SetSkinning(new ShapeSkinningData{VertexCount=3,WeightsPerVertex=4,BoneRefIndices=[aId,bId],BoneIndices=[0,0,0,0,1,0,0,0,0,1,0,0],BoneWeights=[(Half)1,(Half)0,(Half)0,(Half)0,(Half)1,(Half)0,(Half)0,(Half)0,(Half).5f,(Half).5f,(Half)0,(Half)0]});
        var motion=new NiTransformData{Translations=new KeyGroup<Vector3>{NumKeys=2,Interpolation=(KeyType)1,Keys=[new(){Time=0,Value=new(10,0,0)},new(){Time=4,Value=new(10,0,240)}]}};
        var interp=new NiTransformInterpolator{Data=new(n.AddBlock(motion)),Transform=new NiQuatTransform{Scale=1,Translation=b.Translation,Rotation=Quaternion.Identity}};
        b.Controller=new(n.AddBlock(new NiTransformController{Target=new(bId),Frequency=1,StartTime=0,StopTime=4,Flags=new TimeControllerFlags{Value=76},Interpolator=new(n.AddBlock(interp))}));
        var emitter=n.Blocks.OfType<NiPSysMeshEmitter>().Single();emitter.Speed=0;emitter.InitialRadius=3;emitter.InitialVelocityType=(VelocityType)0;
        var keep=ps.Modifiers.Indices.Where(i=>n.Blocks[i] is NiPSysMeshEmitter or NiPSysAgeDeathModifier or NiPSysPositionModifier or NiPSysBoundUpdateModifier).ToArray();ps.Modifiers.Clear();foreach(int i in keep)ps.Modifiers.AddBlockRef(i);
        n.UpdateSkinPartitions(mesh);
        string file=Path.Combine(directory,@"meshes\previewer\skinned-emitter.nif");if(n.Save(file)!=0)throw new InvalidDataException("Cannot save skinned emitter fixture");return file;
    }
    internal static void Run(Action<bool,string> check)
    {
        string file=Fixture(Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures"));
        ParticleNif Load(){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(file));return new(n,n.Blocks.OfType<NiParticleSystem>().Single(),41);}
        var system=Load();check(system.MeshSourceBindings.Single().BoundEmitterMeshes.Length==1,"Saved SSE skin source retains bone palette, partition and vertex influences");
        var sample=system.Simulation.MeshSample!;
        bool Near(Vector3 a,Vector3 b)=>Vector3.Distance(a,b)<.001f;
        check(Near(sample(0).Position,new(0,8f/3,0))&&Near(sample(.5).Position,new(0,8f/3,15))&&Near(sample(1).Position,new(0,8f/3,30)),"Mesh emitter samples the blended two-bone surface at each birth time without applying the mesh transform twice");
        system.Placement=_=>Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(20,30,40);
        check(Near(sample(.5).Position,new(20-8f/3,30,55)),"World-space skin emission applies effect rotation and translation once");
        system.Placement=_=>Matrix4x4.Identity;system.Simulation.Reset();system.Seek(.5);var particles=system.Simulation.Particles;
        check(particles.Count==15&&particles.Min(p=>p.Position.Z)<2&&particles.Max(p=>p.Position.Z)>14&&particles.All(p=>Math.Abs(p.Position.X)<.001f),"Moving skinned source leaves stationary particles at their distinct birth positions");
        string State()=>string.Join(";",system.Simulation.Particles.Select(p=>$"{p.Position}|{p.Velocity}|{p.Age}|{p.FrameSeed}"));string state=State();system.Seek(.1);system.Seek(.5);check(State()==state,"Animated skin emission reproduces exactly after backward seek");
        var local=Load();local.System.WorldSpace=false;local.System.Translation=new(7,8,9);var localSystem=new ParticleNif(local.Nif,local.System,41);
        check(Near(localSystem.Simulation.MeshSample!(.5).Position,new(-7,8f/3-8,6)),"Local-space skin emission converts NIF world positions into particle-system coordinates once");
        var normalsCase=Load();var normalMesh=normalsCase.Nif.Blocks.OfType<BSTriShape>().Single();
        var surface=new ParticleMesh(normalMesh.VertexPositions.ToArray(),normalMesh.Normals.ToArray(),normalMesh.Triangles.ToArray());
        ParticleSkin.Bind(normalsCase.Nif,normalMesh,surface,node=>node is NiNode bone&&bone.Name.ToString()=="Rising bone"?Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationY(MathF.PI/2):Matrix4x4.Identity);
        var posedNormal=surface.Sample(0,()=>.5f).Normal;var savedNormal=Vector3.Normalize(normalMesh.Normals[1]);var expectedNormal=Vector3.TransformNormal(savedNormal,Matrix4x4.CreateRotationY(MathF.PI/2));
        check(Near(posedNormal,expectedNormal)&&Math.Abs(posedNormal.Length()-1)<1e-5f,"Skinned mesh vertex normals follow bone rotation and remain normalized with scale (using saved packed normal)");
        var weightsMesh=normalsCase.Nif.Blocks.OfType<BSTriShape>().Single();var vertex=weightsMesh.VertexDataSSE[0];vertex.BoneWeights[0]=(Half)0;weightsMesh.VertexDataSSE[0]=vertex;
        bool weightRejected=false;try{ParticleSkin.Bind(normalsCase.Nif,weightsMesh,surface,_=>Matrix4x4.Identity);}catch(InvalidDataException ex){weightRejected=ex.Message.Contains("no positive bone weights");}
        check(weightRejected,"Weightless skinned emitter vertices are rejected instead of emitting nonfinite or collapsed positions");
        var broken=Load();var instance=broken.Nif.Blocks.OfType<NiSkinInstance>().Single();instance.Bones.Clear();instance.Bones.AddBlockRef(-1);instance.Bones.AddBlockRef(broken.Nif.Blocks.IndexOf(broken.Nif.Blocks.OfType<NiNode>().Single(n=>n.Name.ToString()=="Rising bone")));
        var rejected=new ParticleNif(broken.Nif,broken.System,41);rejected.Seek(.5);check(rejected.Simulation.TotalBorn==0&&rejected.Diagnostics.Any(d=>d.Contains("invalid bone palette")),"Invalid skin palette is diagnosed and suppresses births instead of shifting bone indices or emitting at bind pose");
    }
}
