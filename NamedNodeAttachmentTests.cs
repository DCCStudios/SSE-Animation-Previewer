using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
namespace SSEPreview;
internal static class NamedNodeAttachmentTests
{
    internal static void Run(Action<bool,string> check)
    {
        string folder=Path.Combine(ParticleTests.VerificationProjectRoot(),"verification","named-attachment","fixture");
        var file=new NifFile();file.Load(ParticleFixture.Create(folder));
        var root=file.Blocks.OfType<NiNode>().First();var extra=new NiStringsExtraData{Name=new("AttachT"),NumStrings=1,Data=[new("NamedNode&Weapon")]};root.ExtraDataList.AddBlockRef(file.AddBlock(extra));
        string path=Path.Combine(folder,"attached.nif");
        (string? Target,string? Issue) Read(){if(file.Save(path)!=0)throw new InvalidDataException("Attachment fixture export failed");var loaded=new Nifcontent_Class_Manolo();loaded.Load_Manolo(File.ReadAllBytes(path));var target=NamedNodeAttachment.Read(loaded,out var issue);return(target,issue);}
        var result=Read();check(result.Target=="Weapon"&&result.Issue==null,"Reloaded root AttachT resolves its explicit named skeleton target");
        extra.Data=[new("NamedNode&")];result=Read();check(result.Target==null&&result.Issue!=null,"Empty named attachment is diagnosed instead of binding an arbitrary bone");
        extra.Data=[new("OtherTechnique&Weapon")];result=Read();check(result.Target==null&&result.Issue!=null,"Unknown attachment technique remains explicitly unsupported");
    }
}
