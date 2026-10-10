namespace SSEPreview;
internal sealed record ReferenceEffectEntry(GameRecord Record,GameRecord? Art,GameRecord? Shader,string Model,uint Flags,string[] Issues)
{
    public string Name=>$"{Record.EditorId}  [{Record.Plugin}]";
    public string Details=>string.Join(Environment.NewLine,new[]{
        Name,"Reference: "+Record.Key,"Art: "+(Art==null?"(unresolved or none)":Art.EditorId+" ["+Art.Key+"]"),
        "Mesh: "+(Model.Length==0?"(none)":Model),"Effect shader: "+(Shader==null?"(none or unresolved)":Shader.EditorId+" ["+Shader.Key+"]"),
        "Flags: "+FlagNames(Flags),"","Preview art opens the model alone. Play here / Stop here add art actions at the current timeline time, at the selected target position, optionally following character root motion. These controls do not simulate a script hit, target actor, or reference attachment.",""}.Concat(Issues));
    static string FlagNames(uint flags){
        var names=new List<string>();if((flags&1)!=0)names.Add("Rotate to face target");if((flags&2)!=0)names.Add("Attach to camera");if((flags&4)!=0)names.Add("Inherit rotation");if((flags&~7u)!=0)names.Add($"Unknown flags 0x{flags&~7u:X}");return names.Count==0?"None":string.Join(", ",names);
    }
    public override string ToString()=>Name;
}
internal static class ReferenceEffects
{
    internal static ReferenceEffectEntry Resolve(GameRecord record,Records records,Assets assets)
    {
        if(record.Kind!="RFCT")throw new ArgumentException("Expected a reference-effect record.");
        var issues=new List<string>();var data=record.First("DATA");
        if(data is not {Bytes.Length:>=12})return new(record,null,null,"",0,["Reference-effect DATA is missing or truncated."]);
        var art=records.Resolve(record,data.UInt());var shader=records.Resolve(record,data.UInt(4));
        if(data.UInt()!=0&&art?.Kind!="ARTO")issues.Add("Missing or invalid art reference: "+record.Reference(data.UInt()));
        if(data.UInt(4)!=0&&shader?.Kind!="EFSH")issues.Add("Missing or invalid effect-shader reference: "+record.Reference(data.UInt(4)));
        string model=art?.Kind=="ARTO"?art.First("MODL")?.Text??"":"";
        if(model.Length>0){model=Assets.GamePath(model);if(!model.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))model=@"meshes\"+model;if(!assets.Exists(model))issues.Add("Missing mesh: "+model);}
        else issues.Add("This record has no resolved art mesh to preview.");
        if(data.UInt(4)!=0)issues.Add("Actor-surface effect shaders are not yet rendered.");
        if(data.UInt(8)!=0)issues.Add("Target/camera attachment flags are shown for inspection; standalone art preview does not apply them.");
        return new(record,art,shader,model,data.UInt(8),issues.ToArray());
    }
    internal static ReferenceEffectEntry[] Catalog(Records records,Assets assets)=>records.Of("RFCT").Select(r=>Resolve(r,records,assets)).ToArray();
}
