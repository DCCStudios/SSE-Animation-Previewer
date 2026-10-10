using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Enums;
using NiflySharp.Bitfields;
namespace SSEPreview;
internal static class MultiEmitterTests
{
    internal static string Fixture(string directory)
    {
        var n=new NifFile();if(n.Load(ParticleFixture.Create(directory))!=0)throw new InvalidDataException("Cannot load original emitter fixture");
        n.Blocks.OfType<BSEffectShaderProperty>().Single().BaseColor=new(){R=1,G=1,B=1,A=1};
        var root=n.Blocks.OfType<NiNode>().First();var ps=n.Blocks.OfType<NiParticleSystem>().Single();int target=n.Blocks.IndexOf(ps);ps.Name=new("Three independent emitters");
        var first=n.Blocks.OfType<NiPSysBoxEmitter>().Single();first.InitialColor=new(){R=1,G=.15f,B=.1f,A=1};first.LifeSpan=3;first.LifeSpanVariation=0;first.Speed=35;first.SpeedVariation=0;first.Declination=0;first.DeclinationVariation=0;first.PlanarAngleVariation=0;
        var left=new NiNode{Name=new("Left source"),Scale=1,Translation=new(-45,0,0)};int leftId=n.AddBlock(left);root.Children.AddBlockRef(leftId);first.EmitterObject=new(leftId);
        var right=new NiNode{Name=new("Right source"),Scale=1,Translation=new(45,0,0)};int rightId=n.AddBlock(right);root.Children.AddBlockRef(rightId);
        var second=new NiPSysSphereEmitter{Name=new("Delayed blue sphere"),Target=new(target),Active=true,Order=(NiPSysModifierOrder)1000,EmitterObject=new(rightId),Radius=5,
            Speed=35,InitialColor=new(){R=.1f,G=.3f,B=1,A=1},InitialRadius=2.2f,LifeSpan=3};ps.Modifiers.AddBlockRef(n.AddBlock(second));
        void Controller(string name,float rate,int visibility){var c=new NiPSysEmitterCtlr{ModifierName=new(name),Target=new(target),Frequency=1,StartTime=0,StopTime=10,Flags=new TimeControllerFlags{Value=76},Interpolator=new(n.AddBlock(new NiFloatInterpolator{Value=rate})),VisibilityInterpolator=new(visibility),NextController=ps.Controller};ps.Controller=new(n.AddBlock(c));}
        int visible=n.AddBlock(new NiBoolInterpolator{Value=true});
        var timed=new NiBoolData{Data=new KeyGroup<byte>{NumKeys=3,Interpolation=(KeyType)5,Keys=[new(){Time=0,Value=0},new(){Time=.25f,Value=1},new(){Time=.75f,Value=0}]}};
        Controller(second.Name.ToString(),60,n.AddBlock(new NiBoolInterpolator{Data=new(n.AddBlock(timed))}));
        var mesh=new BSTriShape(NiVersion.GetSSE(),[new(-8,0,0),new(8,0,0),new(0,8,0)],[new Triangle(0,1,2)],[new(0,0),new(1,0),new(.5f,1)],[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ]){Name=new("Green mesh source"),Scale=1,IsSkinned=false};int meshId=n.AddBlock(mesh);root.Children.AddBlockRef(meshId);
        var third=new NiPSysMeshEmitter{Name=new("Green mesh"),Target=new(target),Active=true,Order=(NiPSysModifierOrder)1000,EmissionType=(EmitFrom)1,InitialVelocityType=(VelocityType)2,EmissionAxis=Vector3.UnitZ,
            Speed=35,InitialColor=new(){R=.1f,G=1,B=.1f,A=1},InitialRadius=2.2f,LifeSpan=3,EmitterMeshes=new()};third.EmitterMeshes.AddBlockRef(meshId);ps.Modifiers.AddBlockRef(n.AddBlock(third));Controller(third.Name.ToString(),30,visible);
        string file=Path.Combine(directory,@"meshes\previewer\multiple-emitters.nif");if(n.Save(file)!=0)throw new InvalidDataException("Cannot save mixed-emitter fixture");return file;
    }
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures");
        var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(Fixture(root)));var system=new ParticleNif(n,n.Blocks.OfType<NiParticleSystem>().Single(),83);
        check(system.Simulation.Emitters.Count()==3,"One authored particle system loads all box, sphere and mesh emission channels");
        var delayed=system.Simulation.AdditionalEmitters[0];check(!delayed.Active(double.BitDecrement(.25))&&delayed.Active(.25)&&!delayed.Active(.75),"Step visibility keys switch exactly at their authored activation and stop times");
        system.Seek(.2);check(system.Simulation.Particles.All(p=>p.BirthColor.Z<.9f),"Delayed emitter remains inactive while the other channels emit");
        system.Seek(.5);var particles=system.Simulation.Particles;
        check(particles.Count(p=>p.BirthColor.X>.9f)==45&&particles.Count(p=>p.BirthColor.Z>.9f)==15&&particles.Count(p=>p.BirthColor.Y>.9f)==15,$"Independent emitter counts: red {particles.Count(p=>p.BirthColor.X>.9f)}, blue {particles.Count(p=>p.BirthColor.Z>.9f)}, green {particles.Count(p=>p.BirthColor.Y>.9f)} (expected 45/15/15); born {system.Simulation.TotalBorn}");
        check(particles.Where(p=>p.BirthColor.X>.9f).All(p=>p.Position.X< -35)&&particles.Where(p=>p.BirthColor.Z>.9f).All(p=>p.Position.X>35),"Separate authored emitter objects place the two volumes independently");
        check(particles.Where(p=>p.BirthColor.Y>.9f).All(p=>Math.Abs(p.Position.X)<1e-5f&&Math.Abs(p.Position.Y-8f/3)<1e-5f),"Secondary mesh channel samples its own authored source geometry");
        string State()=>string.Join(";",system.Simulation.Particles.Select(p=>$"{p.Position}|{p.Velocity}|{p.Age}|{p.BirthColor}|{p.FrameSeed}"));var state=State();system.Seek(.1);system.Seek(.5);check(State()==state,"Backward seeking resets every emitter remainder and the shared random stream");
        var binding=system.MeshSourceBindings.Single();var saved=binding.Source.EmitterMeshes.Indices.ToArray();
        check(!system.IsMeshEmitter&&binding.Name.Contains("Green mesh")&&binding.BoundEmitterMeshes.Length==1,"Source inspector exposes the mesh channel even when the first emitter is a box");
        binding.BindEmitterMeshes([]);system.Seek(.5);
        check(binding.HasEmitterOverride&&system.Simulation.Particles.Count==60&&system.Simulation.Particles.All(p=>p.BirthColor.Y<.9f),"An empty preview binding disables only the selected secondary mesh emitter");
        check(binding.Source.EmitterMeshes.Indices.SequenceEqual(saved)&&binding.EmitterSourceDetails.Contains("Temporary preview override"),"Secondary emitter overrides retain authored links and clearly identify preview-only state");
        binding.BindEmitterMeshes(null);system.Seek(.5);check(!binding.HasEmitterOverride&&State()==state,"Restoring the secondary emitter recovers authored geometry and deterministic particle state");
        bool rejected=false;try{binding.BindEmitterMeshes([new NiNode()]);}catch(ArgumentException){rejected=true;}
        check(rejected&&!binding.HasEmitterOverride&&State()==state,"Foreign geometry is rejected without changing source selection or simulation state");
        system.Simulation.EmissionEnabled=t=>t<.5;system.Simulation.Reset();system.Seek(1);check(system.Simulation.TotalBorn==75,"Effect-wide emission gate stops all channels while existing particles continue");
        var limited=new ParticleSimulation(new(){Capacity=3,Life=3}){BirthRate=_=>120};limited.AdditionalEmitters.Add(new(){BirthRate=_=>120});limited.Seek(.1);
        check(limited.Particles.Count==3&&limited.TotalBorn==3&&limited.CapacityDrops==21,"All emitter channels share one capacity and report dropped births");
        var fractional=new ParticleSimulation(new(){Life=10}){BirthRate=_=>.75f};fractional.AdditionalEmitters.Add(new(){BirthRate=_=>.75f});fractional.Seek(1);check(fractional.TotalBorn==0,"Fractional birth counts remain independent rather than combining across emitters");fractional.Seek(2);check(fractional.TotalBorn==2,"Each fractional emitter carries its own remainder into later ticks");
    }
}
