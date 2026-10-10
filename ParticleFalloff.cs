using System.Numerics;
namespace SSEPreview;
internal static class ParticleFalloff
{
    // Effect.hlsl: smoothstep between authored cosine-angle endpoints, using |N.V|.
    internal static float Evaluate(Vector3 normal,Vector3 towardEye,float start,float stop,float startOpacity,float stopOpacity)
    {
        float n=normal.LengthSquared(),v=towardEye.LengthSquared();
        if(!(n>0)||!(v>0)||!float.IsFinite(n)||!float.IsFinite(v))return 1;
        float cosine=Math.Abs(Vector3.Dot(normal,towardEye)/MathF.Sqrt(n*v));
        float delta=stop-start;
        // Equal endpoints are degenerate authoring; keep a finite step.
        float t=delta==0?(cosine>=stop?1:0):Math.Clamp((cosine-start)/delta,0,1);
        float opacity=startOpacity+(stopOpacity-startOpacity)*t*t*(3-2*t);
        return float.IsFinite(opacity)?opacity:1;
    }
    internal static void Test(Action<bool,string> check)
    {
        float At(float cosine)=>Evaluate(Vector3.UnitZ,new(MathF.Sqrt(1-cosine*cosine),0,cosine),.8f,.2f,1,0);
        check(Math.Abs(At(.8f)-1)<1e-6&&Math.Abs(At(.2f))<1e-6,"Particle falloff respects descending authored cosine endpoints");
        check(Math.Abs(At(.5f)-.5f)<1e-6,"Particle angular opacity uses smoothstep interpolation");
        check(Evaluate(Vector3.UnitZ,-Vector3.UnitZ,.8f,.2f,1,0)==1,"Particle falloff is symmetric on back-facing normals");
        check(Evaluate(Vector3.Zero,Vector3.UnitZ,1,0,1,0)==1,"Degenerate particle normal retains finite opacity");
    }
}
