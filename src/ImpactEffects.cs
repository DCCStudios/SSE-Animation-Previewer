namespace SSEPreview;
internal sealed record ImpactEffectEntry(GameRecord Record,string Model,float Duration,string[] Lines)
{
    public override string ToString()=>$"{Record.EditorId} [{Record.Kind} / {Record.Plugin}]";
    public string Details=>string.Join(Environment.NewLine,new[]{ToString(),"Reference: "+Record.Key,"Mesh: "+(Model.Length>0?Model:"(none)")}.Concat(Lines));
}
internal static class ImpactEffects
{
    internal static ImpactEffectEntry[] Catalog(Records records,Assets assets)
    {
        var result=new List<ImpactEffectEntry>();
        foreach(var record in records.Of("IPCT").Concat(records.Of("EXPL"))){
            var lines=new List<string>();var data=record.First("DATA");float duration=0;
            string model=record.First("MODL")?.Text??"";
            if(model.Length>0){model=Assets.GamePath(model);if(!model.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))model=@"meshes\"+model;if(!assets.Exists(model))lines.Add("Missing mesh: "+model);}
            string Reference(GameRecord owner,uint id)=>id==0?"(none)":records.Resolve(owner,id) is {} linked?$"{linked.EditorId} [{linked.Key}]":"Unresolved: "+owner.Reference(id);
            if(record.Kind=="IPCT"){
                if(data is {Bytes.Length:>=24}){
                    duration=data.Float();uint orientation=data.UInt(4);
                    lines.Add($"Effect duration: {duration:g} seconds");
                    lines.Add("Orientation: "+(orientation switch{0=>"Surface normal",1=>"Projectile vector",2=>"Projectile reflection",_=>$"Unknown ({orientation})"}));
                    lines.Add($"Angle threshold: {data.Float(8):g}; placement radius: {data.Float(12):g}");
                    lines.Add($"Impact result: {data.Bytes[21]}; flags: 0x{data.Bytes[20]:X2}");
                }else lines.Add("Impact DATA is missing or truncated; authored duration unavailable.");
                lines.Add("Hazard: "+Reference(record,record.First("NAM2")?.UInt()??0));
                foreach(var set in records.Of("IPDS"))foreach(var pair in set.All("PNAM")){
                    if(pair.Bytes.Length<8)continue;
                    if(records.Resolve(set,pair.UInt(4))?.Key.Equals(record.Key,StringComparison.OrdinalIgnoreCase)==true)
                        lines.Add($"Material mapping: {set.EditorId} [{set.Key}] -> {Reference(set,pair.UInt())}");
                }
                lines.Add("Standalone preview does not apply collision normal/reflection, random placement, decals, sounds or hazard spawning.");
            }else{
                if(data is {Bytes.Length:>=40}){
                    lines.Add($"Force: {data.Float(24):g}; damage: {data.Float(28):g}; radius: {data.Float(32):g}; image-space radius: {data.Float(36):g}");
                    lines.Add("Impact data set: "+Reference(record,data.UInt(12)));
                    lines.Add("Placed object: "+Reference(record,data.UInt(16)));
                    lines.Add("Spawn projectile: "+Reference(record,data.UInt(20)));
                }else lines.Add("Explosion DATA is missing or truncated.");
                foreach(var projectile in records.Of("PROJ"))if(projectile.First("DATA") is {Bytes.Length:>=40} pd&&records.Resolve(projectile,pd.UInt(36))?.Key.Equals(record.Key,StringComparison.OrdinalIgnoreCase)==true)
                    lines.Add($"Projectile: {projectile.EditorId} [{projectile.Key}]; trigger proximity {pd.Float(28):g}; timer {pd.Float(32):g}; flags 0x{BitConverter.ToUInt16(pd.Bytes):X4}");
                lines.Add("Standalone explosion mesh preview does not apply damage, lights, sounds, secondary spawning or collision triggers.");
            }
            if(!float.IsFinite(duration)||duration<=0){duration=10;lines.Add("No positive finite effect duration; emission preview limit: 10 seconds.");}
            lines.Add("Preview stops emission at the displayed duration and lets remaining particles expire. No impact is inferred from projectile range expiry.");
            result.Add(new(record,model,duration,lines.ToArray()));
        }
        return result.OrderBy(e=>e.Record.EditorId,StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
