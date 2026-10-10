using System.Numerics;
namespace SSEPreview;
// AE NiPSysMeshUpdateModifier: root translation/scale are replaced, while
// root rotation is replaced only when both angle and axis arrays exist.
internal static class ParticleMeshTransform
{
    internal static int TemplateIndex(int generation,int count)=>count<=0?-1:Math.Min(Math.Max(0,generation),count-1);
    internal static Matrix4x4 At(ParticleSimulation.Particle particle,bool hasRotation,Matrix4x4 templateRotation)
    {
        var rotation=hasRotation?Matrix4x4.CreateFromAxisAngle(particle.RotationAxis,particle.Angle):templateRotation;
        rotation.M41=rotation.M42=rotation.M43=0;rotation.M44=1;
        return Matrix4x4.CreateScale(Math.Abs(particle.Radius))*rotation*Matrix4x4.CreateTranslation(particle.Position);
    }
}
