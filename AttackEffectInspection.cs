using System.Text.Json;
using System.Text.Json.Serialization;
namespace SSEPreview;
internal static class AttackEffectInspection
{
    internal static async Task Run(string data,string mod)
    {
        var assets=new Assets();
        await assets.Mount(data,false,new Progress<string>());
        await assets.Mount(mod,true,new Progress<string>());
        var records=new Records();records.Load(assets,null);
        var effects=new Effects(assets,records);effects.ReadConfigs();
        var rows=new List<object>();
        foreach(var entry in assets.Clips.Where(c=>assets.Source(c.Path).StartsWith(Path.GetFullPath(mod),StringComparison.OrdinalIgnoreCase))){
            try{
                var clip=AnimationClip.Load(assets.Read(entry.Path));
                var cues=effects.Build(clip,false);
                rows.Add(new{entry.Path,clip.Duration,events=clip.Events,cues=cues.Select(c=>new{c.Time,c.Model,c.Origin,c.Biped,c.Lifetime,c.Speed,c.Gravity,c.ProjectileType,c.Heading,c.Elevation,c.FollowActor,c.Offset,c.Scale,exists=assets.Exists(c.Model)}),diagnostics=effects.Diagnostics.ToArray()});
            }catch(Exception ex){rows.Add(new{entry.Path,error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"attack-effect-inspection.json"),JsonSerializer.Serialize(new{data,mod,diagnostics=assets.Diagnostics.Concat(records.Diagnostics),clips=rows},new JsonSerializerOptions{WriteIndented=true,IncludeFields=true,NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals}));
    }
}
