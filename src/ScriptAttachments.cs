using System.Text;
namespace SSEPreview;
internal sealed record ScriptObject(uint FormId,short Alias);
internal sealed record ScriptProperty(string Name,byte Type,object? Value);
internal sealed record ScriptAttachment(string Name,ScriptProperty[] Properties);
internal static class ScriptAttachments
{
    internal static ScriptAttachment[] Read(byte[] bytes)
    {
        using var stream=new MemoryStream(bytes,false);using var reader=new BinaryReader(stream,Encoding.UTF8);
        ushort version=reader.ReadUInt16(),format=reader.ReadUInt16();
        if(version>5||format is not (1 or 2))throw new InvalidDataException($"Unsupported VMAD version/format {version}/{format}");
        string Text(){int size=reader.ReadUInt16();var value=reader.ReadBytes(size);if(value.Length!=size)throw new EndOfStreamException();return Encoding.UTF8.GetString(value);}
        object? Value(byte type){
            switch(type){
                case 0:return null;
                case 1:
                    if(format==1){uint id=reader.ReadUInt32();short alias=reader.ReadInt16();reader.ReadUInt16();return new ScriptObject(id,alias);}
                    reader.ReadUInt16();short a=reader.ReadInt16();return new ScriptObject(reader.ReadUInt32(),a);
                case 2:return Text();
                case 3:return reader.ReadInt32();
                case 4:return reader.ReadSingle();
                case 5:return reader.ReadByte()!=0;
                case >=11 and <=15:
                    uint count=reader.ReadUInt32();if(count>stream.Length-stream.Position)throw new InvalidDataException("VMAD array exceeds remaining data");
                    var items=new object?[count];for(int i=0;i<items.Length;i++)items[i]=Value((byte)(type-10));return items;
                default:throw new InvalidDataException($"Unsupported VMAD property type {type}");
            }
        }
        var scripts=new ScriptAttachment[reader.ReadUInt16()];
        for(int i=0;i<scripts.Length;i++){
            string name=Text();reader.ReadByte();var properties=new ScriptProperty[reader.ReadUInt16()];
            for(int p=0;p<properties.Length;p++){string key=Text();byte type=reader.ReadByte();reader.ReadByte();properties[p]=new(key,type,Value(type));}
            scripts[i]=new(name,properties);
        }
        return scripts;
    }
    internal static IEnumerable<string> Describe(GameRecord record,Records records,ISet<string>? handled=null)
    {
        if(record.First("VMAD") is not {} vmad)yield break;
        ScriptAttachment[] scripts=[];string? error=null;
        try{scripts=Read(vmad.Bytes);}catch(Exception ex) when(ex is IOException or ArgumentException or InvalidDataException){error=ex.Message;}
        if(error!=null){yield return record.EditorId+": cannot read script attachments: "+error;yield break;}
        foreach(var script in scripts){
            if(handled?.Contains(script.Name)==true)continue;
            var links=script.Properties.Where(p=>p.Value is ScriptObject).Select(p=>{
                var value=(ScriptObject)p.Value!;var target=records.Resolve(record,value.FormId);
                return $"{p.Name} = "+(value.Alias>=0?$"alias {value.Alias}, ":"")+(target==null?record.Reference(value.FormId):$"{target.Kind} {target.EditorId} [{target.Key}]");
            });
            yield return $"{record.EditorId}: Papyrus script {script.Name} is not executed; scripted spawns and hit reactions are not previewed."+string.Concat(links.Select(l=>"\n  "+l));
        }
    }
}
