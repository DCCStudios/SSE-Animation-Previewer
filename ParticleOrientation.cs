using System.Numerics;
namespace SSEPreview;
internal static class ParticleOrientation
{
    // AE NiParticlesData::LoadBinary D4F35B/D4F38B reads two individual bytes.
    // E51B19/E51BE4 tests each whole byte, not nifxml's named bit flags.
    internal static bool AlongVelocity(int flags)=>(flags&255)!=0;
    internal static bool SpeedAspect(int flags)=>(flags&65280)!=0;
    internal static (Vector3 Right,Vector3 Up) Axes(Vector3 right,Vector3 up,float vx,float vy,float angle,float radius,float aspect,int flags)
    {
        if(!AlongVelocity(flags))return((right*MathF.Cos(angle)+up*MathF.Sin(angle))*radius,(-right*MathF.Sin(angle)+up*MathF.Cos(angle))*(radius/aspect));
        float length=MathF.Sqrt(vx*vx+vy*vy);
        // Native normalization masks a zero projection to zero: the card collapses.
        // Nonfinite inputs are also suppressed to protect the preview GL buffer.
        if(!(length>0)||!float.IsFinite(length))return(Vector3.Zero,Vector3.Zero);
        return((right*(vy/length)-up*(vx/length))*radius,(right*(vx/length)+up*(vy/length))*(radius/aspect));
    }
}
