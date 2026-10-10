using System.Text;
namespace SSEPreview;
internal static class ReferenceEffectTests
{
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var assets=new Assets();
        var art=new GameRecord{Kind="ARTO",Plugin="base.esm",Key="base.esm|000043"};art.Fields.Add(new("MODL",Encoding.UTF8.GetBytes(@"magic\fixture.nif")));records.All[art.Key]=art;
        var effect=new GameRecord{Kind="RFCT",Plugin="effect.esp",Key="effect.esp|000001",Masters=["base.esm"]};var data=new byte[12];BitConverter.GetBytes(0x43u).CopyTo(data,0);BitConverter.GetBytes(5u).CopyTo(data,8);effect.Fields.Add(new("DATA",data));records.All[effect.Key]=effect;
        assets.External[@"meshes\magic\fixture.nif"]="fixture";
        var entry=ReferenceEffects.Resolve(effect,records,assets);
        check(entry.Art==art&&entry.Model==@"meshes\magic\fixture.nif","Reference-effect art resolves through master ownership and normalizes the mesh root");
        check(entry.Flags==5&&entry.Details.Contains("Rotate to face target")&&entry.Details.Contains("Inherit rotation"),"Reference-effect browser preserves readable attachment flags");
        assets.External.Clear();check(ReferenceEffects.Resolve(effect,records,assets).Issues.Any(i=>i.StartsWith("Missing mesh:")),"Missing reference-effect meshes are reported before attempting a preview");
        effect.Fields[0]=new("DATA",[1,2,3]);check(ReferenceEffects.Resolve(effect,records,assets).Model.Length==0,"Truncated RFCT data cannot produce a guessed art reference");
        string mod=Path.Combine(SelfTest.Mods,"Anchor MCO Animations v2 - Cloud Fenrir");string path=Path.Combine(mod,"Anchor_FF7Fenrir.esp");
        if(!File.Exists(path)){SelfTest.Skip("Real Fenrir reference effect checks: "+SelfTest.Missing(path));return;}
        foreach(var record in Records.Parse(File.ReadAllBytes(path),Path.GetFileName(path)))records.All[record.Key]=record;
        foreach(int index in new[]{1,2,3}){
            string name=$"CrossSlash_HitFX0{index}";
            string mesh=$@"meshes\magiceffects\FF7Fenrir\{name}.nif";assets.External[mesh]=Path.Combine(mod,mesh);
            var record=records.Of("RFCT").Single(r=>r.EditorId==name);var resolved=ReferenceEffects.Resolve(record,records,assets);
            check(resolved.Model==mesh&&resolved.Art?.EditorId==name&&resolved.Issues.Length==0,$"Real {name} reference resolves to its authored ARTO and existing NIF");
        }
    }
}
