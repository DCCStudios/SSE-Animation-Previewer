namespace SSEPreview;
internal static class ProjectileVisuals
{
    internal static string ModelPath(string? model)
    {
        if(string.IsNullOrWhiteSpace(model))return "";
        var path=Assets.GamePath(model);return path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase)?path:@"meshes\"+path;
    }
    internal static EffectCue? Muzzle(GameRecord projectile,double time,System.Numerics.Vector3 offset,float elevation,float heading)
    {
        var data=projectile.First("DATA");var model=ModelPath(projectile.First("NAM1")?.Text);
        if(data is not {Bytes.Length:>=48}||(BitConverter.ToUInt16(data.Bytes)&8)==0||model.Length==0)return null;
        float duration=data.Float(44);if(!float.IsFinite(duration)||duration<=0)return null;
        return new(time,model,0,0,duration,1,offset,$"{projectile.EditorId} [{projectile.Key}] muzzle flash; authored duration={duration:g}s; preview launch-frame placement",
            ProjectileType:1,TemporaryParticle:true,Elevation:elevation,Heading:heading);
    }
    internal static double? DetonationTime(GameRecord projectile,Records records,double lifetime)
    {
        var data=projectile.First("DATA");if(data is not {Bytes.Length:>=40})return null;
        if((BitConverter.ToUInt16(data.Bytes)&6)!=6||!ProjectileMotion.IsBallistic(BitConverter.ToUInt16(data.Bytes,2)))return null;
        float timer=data.Float(32);
        return float.IsFinite(timer)&&timer>0&&timer<=lifetime&&records.Resolve(projectile,data.UInt(36))?.Kind=="EXPL"?timer:null;
    }
    internal static EffectCue? Explosion(GameRecord projectile,Records records,EffectCue launch,double delay)
    {
        var data=projectile.First("DATA");var explosion=data is {Bytes.Length:>=40}?records.Resolve(projectile,data.UInt(36)):null;
        if(explosion?.Kind!="EXPL")return null;var model=ModelPath(explosion.First("MODL")?.Text);if(model.Length==0)return null;
        return new(launch.Time+delay,model,0,0,10,1,launch.LocalPlacement(delay).Translation,
            $"{projectile.EditorId} -> {explosion.EditorId} [{explosion.Key}] timed explosion; emission preview limit 10s",WorldSpace:launch.WorldSpace,PlacementTime:launch.PlacementTime??launch.Time);
    }
    internal static IEnumerable<string> Describe(GameRecord projectile,Records records,Assets assets)
    {
        string source=$"{projectile.EditorId} [{projectile.Key}]";var data=projectile.First("DATA");
        string model=ModelPath(projectile.First("MODL")?.Text);
        yield return source+": projectile artwork: "+(model.Length==0?"(none authored; there is no main mesh to draw)":model+(assets.Exists(model)?"":" (missing from loaded files)"));
        if(data is not {Bytes.Length:>=84})yield return source+": projectile DATA is missing or truncated; some motion/lifetime fields are unavailable.";
        string muzzle=ModelPath(projectile.First("NAM1")?.Text);
        if(muzzle.Length>0){
            string duration=data is {Bytes.Length:>=48}?data.Float(44).ToString("g",System.Globalization.CultureInfo.InvariantCulture):"unavailable";
            bool enabled=data is {Bytes.Length:>=2}&&(BitConverter.ToUInt16(data.Bytes)&8)!=0;
            yield return source+$": muzzle-flash artwork: {muzzle}; duration={duration}s; flag={(enabled?"enabled":"disabled")}. "+(assets.Exists(muzzle)?"":"Mesh is missing from loaded files. ")+"Positive finite durations play at the preview launch frame; native weapon-node attachment and muzzle lighting remain unverified.";
        }
        if(data is {Bytes.Length:>=40}&&data.UInt(36)!=0){
            var explosion=records.Resolve(projectile,data.UInt(36));
            if(explosion?.Kind!="EXPL")yield return source+": unresolved explosion reference: "+projectile.Reference(data.UInt(36));
            else yield return source+$": linked explosion {explosion.EditorId} [{explosion.Key}]; artwork: "+(ModelPath(explosion.First("MODL")?.Text) is {Length:>0} artwork?artwork:"(none)")+". Use Impacts / explosions to inspect it; positive alternate timers are previewed for ballistic types; collision and proximity triggers are not implemented.";
        }
    }
    internal static void Test(Action<bool,string> check)
    {
        check(ModelPath(@"meshes\effects\beam.nif")==@"meshes\effects\beam.nif"&&ModelPath(@"effects\beam.nif")==@"meshes\effects\beam.nif","Projectile artwork paths accept an existing meshes prefix without doubling it");
        var records=new Records();var assets=new Assets();var p=new GameRecord{Kind="PROJ",Plugin="test.esp",Key="test.esp|000001"};
        var data=new byte[84];data[0]=8;BitConverter.GetBytes(.125f).CopyTo(data,44);BitConverter.GetBytes(2u).CopyTo(data,36);p.Fields.Add(new("DATA",data));p.Fields.Add(new("NAM1",System.Text.Encoding.UTF8.GetBytes(@"effects\flash.nif")));
        var lines=string.Join("\n",Describe(p,records,assets));
        check(lines.Contains("none authored")&&lines.Contains("test.esp|000001"),"Missing projectile artwork is explicit and identifies the owning record");
        check(lines.Contains("duration=0.125s")&&lines.Contains("flag=enabled")&&lines.Contains("Mesh is missing")&&lines.Contains("launch frame"),"Muzzle flash diagnostics retain authored duration, flag, missing file and attachment limitation");
        check(lines.Contains("unresolved explosion reference"),"Missing projectile explosion links are reported");
        var explosion=new GameRecord{Kind="EXPL",Plugin="test.esp",Key="test.esp|000002",Id=2};records.All[explosion.Key]=explosion;
        check(string.Join("\n",Describe(p,records,assets)).Contains("linked explosion"),"Resolved explosion links remain identifiable without claiming automatic detonation");
    }
}
