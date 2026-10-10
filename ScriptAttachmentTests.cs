using System.Text;
namespace SSEPreview;
internal static class ScriptAttachmentTests
{
    internal static void Run(Action<bool,string> check)
    {
        byte[] Fixture(ushort format){
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.UTF8,true);
            void Text(string s){var b=Encoding.UTF8.GetBytes(s);writer.Write((ushort)b.Length);writer.Write(b);}
            writer.Write((ushort)5);writer.Write(format);writer.Write((ushort)1);Text("TestEffect");writer.Write((byte)0);writer.Write((ushort)3);
            Text("Effect");writer.Write((byte)1);writer.Write((byte)1);
            if(format==1){writer.Write(0x01000946u);writer.Write((short)-1);writer.Write((ushort)0);}else{writer.Write((ushort)0);writer.Write((short)-1);writer.Write(0x01000946u);}
            Text("Delay");writer.Write((byte)4);writer.Write((byte)1);writer.Write(.25f);
            Text("Labels");writer.Write((byte)12);writer.Write((byte)1);writer.Write(2u);Text("start");Text("stop");
            return stream.ToArray();
        }
        foreach(ushort format in new ushort[]{1,2}){
            var script=ScriptAttachments.Read(Fixture(format)).Single();
            check(script.Name=="TestEffect"&&script.Properties[0].Value is ScriptObject {FormId:0x01000946,Alias:-1},$"VMAD format {format} preserves attached script and object form ID");
            check(script.Properties[1].Value is float f&&f==.25f&&script.Properties[2].Value is object?[] a&&a.SequenceEqual(new[]{"start","stop"}),$"VMAD format {format} reads scalar and array properties without losing alignment");
        }
        string plugin=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\Anchor_FF7Fenrir.esp");
        if(File.Exists(plugin)){
            var parsed=Records.Parse(File.ReadAllBytes(plugin),Path.GetFileName(plugin)).ToArray();
            check(parsed.Count(r=>r.Kind=="RFCT"&&r.EditorId.StartsWith("CrossSlash_HitFX"))==3,"Real Anchor plugin retains all three scripted Cross Slash reference effects");
            var fx=parsed.Single(r=>r.Kind=="MGEF"&&r.EditorId=="Fenrir_CrossSlash_FX1_index");
            var link=ScriptAttachments.Read(fx.First("VMAD")!.Bytes).Single().Properties.Single(p=>p.Name=="OnHitVFXplay");
            check(link.Value is ScriptObject {FormId:0x02000946,Alias:-1},"Real Cross Slash script property resolves the first visual-effect form rather than a direct particle NIF");
        }
        var bytes=Fixture(2);bool truncated=false;try{ScriptAttachments.Read(bytes[..^1]);}catch(EndOfStreamException){truncated=true;}
        check(truncated,"Truncated VMAD property data fails explicitly");
        var records=new Records();var owner=new GameRecord{Plugin="owner.esp",Key="owner.esp|000001",Masters=["Skyrim.esm"]};owner.Fields.Add(new("VMAD",bytes));
        var target=new GameRecord{Kind="RFCT",Plugin="owner.esp",Key="owner.esp|000946"};target.Fields.Add(new("EDID",Encoding.UTF8.GetBytes("ImpactVisual\0")));records.All[target.Key]=target;
        string message=ScriptAttachments.Describe(owner,records).Single();
        check(message.Contains("RFCT ImpactVisual")&&message.Contains("is not executed"),"Script diagnostics resolve property form IDs through the owning plugin without implying script execution");
        owner.Fields[0]=new("VMAD",bytes[..^1]);
        check(ScriptAttachments.Describe(owner,records).Single().Contains("cannot read script attachments"),"Malformed attachment data produces a diagnostic instead of aborting spell preview");
    }
}
