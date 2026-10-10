using System.Numerics;
namespace SSEPreview;
internal static class ParticleEmitterSamplingTests
{
    internal static void Run(Action<bool,string> check)
    {
        Func<float> Values(params int[] values){int i=0;return ()=>values[i++]/32768f;}
        check(ParticleEmitterSampling.SignedVariation(10,2,Values(0))==8&&ParticleEmitterSampling.SignedVariation(10,2,Values(32767))==12,"Angular/radius variation uses native full signed 15-bit endpoints");
        float midpoint=ParticleEmitterSampling.SignedVariation(0,1,Values(16384));check(midpoint>0&&midpoint<.00004f,"Signed 15-bit variation preserves its nonzero midpoint step");
        check(ParticleEmitterSampling.LaunchVelocity(10,.001f,.3f)==new Vector3(0,0,10)&&ParticleEmitterSampling.LaunchVelocity(10,-.001f,.3f)==new Vector3(0,0,10),"Native launch-angle conversion truncates small positive and negative declinations to the same bin");
        var launched=ParticleEmitterSampling.LaunchVelocity(10,.3f,.4f);
        check(launched==ParticleEmitterSampling.LaunchVelocity(10,.301f,.401f)&&Math.Abs(launched.Length()-10)<1e-5,"Launch directions share quantized angular bins without changing speed");
        check(ParticleEmitterSampling.LaunchVelocity(10,float.NaN,0)==Vector3.Zero,"Invalid authored launch angles cannot create nonfinite velocity");
        check(ParticleEmitterSampling.Sphere(10,Values(0,100,200))==Vector3.Zero,"Sphere emitter permits a zero-radius birth at the origin");
        var pole=ParticleEmitterSampling.Sphere(10,Values(32767,0,0));check(Vector3.Distance(pole,new(0,0,10))<1e-5,"Sphere radius reaches the authored boundary with a polar-axis sample");
        var cylinder=ParticleEmitterSampling.Cylinder(10,20,Values(16384,0,32767));check(Math.Abs(cylinder.X-5)<.001&&cylinder.Y==0&&Math.Abs(cylinder.Z-10)<1e-5,"Cylinder uses linear radius and centred height sampling");
        var box=ParticleEmitterSampling.Box(new(10,20,30),Values(0,32767,16384));check(Math.Abs(box.X)<.001&&Math.Abs(box.Y-10)<1e-5&&Math.Abs(box.Z+15)<1e-5,"Box assigns native random samples in Z, Y, X order");
        double radiusSum=0,polarSum=0,cylinderSum=0;
        const int count=4096;
        for(int i=0;i<count;i++){
            int sample=i*8;
            var sphere=ParticleEmitterSampling.Sphere(1,Values(sample,8192,16384));radiusSum+=sphere.Length();
            var direction=ParticleEmitterSampling.Sphere(1,Values(32767,0,sample));polarSum+=direction.Z*direction.Z;
            var disk=ParticleEmitterSampling.Cylinder(1,0,Values(sample,8192,0));cylinderSum+=disk.Length();
        }
        check(Math.Abs(radiusSum/count-.5)<.001&&Math.Abs(cylinderSum/count-.5)<.001,"Native sphere/cylinder radial means are one-half, not uniform-volume means");
        check(Math.Abs(polarSum/count-.5)<.002,"Native full-circle polar sampling retains its pole-weighted distribution");
        var nif=new FO4_Base_Library.Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(ParticleFixture.Create(Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures"))));
        var emitter=nif.Blocks.OfType<NiflySharp.Blocks.NiPSysBoxEmitter>().Single();emitter.Width=10;emitter.Height=20;emitter.Depth=30;
        var system=new ParticleNif(nif,nif.Blocks.OfType<NiflySharp.Blocks.NiParticleSystem>().Single());
        check(system.Simulation.PrimaryEmitter.SettingsAt!(0).Dimensions==new Vector3(10,20,30),"NIF box Width/Height/Depth map to native local X/Y/Z respectively");
        var controller=nif.Blocks.OfType<NiflySharp.Blocks.NiPSysEmitterCtlr>().Single();
        controller.Flags=new NiflySharp.Bitfields.TimeControllerFlags{Value=68};system.Seek(.5);
        check(!system.Simulation.PrimaryEmitter.Active(.5)&&system.Simulation.TotalBorn==0,"Disabled ordinary emitter controller blocks births despite a positive rate curve");
        controller.Flags=new NiflySharp.Bitfields.TimeControllerFlags{Value=76};system.Simulation.Reset();system.Seek(.5);
        check(system.Simulation.TotalBorn>0,"Re-enabled ordinary emitter controller permits authored births");
        long births=system.Simulation.TotalBorn;float ageBefore=system.Simulation.Particles[0].Age;
        controller.Flags=new NiflySharp.Bitfields.TimeControllerFlags{Value=68};system.Seek(.6);
        check(system.Simulation.TotalBorn==births&&system.Simulation.Particles[0].Age>ageBefore,"Disabling emission preserves and ages already-live particles");
        controller.Flags=new NiflySharp.Bitfields.TimeControllerFlags{Value=69};system.Simulation.Reset();system.Seek(.5);
        check(system.Simulation.PrimaryEmitter.Active(.25)&&system.Simulation.TotalBorn>0,"One-shot emitter controller (flags 69: APP_INIT, saved inactive) emits from effect time zero as spawned game effects do");
        check(NifAnimation.Started(69)&&NifAnimation.Started(72)&&NifAnimation.Started(76)&&NifAnimation.Started(104)&&!NifAnimation.Started(68)&&!NifAnimation.Started(64),"Controller start rule: active bit or APP_INIT; inactive APP_TIME stays off");
        var wrap=ParticleEmitterSampling.Cylinder(1,0,Values(32767,32767,0));check(Vector3.Distance(wrap,Vector3.UnitX)<1e-5,"Quantized angle endpoint wraps index 512 back to zero");
    }
}
