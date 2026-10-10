using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Bitfields;
using NiflySharp.Enums;
namespace SSEPreview;
internal static class BillboardTests
{
    internal static string Fixture(string directory)
    {
        ParticleFixture.Create(directory); // original radial DDS texture
        var n=new NifFile(NiVersion.GetSSE(),true);var root=n.Blocks.OfType<NiNode>().First();root.Name=new("Billboard conformance fixture");
        foreach(int mode in new[]{2,4}){
            var billboard=new NiBillboardNode{Name=new("Facing mode "+mode),BillboardMode=(BillboardMode)mode,Scale=1,Translation=new(mode==2?-50:50,0,25)};
            int node=n.AddBlock(billboard);root.Children.AddBlockRef(node);
            var mesh=new BSTriShape(NiVersion.GetSSE(),[new(-32,-12,0),new(32,-12,0),new(32,12,0),new(-32,12,0)],
                [new Triangle(0,1,2),new Triangle(0,2,3)],[new(0,0),new(1,0),new(1,1),new(0,1)],
                [Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ]){Name=new("Facing card "+mode),Scale=1,IsSkinned=false};
            mesh.ShaderPropertyRef=new(n.AddBlock(new BSEffectShaderProperty{SourceTexture=new(@"textures\previewer\particle.dds"),BaseColor=new Color4{R=mode==2?1:.2f,G=.6f,B=mode==2?.15f:1,A=1},BaseColorScale=1,UVScale=new(){U=1,V=1},TextureClampMode=3,
                ShaderFlags_SSPF1=(SkyrimShaderPropertyFlags1)0x80000000u,ShaderFlags_SSPF2=(SkyrimShaderPropertyFlags2)16u}));
            mesh.AlphaPropertyRef=new(n.AddBlock(new NiAlphaProperty{Flags=new AlphaFlags{Value=237}}));
            billboard.Children.AddBlockRef(n.AddBlock(mesh));
        }
        string file=Path.Combine(directory,@"meshes\previewer\billboards.nif");
        if(n.Save(file)!=0)throw new InvalidDataException("Billboard fixture save failed");return file;
    }
    internal static void Run()
    {
        List<string> log=[];
        void Check(bool ok,string label){log.Add((ok?"PASS ":"FAIL ")+label);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"billboard-tests.txt"),log);if(!ok)throw new InvalidDataException(label);}
        var view=new NifBillboards.BillboardView(Vector3.UnitX,Vector3.UnitZ,new(0,-100,0));
        var original=Matrix4x4.CreateScale(2)*Matrix4x4.CreateRotationZ(.7f)*Matrix4x4.CreateTranslation(30,20,10);
        var facing=NifBillboards.Face(original,2,view);
        Check(facing.Translation==original.Translation,"Rigid camera-facing preserves the billboard pivot");
        Check(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX,facing),new(2,0,0))<1e-5f&&Vector3.Distance(Vector3.TransformNormal(Vector3.UnitY,facing),new(0,0,2))<1e-5f,"Rigid mode follows camera right/up while preserving scale");
        Check(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ,facing),new(0,-2,0))<1e-5f,"Billboard local Z faces out toward the camera");
        var centered=NifBillboards.Face(original,4,view);var toward=Vector3.Normalize(view.Eye-original.Translation);
        Check(Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ,centered)),toward)>.99999f,"Face-center points to the eye rather than sharing the parallel camera direction");
        Check(Math.Abs(Vector3.Dot(Vector3.TransformNormal(Vector3.UnitX,centered),Vector3.TransformNormal(Vector3.UnitY,centered)))<1e-5f&&Math.Abs(centered.GetDeterminant()-8)<1e-4f,"Center-facing preserves an orthogonal, uniformly scaled basis");
        Check(NifBillboards.Face(original,4,view with{Eye=original.Translation+new Vector3(.001f,0,0)})==original,"Near-coincident eye uses the native center-facing distance guard");
        Check(NifBillboards.Face(original,6,view)==original,"Unsupported facing mode retains authored geometry");
        Check(NifBillboards.Face(original,10,view)==facing,"Billboard mode uses the native low three flag bits");
        var mirror=NifBillboards.Face(Matrix4x4.CreateScale(-2),2,view);
        Check(Math.Abs(mirror.GetDeterminant()+8)<1e-4f,"Negative authored uniform scale keeps its handedness");
        string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures"));
        var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(Fixture(root)));
        var animation=new NifAnimation(nif);var camera=new OrbitCamera();camera.Rotate(35,25);
        var mesh=nif.Blocks.OfType<BSTriShape>().First();var node=nif.Blocks.OfType<NiBillboardNode>().First();
        var saved=node.Rotation;var unturned=animation.World(mesh);
        var rendered=animation.RenderWorld(mesh,Matrix4x4.Identity,camera);
        var right=new Vector3(camera.right.X,camera.right.Y,camera.right.Z);
        Check(Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX,rendered)),Vector3.Normalize(right))>.99999f,"Loaded NIF mesh inherits its billboard parent camera-facing orientation");
        Check(node.Rotation.Equals(saved)&&animation.World(mesh)==unturned,"Camera-facing rendering does not mutate authored or simulation transforms");
        camera.Rotate(65,-15);var moved=animation.RenderWorld(mesh,Matrix4x4.Identity,camera);
        Check(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitX,rendered),Vector3.TransformNormal(Vector3.UnitX,moved))>.1f,"Orbiting camera updates billboard geometry without seeking animation");
    }
}