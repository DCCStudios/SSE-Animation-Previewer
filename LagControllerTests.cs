using System.Numerics;
using FO4_Base_Library;
using NiflySharp.Blocks;
namespace SSEPreview;
internal static class LagControllerTests
{
    internal static void Run(Action<bool,string> check,string root)
    {
        check(Vector3.Distance(NifLagControllers.AdvancePosition(Vector3.Zero,new(10,0,0),3,40,.1f),new(3,0,0))<1e-5f,"Lag translation uses native proportional return");
        check(Vector3.Distance(NifLagControllers.AdvancePosition(Vector3.Zero,new(100,0,0),3,20,.1f),new(86,0,0))<1e-5f,"Lag translation limits the remaining trailing distance before blending");
        check(NifLagControllers.AdvancePosition(Vector3.Zero,new(.1f,0,0),3,40,.1f)==new Vector3(.1f,0,0),"Lag native short-distance branch snaps to its rest target");
        check(NifLagControllers.AdvancePosition(Vector3.Zero,new(10,0,0),0,40,.1f)==Vector3.Zero,"Zero lag velocity does not move the saved position");
        // Independent float32 transcription of AE RVA D340E0; expected values
        // are preserved in verification/lag-rotation/golden.json (native WXYZ order).
        void CheckRotation(float endpointW,float time,float expectedZ,float expectedW,int angle){
            var result=NifLagControllers.BlendRotation(Quaternion.Identity,new(0,0,.8660253882f,endpointW),time);
            check(Vector4.Distance(new(result.X,result.Y,result.Z,result.W),new(0,0,expectedZ,expectedW))<2e-6f,
                $"Native lag quaternion reference: {angle} degree endpoint at {time}");
        }
        CheckRotation(0.5f,0.1f,0.1047049984f,0.9945033193f,120);
        CheckRotation(-0.5f,0.1f,0.2149125189f,0.9766333103f,240);
        CheckRotation(0.5f,0.25f,0.259930104f,0.9656273723f,120);
        CheckRotation(-0.5f,0.25f,0.5595164299f,0.8288192749f,240);
        CheckRotation(0.5f,0.5f,0.4999999702f,0.8660253882f,120);
        CheckRotation(-0.5f,0.5f,0.8660253882f,0.5f,240);
        CheckRotation(0.5f,0.75f,0.7062927485f,0.7079197764f,120);
        CheckRotation(-0.5f,0.75f,0.9975367188f,0.07014580816f,240);
        CheckRotation(0.5f,0.9f,0.8089126348f,0.5879288316f,120);
        CheckRotation(-0.5f,0.9f,0.9532455206f,-0.3021970093f,240);
        check(NifLagControllers.BlendRotation(Quaternion.Identity,-Quaternion.Identity,.5f)==Quaternion.Identity,"Singular lag quaternion midpoint keeps a finite orientation");
        foreach(var axis in new[]{Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ,Vector3.Normalize(new Vector3(1,1,1))}){
            var expectedRotation=Quaternion.CreateFromAxisAngle(axis,2.4f);
            foreach(float scale in new[]{.25f,2f,-.25f,-2f}){
                var transform=Matrix4x4.CreateScale(scale)*Matrix4x4.CreateFromQuaternion(expectedRotation)*Matrix4x4.CreateTranslation(12,34,56);
                bool valid=NifLagControllers.RotationFromUniformTransform(transform,out var actual);
                check(valid&&Math.Abs(Quaternion.Dot(actual,expectedRotation))>1-1e-6f,$"Lag rotation excludes signed uniform scale {scale} around {axis}");
            }
        }
        check(!NifLagControllers.RotationFromUniformTransform(Matrix4x4.CreateScale(0),out _),"Singular lag transforms cannot inject invalid rotations");
        (NifAnimation Animation,NiNode Node) Load(bool enabled){
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateLag(root,enabled)));
            return(new(n),n.Blocks.OfType<NiNode>().Single(n=>n.Name.ToString()=="Lag bone"));
        }
        var direct=Load(true);direct.Animation.Evaluate(1);var expected=direct.Node.Translation;
        check(expected.X<0&&expected.X>=-40,$"Loaded lag controller trails the moving parent within authored maximum distance ({expected})");
        foreach(int fps in new[]{30,60}){
            var stepped=Load(true);for(int i=0;i<=fps;i++)stepped.Animation.Evaluate(i/(double)fps);
            check(Vector3.Distance(expected,stepped.Node.Translation)<1e-5f,$"Lag motion matches direct seeking at {fps} FPS");
        }
        long steps=direct.Animation.ReplayedMotionSteps;
        direct.Animation.Evaluate(.37);direct.Animation.Evaluate(1);
        check(direct.Animation.ReplayedMotionSteps==steps,"Recent lag seeks reuse fixed-step history instead of replaying from zero");
        check(Vector3.Distance(expected,direct.Node.Translation)<1e-5f,"Backward and fractional lag scrubbing replay deterministically");
        direct.Animation.ResetMotion();direct.Animation.Evaluate(1);
        check(Vector3.Distance(expected,direct.Node.Translation)<1e-5f,"Lag reset restores authored rest transforms before replay");
        var disabled=Load(false);disabled.Animation.Evaluate(1);
        check(Math.Abs(disabled.Node.Translation.X-20)<1e-5f,"Inactive lag controller preserves the local rest transform");
        var rotated=Load(true);rotated.Animation.Placement=t=>Matrix4x4.CreateRotationZ((float)t)*Matrix4x4.CreateTranslation(0,(float)t*100,0);rotated.Animation.Evaluate(1);
        var rotation=rotated.Node.Rotation;
        check(Math.Abs(rotation.M12)>.01&&float.IsFinite(rotation.M12),"Moving rotated placement produces a finite lag rotation");
        rotated.Animation.Evaluate(.2);rotated.Animation.Evaluate(1);
        check(Math.Abs(rotated.Node.Rotation.M12-rotation.M12)<1e-5,"Placement-dependent lag rotation replays after a backward seek");
    }
}