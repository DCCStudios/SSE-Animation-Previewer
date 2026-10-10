using System.Numerics;
using FO4_Base_Library;
using NiflySharp.Blocks;
namespace SSEPreview;

// Camera-facing transforms are render state; never write them back into authored or simulated node transforms.
internal sealed class NifBillboards
{
    readonly Dictionary<NiAVObject,NiNode> parents=[];
    readonly bool any;
    internal bool Any=>any;
    internal NifBillboards(Nifcontent_Class_Manolo nif,List<string> diagnostics)
    {
        foreach(var node in nif.Blocks.OfType<NiNode>())foreach(var reference in node.Children.References)
            if(nif.GetBlock(reference) is NiAVObject child)parents.TryAdd(child,node);
        var billboards=nif.Blocks.OfType<NiBillboardNode>().ToArray();any=billboards.Length>0;
        foreach(int mode in billboards.Select(b=>(int)b.BillboardMode&7).Distinct())
            if(mode is not 2 and not 4)diagnostics.Add($"NiBillboardNode mode {mode}: camera-facing transform is not yet implemented; authored transform is retained");
    }
    static Matrix4x4 Local(NiAVObject node)=>ParticleNif.Matrix(new Transform_Class{Rotation=node.Rotation,Translation=node.Translation,Scale=node.Scale}.ToMatrix4());
    internal Matrix4x4 World(NiAVObject node,Matrix4x4 placement,OrbitCamera camera)
    {
        var eye=camera.GetEyePosition();
        var view=new BillboardView(new(camera.right.X,camera.right.Y,camera.right.Z),new(camera.upPlane.X,camera.upPlane.Y,camera.upPlane.Z),new(eye.X,eye.Y,eye.Z));
        var seen=new HashSet<NiAVObject>();
        Matrix4x4 Visit(NiAVObject current){
            if(!seen.Add(current))return placement;
            var world=Local(current)*(parents.TryGetValue(current,out var parent)?Visit(parent):placement);
            return any&&current is NiBillboardNode billboard?Face(world,(int)billboard.BillboardMode,view):world;
        }
        return Visit(node);
    }
    internal readonly record struct BillboardView(Vector3 Right,Vector3 Up,Vector3 Eye);
    internal static Matrix4x4 Face(Matrix4x4 world,int mode,BillboardView view)
    {
        mode&=7;if(mode is not 2 and not 4)return world;
        var right=Vector3.Normalize(view.Right);var up=Vector3.Normalize(view.Up);var normal=Vector3.Normalize(Vector3.Cross(right,up));
        if(mode==4){
            var toward=view.Eye-world.Translation;
            // AE D3F292: no center-facing correction for an eye less than sqrt(.001) units away.
            if(toward.LengthSquared()<.001f)return world;
            toward=Vector3.Normalize(toward);
            float dot=Math.Clamp(Vector3.Dot(normal,toward),-1,1);
            if(dot<BitConverter.Int32BitsToSingle(0x3F7FFFEF)){
                var axis=Vector3.Cross(normal,toward);
                if(axis.LengthSquared()<1e-12f)axis=up;else axis=Vector3.Normalize(axis);
                var turn=Quaternion.CreateFromAxisAngle(axis,MathF.Acos(dot));
                right=Vector3.Transform(right,turn);up=Vector3.Transform(up,turn);normal=toward;
            }
        }
        float scale=new Vector3(world.M11,world.M12,world.M13).Length();
        if(world.GetDeterminant()<0)scale=-scale;
        var result=new Matrix4x4(right.X*scale,right.Y*scale,right.Z*scale,0,up.X*scale,up.Y*scale,up.Z*scale,0,normal.X*scale,normal.Y*scale,normal.Z*scale,0,world.M41,world.M42,world.M43,1);
        return float.IsFinite(result.GetDeterminant())?result:world;
    }
}