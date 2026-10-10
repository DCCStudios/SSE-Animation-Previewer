using System.Numerics;
using FO4_Base_Library;
namespace SSEPreview;
internal static class ParticleLighting
{
    // The standalone studio provides a constant environment to Effect.hlsl's
    // lighting blend. It does not synthesize Skyrim cell/weather or point lights.
    internal static Vector3 Studio(PreviewLightRig rig)
    {
        Vector3 Linear(OpenTK.Mathematics.Vector3 c)=>new(Channel(c.X),Channel(c.Y),Channel(c.Z));
        return (Linear(rig.AmbientSkyDiffuse())+Linear(rig.AmbientGroundDiffuse())*Math.Clamp(rig.AmbientGroundLevel,0,1))*.5f
            +Linear(rig.KeyLight.Diffuse())+Linear(rig.FillLeft.Diffuse())+Linear(rig.FillRight.Diffuse())+Linear(rig.BackLight.Diffuse());
    }
    static float Channel(float v)=>float.IsFinite(v)?MathF.Pow(Math.Max(0,v),2.2f):0;
    internal static float Influence(uint flags,float authored)=>(flags&0x40000000u)!=0&&float.IsFinite(authored)?Math.Clamp(authored/255f,0,1):0;
    internal static void Test(Action<bool,string> check)
    {
        check(Studio(new PreviewLightRig())==Vector3.Zero,"Disabled studio lights supply zero particle radiance");
        var rig=new PreviewLightRig{AmbientIntensity=1,AmbientSkyColor=RigColor.White,AmbientGroundColor=RigColor.White,AmbientGroundLevel=1};
        check(Vector3.Distance(Studio(rig),Vector3.One)<1e-6,"Unit neutral studio ambient supplies unit particle radiance");
        check(Influence(0,255)==0&&Influence(0x40000000,255)==1&&Math.Abs(Influence(0x40000000,128)-128f/255)<1e-6,"Particle effect-lighting flag and byte influence control the lighting blend");
    }
}
