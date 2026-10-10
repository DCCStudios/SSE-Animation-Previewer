using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Enums;
using NiflySharp.Bitfields;
namespace SSEPreview;
internal static class MultiTargetEmitterTests
{
    internal static string Fixture(string directory)
    {
        var n=new NifFile();if(n.Load(ParticleFixture.Create(directory))!=0)throw new InvalidDataException("Cannot load multi-target fixture source");
        var old=n.Blocks.OfType<NiPSysEmitterCtlr>().Single();
        var keys=new NiFloatData{Data=new KeyGroup<float>{NumKeys=2,Interpolation=(KeyType)1,Keys=[new(){Time=0,Value=24},new(){Time=1,Value=240}]}};
        var controller=new BSPSysMultiTargetEmitterCtlr{ModifierName=old.ModifierName,Target=old.Target,NextController=old.NextController,Frequency=1,StartTime=0,StopTime=1,Flags=new TimeControllerFlags{Value=76},MaxEmitters=1,
            Interpolator=new(n.AddBlock(new NiFloatInterpolator{Data=new(n.AddBlock(keys))})),VisibilityInterpolator=new(n.AddBlock(new NiBoolInterpolator{Value=false}))};
        if(!n.ReplaceBlock(n.Blocks.IndexOf(old),controller))throw new InvalidDataException("Cannot replace multi-target fixture controller");
        string file=Path.Combine(directory,@"meshes\previewer\multi-target-emitter.nif");if(n.Save(file)!=0)throw new InvalidDataException("Cannot save multi-target fixture");return file;
    }
    internal static void Run(Action<bool,string> check)
    {
        var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(Fixture(Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures"))));
        var node=n.Blocks.OfType<NiParticleSystem>().Single();var controller=n.Blocks.OfType<BSPSysMultiTargetEmitterCtlr>().Single();
        var system=new ParticleNif(n,node,61);var target=Matrix4x4.CreateRotationZ(.5f)*Matrix4x4.CreateTranslation(40,50,60);system.AddonEmitterTarget=_=>target;
        var channel=system.Simulation.PrimaryEmitter;
        check(channel.RotateVelocityOnly,"NIF geometric emitters preserve speed when their source transform is scaled");
        var rotation=Matrix4x4.CreateRotationY(.7f)*Matrix4x4.CreateRotationZ(.3f);var velocity=new Vector3(3,4,5);
        var expected=Vector3.TransformNormal(velocity,rotation);
        foreach(float scale in new[]{.25f,4f,-2f})check(Vector3.Distance(ParticleEmitterTransform.RotateVelocity(velocity,Matrix4x4.CreateScale(scale)*rotation*Matrix4x4.CreateTranslation(100,200,300)),expected)<1e-5,"Geometric emission velocity excludes signed uniform scale "+scale);
        check(ParticleEmitterTransform.RotateVelocity(velocity,Matrix4x4.CreateScale(0))==Vector3.Zero,"Singular emission transforms cannot create nonfinite velocity");
        ParticleSimulation Emit(float scale){
            var sim=new ParticleSimulation(new(){Speed=10,Dimensions=new(6,8,10),Life=1},94){BirthRate=_=>120};
            sim.PrimaryEmitter.RotateVelocityOnly=true;sim.EmitterTransform=_=>Matrix4x4.CreateScale(scale)*rotation;sim.Seek(ParticleSimulation.Step);return sim;
        }
        var unit=Emit(1).Particles.Single();var enlarged=Emit(3).Particles.Single();
        check(Vector3.Distance(unit.Velocity,enlarged.Velocity)<1e-5&&Vector3.Distance(enlarged.Position,unit.Position*3)<1e-5,"Birth positions expand with emitter scale while birth speed stays unchanged");

        check(channel.BirthRate(.2)==24&&channel.BirthRate(.8)==24,"Bound multi-target controller samples birth-rate keys at time zero");
        check(channel.Active(.2),"Bound multi-target dispatch does not use the ordinary visibility interpolator");
        check(channel.Transform(.2)==target,"Bound multi-target emission uses its registered target transform");
        float radius=channel.SettingsAt!(.5).Radius,variation=channel.SettingsAt(.5).RadiusVariation;
        system.AddonEmitterScale=t=>(float)(1+2*t);
        check(channel.SettingsAt(.5).Radius==radius*2&&channel.SettingsAt(.5).RadiusVariation==variation*2,"Bound emitter scales both birth radius and variation by the target local scale");
        check(channel.SettingsAt(.25).Radius==radius*1.5f,"Bound emitter samples animated local scale at birth time");
        system.AddonEmitterScale=_=>float.NaN;check(channel.SettingsAt(.5).Radius==0,"Nonfinite bound scale cannot contaminate particle radii");
        system.AddonEmitterScale=_=>1;
        system.Seek(.5);check(system.Simulation.TotalBorn==12,"Single bound target emits from its constant-rate schedule");
        string first=string.Join(";",system.Simulation.Particles.Select(p=>$"{p.Position}|{p.Age}"));system.Seek(.1);system.Seek(.5);
        check(first==string.Join(";",system.Simulation.Particles.Select(p=>$"{p.Position}|{p.Age}")),"Bound multi-target particle history rewinds deterministically");
        controller.MaxEmitters=0;check(!channel.Active(.5),"Zero maximum emitters prevents bound-target dispatch");controller.MaxEmitters=1;
        controller.Flags=new TimeControllerFlags{Value=68};check(!channel.Active(.5),"Inactive multi-target controller prevents bound-target dispatch");controller.Flags=new TimeControllerFlags{Value=76};
        system.AddonEmitterTarget=null;system.AddonEmitterScale=_=>7;check(channel.SettingsAt(.5).Radius==radius,"Unbound ordinary fallback does not apply an ADDN radius multiplier");check(!channel.Active(.5)&&channel.BirthRate(.5)>24,"Unbound multi-target preview retains explicitly diagnosed ordinary-emitter fallback");
    }
}

