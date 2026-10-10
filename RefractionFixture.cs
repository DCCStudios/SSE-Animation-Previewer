using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Enums;
namespace SSEPreview;
internal static class RefractionFixture
{
    internal static string Create(string directory){
        string textures=Path.Combine(directory,@"textures\previewer");Directory.CreateDirectory(textures);
        void Dds(string name,bool normal){using var w=new BinaryWriter(File.Create(Path.Combine(textures,name)));
            w.Write(0x20534444u);w.Write(124u);w.Write(0x100Fu);w.Write(64);w.Write(64);w.Write(256);w.Write(0);w.Write(0);for(int i=0;i<11;i++)w.Write(0);
            w.Write(32);w.Write(0x41);w.Write(0);w.Write(32);w.Write(0x000000FFu);w.Write(0x0000FF00u);w.Write(0x00FF0000u);w.Write(0xFF000000u);w.Write(0x1000);for(int i=0;i<4;i++)w.Write(0);
            for(int y=0;y<64;y++)for(int x=0;x<64;x++){byte v=(byte)(((x/8+y/8)&1)==0?35:220);w.Write(normal?(byte)255:v);w.Write(normal?(byte)128:v);w.Write(normal?(byte)255:v);w.Write((byte)255);}}
        Dds("refraction-checker.dds",false);Dds("refraction-vector.dds",true);
        var n=new NifFile(NiVersion.GetSSE(),true);var root=n.Blocks.OfType<NiNode>().First();root.Name=new("Original refraction verification");
        BSTriShape Quad(string name,float y,float size)=>new(NiVersion.GetSSE(),[new(-size,y,-size),new(size,y,-size),new(size,y,size),new(-size,y,size)],
            [new Triangle(0,1,2),new Triangle(0,2,3)],[new(0,0),new(1,0),new(1,1),new(0,1)],
            [Vector3.UnitY,Vector3.UnitY,Vector3.UnitY,Vector3.UnitY]){Name=new(name),Scale=1,IsSkinned=false};
        var back=Quad("Checker backdrop",0,100);
        back.ShaderPropertyRef=new(n.AddBlock(new BSEffectShaderProperty{SourceTexture=new(@"textures\previewer\refraction-checker.dds"),BaseColor=new(){R=1,G=1,B=1,A=1},BaseColorScale=1,UVScale=new(){U=1,V=1},TextureClampMode=0,
            ShaderFlags_SSPF1=(SkyrimShaderPropertyFlags1)0x80000000u,ShaderFlags_SSPF2=(SkyrimShaderPropertyFlags2)17u}));root.Children.AddBlockRef(n.AddBlock(back));
        var front=Quad("Distortion card",10,40);
        var set=new BSShaderTextureSet{Textures=new[]{@"textures\previewer\refraction-vector.dds",@"textures\previewer\refraction-checker.dds","","","","","","",""}.Select(s=>new NiString4(s)).ToList()};
        front.ShaderPropertyRef=new(n.AddBlock(new BSLightingShaderProperty{TextureSetRef=new(n.AddBlock(set)),RefractionStrength=1,Alpha=1,UVScale=new(){U=1,V=1},TextureClampMode=(TexClampMode)0,
            ShaderFlags_SSPF1=(SkyrimShaderPropertyFlags1)0x80008000u,ShaderFlags_SSPF2=(SkyrimShaderPropertyFlags2)24u}));root.Children.AddBlockRef(n.AddBlock(front));
        var occluder=Quad("Opaque foreground occluder",20,12);
        occluder.ShaderPropertyRef=new(n.AddBlock(new BSEffectShaderProperty{SourceTexture=new(""),BaseColor=new(){R=.8f,G=.15f,B=.1f,A=1},BaseColorScale=1,UVScale=new(){U=1,V=1},
            ShaderFlags_SSPF1=(SkyrimShaderPropertyFlags1)0x80000000u,ShaderFlags_SSPF2=(SkyrimShaderPropertyFlags2)17u}));root.Children.AddBlockRef(n.AddBlock(occluder));
        string file=Path.Combine(directory,@"meshes\previewer\refraction.nif");Directory.CreateDirectory(Path.GetDirectoryName(file)!);if(n.Save(file)!=0)throw new InvalidDataException("Refraction fixture save failed");return file;
    }
}
