using System.Numerics;
namespace SSEPreview;
internal static class ParticleEmitterTransform
{
    // NiTransform stores a signed uniform scale separately from its rotation.
    // Recover that scale from the composed matrix before rotating velocity.
    internal static Vector3 RotateVelocity(Vector3 velocity,Matrix4x4 transform)
    {
        double scale=Math.Sqrt((double)transform.M11*transform.M11+(double)transform.M12*transform.M12+(double)transform.M13*transform.M13);
        if(!(scale>0)||!double.IsFinite(scale))return Vector3.Zero;
        double determinant=(double)transform.M11*(transform.M22*(double)transform.M33-transform.M23*(double)transform.M32)
            -(double)transform.M12*(transform.M21*(double)transform.M33-transform.M23*(double)transform.M31)
            +(double)transform.M13*(transform.M21*(double)transform.M32-transform.M22*(double)transform.M31);
        if(determinant<0)scale=-scale;
        var result=new Vector3(
            (float)((velocity.X*(double)transform.M11+velocity.Y*(double)transform.M21+velocity.Z*(double)transform.M31)/scale),
            (float)((velocity.X*(double)transform.M12+velocity.Y*(double)transform.M22+velocity.Z*(double)transform.M32)/scale),
            (float)((velocity.X*(double)transform.M13+velocity.Y*(double)transform.M23+velocity.Z*(double)transform.M33)/scale));
        return float.IsFinite(result.X)&&float.IsFinite(result.Y)&&float.IsFinite(result.Z)?result:Vector3.Zero;
    }
}
