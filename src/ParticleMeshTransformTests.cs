using System.Numerics;
using FO4_Base_Library;
using NiflySharp.Blocks;
namespace SSEPreview;
internal static class ParticleMeshTransformTests
{
    internal static void Run(Action<bool,string> check,string root)
    {
        check(ParticleMeshTransform.TemplateIndex(0,3)==0&&ParticleMeshTransform.TemplateIndex(1,3)==1&&ParticleMeshTransform.TemplateIndex(7,3)==2,"Mesh particle generation selects its template and clamps to the last authored mesh");
        check(ParticleMeshTransform.TemplateIndex(0,0)==-1,"Empty mesh template lists produce no instance");
        var p=new ParticleSimulation.Particle{Position=new(10,20,30),Radius=-2,Angle=MathF.PI/2,RotationAxis=Vector3.UnitX};
        var transformed=Vector3.Transform(Vector3.UnitY,ParticleMeshTransform.At(p,true,Matrix4x4.Identity));
        check(Vector3.Distance(transformed,new(10,20,32))<1e-5f,"Mesh particle transform uses absolute radius, authored 3D axis, and particle position");
        var retained=ParticleMeshTransform.At(p,false,Matrix4x4.CreateRotationZ(MathF.PI/2));
        check(Vector3.Distance(Vector3.Transform(Vector3.UnitX,retained),new(10,22,30))<1e-5f,"Mesh particles without rotation arrays retain the template root rotation");
        check(ParticleEmitterSampling.RotationAxis(()=>0)==Vector3.UnitZ,"Native random rotation axis starts at the polar north endpoint");
        int draw=0;var equator=ParticleEmitterSampling.RotationAxis(()=>draw++==0?.5f:.25f);
        check(draw==2&&Vector3.Distance(equator,Vector3.UnitY)<1e-5f,"Rotation axes use two native quantized angle draws");
        check(Vector3.Distance(ParticleEmitterSampling.RotationAxis(()=>1),-Vector3.UnitZ)<1e-5f,"Native random rotation axis includes the south endpoint");
        var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateRotation(root,true)));
        var data=nif.Blocks.OfType<NiPSysData>().Single();data.HasRotationAxes=true;
        var modifier=nif.Blocks.OfType<NiPSysRotationModifier>().Single();modifier.Axis=new(0,2,0);modifier.RandomAxis=false;
        var system=new ParticleNif(nif,nif.Blocks.OfType<NiParticleSystem>().Single());system.Seek(.3);
        check(system.Simulation.Particles.Count>0&&system.Simulation.Particles.All(p=>p.RotationAxis==new Vector3(0,2,0)),"Fixed authored particle axes are preserved without an invented normalization");
        modifier.RandomAxis=true;system.Simulation.Reset();system.Seek(.3);
        var axes=system.Simulation.Particles.Select(p=>p.RotationAxis).ToArray();
        check(axes.Distinct().Count()>1&&axes.All(a=>Math.Abs(a.Length()-1)<1e-5),"Random mesh particle axes remain finite unit vectors");
        system.Seek(.1);system.Seek(.3);
        check(axes.SequenceEqual(system.Simulation.Particles.Select(p=>p.RotationAxis)),"Random particle axes reproduce after rewind");
        var meshNif=new Nifcontent_Class_Manolo();meshNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateMeshParticles(root)));
        var meshSystem=new ParticleNif(meshNif,meshNif.Blocks.OfType<NiMeshParticleSystem>().Single());meshSystem.Seek(.5);
        check(meshSystem.MeshArtwork is {HasRotation:true}&&meshSystem.MeshArtwork.Parts.Count==1&&meshSystem.MeshArtwork.Parts[0].Vertices.Length==12,"Serialized mesh-particle NIF resolves the authored tetrahedron and rotation arrays");
        check(ParticleMeshArtwork.TemplateObjects(meshNif).OfType<NiflySharp.INiShape>().Count()==1,"Template geometry is recognized separately from ordinary scene meshes");
        check(meshSystem.MeshArtwork!.Parts.Single().Texture==@"textures\previewer\particle.dds","Mesh template texture uses the serialized texture string content");
        var instance=meshSystem.Simulation.Particles.First();var part=meshSystem.MeshArtwork!.Parts.Single();
        var first=ParticleMeshArtwork.Vertices(part,instance,true,Matrix4x4.Identity).Select(v=>v.Position).ToArray();
        check(first.Distinct().Count()==4&&first.Any(v=>Math.Abs(v.Z-instance.Position.Z)>.1f),"Mesh particle output contains three-dimensional tetrahedron vertices instead of billboard corners");
        var animated=new Nifcontent_Class_Manolo();animated.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateAnimatedMeshParticles(root)));
        var animatedSystem=new ParticleNif(animated,animated.Blocks.OfType<NiMeshParticleSystem>().Single());
        var animatedPart=animatedSystem.MeshArtwork!.Parts.Single();
        var young=new ParticleSimulation.Particle{Radius=1,Age=.2f};var old=new ParticleSimulation.Particle{Radius=1,Age=1.2f};
        Vector3[] At(ParticleSimulation.Particle q)=>ParticleMeshArtwork.Vertices(animatedPart,q,false,Matrix4x4.Identity).Select(v=>v.Position).ToArray();
        var before=At(young);var later=At(old);
        check(Vector3.Distance(later[0]-before[0],new(0,0,5))<1e-5f,"Mesh descendant transform uses individual particle age");
        check(before.SequenceEqual(At(young)),"Mesh template sampling is independent of evaluation order");
        check(animated.Blocks.OfType<BSTriShape>().Single().Translation==Vector3.Zero,"Particle controller sampling preserves shared authored template state");
        var visibilityNif=new Nifcontent_Class_Manolo();visibilityNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateMeshVisibility(root)));
        var visibleSystem=new ParticleNif(visibilityNif,visibilityNif.Blocks.OfType<NiMeshParticleSystem>().Single());
        var visiblePart=visibleSystem.MeshArtwork!.Parts.Single();
        bool Draws(float age)=>ParticleMeshArtwork.Vertices(visiblePart,new(){Radius=1,Age=age},false,Matrix4x4.Identity).Any();
        check(!Draws(.249f)&&Draws(.25f)&&Draws(.749f)&&!Draws(.75f),"Serialized template root visibility honors exact step-key boundaries at particle age");
        check(Draws(.5f)&&!Draws(1)&&Draws(.5f),"Mesh visibility sampling supports backward seek and mixed particle ages");
        check(visibilityNif.Blocks.OfType<NiNode>().All(n=>(n.Flags_ui&1)==0),"Mesh visibility leaves shared template flags unchanged");
        var controller=visibilityNif.Blocks.OfType<NiVisController>().Single();controller.Flags=new NiflySharp.Bitfields.TimeControllerFlags{Value=68};
        check(Draws(.1f)&&Draws(1),"Inactive mesh visibility controller preserves authored visibility");
        var mesh=visibilityNif.Blocks.OfType<BSTriShape>().Single();mesh.Flags_ui|=1;
        var hiddenSystem=new ParticleNif(visibilityNif,visibilityNif.Blocks.OfType<NiMeshParticleSystem>().Single());
        check(!hiddenSystem.MeshArtwork!.Parts.Single().Visible(.5),"Authored hidden child remains hidden under a visible template root");

    }
}
