using System.Numerics;
using System.Text;
namespace SSEPreview;
internal static class MuzzleFlashTests
{
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var assets=new Assets();
        GameRecord Record(string kind,uint id){var r=new GameRecord{Kind=kind,Plugin="muzzle.esp",Key=$"muzzle.esp|{id:X6}",Id=id};r.Fields.Add(new("EDID",Encoding.UTF8.GetBytes(kind+id)));records.All[r.Key]=r;return r;}
        var projectile=Record("PROJ",3);var data=new byte[84];data[0]=8;data[2]=1;BitConverter.GetBytes(.25f).CopyTo(data,44);
        projectile.Fields.Add(new("DATA",data));projectile.Fields.Add(new("NAM1",Encoding.UTF8.GetBytes(@"effects\flash.nif")));
        var effect=Record("MGEF",2);var magic=new byte[100];BitConverter.GetBytes(3u).CopyTo(magic,72);effect.Fields.Add(new("DATA",magic));
        var spell=Record("SPEL",1);spell.Fields.Add(new("EFID",BitConverter.GetBytes(2u)));
        assets.External[@"meshes\effects\flash.nif"]="fixture";
        var effects=new Effects(assets,records);var trigger=new Trigger("Muzzle","@CAST|0x1|muzzle.esp","fixture");
        var cue=effects.Preview(trigger,1.5).Single();
        check(cue.Time==1.5&&cue.Lifetime==.25&&cue.Model==@"meshes\effects\flash.nif","ESP muzzle art resolves independently of a main projectile mesh at cast time");
        check(cue.TemporaryParticle&&cue.ParticlesVisible(.2)&&!cue.ParticlesVisible(.3),"Authored muzzle duration limits particle playback as well as geometry");
        var remote=ProjectileVisuals.Muzzle(projectile,2,new(10,20,30),.4f,.7f)!;
        check(remote.LocalPlacement(0).Translation==new Vector3(10,20,30)&&remote.LocalPlacement(.2).Translation==new Vector3(10,20,30),"Muzzle art stays at its launch position instead of flying with the projectile");
        var direction=Vector3.TransformNormal(Vector3.UnitY,remote.LocalPlacement(0));
        var expected=Vector3.TransformNormal(Vector3.UnitY,Matrix4x4.CreateRotationX(.4f)*Matrix4x4.CreateRotationZ(.7f));
        check(Vector3.Distance(direction,expected)<1e-6,"Muzzle art retains aimed launch elevation and heading");
        data[0]=0;check(effects.Preview(trigger,0).Count==0,"Disabled muzzle flag prevents automatic launch art");data[0]=8;
        foreach(float duration in new[]{0f,-1,float.NaN,float.PositiveInfinity}){BitConverter.GetBytes(duration).CopyTo(data,44);check(ProjectileVisuals.Muzzle(projectile,0,Vector3.Zero,0,0)==null,"Invalid muzzle duration is not replaced by an invented lifetime: "+duration);}
        BitConverter.GetBytes(.25f).CopyTo(data,44);assets.External.Clear();check(effects.Preview(trigger,0).Count==0&&effects.Diagnostics.Any(d=>d.Contains("Missing effect mesh")),"Missing muzzle art is diagnosed and not enqueued");
    }
}
