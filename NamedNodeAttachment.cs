using FO4_Base_Library;
using NiflySharp.Blocks;
namespace SSEPreview;
internal static class NamedNodeAttachment
{
    internal static string? Read(Nifcontent_Class_Manolo nif,out string? issue)
    {
        issue=null;var children=nif.Blocks.OfType<NiNode>().SelectMany(n=>n.Children.Indices).ToHashSet();
        var directives=nif.Blocks.OfType<NiAVObject>().Where(n=>!children.Contains(nif.Blocks.IndexOf(n)))
            .SelectMany(n=>n.ExtraDataList.References).Select(r=>nif.GetBlock(r)).OfType<NiStringsExtraData>()
            .Where(e=>e.Name.ToString()=="AttachT").SelectMany(e=>e.Data).Select(s=>s.Content).ToArray();
        if(directives.Length==0)return null;
        if(directives.Length!=1||!directives[0].StartsWith("NamedNode&",StringComparison.Ordinal)||directives[0].Length<=10||directives[0][10..].Contains('&')){
            issue="Unsupported AttachT instructions: "+string.Join("; ",directives);return null;
        }
        return directives[0][10..];
    }
}
