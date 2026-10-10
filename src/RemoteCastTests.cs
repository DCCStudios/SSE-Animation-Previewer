using System.Numerics;
namespace SSEPreview;
internal static class RemoteCastTests
{
    internal static void Run(Action<bool,string> check)
    {
        var frames=ScriptedCasts.FenrirFrames();
        check(frames.Length==3&&frames.All(f=>f.Start==new Vector3(0,80,20)),"Fenrir remote casts share the authored 80-forward, 20-high start");
        check(frames[0].Target==new Vector3(0,180,50)&&Math.Abs(frames[1].Target.X+frames[2].Target.X)<1e-5&&frames.All(f=>f.Target.Z==50),"Fenrir endpoints use 180-unit radius and symmetric 12-degree offsets");
        foreach(var frame in frames){
            var cue=new EffectCue(0,"fixture",100,0,10,1,frame.Start,"remote",ProjectileType:1,Elevation:frame.Elevation,Heading:frame.Heading);
            double arrival=Vector3.Distance(frame.Start,frame.Target)/100;
            var transform=cue.LocalPlacement(arrival);
            check(Vector3.Distance(transform.Translation,frame.Target)<.0001,"Aimed projectile reaches its endpoint without rotating its launch offset");
            var forward=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,transform));
            check(Vector3.Distance(forward,Vector3.Normalize(frame.Target-frame.Start))<1e-5,"Remote projectile mesh faces its actual launch direction");
        }
        check(!ScriptedCasts.VerifiedFenrir([0,1,2,3]),"Unrecognized PEX content cannot activate a filename-only script adapter");
        string mod=Path.Combine(SelfTest.Mods,"Anchor MCO Animations v2 - Cloud Fenrir");
        string pex=Path.Combine(mod,@"Scripts\FenrirBladeBeam.pex");
        if(!File.Exists(pex)){SelfTest.Skip("Real Fenrir remote cast checks: "+SelfTest.Missing(pex));return;}
        check(ScriptedCasts.VerifiedFenrir(File.ReadAllBytes(pex)),"Installed Fenrir script matches the inspected implementation");
        var assets=new Assets();var records=new Records();
        foreach(var path in Directory.EnumerateFiles(mod,"*.esp"))foreach(var record in Records.Parse(File.ReadAllBytes(path),Path.GetFileName(path)))records.All[record.Key]=record;
        assets.External[@"scripts\FenrirBladeBeam.pex"]=pex;
        foreach(var path in Directory.EnumerateFiles(Path.Combine(mod,"meshes"),"*.nif",SearchOption.AllDirectories))assets.External[Assets.Normalize(Path.GetRelativePath(mod,path))]=path;
        var effects=new Effects(assets,records);
        var cues=effects.Preview(new("Dual beam","@CAST|0x8F6|Anchor_FF7Fenrir.esp","fixture"),.9);
        check(cues.Count==3&&cues.All(c=>c.Time==.9&&c.Offset==new Vector3(0,80,20)&&!c.FollowActor),"Real dual Blade Beam spell resolves three remotely placed launch cues at the annotation time");
        check(cues.Select(c=>c.Heading).Distinct().Count()==3&&cues.All(c=>c.Elevation>0),"Real remote casts retain distinct aimed headings and upward launch angles");
        check(effects.Diagnostics.Any(d=>d.Contains("verified FenrirBladeBeam adapter"))&&!effects.Diagnostics.Any(d=>d.Contains("Papyrus script FenrirBladeBeam is not executed")),"Recognized script support replaces the generic skipped-script diagnostic with explicit remaining limits");
        assets.External.Remove(@"scripts\FenrirBladeBeam.pex");
        // Other globally mounted sources may exist in interactive runs; this standalone test starts without them.
        if(!assets.Exists(@"scripts\FenrirBladeBeam.pex"))check(effects.Preview(new("Dual beam","@CAST|0x8F6|Anchor_FF7Fenrir.esp","fixture"),0).Count==0,"Missing script bytes never trigger the verified adapter");
    }
}
