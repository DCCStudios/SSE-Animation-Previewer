using System.Numerics;
using System.Text;
namespace SSEPreview;
internal static class TimedExplosionTests
{
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var assets=new Assets();
        GameRecord Record(string kind,uint id){var r=new GameRecord{Kind=kind,Plugin="timer.esp",Key=$"timer.esp|{id:X6}",Id=id};records.All[r.Key]=r;return r;}
        var explosion=Record("EXPL",4);explosion.Fields.Add(new("MODL",Encoding.UTF8.GetBytes(@"effects\blast.nif")));
        var projectile=Record("PROJ",3);var data=new byte[84];data[0]=6;data[2]=1;
        BitConverter.GetBytes(100f).CopyTo(data,8);BitConverter.GetBytes(1000f).CopyTo(data,12);BitConverter.GetBytes(.5f).CopyTo(data,32);BitConverter.GetBytes(4u).CopyTo(data,36);
        projectile.Fields.Add(new("DATA",data));projectile.Fields.Add(new("MODL",Encoding.UTF8.GetBytes(@"effects\projectile.nif")));
        var magic=Record("MGEF",2);var md=new byte[100];BitConverter.GetBytes(3u).CopyTo(md,72);magic.Fields.Add(new("DATA",md));
        var spell=Record("SPEL",1);spell.Fields.Add(new("EFID",BitConverter.GetBytes(2u)));
        assets.External[@"meshes\effects\blast.nif"]="fixture";assets.External[@"meshes\effects\projectile.nif"]="fixture";
        var effects=new Effects(assets,records);var trigger=new Trigger("Timed","@CAST|0x1|timer.esp","fixture");
        var cues=effects.Preview(trigger,2);var flight=cues.Single(c=>c.Model.EndsWith("projectile.nif"));var blast=cues.Single(c=>c.Model.EndsWith("blast.nif"));
        check(flight.Lifetime==.5&&flight.RemovedAt==2.5&&!flight.ParticlesVisible(.5),"Timer detonation stops projectile geometry and particles at the event time");
        check(blast.Time==2.5&&blast.Offset==new Vector3(0,50,100)&&blast.PlacementTime==2,"Explosion uses trajectory position and preserves launch actor frame after delayed activation");
        check(!blast.ParticlesVisible(-.01)&&blast.ParticlesVisible(0),"Delayed explosion cannot appear before its scheduled event");
        var aimed=flight with{Offset=new(10,20,30),Gravity=.2f,Elevation=.3f,Heading=.5f,WorldSpace=true};
        var aimedBlast=ProjectileVisuals.Explosion(projectile,records,aimed,.5)!;
        check(aimedBlast.Offset==aimed.LocalPlacement(.5).Translation&&aimedBlast.WorldSpace,"Explosion placement includes gravity and launch direction and preserves world-space casts");
        check(ProjectileVisuals.DetonationTime(projectile,records,.25)==null,"Projectile range/lifetime ending before timer does not invent a detonation");
        data[0]=2;check(ProjectileVisuals.DetonationTime(projectile,records,10)==null,"Impact-only explosion flag does not trigger a timer");data[0]=6;
        foreach(float timer in new[]{0f,-1,float.NaN,float.PositiveInfinity}){BitConverter.GetBytes(timer).CopyTo(data,32);check(ProjectileVisuals.DetonationTime(projectile,records,10)==null,"Invalid alternate timer is not scheduled: "+timer);}
        BitConverter.GetBytes(.5f).CopyTo(data,32);data[2]=4;check(ProjectileVisuals.DetonationTime(projectile,records,10)==null,"Unsupported beam timing is not guessed from ballistic behavior");data[2]=1;
        assets.External.Remove(@"meshes\effects\blast.nif");check(effects.Preview(trigger,2).Count==1&&effects.Diagnostics.Any(d=>d.Contains("Missing effect mesh")),"Missing explosion artwork is diagnosed while timed projectile termination remains active");
    }
}
