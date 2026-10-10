using FO4_Base_Library;
using BSA_BA2_Library_DLL.BethesdaArchive.Core;
using System.Collections;
using System.Text.Json;
namespace SSEPreview;
internal static class EffectGraphInspection
{
    public static void Run()
    {
        using var stream=File.OpenRead(Path.Combine(SelfTest.GameData,"Skyrim - Animations.bsa"));
        using var reader=new BethesdaReader(stream);
        foreach(var entry in reader.EntriesFiles.Where(e=>new[]{"beginloopend.hkx","beginloopendchar.hkx","beginloopendbehavior.hkx"}.Contains(Path.GetFileName(e.FullPath),StringComparer.OrdinalIgnoreCase))){
        var bytes=reader.ExtractToMemory(entry.Index);
        var folder=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\effect-graph"));
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder,Path.GetFileName(entry.FullPath)),bytes);
        string asset=Path.Combine(folder,"assets",Assets.Normalize(entry.FullPath));
        Directory.CreateDirectory(Path.GetDirectoryName(asset)!);File.WriteAllBytes(asset,bytes);
        var graph=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(bytes));
        object? Safe(object? value,int depth=0) {
            if(value==null||value is string||value.GetType().IsPrimitive||value.GetType().IsEnum)return value;
            if(value is HkxVirtualObjectGraph_Class obj)return new{obj.ClassName,obj.RelativeOffset};
            if(depth>4)return value.ToString();
            if(value is IDictionary dict){var result=new Dictionary<string,object?>();foreach(DictionaryEntry pair in dict)result[pair.Key.ToString()!]=Safe(pair.Value,depth+1);return result;}
            if(value is IEnumerable list)return list.Cast<object?>().Select(v=>Safe(v,depth+1)).ToArray();
            return value.GetType().GetProperties().Where(p=>p.CanRead&&p.GetIndexParameters().Length==0&&p.Name is not "Graph" and not "Source" and not "Raw").ToDictionary(p=>p.Name,p=>Safe(p.GetValue(value),depth+1));
        }
        if(Path.GetFileName(entry.FullPath).Equals("beginloopendbehavior.hkx",StringComparison.OrdinalIgnoreCase)){
            var behavior=EffectSequenceGraph.Parse(bytes);
            File.WriteAllText(Path.Combine(folder,"parser-check.txt"),behavior.At(.5,1,s=>s=="mBegin"?new[]{(.25,"end")}:s=="mEnd"?new[]{(.4,"EndAnim")}:[]).ToString());
        }
        File.WriteAllLines(Path.Combine(folder,"text-key-api.txt"),typeof(NiflySharp.Blocks.NiControllerSequence).GetProperties().Select(p=>p.Name+" "+p.PropertyType.FullName));
        var rows=graph.Objects.Select(o=>new{o.ClassName,o.RelativeOffset,strings=graph.ReadAllReferencedStrings(o),
            fields=Safe(FO4_Base_Library.Havok.Canon.HavokObjetoGenerico.Leer(graph,o))});
        File.WriteAllText(Path.Combine(folder,Path.GetFileName(entry.FullPath)+".json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
    }
}
}
