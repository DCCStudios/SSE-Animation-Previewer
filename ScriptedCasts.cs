using System.Numerics;
using System.Security.Cryptography;
namespace SSEPreview;
internal readonly record struct RemoteCastFrame(Vector3 Start,Vector3 Target)
{
    public float Heading=>-MathF.Atan2(Target.X-Start.X,Target.Y-Start.Y);
    public float Elevation=>MathF.Atan2(Target.Z-Start.Z,new Vector2(Target.X-Start.X,Target.Y-Start.Y).Length());
}
internal static class ScriptedCasts
{
    internal const string FenrirHash="EE2FE182E237F49521C0E6F7A4939A60BAD5134E629F23011C2957264A8AE6FE";
    // Only this inspected PEX is recognized. A matching filename alone is insufficient.
    internal static bool VerifiedFenrir(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes))==FenrirHash;
    internal static RemoteCastFrame[] FenrirFrames()=>new[]{0f,12f,-12f}.Select(degrees=>{
        float angle=degrees*MathF.PI/180;
        return new RemoteCastFrame(new(0,80,20),new(180*MathF.Sin(angle),180*MathF.Cos(angle),50));
    }).ToArray();
}
