using System.Numerics;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Enums;
using NiflySharp.Bitfields;
namespace SSEPreview;
internal static class AddonArtworkTests
{
    internal static string Fixture(string directory)
    {
        ParticleFixture.Create(directory);
        var nif=new NifFile(NiVersion.GetSSE(),true);var root=nif.Blocks.OfType<NiNode>().First();
        root.Translation=new(10,20,30);root.Scale=2;
        var node=new BSValueNode{Name=new("Original animated add-on"),Scale=1,Value=987654,Children=new()};
        int index=nif.AddBlock(node);root.Children.AddBlockRef(index);
        var data=new NiTransformData{Translations=new KeyGroup<Vector3>{NumKeys=2,Interpolation=(KeyType)1,Keys=[new(){Time=0,Value=Vector3.Zero},new(){Time=2,Value=new(120,0,0)}]}};
        data.Scales=new KeyGroup<float>{NumKeys=2,Interpolation=(KeyType)1,Keys=[new(){Time=0,Value=1},new(){Time=2,Value=3}]};
        var interpolator=new NiTransformInterpolator{Transform=new NiQuatTransform{Scale=1,Translation=Vector3.Zero,Rotation=Quaternion.Identity},Data=new(nif.AddBlock(data))};
        node.Controller=new(nif.AddBlock(new NiTransformController{Target=new(index),Flags=new TimeControllerFlags{Value=76},Frequency=1,StartTime=0,StopTime=2,Interpolator=new(nif.AddBlock(interpolator))}));
        string file=Path.Combine(directory,@"meshes\previewer\addon-parent.nif");if(nif.Save(file)!=0)throw new InvalidDataException("Cannot save original add-on fixture");return file;
    }
    internal static GameRecord Record()=>new(){Kind="ADDN",Plugin="preview-fixture.esp",Key="preview-fixture.esp|000001",Fields=[new("DATA",BitConverter.GetBytes(987654u)),new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\fountain.nif"))]};
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var record=Record();records.All[record.Key]=record;
        check(AddonArtwork.Resolve(records,987654,out var issue)==record&&issue==null,"ADDN resolves by authored node index instead of form ID");
        check(AddonArtwork.Model(record)==@"meshes\previewer\fountain.nif","ADDN mesh resolves through a normalized virtual path");
        check(AddonArtwork.Resolve(records,9,out issue)==null&&issue!.Contains("No loaded"),"Missing ADDN index has an explicit diagnostic");
        var other=Record();other.Key="other.esp|000001";records.All[other.Key]=other;
        check(AddonArtwork.Resolve(records,987654,out issue)==null&&issue!.Contains("Ambiguous"),"Conflicting ADDN indices do not silently select an arbitrary record");
        using var memory=new MemoryStream();using var writer=new BinaryWriter(memory);writer.Write(System.Text.Encoding.ASCII.GetBytes("ADDN"));writer.Write(0u);writer.Write(0u);writer.Write(1u);writer.Write(0u);writer.Write(0u);writer.Flush();
        check(Records.Parse(memory.ToArray(),"test.esp").Single().Kind=="ADDN","Plugin parser retains ADDN records");
    }
}
