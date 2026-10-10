using System.Numerics;
namespace SSEPreview;
internal static class ParticleEmitterSampling
{
    static readonly float Unit15=BitConverter.Int32BitsToSingle(0x38000100);
    static readonly float Angle15=BitConverter.Int32BitsToSingle(0x3C800100);
    // Native uses 512-entry trigonometric tables. Runtime table bit patterns are
    // not copied here; .NET-generated values preserve the observed index rule.
    static readonly Vector2[] circle=Enumerable.Range(0,512).Select(i=>new Vector2(MathF.Cos(i*(MathF.Tau/512)),MathF.Sin(i*(MathF.Tau/512)))).ToArray();
    static Vector2 Direction(int random)=>circle[((int)(random*Angle15))&511];
    internal static float SignedVariation(float value,float variation,Func<float> random)=>value+(ParticleMesh.Random15(random)*BitConverter.Int32BitsToSingle(0x38800100)-1)*variation;
    internal static Vector3 LaunchVelocity(float speed,float declination,float planarAngle)
    {
        if(!float.IsFinite(speed)||!float.IsFinite(declination)||!float.IsFinite(planarAngle))return Vector3.Zero;
        float factor=BitConverter.Int32BitsToSingle(0x42A2F983);
        var polar=circle[((int)(declination*factor))&511];var planar=circle[((int)(planarAngle*factor))&511];
        return new(polar.Y*planar.X*speed,polar.Y*planar.Y*speed,polar.X*speed);
    }
    internal static Vector3 RotationAxis(Func<float> random)
    {
        // NiPSysRotationModifier::Initialize: polar covers half the table,
        // azimuth covers the full table, each from one 15-bit random draw.
        int polar=((int)(ParticleMesh.Random15(random)*BitConverter.Int32BitsToSingle(0x3C000100)))&511;
        float z=circle[polar].X,radial=MathF.Sqrt(Math.Max(0,1-z*z));
        var azimuth=Direction(ParticleMesh.Random15(random));
        return new(radial*azimuth.X,radial*azimuth.Y,z);
    }
    internal static Vector3 Sphere(float radius,Func<float> random)
    {
        float r=ParticleMesh.Random15(random)*radius*Unit15;
        var azimuth=Direction(ParticleMesh.Random15(random));var polar=Direction(ParticleMesh.Random15(random));
        float planar=r*polar.Y;return new(planar*azimuth.X,planar*azimuth.Y,r*polar.X);
    }
    internal static Vector3 Cylinder(float radius,float height,Func<float> random)
    {
        float r=ParticleMesh.Random15(random)*radius*Unit15;var direction=Direction(ParticleMesh.Random15(random));
        float z=(ParticleMesh.Random15(random)*Unit15-.5f)*height;return new(r*direction.X,r*direction.Y,z);
    }
    internal static Vector3 Box(Vector3 dimensions,Func<float> random)
    {
        float z=ParticleMesh.Random15(random)*Unit15-.5f,y=ParticleMesh.Random15(random)*Unit15-.5f,x=ParticleMesh.Random15(random)*Unit15-.5f;
        return new(x*dimensions.X,y*dimensions.Y,z*dimensions.Z);
    }
}
