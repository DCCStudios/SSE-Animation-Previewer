using System.Numerics;
namespace SSEPreview;
internal static class ParticleVertexColor
{
    // AE E51774..E517C4 / E51AC5..E51B16: multiply by 255, signed
    // truncation to 64 bits, then keep each low byte. This is not rounding/clamping.
    internal static byte Channel(float value)
    {
        float scaled=value*255f;
        if(!float.IsFinite(scaled)||scaled>=9223372036854775808d||scaled< -9223372036854775808d)return 0;
        return unchecked((byte)(long)scaled);
    }
    internal static Vector4 Normalize(Vector4 value)=>new(Channel(value.X)/255f,Channel(value.Y)/255f,Channel(value.Z)/255f,Channel(value.W)/255f);
}
