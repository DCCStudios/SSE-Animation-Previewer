using System.Numerics;
using System.Text.Json;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace SSEPreview;
public static class ParticleTests
{
    // Which annotations count as active at a time (the Annotations tab highlight).
    static void AnnotationActivityTests(Action<bool,string> Assert)
    {
        (double,string,bool)[] source=[(0,"Collision_AttackStart",true),(0,"weaponSwing",true),(.05,"PIE.@SGVF|MCO_AttackSpeed|0.5",true),(.05,"PIE.@SGVI|MCO_nextattack|2",true),
            (.1,"Collision_Add.Node(WEAPON)",true),(.2,"HitFrame",true),(.25,"animmotion 0 10 0",true),(.3,"MCO_WinOpen",true),(.3,"MCO_PowerWinOpen",true),(.35,"Collision_Remove.node(WEAPON)",true),
            (.4,"PIE.@SGVF|MCO_AttackSpeed|1.0",true),(.5,"MCO_WinClose",true),(.6,"PIE.@SGVI|MCO_nextattack|3",true),(.7,"attackStop",true),(.8,"afterHandOver",false)];
        var rows=source.Select(r=>new AnnotationRow(r.Item1,r.Item2,r.Item3,r.Item2)).ToList();
        var precision=PrecisionTimeline.Build(rows.Where(r=>r.Fires).Select(r=>new Annotation(r.Time,r.Text,0)),new PrecisionSettings(),true,_=>false,_=>true);
        string At(double t)=>string.Join(",",AnnotationActivity.ActiveAt(rows,t,precision).OrderBy(i=>i));
        Assert(At(.02)=="0,1","Annotations that fire together are active together (AttackStart and weaponSwing at 0)");
        Assert(At(.15)=="0,2,3,4","An ordinary annotation ends when the next one fires; AttackSpeed, nextattack, AttackStart and a live Collision_Add stay active");
        Assert(At(.26)=="0,2,3,4,5,6","animmotion keys do not end HitFrame; the latest animmotion key is active");
        Assert(At(.32)=="0,2,3,4,6,7,8","WinOpen and PowerWinOpen become active and end HitFrame");
        Assert(At(.37)=="0,2,3,6,7,8,9","Collision_Remove ends its Collision_Add and is active itself");
        Assert(At(.45)=="0,3,6,7,8,9,10","A new MCO_AttackSpeed replaces the previous one");
        Assert(At(.55)=="0,3,6,8,9,10,11","WinClose ends WinOpen; the power window stays open");
        Assert(At(.65)=="0,6,8,9,10,11,12","A new MCO_nextattack replaces the previous one");
        Assert(At(.75)=="6,8,9,10,11,12,13"&&At(.9)==At(.75),"attackStop ends Collision_AttackStart; an annotation that never fires is never active");
        Assert(At(-.1)=="","Nothing is active before the first annotation");
    }
    // MCO_AttackSpeed: every annotation changes speed from its time on; a queue carries the speed into the next clip.
    // BFCO clips: detection, the speed rule, the hand-over annotation, lasting annotations and the tab's notes.
    static void BfcoTests(Action<bool,string> Assert)
    {
        static bool Near(double a,double b)=>Math.Abs(a-b)<1e-9;
        static AnimationClip Clip(double duration,params (double Time,string Text)[] events){var list=events.Select(e=>new Annotation(e.Time,e.Text,0)).OrderBy(e=>e.Time).ToList();return new AnimationClip{Duration=duration,Events=list,SpeedChanges=AnimationClip.ReadSpeedChanges(list),BfcoSpeedChanges=AnimationClip.ReadSpeedChanges(list,"BFCO_AttackSpeed")};}
        var plain=Clip(1,(0,"PIE.@SGVF|BFCO_AttackSpeed|0.5"));
        Assert(Bfco.IsClip(@"x\BFCO_Attack1.HKX",[])&&Bfco.IsClip("bfco_powerattack2.hkx",[])&&Bfco.IsClip("attack.hkx",[new(0,"BFCO_NextIsAttack2",0)])&&!Bfco.IsClip("mco_attack1.hkx",plain.Events)&&!Bfco.IsClip(@"BFCO_folder\mco_attack1.hkx",[]),
            "A BFCO clip is one named BFCO_* or carrying BFCO's chain annotations; a BFCO_AttackSpeed payload alone, or a BFCO folder, is not enough");
        Assert(AnimationClip.Timeline("mco_attack1.hkx",plain).Segments==null,"BFCO_AttackSpeed in a clip that is not a BFCO clip is still ignored");
        var both=Clip(1,(0,"PIE.@SGVF|MCO_AttackSpeed|2"),(0,"PIE.@SGVF|BFCO_AttackSpeed|0.5"));
        Assert(Bfco.SpeedChanges(both,1) is [(0,.5)],"A BFCO clip plays at BFCO_AttackSpeed when it is not 1");
        var fallback=Clip(1,(0,"PIE.@SGVF|MCO_AttackSpeed|2"),(.4,"PIE.@SGVF|BFCO_AttackSpeed|0.5"),(.8,"PIE.@SGVF|BFCO_AttackSpeed|1"));
        Assert(Bfco.SpeedChanges(fallback,1) is [(0,2),(.4,.5),(.8,2)],"While BFCO_AttackSpeed is exactly 1 the clip plays at MCO_AttackSpeed, and returns to it when BFCO_AttackSpeed goes back to 1");
        var chain=AnimationClip.Queue([("BFCO_Attack1.hkx",both),("BFCO_Attack2.hkx",Clip(1))],0);
        Assert(Near(chain.Segments![0].Length,2)&&Near(chain.Segments[1].Speed.StartSpeed,2)&&Near(chain.Segments[1].Length,.5)&&chain.Segments[0].SpeedVariable.StartsWith("BFCO_AttackSpeed"),
            "BFCO_AttackSpeed starts again at 1 in the next clip of a chain, which then plays at the MCO_AttackSpeed carried over (0.5x clip, then 2x)");
        var mixed=AnimationClip.Queue([("mco_attack1.hkx",both),("mco_attack2.hkx",Clip(1))],0);
        Assert(Near(mixed.Segments![0].Length,.5)&&Near(mixed.Segments[1].Length,.5)&&mixed.Segments[0].SpeedVariable=="MCO_AttackSpeed","The same annotations in MCO clips play at MCO_AttackSpeed throughout");
        var loop=AnimationClip.Queue([("BFCO_Attack1.hkx",both),("BFCO_PowerAttackLoop1.hkx",Clip(1,(0,"PIE.@SGVF|MCO_AttackSpeed|3"))),("BFCO_Attack2.hkx",Clip(1))],0);
        Assert(Near(loop.Segments![1].Length,1)&&loop.Segments[1].Speed.Uniform&&Near(loop.Segments[2].Speed.StartSpeed,2),"A BFCO hold loop plays at 1x whatever it carries, and passes the carried speed on unchanged");
        var windows=Clip(1,(0,"BFCO_NextIsAttack3"),(0,"BFCO_NextIsPowerAttack2"),(.3,"BFCO_NextWinStart"),(.35,"BFCO_NextPowerWinStart"),(.5,"MCO_WinOpen"),(.55,"MCO_PowerWinOpen"));
        var light=AnimationClip.Queue([("BFCO_Attack2.hkx",windows),("b",Clip(1))],0,"MCO_WinOpen");var power=AnimationClip.Queue([("BFCO_Attack2.hkx",windows),("b",Clip(1))],0,"MCO_PowerWinOpen");
        Assert(Near(light.Segments![0].HandOver,.3)&&light.Segments[0].Window=="BFCO_NextWinStart"&&Near(power.Segments![0].HandOver,.35)&&power.Segments[0].Window=="BFCO_NextPowerWinStart","A queued BFCO clip hands over at BFCO_NextWinStart or BFCO_NextPowerWinStart, not at an MCO window it also carries");
        var mcoOnly=Clip(1,(.5,"MCO_WinOpen"));
        Assert(Bfco.Window("BFCO_Attack1.hkx",mcoOnly.Events,"MCO_WinOpen") is ("MCO_WinOpen",[.5])&&Bfco.Window("mco_attack1.hkx",mcoOnly.Events,"MCO_WinOpen") is ("MCO_WinOpen",[.5])&&Bfco.Window("mco_attack1.hkx",mcoOnly.Events,"MCO_PowerWinOpen").Annotation==null,
            "A BFCO clip with only MCO windows hands over at those; a clip without the chosen window plays to its end");
        Assert(Bfco.Next(windows.Events,false) is (3,0)&&Bfco.Next(windows.Events,true) is (2,0)&&Bfco.Next(mcoOnly.Events,false)==null,"The next clip number is read from the BFCO_NextIsAttackN and BFCO_NextIsPowerAttackN names");
        (double,string)[] source=[(0,"BFCO_NextIsAttack2"),(0,"BFCO_NextIsPowerAttack2"),(.2,"HitFrame"),(.4,"BFCO_NextWinStart"),(.4,"BFCO_NextPowerWinStart"),(.7,"BFCO_DIY_EndLoop"),(.8,"BFCO_DIY_recovery"),(.9,"attackStop")];
        var rows=source.Select(r=>new AnnotationRow(r.Item1,r.Item2,true,r.Item2)).ToList();
        string At(double t)=>string.Join(",",AnnotationActivity.ActiveAt(rows,t,null).OrderBy(i=>i));
        Assert(At(.3)=="0,1,2"&&At(.5)=="0,1,3,4","BFCO's next-attack annotations stay active while the buffer is open, and the windows from BFCO_NextWinStart and BFCO_NextPowerWinStart");
        Assert(At(.75)=="5"&&At(.85)=="6"&&At(.95)=="6,7","BFCO_DIY_EndLoop ends the next-attack and window annotations together; BFCO_DIY_recovery lasts to the end of the clip");
        var open=rows.Where(r=>r.Text!="BFCO_DIY_EndLoop").ToList();
        Assert(string.Join(",",AnnotationActivity.ActiveAt(open,.95,null).OrderBy(i=>i))=="0,1,3,4,5,6","Without BFCO_DIY_EndLoop the buffer and windows stay open to the end of the clip");
        string notes=Bfco.Describe("BFCO_Attack2.hkx",Clip(1,(0,"BFCO_NextIsAttack3"),(.3,"BFCO_NextWinStart"),(.35,"BFCO_NextPowerWinStart"),(.8,"BFCO_DIY_Rcovery")));
        Assert(notes.Contains("Next light attack: BFCO_Attack3.hkx")&&notes.Contains("BFCO_NextWinStart, 0.300 s")&&notes.Contains("BFCO_NextPowerWinStart at 0.350 s has no BFCO_NextIsPowerAttackN")&&notes.Contains("BFCO_DIY_Rcovery at 0.800 s is not an event BFCO registers")&&notes.Contains("stay open to the end of the clip"),
            "The Annotations tab names the next BFCO clip and warns about a window with no next-attack annotation and about the BFCO_DIY_Rcovery misspelling");
        Assert(Bfco.Describe("mco_attack1.hkx",mcoOnly)==""&&Bfco.Describe("BFCO_Attack9.hkx",Clip(1)).Contains("cannot chain"),"A clip that is not BFCO gets no BFCO notes; a BFCO clip with no next-attack annotation is said not to chain");
    }
    static void SpeedMapTests(Action<bool,string> Assert)
    {
        static bool Near(double a,double b)=>Math.Abs(a-b)<1e-9;
        var read=AnimationClip.ReadSpeedChanges([new(.4,"PIE.@SGVF|MCO_AttackSpeed|2.0",0),new(0," PIE.@SGVF|MCO_AttackSpeed|0.5",0),new(.2,"PIE.@SGVF|MCO_nextattack|2",0),new(.8,"PIE.@SGVF|MCO_AttackSpeed|1",0)]);
        Assert(read.Count==3&&read[0]==(0,.5)&&read[1]==(.4,2)&&read[2]==(.8,1),"Every MCO_AttackSpeed annotation is read, 1.0 included, in time order; other variables are not");
        Assert(AnimationClip.ReadSpeedChanges([new(0,"PIE.@SGVF|BFCO_AttackSpeed|0.9",0)]).Count==0&&AnimationClip.ReadSpeedChanges([new(0,"PIE.@SGVF|BFCO_AttackSpeed|0.9",0)],"BFCO_AttackSpeed").Count==1,"BFCO_AttackSpeed is kept apart from MCO_AttackSpeed");
        var map=new SpeedMap(read,1,1.0);
        // 0..0.4 at 0.5x = 0.8 s, 0.4..0.8 at 2x = 0.2 s, 0.8..1.0 at 1x = 0.2 s.
        Assert(Near(map.ToPlay(1.0),1.2)&&Near(map.ToPlay(.4),.8)&&Near(map.ToClip(.9),.6)&&Near(map.ToClip(1.1),.9)&&Near(map.SpeedAt(.5),2),"A clip with three speed changes maps clip time to playback time piece by piece");
        var a=new AnimationClip{Duration=1,SpeedChanges=read.Take(2).ToList()};var b=new AnimationClip{Duration=.5};
        var q=AnimationClip.Queue([("a",a),("b",b)],0);
        Assert(Near(q.Segments![0].Length,.8+.6/2)&&Near(q.Segments[1].Speed.StartSpeed,2)&&Near(q.Segments[1].Length,.25)&&Near(q.Duration,1.35),"The next queued clip starts at the speed the previous one ended on (2x carries into a clip with no annotation)");
        var w=new AnimationClip{Duration=1,Events=[new(.3,"MCO_WinOpen",0),new(.3,"MCO_WinOpen",1),new(.7,"MCO_WinOpen",0)]};
        Assert(w.AnnotationTimes("mco_winopen") is [.3,.7],"Window annotations are listed by distinct time; a copy on a second track at the same time counts once");
        var first=AnimationClip.Queue([("w",w),("b",b)],0,"MCO_WinOpen");var later=AnimationClip.Queue([("w",w),("b",b)],0,"MCO_WinOpen",windowPicks:[1]);
        Assert(Near(first.Segments![0].HandOver,.3)&&Near(later.Segments![0].HandOver,.7)&&Near(later.Segments[1].Start,.7)&&Near(AnimationClip.Queue([("w",w),("b",b)],0,"MCO_WinOpen",windowPicks:[9]).Segments![0].HandOver,.7),"A queued clip hands over at the chosen window occurrence (the first by default; an out-of-range choice takes the last)");
        var single=AnimationClip.Timeline("a",a);
        Assert(single is {Queued:false,Segments.Count:1}&&Near(single.Duration,1.1)&&ReferenceEquals(AnimationClip.Timeline("b",b),b),"A single clip with speed changes becomes a one-clip timeline; a clip without them stays itself");
    }
    // Developer regression tests need the source fixtures; never infer writable paths
    // from a fixed number of parents (portable and build layouts differ).
    internal static string VerificationProjectRoot()
    {
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory!=null;directory=directory.Parent)
            if(File.Exists(Path.Combine(directory.FullName,"SSEAnimationPreviewer.sln")))return directory.FullName;
        throw new InvalidOperationException("Particle regression tests require the previewer source workspace. Normal preview playback does not require it.");
    }
    public static void Run()
    {
        string projectRoot=VerificationProjectRoot();
        var lines=new List<string>();
        void Assert(bool ok,string label){lines.Add((ok?"PASS ":"FAIL ")+label);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-tests.txt"),lines);if(!ok)throw new Exception(label);}
        void Skip(string label){lines.Add("SKIP "+label);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-tests.txt"),lines);}
        SelfTest.Skip=Skip;
        ParticleEmitterSamplingTests.Run(Assert);
        ParticleFalloff.Test(Assert);
        ParticleLighting.Test(Assert);
        SpeedMapTests(Assert);
        BfcoTests(Assert);
        Assert(ParticleNif.CellRect(new(.125f,.125f,.25f,.125f))==new System.Numerics.Vector4(.125f,.25f,.25f,.375f)&&ParticleNif.CellRect(new(0,1,0,1))==new System.Numerics.Vector4(0,0,1,1),"A sub-texture cell (u, width, v, height) maps to the corners (u, v, u+width, v+height)");
        AnnotationActivityTests(Assert);
        var equip=new AnimationClip{WeaponEvents=AnimationClip.ReadWeaponEvents([new(.3,"weaponDraw",0),new(.8,"FootScuffLeft",0)])};
        Assert(equip.WeaponSheathedAt(0)==true&&equip.WeaponSheathedAt(.29)==true&&equip.WeaponSheathedAt(.3)==false&&equip.WeaponSheathedAt(5)==false,"weaponDraw: sheathed before the annotation, drawn from its time on");
        var unequip=new AnimationClip{WeaponEvents=AnimationClip.ReadWeaponEvents([new(.97,"WeaponSheathe ",0)])};
        Assert(unequip.WeaponSheathedAt(.5)==false&&unequip.WeaponSheathedAt(.97)==true,"weaponSheathe: drawn before the annotation, sheathed after; name match ignores case and padding");
        var both=new AnimationClip{WeaponEvents=AnimationClip.ReadWeaponEvents([new(1.5,"weaponSheathe",0),new(.4,"weaponDraw",0)])};
        Assert(both.WeaponSheathedAt(0)==true&&both.WeaponSheathedAt(1)==false&&both.WeaponSheathedAt(2)==true,"A clip with both annotations follows them in time order");
        Assert(new AnimationClip{WeaponEvents=AnimationClip.ReadWeaponEvents([new(0,"weaponSwing",0)])}.WeaponSheathedAt(1)==null,"Clips without either annotation leave the weapon to the manual toggle");
        Assert(ParticleRenderer.BaseColor(new BSLightingShaderProperty{Alpha=.25f}).W==.25f,"Lighting-shader particles use the shader Alpha field as opacity");
        Assert(ParticleRenderer.BaseColor(new BSEffectShaderProperty{BaseColor=new Color4{R=1,G=.5f,B=0,A=.5f}}).W==.5f,"Effect-shader particles use BaseColor alpha as opacity");
        PlaybackClockTests.Run(Assert);
        ProjectileVisuals.Test(Assert);
        NamedNodeAttachmentTests.Run(Assert);
        MultiTargetEmitterTests.Run(Assert);
        AddonArtworkTests.Run(Assert);
        ImpactEffectTests.Run(Assert);
        MultiEmitterTests.Run(Assert);
        SkinnedEmitterTests.Run(Assert);
        Assert(!ParticleOrientation.AlongVelocity(256)&&Enumerable.Range(1,255).All(ParticleOrientation.AlongVelocity),"AE orientation uses any nonzero low byte, including nifxml's initial-rotation bit");
        Assert(!ParticleOrientation.SpeedAspect(255)&&Enumerable.Range(1,255).All(b=>ParticleOrientation.SpeedAspect(b<<8)),"AE speed-to-aspect uses any nonzero high byte independently of orientation");
        var aligned=ParticleOrientation.Axes(Vector3.UnitX,Vector3.UnitY,3,4,.7f,2,.5f,2);
        Assert(Vector3.Distance(aligned.Up,new(2.4f,3.2f,0))<1e-5f&&Vector3.Distance(aligned.Right,new(1.6f,-1.2f,0))<1e-5f,"Velocity-oriented billboard follows projected direction with authored width/aspect and overrides spin");
        var zeroProjection=ParticleOrientation.Axes(Vector3.UnitX,Vector3.UnitY,0,0,.7f,2,.5f,1);
        Assert(zeroProjection.Right==Vector3.Zero&&zeroProjection.Up==Vector3.Zero,"Zero projected velocity collapses a velocity-oriented particle instead of showing an arbitrary fallback card");
        var tiny=ParticleOrientation.Axes(Vector3.UnitX,Vector3.UnitY,1e-8f,0,0,2,1,2);
        Assert(Vector3.Distance(tiny.Up,new(2,0,0))<1e-5f,"Small finite projected velocities still orient rather than switching at an artificial epsilon");
        var unaligned=ParticleOrientation.Axes(Vector3.UnitX,Vector3.UnitY,3,4,0,2,.5f,0);
        Assert(unaligned.Right==new Vector3(2,0,0)&&unaligned.Up==new Vector3(0,4,0),"Disabled velocity orientation retains authored billboard axes and aspect");
        foreach(int flags in new[]{0,1,2,256,512}){
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateOrientation(Path.Combine(projectRoot,"particle-fixtures"),flags)));
            var ps=new ParticleNif(n,n.Blocks.OfType<NiParticleSystem>().Single());
            Assert(Convert.ToInt32(((NiPSysData)ps.Data!).AspectFlags)==flags&&Math.Abs(ps.AspectAtSpeed(50)-(flags>=256?.5f:.2f))<1e-6f,"Saved orientation fixture preserves byte flags and aspect behavior: "+flags);
        }
        ParticleStripTests.Run(Assert);
        RefractionFade.Tests(Assert);
        ScriptAttachmentTests.Run(Assert);
        RemoteCastTests.Run(Assert);
        ReferenceEffectTests.Run(Assert);
        ReferenceTimelineTests.Run(Assert);
        Assert(ParticleDistanceFade.At(0,0,0,0,0)==0&&ParticleDistanceFade.At(1,0,0,0,0)==1,"Default distance fade preserves visibility except at the camera position");
        Assert(ParticleDistanceFade.At(25,300,200,100,50)==0&&ParticleDistanceFade.At(50,300,200,100,50)==0&&ParticleDistanceFade.At(75,300,200,100,50)==.5f,"Near distance fade suppresses the inner range then rises linearly");
        Assert(ParticleDistanceFade.At(100,300,200,100,50)==1&&ParticleDistanceFade.At(150,300,200,100,50)==1&&ParticleDistanceFade.At(200,300,200,100,50)==1,"Authored distance fade retains full alpha across the middle range");
        Assert(ParticleDistanceFade.At(250,300,200,100,50)==.5f&&ParticleDistanceFade.At(300,300,200,100,50)==0&&ParticleDistanceFade.At(350,300,200,100,50)==0,"Far distance fade falls linearly and vanishes at the outer cutoff");
        Assert(ParticleDistanceFade.At(500,200,0,0,0)==1,"Zero FarEnd disables native far fading regardless of FarBegin");
        Assert(ParticleDistanceFade.At(199,200,200,50,50)==1&&ParticleDistanceFade.At(200,200,200,50,50)==0,"Coincident fade distances preserve native hard cutoffs without dividing by zero");
        Assert(ParticleVertexColor.Channel(.5f*ParticleDistanceFade.At(75,300,200,100,50))==63,"Distance fade multiplies particle alpha before eight-bit packing");
        Assert(ParticleVertexColor.Channel(.5f)==127&&ParticleVertexColor.Channel(1)==255&&ParticleVertexColor.Channel(0)==0,"Native particle colors truncate to eight bits rather than rounding");
        Assert(ParticleVertexColor.Channel(1.5f)==126&&ParticleVertexColor.Channel(-.5f)==129,"Native particle color packing retains the low byte for finite out-of-range channels");
        Assert(ParticleVertexColor.Channel(float.NaN)==0&&ParticleVertexColor.Channel(float.PositiveInfinity)==0&&ParticleVertexColor.Channel(float.MaxValue)==0,"Invalid native particle color conversions produce the zero low byte");
        Assert(ParticleVertexColor.Normalize(new(.5f,.25f,.75f,.5f))==new Vector4(127/255f,63/255f,191/255f,127/255f),"Normalized particle vertex channels preserve native packed values for shader and palette inputs");
        Assert(BitConverter.SingleToInt32Bits(ProjectileMotion.GravityUnits)==0x442B983C,"Projectile acceleration uses the exact AE gravity constant");
        var fall=ProjectileMotion.Displacement(100,1,1,1);
        Assert(Math.Abs(fall.Y-100)<1e-6&&Math.Abs(fall.Z+343.18933)<.001,"One-second missile fall uses AE world units instead of centimeter gravity");
        Assert(ProjectileMotion.Displacement(100,0,2,1)==fall&&ProjectileMotion.Displacement(100,9,2,1)==fall,"Lobber type forces gravity factor one independent of the authored value");
        Assert(ProjectileMotion.Displacement(100,0,1,2)==new Vector3(0,200,0),"Zero-gravity missiles retain straight constant-speed travel");
        Assert(Math.Abs(ProjectileMotion.RangeTime(100,0,1,200)-2)<1e-9,"Straight projectile range keeps its exact speed-derived duration");
        double curvedTime=ProjectileMotion.RangeTime(100,1,1,200);
        Assert(curvedTime<2&&Math.Abs(ProjectileMotion.Distance(100,1,1,curvedTime)-200)<1e-6,"Curved projectile range expires by arc length, not horizontal travel");
        Assert(Math.Abs(ProjectileMotion.RangeTime(0,1,1,200)-Math.Sqrt(400/ProjectileMotion.GravityUnits))<1e-6,"Stationary falling projectiles can exhaust range through gravity alone");
        Assert(ProjectileMotion.RangeTime(100,1,1,0)==0&&double.IsPositiveInfinity(ProjectileMotion.RangeTime(0,0,1,200)),"Zero range and stationary zero-gravity cases are handled explicitly");
        Assert(ProjectileMotion.Displacement(100,1,1,-1)==Vector3.Zero,"Projectile motion does not advance before spawn");
        float elevation=MathF.PI/4;
        var upward=ProjectileMotion.Displacement(100,0,1,1,elevation);
        Assert(Math.Abs(upward.Y-70.710678)<.001&&Math.Abs(upward.Z-70.710678)<.001,"Positive elevation splits launch speed into forward and upward components");
        var angledFall=ProjectileMotion.Displacement(100,1,1,1,elevation);
        Assert(Math.Abs(angledFall.Y-upward.Y)<.001&&Math.Abs(angledFall.Z-upward.Z+ProjectileMotion.GravityUnits*.5)<.001,"Launch elevation never tilts world gravity");
        float nearlyVertical=89*MathF.PI/180;
        double angledTime=ProjectileMotion.RangeTime(100,.01f,1,100,nearlyVertical);
        Assert(angledTime>1&&Math.Abs(ProjectileMotion.Distance(100,.01f,1,angledTime,nearlyVertical)-100)<1e-6,"An upward shot can need longer than range divided by launch speed");
        double apex=100*Math.Sin(nearlyVertical)/ProjectileMotion.GravityUnits;
        Assert(ProjectileMotion.Distance(100,1,1,apex+.3,nearlyVertical)>ProjectileMotion.Distance(100,1,1,apex,nearlyVertical),"Travel distance keeps growing after the projectile passes its apex");
        Assert(Math.Abs(ProjectileMotion.Distance(100,1e-20f,1,1,elevation)-100)<1e-6,"Tiny gravity avoids cancellation in the angled distance integral");
        double trapezoid=0,previousVelocity=100,dt=.0001;
        for(int step=1;step<=10000;step++){double at=step*dt;double vz=100*Math.Sin(elevation)-ProjectileMotion.GravityUnits*at;double velocity=Math.Sqrt(5000+vz*vz);trapezoid+=(previousVelocity+velocity)*.5*dt;previousVelocity=velocity;}
        Assert(Math.Abs(ProjectileMotion.Distance(100,1,1,1,elevation)-trapezoid)<1e-4,"Angled arc length matches independent velocity integration");
        var turning=new EffectCue(0,"fixture",100,1,2,1,Vector3.Zero,"pitch",ProjectileType:1,Elevation:elevation,Heading:.7f);
        double pitchTime=.2;
        var forward=Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY,turning.LocalPlacement(pitchTime)));
        var expectedVelocity=Vector3.Normalize(Vector3.Transform(new Vector3(0,100*MathF.Cos(elevation),100*MathF.Sin(elevation)-ProjectileMotion.GravityUnits*(float)pitchTime),Matrix4x4.CreateRotationZ(.7f)));
        Assert(Vector3.Distance(forward,expectedVelocity)<1e-5,"Gravity projectile model turns with its descending trajectory while retaining heading");
        Assert(ProjectileMotion.ModelElevation(100,1,1,100,elevation)==-ProjectileMotion.PitchLimit,"Projectile model pitch stops at the exact native 89-degree clamp");
        Assert(ProjectileMotion.ModelElevation(100,0,1,10,elevation)==elevation&&ProjectileMotion.ModelElevation(100,1,16,10,elevation)==elevation,"Zero-gravity and unsupported beam types retain authored launch orientation");
        Assert(ProjectileMotion.ModelElevation(100,0,2,.2,elevation)==ProjectileMotion.ModelElevation(100,1,1,.2,elevation),"Lobber pitch uses forced gravity just like its trajectory");
        Assert(ProjectileMotion.ModelElevation(100,-1,1,.2,0)<0,"Negative gravity preserves the inspected unsigned downward pitch-turn convention");
        Assert(ProjectileMotion.ModelElevation(0,1,1,.2,elevation)==elevation&&ProjectileMotion.ModelElevation(100,1,1,-1,elevation)==elevation,"Degenerate launch and pre-spawn queries preserve finite launch orientation");
        var replayPlacement=turning.LocalPlacement(.5);_=turning.LocalPlacement(.1);
        Assert(turning.LocalPlacement(.5)==replayPlacement&&turning.LocalPlacement(3)==turning.LocalPlacement(2),"Projectile pitch rewinds deterministically and stops at cue lifetime");
        var projectileRecords=new Records();var projectileAssets=new Assets();
        projectileAssets.External[@"meshes\previewer\fountain.nif"]="record-only fixture";
        GameRecord ProjectileRecord(string kind,uint id){var r=new GameRecord{Kind=kind,Id=id,Plugin="motion.esp",Key=$"motion.esp|{id:X6}"};projectileRecords.All[r.Key]=r;return r;}
        ProjectileRecord("SPEL",1).Fields.Add(new("EFID",BitConverter.GetBytes(2u)));
        var effectData=new byte[152];BitConverter.GetBytes(3u).CopyTo(effectData,72);ProjectileRecord("MGEF",2).Fields.Add(new("DATA",effectData));
        var projectileRecord=ProjectileRecord("PROJ",3);var projectileData=new byte[84];
        BitConverter.GetBytes((ushort)1).CopyTo(projectileData,2);BitConverter.GetBytes(1f).CopyTo(projectileData,4);
        BitConverter.GetBytes(100f).CopyTo(projectileData,8);BitConverter.GetBytes(200f).CopyTo(projectileData,12);
        projectileRecord.Fields.Add(new("DATA",projectileData));projectileRecord.Fields.Add(new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\fountain.nif")));
        var projectileEffects=new Effects(projectileAssets,projectileRecords);
        var resolvedProjectile=projectileEffects.Preview(new("Cast","@CAST|1|motion.esp","fixture"),0).Single();
        Assert(Math.Abs(resolvedProjectile.Lifetime-curvedTime)<1e-7&&resolvedProjectile.ProjectileType==1,"ESP projectile resolution uses the curved-path range duration");
        projectileEffects.ProjectileElevation=elevation;
        var angledCue=projectileEffects.Preview(new("Cast","@CAST|1|motion.esp","fixture"),0).Single();
        Assert(angledCue.Elevation==elevation&&Math.Abs(ProjectileMotion.Distance(100,1,1,angledCue.Lifetime,elevation)-200)<1e-6,"Resolved projectile cues retain chosen elevation and its matching range time");
        projectileEffects.ProjectileElevation=0;
        BitConverter.GetBytes((ushort)2).CopyTo(projectileData,2);BitConverter.GetBytes(0f).CopyTo(projectileData,4);
        Assert(Math.Abs(projectileEffects.Preview(new("Cast","@CAST|1|motion.esp","fixture"),0).Single().Lifetime-curvedTime)<1e-7,"ESP lobber range includes its forced native gravity");
        ParticleSimulation Make()=>new(new(){Capacity=1000,Life=2,Speed=10,Dimensions=new(10,20,30)}){BirthRate=_=>60};
        var direct=Make();direct.Seek(1);
        Assert(direct.Particles.Count==60,"60 particles born in one second at 60/second");
        var stepped=Make();for(int i=0;i<=60;i++)stepped.Seek(i/60d);
        Assert(State(direct)==State(stepped),"60 FPS and direct seeking have identical state");
        var thirty=Make();for(int i=0;i<=30;i++)thirty.Seek(i/30d);
        Assert(State(direct)==State(thirty),"30 and 60 FPS have identical state");
        var expected=State(direct);direct.Seek(.25);direct.Seek(1);
        Assert(State(direct)==expected,"Backward seek then replay reproduces exact state");
        direct.Seek(4);Assert(direct.Particles.Count is >=119 and <=121,"Lifetime removes expired particles");
        var capped=new ParticleSimulation(new(){Capacity=7,Life=10}){BirthRate=_=>1000};capped.Seek(1);
        Assert(capped.Particles.Count==7&&capped.CapacityDrops>0,"NIF capacity is enforced");
        var gated=Make();gated.EmitterActive=t=>t<.5;gated.Seek(1);
        Assert(gated.Particles.Count==30,"Emitter visibility gates births without killing live particles");
        var moving=Make();moving.EmitterTransform=t=>Matrix4x4.CreateTranslation((float)t*100,0,0);moving.Seek(1);
        Assert(moving.Particles.First().Position.X<15&&moving.Particles.Last().Position.X>90,"World-space emission retains historical emitter positions");
        Assert(Assets.GamePath(@"C:\Users\anchor\Desktop\export\textures\effects\gradients\fire.dds")==@"textures\effects\gradients\fire.dds","Author-machine texture paths resolve through mounted Data");
        Assert(Math.Abs(ParticleSimulation.AdvanceRotation(6,2,.5f)-(7-MathF.Tau))<1e-6f,"Native rotation wraps positive angles above one turn");
        Assert(ParticleSimulation.AdvanceRotation(0,40,1)==0&&ParticleSimulation.AdvanceRotation(0,-40,1)==-40,
            "Native rotation resets large positive excursions and preserves negative angles");
        var spinGate=new ParticleSimulation(new()){BirthRate=_=>0,SpinEnabled=_=>false};
        spinGate.Particles.Add(new(){Life=10,Angle=.3f,Spin=2});spinGate.Seek(.25);
        Assert(spinGate.Particles.Single().Angle==.3f,"Disabled rotation leaves an existing particle angle unchanged");
        spinGate.SpinEnabled=_=>true;spinGate.Seek(.5);
        Assert(Math.Abs(spinGate.Particles.Single().Angle-.8f)<1e-5f,"Re-enabling rotation resumes without accumulating disabled time");
        Assert(ParticleAspect.AtSpeed(1,.25f,10,100,0,true)==1&&ParticleAspect.AtSpeed(1,.25f,10,100,200,true)==.25f,
            "Speed-to-aspect clamps below and above the authored speed limits");
        Assert(ParticleAspect.AtSpeed(1,.25f,0,100,50,true)==.625f,"Speed-to-aspect interpolates authored ratios at projected speed");
        Assert(ParticleAspect.AtSpeed(1,.25f,0,100,50,false)==1,"Disabled speed-to-aspect retains the base ratio");
        Assert(ParticleAspect.AtSpeed(1,.25f,10,10,50,true)==1,"Equal speed limits use a finite base-aspect fallback");
        Assert(ParticleAspect.AtSpeed(1,.25f,100,10,0,true)==1&&ParticleAspect.AtSpeed(1,.25f,100,10,20,true)==.25f,
            "Reversed speed limits follow the native branch ordering");
        var directionDraws=new Queue<float>([0,32767f/32768f,0]);
        Assert(Vector3.Distance(ParticleMesh.RandomVelocityDirection(()=>directionDraws.Dequeue()),Vector3.Normalize(new Vector3(-1,1,-1)))<1e-6f,
            "AE random mesh velocity normalizes independent signed component draws");
        var directionRng=new ParticleSimulation(new());double fourthMoment=0;bool unitDirections=true;
        for(int sample=0;sample<20000;sample++){
            var direction=ParticleMesh.RandomVelocityDirection(directionRng.Random01);
            unitDirections&=Math.Abs(direction.LengthSquared()-1)<1e-5;fourthMoment+=Math.Pow(direction.X,4);
        }
        Assert(unitDirections&&fourthMoment/20000 is >.175 and <.187,
            "Random mesh velocities retain the native cube-normalized distribution rather than a uniform sphere");
        var triangle=new ParticleMesh([new(0,0,0),new(6,0,0),new(0,6,0)],[Vector3.UnitZ,Vector3.UnitZ,Vector3.UnitZ],[new(0,1,2)]);
        var rng=new ParticleSimulation(new(),7);
        var center=triangle.Sample(1,rng.Random01);
        Assert(Vector3.Distance(center.Position,new(2,2,0))<1e-6f,"Mesh face-center emission uses the actual triangle centroid");
        Assert(center.Normal==Vector3.UnitZ,"Mesh emission interpolates authored normals");
        Vector3 mean=Vector3.Zero;bool inside=true,onEdges=true,onVertices=true;
        for(int i=0;i<10000;i++){
            var p=triangle.Sample(3,rng.Random01).Position;mean+=p;
            inside&=p.X>=0&&p.Y>=0&&p.X+p.Y<=6.00001f&&p.Z==0;
            var e=triangle.Sample(4,rng.Random01).Position;onEdges&=e.X==0||e.Y==0||Math.Abs(e.X+e.Y-6)<.00001f;
            var v=triangle.Sample(0,rng.Random01).Position;onVertices&=v==Vector3.Zero||v==new Vector3(6,0,0)||v==new Vector3(0,6,0);
        }
        Assert(inside&&Vector3.Distance(mean/10000,new(4,2,0))<.06f,"AE face-surface mode follows the native second-to-third vertex span distribution");
        Assert(Math.Abs(ParticleMesh.SurfaceFraction(.25f,.5f)-.24957679f)<1e-7f,"AE surface coefficient reproduces the native inverse-square-root iteration");
        Assert(ParticleMesh.SurfaceFraction(0,1)==0,"Zero surface random input keeps the second vertex without NaN");
        var sloped=new ParticleMesh([Vector3.Zero,new(6,0,0),new(0,6,0)],[Vector3.UnitX,Vector3.UnitY,Vector3.UnitZ],[new(0,1,2)]);
        Assert(Vector3.Distance(sloped.Sample(3,rng.Random01).Normal,Vector3.Normalize(Vector3.One))<1e-6f,"Surface emission uses averaged vertex normals independent of the sampled position");
        Assert(onEdges,"Mesh edge-surface samples remain on real edges");
        Assert(onVertices,"Mesh vertex emission selects only real vertices");
        Assert(!new ParticleMesh([],[],[]).Valid(3),"Missing emitter geometry is rejected");
        Assert(ParticleSimulation.BirthVariation(10,4,0)==8&&ParticleSimulation.BirthVariation(10,4,1)==12,
            "Emitter speed/lifespan variation spans half the authored width on either side");
        var birthVariation=new ParticleSimulation(new(){Capacity=1000,Speed=100,SpeedVariation=40,Life=10,LifeVariation=4}){
            BirthRate=_=>60000,EmitterActive=t=>t<ParticleSimulation.Step};
        birthVariation.Seek(ParticleSimulation.Step);
        Assert(birthVariation.Particles.Count==500&&birthVariation.Particles.All(p=>p.Velocity.Z>=80&&p.Velocity.Z<=120&&p.Life>=8&&p.Life<=12),
            "Actual emitted particles respect native speed and lifespan variation bounds");
        Assert(birthVariation.Particles.Min(p=>p.Velocity.Z)<81&&birthVariation.Particles.Max(p=>p.Velocity.Z)>119
            &&birthVariation.Particles.Min(p=>p.Life)<8.1&&birthVariation.Particles.Max(p=>p.Life)>11.9,
            "Emitter variation reaches both ends without collapsing its distribution");
        string variationState=State(birthVariation);birthVariation.Seek(0);birthVariation.Seek(ParticleSimulation.Step);
        Assert(State(birthVariation)==variationState,"Varied emitter births reproduce after rewinding");
        var tinyLife=new ParticleSimulation(new(){Life=-1}){BirthRate=_=>120,AgeEnabled=_=>false};tinyLife.Seek(ParticleSimulation.Step);
        Assert(tinyLife.Particles.Single().Life==.000001f,"Nonpositive emitter lifespan uses AE's one-microsecond floor");
        ParticleSimulation Spawner(int capacity=100)=>new(new(){Capacity=capacity,Life=.05f,Speed=20}){
            BirthRate=_=>120,EmitterActive=t=>t<ParticleSimulation.Step,
            DeathSpawn=(_,_)=>new(){Generations=2,Minimum=2,Maximum=2,Life=.1f}
        };
        var spawning=Spawner();spawning.Seek(.08);
        Assert(spawning.Particles.Count==2&&spawning.Particles.All(p=>p.Generation==1)&&spawning.TotalBorn==3,"Death creates the authored first child generation");
        Assert(spawning.Particles.All(p=>p.Position.Z>1&&p.Velocity.Z==20),"Children inherit the parent's death position and velocity");
        spawning.Seek(.2);
        Assert(spawning.Particles.Count==4&&spawning.Particles.All(p=>p.Generation==2)&&spawning.TotalBorn==7,"Second generation respects authored child count");
        string spawnedState=State(spawning);spawning.Seek(.08);spawning.Seek(.2);
        Assert(State(spawning)==spawnedState,"Spawn generations reproduce exactly after backward seeking");
        var steppedSpawn=Spawner();for(int i=0;i<=12;i++)steppedSpawn.Seek(i/60d);
        Assert(State(steppedSpawn)==spawnedState,"Child emission is independent of presentation frame rate");
        spawning.Seek(.4);Assert(spawning.Particles.Count==0&&spawning.TotalBorn==7,"Maximum spawn generation terminates reproduction");
        var cappedSpawn=Spawner(3);cappedSpawn.Seek(.2);
        Assert(cappedSpawn.Particles.Count==3&&cappedSpawn.CapacityDrops==1,"Spawned children share the authored particle capacity");
        var disabledSpawn=Spawner();disabledSpawn.DeathSpawn=(_,_)=>new(){Generations=2,Probability=0,Minimum=2,Maximum=2,Life=.1f};disabledSpawn.Seek(.2);
        Assert(disabledSpawn.TotalBorn==1&&disabledSpawn.Particles.Count==0,"Zero spawn probability creates no descendants");
        var inactiveAge=Spawner();inactiveAge.AgeEnabled=_=>false;inactiveAge.Seek(.3);
        Assert(inactiveAge.Particles.Count==1&&inactiveAge.Particles[0].Age==0&&inactiveAge.Particles[0].Position.Z>5,"Inactive age modifier preserves particles while position still updates");
        var down=-Vector3.UnitZ;
        Assert(ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,down,10,0,false)==new Vector3(0,0,-16),"AE gravity applies the native 1.6 strength multiplier");
        var planeGravity=ParticleForces.Gravity(new(100,0,10),Matrix4x4.Identity,down,10,.1f,false);
        Assert(Math.Abs(planeGravity.Z+16/MathF.E)<1e-5f,"Planar gravity decay uses perpendicular distance, ignoring lateral displacement");
        Assert(ParticleForces.Gravity(new(100,0,-10),Matrix4x4.Identity,down,10,.1f,false)==planeGravity,"Planar decay is symmetric around the gravity plane");
        var sphereGravity=ParticleForces.Gravity(new(3,4,0),Matrix4x4.Identity,down,10,.2f,true);
        Assert(Vector3.Distance(sphereGravity,new Vector3(-.6f,-.8f,0)*(16/MathF.E))<1e-5f,"Spherical gravity attracts toward its center with exponential radial decay");
        Assert(ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,down,10,.2f,true)==Vector3.Zero,"Spherical gravity at its center has no undefined direction");
        Assert(ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,Vector3.Zero,10,0,false)==Vector3.Zero,"Degenerate planar gravity axis produces no force");
        Assert(ParticleForces.Gravity(new(3,4,0),Matrix4x4.Identity,down,-10,.2f,true)==-sphereGravity,"Negative native gravity strength repels particles");
        var noiseForce=ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,down,0,0,false,2,.5f,new(-1,0,1));
        Assert(noiseForce==new Vector3(-500,0,500),"Native turbulence contributes independent signed acceleration on all axes");
        Assert(ParticleForces.Gravity(new(0,0,100),Matrix4x4.Identity,down,0,2,false,2,.5f,new(-1,0,1))==noiseForce,
            "Gravity decay does not attenuate native turbulence");
        Assert(ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,Vector3.Zero,0,0,false,2,.5f,new(-1,0,1))==noiseForce,
            "A zero gravity axis still permits native random acceleration");
        var radialTurb=ParticleForces.Gravity(new(3,4,0),Matrix4x4.Identity,down,10,0,true,1,0);
        Assert(Vector3.Distance(radialTurb,new Vector3(-.6f,-.8f,0)*16)<1e-5f,"Spherical turbulence without decay retains radial attraction");
        var decayedTurb=ParticleForces.Gravity(new(3,4,0),Matrix4x4.Identity,down,10,.2f,true,1,0);
        Assert(Vector3.Distance(decayedTurb,down*(16/MathF.E))<1e-5f,"AE spherical decay with nonzero turbulence uses the authored axis for attraction");
        Assert(ParticleForces.Gravity(Vector3.Zero,Matrix4x4.Identity,down,0,0,false,0,1,new(-1,0,1))==Vector3.Zero,
            "Zero turbulence disables random acceleration");
        var dragVelocity=new Vector3(10,20,30);
        var dragX=ParticleForces.Drag(dragVelocity,Vector3.Zero,Matrix4x4.Identity,Vector3.UnitX,.02f,10,20,.5f);
        Assert(dragX.X>0&&dragX.X<10&&dragX.Y==20&&dragX.Z==30,"Directional drag preserves both perpendicular velocity components");
        var rotatedDrag=ParticleForces.Drag(dragVelocity,new(100,0,0),Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(100,0,0),Vector3.UnitX,.02f,10,20,.5f);
        Assert(Math.Abs(rotatedDrag.X-10)<1e-5f&&rotatedDrag.Y<20&&rotatedDrag.Z==30,"Drag object translation and rotation determine the force volume and axis");
        var midwayDrag=ParticleForces.Drag(dragVelocity,new(15,0,0),Matrix4x4.Identity,Vector3.UnitX,.02f,10,20,.5f);
        Assert(midwayDrag.X>dragX.X&&midwayDrag.X<10,"Drag diminishes between full range and the outer falloff distance");
        Assert(ParticleForces.Drag(dragVelocity,new(21,0,0),Matrix4x4.Identity,Vector3.UnitX,.02f,10,20,.5f)==dragVelocity,"Particles outside the drag volume retain their velocity");
        Assert(ParticleForces.Drag(dragVelocity,Vector3.Zero,Matrix4x4.Identity,Vector3.Zero,.02f,10,20,.5f)==dragVelocity,"A zero drag axis cannot corrupt particle velocities");
        Assert(Math.Abs(dragX.X-7)<1e-6f,"AE drag removes percentage times 30 times elapsed axial velocity");
        Assert(Math.Abs(midwayDrag.X-8.5f)<1e-6f,"Native halfway falloff halves the drag fraction");
        Assert(ParticleForces.Drag(dragVelocity,Vector3.Zero,Matrix4x4.Identity,Vector3.UnitX,2,10,20,.5f)==new Vector3(0,20,30),
            "Strong drag caps at zero axial speed without reversing motion");
        Assert(ParticleForces.Drag(new(-10,20,30),Vector3.Zero,Matrix4x4.Identity,Vector3.UnitX,2,10,20,.5f)==new Vector3(0,20,30),
            "Native drag damps motion in either direction along its axis");
        string fixtureRoot=Path.Combine(projectRoot,"particle-fixtures");
        ParticleNif Inheritance(bool enabled=true){
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateInheritance(fixtureRoot,enabled)));
            return new(n,n.Blocks.OfType<NiParticleSystem>().Single());
        }
        var inherited=Inheritance();inherited.Seek(.5);
        Assert(inherited.Simulation.Particles.Count>0&&inherited.Simulation.Particles.All(p=>Math.Abs(p.Velocity.X-30)<.002f),
            "Native moving-source modifier adds half of the authored 60-unit/second translation once at birth");
        var noInheritance=Inheritance(false);noInheritance.Seek(.5);
        Assert(noInheritance.Simulation.Particles.All(p=>p.Velocity.X==0),"Inactive inheritance leaves emission velocity unchanged");
        var pausedInheritance=Inheritance();pausedInheritance.Placement=t=>Matrix4x4.CreateTranslation((float)(100*t*t),0,0);pausedInheritance.Seek(.25);
        var cachedModifier=pausedInheritance.Nif.Blocks.OfType<BSPSysInheritVelocityModifier>().Single();
        var cachedProbe=new ParticleSimulation.Particle();pausedInheritance.Simulation.Initialize!(cachedProbe,.25);
        cachedModifier.Active=false;pausedInheritance.Seek(.5);
        Assert(pausedInheritance.Simulation.Particles.Where(p=>p.Age<.24f).All(p=>Math.Abs(p.Velocity.X-cachedProbe.Velocity.X)<1e-5f),
            "Inactive inheritance retains its last cached velocity for newly born particles");
        cachedModifier.Active=true;pausedInheritance.Seek(.5);pausedInheritance.Simulation.BeforeStep!(.5);
        var resumedProbe=new ParticleSimulation.Particle();pausedInheritance.Simulation.Initialize!(resumedProbe,.5);
        double lastSample=.25-ParticleSimulation.Step;
        Assert(Math.Abs(resumedProbe.Velocity.X-(30+50*(.5+lastSample)))<.002f,
            "Reactivated inheritance averages source displacement over the full inactive interval");
        cachedModifier.Active=false;pausedInheritance.Simulation.Reset();pausedInheritance.Seek(.5);
        Assert(pausedInheritance.Simulation.Particles.All(p=>p.Velocity.X==0),"Reset clears stale inheritance velocity before replay");
        var noChance=Inheritance();noChance.Nif.Blocks.OfType<BSPSysInheritVelocityModifier>().Single().ChanceToInherit=0;noChance.Seek(.5);
        Assert(noChance.Simulation.Particles.All(p=>p.Velocity.X==0),"Zero inheritance chance affects no particles");
        var halfChance=Inheritance();halfChance.Nif.Blocks.OfType<BSPSysInheritVelocityModifier>().Single().ChanceToInherit=50;halfChance.Seek(1);
        Assert(halfChance.Simulation.Particles.Any(p=>p.Velocity.X==0)&&halfChance.Simulation.Particles.Any(p=>p.Velocity.X>29),
            "Partial inheritance chance selects particles individually");
        var varied=Inheritance();varied.Nif.Blocks.OfType<BSPSysInheritVelocityModifier>().Single().VelocityVariation=.25f;varied.Seek(.5);
        Assert(varied.Simulation.Particles.All(p=>p.Velocity.X>=22.49f&&p.Velocity.X<=37.51f)&&
            varied.Simulation.Particles.Max(p=>p.Velocity.X)-varied.Simulation.Particles.Min(p=>p.Velocity.X)>1,
            "AE relative inheritance variation gives bounded per-particle multipliers");
        var rootInherited=Inheritance();rootInherited.Placement=t=>Matrix4x4.CreateTranslation((float)t*100,0,0);rootInherited.Seek(.5);
        Assert(rootInherited.Simulation.Particles.All(p=>Math.Abs(p.Velocity.X-80)<.01f),
            "World-space inheritance includes external actor/effect placement motion");
        var localSource=Inheritance();localSource.System.WorldSpace=false;
        var localInherited=new ParticleNif(localSource.Nif,localSource.System);
        localInherited.Placement=t=>Matrix4x4.CreateRotationZ(MathF.PI/2)*Matrix4x4.CreateTranslation(0,(float)t*100,0);
        localInherited.Seek(.5);
        Assert(localInherited.Simulation.Particles.All(p=>Math.Abs(p.Velocity.Y-80)<.01f&&Math.Abs(p.Velocity.X)<.01f),
            "AE inheritance initializer adds the source world-translation velocity directly");
        inherited.Seek(.75);
        var sourceNode=inherited.Nif.Blocks.OfType<NiNode>().Single(n=>n.Name.ToString()=="Moving source");
        var sourceBefore=sourceNode.Translation;inherited.Simulation.Initialize!(new ParticleSimulation.Particle(),.3);
        Assert(Vector3.Distance(sourceNode.Translation,sourceBefore)<1e-5f,
            "Historical inheritance sampling restores the displayed source transform");
        var missingSource=Inheritance();missingSource.Nif.Blocks.OfType<BSPSysInheritVelocityModifier>().Single().InheritObject=new(-1);missingSource.Seek(.5);
        Assert(missingSource.Simulation.Particles.All(p=>p.Velocity.X==0),"Missing inheritance source does not substitute unrelated scene motion");
        var inherit30=Inheritance();var inherit60=Inheritance();
        for(int i=0;i<=30;i++)inherit30.Seek(i/30d);
        for(int i=0;i<=60;i++)inherit60.Seek(i/60d);
        Assert(State(inherit30.Simulation)==State(inherit60.Simulation),"Inherited motion is identical at 30 and 60 FPS");
        var inheritState=State(inherit30.Simulation);inherit30.Seek(.1);inherit30.Seek(1);
        Assert(State(inherit30.Simulation)==inheritState,"Inherited motion rewind restores identical probability and velocity samples");
        var bombOut=ParticleForces.Bomb(new(3,4,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,0,0);
        Assert(Vector3.Distance(bombOut,new(6,8,0))<1e-6f,"Spherical bomb force accelerates outward with authored magnitude");
        var bombIn=ParticleForces.Bomb(new(10,0,30),Matrix4x4.Identity,Vector3.UnitZ,-20,100,0,1);
        Assert(bombIn==new Vector3(-20,0,0),"Negative cylindrical bomb force pulls toward its axis without axial acceleration");
        var bombPlane=ParticleForces.Bomb(new(10,5,-3),Matrix4x4.Identity,Vector3.UnitZ,12,100,0,2);
        Assert(bombPlane==new Vector3(0,0,-12),"Planar bomb symmetry pushes away from the plane on either side");
        var bombTransform=Matrix4x4.CreateRotationY(MathF.PI/2)*Matrix4x4.CreateTranslation(15,0,0);
        var rotatedBomb=ParticleForces.Bomb(new(15,5,0),bombTransform,Vector3.UnitZ,-12,100,0,1);
        Assert(Vector3.Distance(rotatedBomb,new(0,-12,0))<1e-5f,"Bomb object transforms rotate and translate the force axis");
        var falloffHalf=ParticleForces.Bomb(new(50,0,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,1,0);
        var falloffOutside=ParticleForces.Bomb(new(110,0,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,1,0);
        Assert(falloffHalf==new Vector3(5,0,0)&&falloffOutside==Vector3.Zero,"AE linear bomb falloff decreases to zero at the configured distance");
        var expNear=ParticleForces.Bomb(new(10,0,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,2,0);
        var expFar=ParticleForces.Bomb(new(50,0,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,2,0);
        Assert(expNear.X>expFar.X&&expFar.X>0,"AE exponential bomb falloff decreases inside its bounded radius");
        Assert(ParticleForces.Bomb(Vector3.Zero,Matrix4x4.Identity,Vector3.UnitZ,10,100,0,0)==Vector3.Zero&&
            ParticleForces.Bomb(Vector3.One,Matrix4x4.Identity,Vector3.Zero,10,100,0,1)==Vector3.Zero,
            "Bomb force origins and degenerate axes do not generate nonfinite velocities");
        Assert(ParticleForces.Bomb(new(110,0,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,2,0)==Vector3.Zero,
            "AE exponential bomb force also stops outside its distance bound");
        Assert(ParticleForces.Bomb(new(10,0,110),Matrix4x4.Identity,Vector3.UnitZ,10,100,1,1)==Vector3.Zero,
            "AE bomb range cutoff uses full source distance before cylindrical projection");
        Assert(ParticleForces.InheritanceFactor(.5f,.25f,0)==.375f&&ParticleForces.InheritanceFactor(.5f,.25f,1)==.625f,
            "AE inheritance variation endpoints are relative to the multiplier");
        Assert(ParticleForces.InheritanceFactor(.5f,2,0)==0&&ParticleForces.InheritanceFactor(.5f,2,1)==1.5f,
            "AE inheritance variation clamps the lower endpoint at zero");
        Assert(ParticleForces.Bomb(new(3,4,0),Matrix4x4.Identity,Vector3.UnitZ,10,100,1,2)==new Vector3(0,0,10),
            "AE planar bomb uses its positive axis for a point exactly on the plane");
        ParticleNif BombFixture(bool enabled){
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateBomb(fixtureRoot,enabled)));
            return new(n,n.Blocks.OfType<NiParticleSystem>().Single());
        }
        var bombNative=BombFixture(true);var bombDisabled=BombFixture(false);
        bombNative.Seek(.5);bombDisabled.Seek(.5);
        float RadialSpread(ParticleNif system)=>system.Simulation.Particles.Sum(p=>p.Position.X*p.Position.X+p.Position.Y*p.Position.Y);
        Assert(bombNative.Simulation.Particles.Count>0&&RadialSpread(bombNative)<RadialSpread(bombDisabled),
            "Serialized native inward bomb narrows the actual emitted cloud compared with an inactive modifier");
        Assert(bombDisabled.Simulation.Particles.All(p=>p.Velocity.X==0&&p.Velocity.Y==0),"Inactive native bomb leaves transverse velocity unchanged");
        var bombThirty=BombFixture(true);var bombSixty=BombFixture(true);
        for(int i=0;i<=30;i++)bombThirty.Seek(i/30d);
        for(int i=0;i<=60;i++)bombSixty.Seek(i/60d);
        Assert(State(bombThirty.Simulation)==State(bombSixty.Simulation),"Bomb-force playback is identical at 30 and 60 FPS");
        var bombState=State(bombThirty.Simulation);bombThirty.Seek(.2);bombThirty.Seek(1);
        Assert(State(bombThirty.Simulation)==bombState,"Bomb-force rewind and replay reproduce identical state");
        var planeHit=ParticleCollision.Sweep(new(0,0,10),new(0,0,-10),Matrix4x4.Identity,false,0,4,4,Vector3.UnitX,Vector3.UnitY);
        Assert(planeHit is {} ph&&Math.Abs(ph.Fraction-.5f)<1e-6f&&ph.Normal==Vector3.UnitZ,"Swept planar contact catches a fast crossing at its time of impact");
        Assert(ParticleCollision.Sweep(new(3,0,10),new(3,0,-10),Matrix4x4.Identity,false,0,4,4,Vector3.UnitX,Vector3.UnitY)==null,
            "Finite planar collider rejects crossings outside its authored extent");
        var sphereHit=ParticleCollision.Sweep(new(0,0,20),new(0,0,-20),Matrix4x4.Identity,true,8,0,0,default,default);
        Assert(sphereHit is {} sh&&Math.Abs(sh.Fraction-.3f)<1e-6f,"Swept sphere catches tunnelling even when both endpoints are outside");
        var collisionFrame=Matrix4x4.CreateRotationY(MathF.PI/2)*Matrix4x4.CreateTranslation(10,0,0);
        var rotatedHit=ParticleCollision.Sweep(new(20,0,0),new(0,0,0),collisionFrame,false,0,4,4,Vector3.UnitX,Vector3.UnitY);
        Assert(rotatedHit is {} rh&&Math.Abs(rh.Fraction-.5f)<1e-6f&&rh.Normal.X>.99f,"Collider frame translation and rotation transform contact normals");
        Assert(Vector3.Distance(ParticleCollision.Reflect(new(3,4,-10),Vector3.UnitZ,.5f),new(3,4,5))<1e-6f,
            "Bounce changes normal velocity and preserves tangential motion");
        ParticleNif CollisionFixture(bool sphere=false,bool die=false,bool spawn=false){
            var file=ParticleFixture.CreateCollider(fixtureRoot,sphere,die,spawn);
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(file));
            var system=new ParticleNif(n,n.Blocks.OfType<NiParticleSystem>().Single());
            system.Simulation.EmitterActive=_=>false;
            system.Simulation.Particles.Add(new(){Position=new(0,0,10),Velocity=new(0,0,-100),Life=2,Radius=1,BaseRadius=1});
            return system;
        }
        var plane=CollisionFixture();plane.Seek(.2);
        Assert(plane.Simulation.Particles.Count==1&&Math.Abs(plane.Simulation.Particles[0].Position.Z-6.5f)<.01f&&plane.Simulation.Particles[0].Velocity.Z>0,
            "Native plane bounce integrates the remaining tick after impact");
        var sphere=CollisionFixture(sphere:true);sphere.Seek(.1);
        Assert(sphere.Simulation.Particles[0].Position.Z>8&&sphere.Simulation.Particles[0].Velocity.Z>0,"Native spherical collider reflects incoming particles");
        var killed=CollisionFixture(die:true);killed.Seek(.2);
        Assert(killed.Simulation.Particles.Count==0,"Die-on-collide removes particles before their natural expiry");
        var spawned=CollisionFixture(die:true,spawn:true);spawned.Seek(.1);
        Assert(spawned.Simulation.TotalBorn==2,"Spawn-on-collide invokes the native linked spawn modifier");
        var inactive=CollisionFixture();inactive.Nif.Blocks.OfType<NiPSysColliderManager>().Single().Active=false;inactive.Seek(.2);
        Assert(inactive.Simulation.Particles[0].Position.Z<0,"Inactive collider managers do not affect particles");
        var local=CollisionFixture();local.Nif.Blocks.OfType<NiPSysPlanarCollider>().Single().Width=0;
        local.Seek(.2);
        Assert(local.Simulation.Particles[0].Position.Z>0,"Plane zero extent still permits its exact central point contact");
        var cyclic=CollisionFixture();var collider=cyclic.Nif.Blocks.OfType<NiPSysPlanarCollider>().Single();
        collider.NextCollider=new(cyclic.Nif.Blocks.IndexOf(collider));
        var cycleSystem=new ParticleNif(cyclic.Nif,cyclic.System);
        Assert(cycleSystem.Diagnostics.Any(d=>d.Contains("cycle")),"Cyclic native collider chains are bounded and diagnosed");
        var collisionCapacity=CollisionFixture(die:true,spawn:true);collisionCapacity.Simulation.Config.Capacity=1;collisionCapacity.Seek(.2);
        Assert(collisionCapacity.Simulation.Particles.Count<=1&&collisionCapacity.Simulation.TotalBorn==1,"Impact descendants obey the shared particle capacity");
        var repeatFile=ParticleFixture.CreateCollider(fixtureRoot);
        ParticleNif Repeat(){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(repeatFile));return new(n,n.Blocks.OfType<NiParticleSystem>().Single());}
        var c30=Repeat();var c60=Repeat();
        for(int i=0;i<=30;i++)c30.Seek(i/30d);
        for(int i=0;i<=60;i++)c60.Seek(i/60d);
        Assert(State(c30.Simulation)==State(c60.Simulation),"Native collision playback is identical at 30 and 60 FPS");
        var collisionState=State(c30.Simulation);c30.Seek(.2);c30.Seek(1);
        Assert(State(c30.Simulation)==collisionState,"Collision rewind and replay reproduce the exact particle state");
        string fixture=ParticleFixture.Create(fixtureRoot);
        ParticleFixture.CreateRenderStates(fixtureRoot);
        var cueAssets=new Assets();cueAssets.External[@"meshes\previewer\fountain.nif"]=fixture;
        var cueEffects=new Effects(cueAssets,new Records());
        var particleCue=cueEffects.Preview(new("Particle","@PLAYPARTICLE|previewer/fountain.nif|37|1.5|2|4U|1|2|3","fixture"),.25).Single();
        Assert(particleCue.Biped==37&&particleCue.SpawnFlags==4&&particleCue.Offset==new Vector3(1,2,3)&&particleCue.Time==.25&&particleCue.Scale==1.5f,
            "Payload particle parsing preserves biped, unsigned spawn flags, scale, time and local offsets");
        Assert(particleCue.TemporaryParticle&&particleCue.ParticlesVisible(2)&&!particleCue.ParticlesVisible(2.001),"PI temporary particle is alive through its lifetime boundary and expires afterward");
        Assert(!particleCue.ParticlesVisible(-.001),"Temporary particles remain hidden before their cue time");
        var zeroLife=particleCue with {Lifetime=0};var negativeLife=particleCue with {Lifetime=-1};
        Assert(zeroLife.ParticlesVisible(0)&&!zeroLife.ParticlesVisible(.001)&&!negativeLife.ParticlesVisible(0),"Zero and negative temporary lifetimes do not become indefinite effects");
        Assert((particleCue with {TemporaryParticle=false}).ParticlesVisible(3),"Standalone NIF playback retains its existing particle-tail behavior");
        Assert(cueEffects.Preview(new("Invalid","@PLAYPARTICLE|previewer/fountain.nif|42|1|2|0|0|0|0","fixture"),0).Count==0,
            "Out-of-range biped indices do not silently emit at the actor origin");
        var reload=new Nifcontent_Class_Manolo();reload.Load_Manolo(File.ReadAllBytes(fixture));
        var authored=new ParticleNif(reload,reload.Blocks.OfType<NiParticleSystem>().Single());authored.Seek(1);
        Assert(authored.Simulation.Particles.Count==90,"Authored Skyrim SE NIF saves, reloads and emits its authored 90/second");
        Assert(authored.Simulation.Config.Capacity==512,"Serialized Skyrim particle capacity is read correctly");
        Assert(authored.Texture==@"textures\previewer\particle.dds","NIF source texture resolves to actual authored path");
        foreach(bool enabled in new[]{false,true}){
            var aspectNif=new Nifcontent_Class_Manolo();aspectNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateAspect(fixtureRoot,enabled)));
            var aspectSystem=new ParticleNif(aspectNif,aspectNif.Blocks.OfType<NiParticleSystem>().Single());
            Assert(aspectSystem.AspectAtSpeed(50)==(enabled?.625f:1)&&aspectSystem.MinimumAspect==(enabled?.25f:1),"Serialized speed/aspect fields feed rendering and camera bounds: "+enabled);
        }
        foreach(bool enabled in new[]{false,true}){
            var rotationNif=new Nifcontent_Class_Manolo();rotationNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateRotation(fixtureRoot,enabled)));
            var rotationSystem=new ParticleNif(rotationNif,rotationNif.Blocks.OfType<NiParticleSystem>().Single());rotationSystem.Seek(.25);
            Assert(rotationSystem.Simulation.Particles.Count>0&&rotationSystem.Simulation.Particles.All(p=>p.Spin==2&&(enabled?p.Angle>.3f:p.Angle==.3f)),
                "Saved NIF rotation always initializes angle/spin and gates only update: "+enabled);
            if(enabled){
                var tracked=rotationSystem.Simulation.Particles.First();float angle=tracked.Angle;
                var modifier=rotationNif.Blocks.OfType<NiPSysRotationModifier>().Single();modifier.Active=false;rotationSystem.Seek(.5);
                Assert(tracked.Angle==angle,"Deactivating a loaded NIF rotation modifier freezes existing particle angles");
                modifier.Active=true;rotationSystem.Seek(.75);
                Assert(Math.Abs(tracked.Angle-angle-.5f)<1e-5f,"Reactivated NIF rotation resumes its stored spin without a time jump");
            }
        }
        var decayNif=new Nifcontent_Class_Manolo();decayNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateGravityDecay(fixtureRoot)));
        var nativeGravity=new ParticleNif(decayNif,decayNif.Blocks.OfType<NiParticleSystem>().Single());
        nativeGravity.Seek(.5);var gravityTracked=nativeGravity.Simulation.Particles.First();
        var gravityBefore=gravityTracked.Velocity;float gravityHeight=gravityTracked.Position.Z;
        nativeGravity.Seek(.5+ParticleSimulation.Step);
        Assert(Math.Abs(gravityTracked.Velocity.Z-(gravityBefore.Z-64*MathF.Exp(-.05f*Math.Abs(gravityHeight))*(float)ParticleSimulation.Step))<1e-4f,
            "Saved and reloaded gravity NIF applies native planar decay at the particle position");
        nativeGravity.Seek(1);var gravityState=State(nativeGravity.Simulation);nativeGravity.Seek(0);
        for(int frame=1;frame<=30;frame++)nativeGravity.Seek(frame/30d);
        Assert(State(nativeGravity.Simulation)==gravityState,"Native gravity decay is deterministic across rewind and 30 FPS presentation");
        ParticleNif Turbulent(bool enabled){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateGravityTurbulence(fixtureRoot,enabled)));return new(n,n.Blocks.OfType<NiParticleSystem>().Single());}
        var turbulent=Turbulent(true);var calm=Turbulent(false);turbulent.Seek(1);calm.Seek(1);
        Assert(Vector3.Distance(turbulent.Simulation.Particles.First().Velocity,calm.Simulation.Particles.First().Velocity)>1,
            "Serialized turbulence changes the native fixture's particle velocities");
        var turbulentState=State(turbulent.Simulation);turbulent.Seek(0);
        for(int frame=1;frame<=30;frame++)turbulent.Seek(frame/30d);
        Assert(State(turbulent.Simulation)==turbulentState,"Turbulent NIF rewind and 30 FPS presentation reproduce exactly");
        turbulent.Seek(0);for(int frame=1;frame<=60;frame++)turbulent.Seek(frame/60d);
        Assert(State(turbulent.Simulation)==turbulentState,"Turbulence is independent of 30 versus 60 FPS presentation");
        ParticleNif AlignedGravity(bool aligned){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateWorldAlignedGravity(fixtureRoot,aligned)));return new(n,n.Blocks.OfType<NiParticleSystem>().Single());}
        var alignedGravity=AlignedGravity(true);var parentGravity=AlignedGravity(false);
        alignedGravity.Seek(.5);parentGravity.Seek(.5);
        var alignedParticle=alignedGravity.Simulation.Particles.First();var parentParticle=parentGravity.Simulation.Particles.First();
        var alignedVelocity=alignedParticle.Velocity;var parentVelocity=parentParticle.Velocity;
        alignedGravity.Seek(.5+ParticleSimulation.Step);parentGravity.Seek(.5+ParticleSimulation.Step);
        var alignedDelta=alignedParticle.Velocity-alignedVelocity;var parentDelta=parentParticle.Velocity-parentVelocity;
        Assert(Vector3.Distance(alignedDelta,new(0,0,-64*(float)ParticleSimulation.Step))<1e-4f,"Serialized World Aligned gravity ignores the source parent's rotation");
        Assert(Math.Abs(parentDelta.Z)<1e-4f&&Math.Abs(Math.Abs(parentDelta.X)-64*(float)ParticleSimulation.Step)<1e-4f,"Ordinary gravity follows the rotated parent axis");
        var placedGravity=AlignedGravity(true);placedGravity.Placement=_=>Matrix4x4.CreateRotationX(MathF.PI/2);placedGravity.Seek(.5);
        var placedParticle=placedGravity.Simulation.Particles.First();var placedVelocity=placedParticle.Velocity;placedGravity.Seek(.5+ParticleSimulation.Step);
        Assert(Vector3.Distance(placedParticle.Velocity-placedVelocity,alignedDelta)<1e-4f,"World Aligned gravity bypasses external effect placement rotation");
        var decayedAligned=AlignedGravity(true);var alignedModifier=decayedAligned.Modifiers.OfType<NiPSysGravityModifier>().Single();
        alignedModifier.Decay=.05f;
        var alignedSource=(NiAVObject)decayedAligned.Block(alignedModifier.GravityObject)!;alignedSource.Translation=new(10,0,0);
        decayedAligned.Placement=_=>Matrix4x4.CreateTranslation(0,0,30);decayedAligned.Seek(.5);
        var decayParticle=decayedAligned.Simulation.Particles.First();var decayVelocity=decayParticle.Velocity;
        var sourceFrame=ParticleNif.Matrix(Transform_Class.GetGlobalTransform((NiNode)alignedSource,decayedAligned.Nif).ToMatrix4())*decayedAligned.Placement(.5);
        float alignedDistance=Math.Abs(decayParticle.Position.Z-sourceFrame.Translation.Z);
        decayedAligned.Seek(.5+ParticleSimulation.Step);
        Assert(Math.Abs(decayParticle.Velocity.Z-(decayVelocity.Z-64*MathF.Exp(-.05f*alignedDistance)*(float)ParticleSimulation.Step))<1e-4f,
            "World Aligned decay retains the transformed source position while bypassing parent axis rotation");
        var alignedState=State(alignedGravity.Simulation);alignedGravity.Seek(0);alignedGravity.Seek(.5+ParticleSimulation.Step);
        Assert(State(alignedGravity.Simulation)==alignedState,"World Aligned gravity replays deterministically after rewind");
        Assert(ParticleLod.Evaluate(.05f,.1f,.5f,.25f,2)==(1f,1f),"Particle LOD retains full emission and size below its begin threshold");
        var midpointLod=ParticleLod.Evaluate(.3f,.1f,.5f,.25f,2);
        Assert(Math.Abs(midpointLod.Emission-.625f)<1e-6f&&Math.Abs(midpointLod.Size-1.5f)<1e-6f,"Native LOD linearly interpolates emission and size independently");
        Assert(ParticleLod.Evaluate(2,.1f,.5f,.25f,2)==(.25f,2f),"Particle LOD holds authored endpoint values beyond its end threshold");
        ParticleNif LodFixture(float distance){var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateLod(fixtureRoot)));return new(n,n.Blocks.OfType<NiParticleSystem>().Single()){NormalizedLodDistance=distance};}
        var nearLod=LodFixture(0);var farLod=LodFixture(1);nearLod.Seek(1);farLod.Seek(1);
        Assert(nearLod.Simulation.TotalBorn==90&&farLod.Simulation.TotalBorn==22,$"Serialized LOD scales fixed-distance birth counts from 90 to 22 (got {nearLod.Simulation.TotalBorn} and {farLod.Simulation.TotalBorn})");
        Assert(nearLod.Simulation.Particles.All(p=>p.BaseRadius==4)&&farLod.Simulation.Particles.All(p=>p.BaseRadius==8),"Serialized LOD size multiplier is applied at birth");
        var oldLodParticle=nearLod.Simulation.Particles.First();nearLod.NormalizedLodDistance=1;nearLod.Seek(1.1);
        Assert(oldLodParticle.BaseRadius==4&&nearLod.Simulation.Particles.Any(p=>p.BaseRadius==8),"Changing LOD does not retroactively resize existing particles");
        var farLodState=State(farLod.Simulation);farLod.Seek(0);for(int frame=1;frame<=30;frame++)farLod.Seek(frame/30d);
        Assert(State(farLod.Simulation)==farLodState,"Fixed-distance LOD replays deterministically at 30 FPS");
        var dragNif=new Nifcontent_Class_Manolo();dragNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateDrag(fixtureRoot)));
        var nativeDrag=new ParticleNif(dragNif,dragNif.Blocks.OfType<NiParticleSystem>().Single());
        nativeDrag.Seek(.1);var dragParticle=nativeDrag.Simulation.Particles.First();
        var initialDragVelocity=dragParticle.Velocity;nativeDrag.Seek(.5);
        Assert(dragParticle.Velocity.X<initialDragVelocity.X&&Math.Abs(dragParticle.Velocity.Z-initialDragVelocity.Z)<1e-5f,
            "Saved and reloaded native X drag slows horizontal motion while retaining upward speed");
        var dragState=State(nativeDrag.Simulation);nativeDrag.Seek(0);
        for(int frame=1;frame<=30;frame++)nativeDrag.Seek(frame/60d);
        Assert(State(nativeDrag.Simulation)==dragState,"Native drag controller replay remains identical across seeks and render rates");
        var spawnNif=new Nifcontent_Class_Manolo();spawnNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateSpawn(fixtureRoot)));
        var nativeSpawn=new ParticleNif(spawnNif,spawnNif.Blocks.OfType<NiParticleSystem>().Single());
        nativeSpawn.Seek(.2);
        Assert(nativeSpawn.Simulation.TotalBorn==7&&nativeSpawn.Simulation.Particles.Count==4&&nativeSpawn.Simulation.Particles.All(p=>p.Generation==2),$"Saved and reloaded NiPSysSpawnModifier produces both authored child generations (born {nativeSpawn.Simulation.TotalBorn}, live {nativeSpawn.Simulation.Particles.Count}, generations {string.Join(",",nativeSpawn.Simulation.Particles.Select(p=>p.Generation))})");
        nativeSpawn.Seek(.4);Assert(nativeSpawn.Simulation.Particles.Count==0,"Native spawn fixture terminates after its generation limit");
        string cross=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\FF7Fenrir\CrossSlash_HitFX01.nif");
        if(!File.Exists(cross))Skip("Cross Slash emitter binding checks: "+SelfTest.Missing(cross));else{
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(cross));
            var crossSystems=n.Blocks.OfType<NiParticleSystem>().Select(ps=>new ParticleNif(n,ps)).ToArray();
            var source=crossSystems.SelectMany(p=>p.BoundEmitterMeshes).First();
            var unbound=crossSystems.Single(p=>p.Name=="APGlow2");unbound.Seek(.5);
            Assert(unbound.Simulation.TotalBorn==0,"Cross Slash null emitter references do not silently bind unrelated geometry");
            unbound.BindEmitterMeshes([source]);unbound.Seek(.5);
            Assert(unbound.HasEmitterOverride&&unbound.Simulation.TotalBorn==0,"Binding APGlow2 preserves its authored pre-burst delay rather than forcing early particles");
            Assert(unbound.AuthoredEmitterSources.Contains("null, null")&&unbound.EmitterSourceDetails.Contains("Temporary preview override"),"Cross Slash diagnostics retain both saved null links while an override is active");
            unbound.Seek(1);
            Assert(unbound.Simulation.TotalBorn>0,"APGlow2 has an authored delayed burst after 0.8 seconds when explicitly supplied geometry");
            unbound.BindEmitterMeshes(null);unbound.Seek(1);
            Assert(unbound.Simulation.TotalBorn==0&&unbound.EmitterSourceDetails.Contains("preview limitation"),"Unbound delayed bursts report preview suppression without claiming native suppression");
            // Create an in-memory missing-source fixture from the active authored system.
            // The user's NIF on disk is never changed.
            var activeTemplate=crossSystems.First(p=>p.BoundEmitterMeshes.Length>0);
            var activeMesh=activeTemplate.Modifiers.OfType<NiPSysMeshEmitter>().Single();
            var originalIndices=activeMesh.EmitterMeshes.Indices.ToArray();
            activeMesh.EmitterMeshes.Clear();
            unbound=new ParticleNif(n,activeTemplate.System);
            unbound.BindEmitterMeshes([source]);unbound.Seek(.5);
            Assert(unbound.HasEmitterOverride&&unbound.Simulation.TotalBorn>0,"An explicit Cross Slash emitter binding enables the selected system");
            var boundState=State(unbound.Simulation);unbound.BindEmitterMeshes([source,source]);unbound.Seek(.5);
            Assert(unbound.BoundEmitterMeshes.Length==1&&State(unbound.Simulation)==boundState,"Repeated emitter binding deduplicates sources and replays deterministically");
            unbound.BindEmitterMeshes(null);unbound.Seek(.5);
            Assert(!unbound.HasEmitterOverride&&unbound.Simulation.TotalBorn==0,"Restoring authoredSystem references disables the originally unbound system");
            foreach(var index in originalIndices)activeMesh.EmitterMeshes.AddBlockRef(index);
            var authoredSystem=activeTemplate;authoredSystem.BindEmitterMeshes([]);authoredSystem.Seek(.5);
            Assert(authoredSystem.Simulation.TotalBorn==0,"Explicitly empty source selection disables an otherwise bound emitter");
            authoredSystem.BindEmitterMeshes(null);authoredSystem.Seek(.5);
            Assert(authoredSystem.Simulation.TotalBorn>0&&authoredSystem.BoundEmitterMeshes.Length==1,"Restoring authoredSystem binding re-enables its original mesh without accumulating copies");
            var oldVelocityType=activeMesh.InitialVelocityType;activeMesh.InitialVelocityType=(NiflySharp.Enums.VelocityType)1;
            var randomSource=new ParticleNif(n,activeTemplate.System,19);
            var randomPlaced=new ParticleNif(n,activeTemplate.System,19){Placement=_=>Matrix4x4.CreateRotationZ(.7f)};
            var baseDirection=randomSource.Simulation.MeshSample!(0).Direction;
            var placedDirection=randomPlaced.Simulation.MeshSample!(0).Direction;
            var expectedDirection=randomSource.WorldSpace?Vector3.TransformNormal(baseDirection,Matrix4x4.CreateRotationZ(.7f)):baseDirection;
            Assert(Vector3.Distance(placedDirection,expectedDirection)<1e-5f,"Real mesh-emitter random velocity follows particle-system placement in world space");
            randomSource.Simulation.Reset();
            Assert(Vector3.Distance(randomSource.Simulation.MeshSample!(0).Direction,baseDirection)<1e-6f,"Real mesh random velocity reproduces after simulation reset");
            activeMesh.InitialVelocityType=oldVelocityType;
            bool rejected=false;try{unbound.BindEmitterMeshes([new NiNode()]);}catch(ArgumentException){rejected=true;}
            Assert(rejected,"Emitter source selection rejects geometry outside the effect NIF");
        }
        string beam=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\FF7Fenrir\Fenrir_BladeBeam.nif");
        if(!File.Exists(beam))Skip("Anchor Blade Beam multi-target checks: "+SelfTest.Missing(beam));else{
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(beam));var animation=new NifAnimation(n);
            var names=n.Blocks.OfType<NiControllerSequence>().SelectMany(s=>NifAnimation.Items(s.ControlledBlocks))
                .Where(b=>animation.Block(NifAnimation.Get(b,"Controller")) is NiMultiTargetTransformController)
                .Select(b=>NifAnimation.Get(b,"NodeName")?.ToString()).ToHashSet();
            var nodes=n.Blocks.OfType<NiAVObject>().Where(n=>names.Contains(n.Name.ToString())).ToArray();
            Assert(nodes.Length>0,"Anchor Blade Beam has multi-target sequence bindings to actual scene nodes");
            animation.Evaluate(.1);var before=nodes.Select(n=>(n.Translation,n.Scale,n.Rotation)).ToArray();
            animation.Evaluate(.5);var after=nodes.Select(n=>(n.Translation,n.Scale,n.Rotation)).ToArray();
            Assert(!before.SequenceEqual(after),"Anchor Blade Beam multi-target nodes animate between authored times");
            animation.Evaluate(.1);Assert(before.SequenceEqual(nodes.Select(n=>(n.Translation,n.Scale,n.Rotation))),"Anchor Blade Beam multi-target animation reproduces a backward seek");
        }
        // The Stances mod sits in PluginTemplate beside the source folder, or further up when this is a git worktree.
        const string stances=@"PluginTemplate\Stances Dynamic Weapon Movesets SE\SKSE\plugins\dtryKeyUtil\config\custom\Stances.ini";
        string? keyConfig=null;for(var folder=Directory.GetParent(projectRoot);folder!=null&&keyConfig==null;folder=folder.Parent)if(File.Exists(Path.Combine(folder.FullName,stances)))keyConfig=Path.Combine(folder.FullName,stances);
        if(keyConfig==null)Skip("Real Stances dtry key binding checks: no folder above the source holds "+stances);else{
            var keyAssets=new Assets();
            keyAssets.External[@"SKSE\plugins\dtryKeyUtil\config\custom\Stances.ini"]=keyConfig;
            var keyEffects=new Effects(keyAssets,new Records());keyEffects.ReadConfigs();
            var keyPresses=keyEffects.Triggers.Where(t=>t.Instruction.StartsWith("@APPLYSPELL")).ToArray();
            var releases=keyEffects.Triggers.Where(t=>t.Instruction.StartsWith("@UNAPPLYSPELL")).ToArray();
            Assert(keyPresses.Length==5&&releases.Length==5,"Real Stances dtry bindings expose separate press and release triggers");
            Assert(keyPresses.All(p=>releases.Any(r=>r.Instruction==p.Instruction.Replace("@APPLYSPELL","@UNAPPLYSPELL"))),"Every key release targets the same plugin and spell as its press");
            Assert(keyEffects.Triggers.All(t=>t.Conditions.Contains("[KeyTrace_EventID_Keyboard]")&&t.Source.EndsWith("Stances.ini")),"Key trigger inspector retains original section, binding, and source");
        }
        KeyTriggerTests.Run(Assert);
        LagControllerTests.Run(Assert,fixtureRoot);
        ParticleMeshTransformTests.Run(Assert,fixtureRoot);
        LightingControllerTests.Run(Assert);
        foreach(float scale in new[]{.25f,1f,4f}){
            var scaleNif=new Nifcontent_Class_Manolo();scaleNif.Load_Manolo(File.ReadAllBytes(ParticleFixture.CreateBillboardScale(fixtureRoot,scale)));
            var scaleSystem=new ParticleNif(scaleNif,scaleNif.Blocks.OfType<NiParticleSystem>().Single());scaleSystem.Seek(.2);
            Assert(scaleSystem.Simulation.Particles.Count>0&&scaleSystem.Simulation.Particles.All(p=>Math.Abs(ParticleNif.BillboardRadius(p)-25)<1e-5),"Billboard radius remains authored radius under node scale "+scale);
        }
        MuzzleFlashTests.Run(Assert);
        TimedExplosionTests.Run(Assert);
        string anchorRoot=Path.Combine(SelfTest.Mods,"Anchor MCO Animations v2 - Cloud Fenrir");
        string anchorPlugin=Path.Combine(anchorRoot,"Anchor spell for MCO.esp");
        if(!File.Exists(anchorPlugin))Skip("Anchor Berserker spell effect checks: "+SelfTest.Missing(anchorPlugin));else{
            var spellAssets=new Assets();
            foreach(var file in Directory.EnumerateFiles(anchorRoot,"*.nif",SearchOption.AllDirectories))
                spellAssets.External[Assets.Normalize(Path.GetRelativePath(anchorRoot,file))]=file;
            var spellRecords=new Records();
            foreach(var record in Records.Parse(File.ReadAllBytes(anchorPlugin),Path.GetFileName(anchorPlugin)))spellRecords.All[record.Key]=record;
            var spellEffects=new Effects(spellAssets,spellRecords);
            var buff=spellEffects.Preview(new("Berserker","@CAST|0x930|Anchor spell for MCO.esp","test"),2);
            Assert(buff.Count==2&&buff.All(c=>c.Time==2&&c.Lifetime==35&&c.FollowActor),"Anchor Berserker self spell emits both art files for their paired 35-second EFIT durations");
            Assert(spellEffects.Preview(new("Learn","@APPLYSPELL|0x930|Anchor spell for MCO.esp","test"),0).Count==0,"Adding the ordinary Anchor Berserker spell does not falsely cast its visuals");
            var spellData=spellRecords.Resolve("Anchor spell for MCO.esp",0x930)!.First("SPIT")!;
            BitConverter.GetBytes(4u).CopyTo(spellData.Bytes,8); // In-memory passive fixture, original ESP unchanged.
            var removalClip=new AnimationClip{Events=[
                new(2,"PIE.@APPLYSPELL|0x930|Anchor spell for MCO.esp",0),
                new(0,"PIE.![3]@UNAPPLYSPELL|0x930|Anchor spell for MCO.esp",0),
                new(4,"PIE.@APPLYSPELL|0x930|Anchor spell for MCO.esp",0)]};
            var removed=spellEffects.Build(removalClip,false);
            Assert(removed.Count==4&&removed.Take(2).All(c=>c.RemovedAt==3&&c.ParticlesVisible(.999)&&!c.ParticlesVisible(1)),"Delayed ability removal stops every associated art at its absolute event time");
            Assert(removed.Skip(2).All(c=>double.IsPositiveInfinity(c.RemovedAt)),"Reapplying an ability after removal creates a fresh visible effect");
            removalClip.Events[1]=new(3,"PIE.@UNAPPLYSPELL|0x931|Anchor spell for MCO.esp",0);
            Assert(spellEffects.Build(removalClip,false).All(c=>double.IsPositiveInfinity(c.RemovedAt)),"Removing a different spell does not cancel the applied effect");
            Assert(spellEffects.Preview(new("Berserker","@APPLYSPELL|0x930|Anchor spell for MCO.esp","test"),0).All(c=>double.IsPositiveInfinity(c.RemovedAt)),"Manual preview does not inherit removals from an earlier animation");
            var press=new Trigger("Press","@APPLYSPELL|0x930|Anchor spell for MCO.esp","fixture");
            var release=new Trigger("Release","@UNAPPLYSPELL|0x930|Anchor spell for MCO.esp","fixture");
            TimedTrigger[] presses=[new(press,0),new(press,.5)];
            Assert(spellEffects.Build(new(),false,presses).Count==2,"Repeated manual application does not duplicate active ability artwork");
            var manual=spellEffects.Build(new(),false,[..presses,new(release,1),new(press,2)]);
            Assert(manual.Count==4&&manual.Take(2).All(c=>c.RemovedAt==1)&&manual.Skip(2).All(c=>c.Time==2&&double.IsPositiveInfinity(c.RemovedAt)),"Manual release ends earlier applications and a later press starts fresh artwork");
            var sameTime=spellEffects.Build(new(),false,[new(press,0),new(release,0),new(press,0)]);
            Assert(sameTime.Count==4&&sameTime.Take(2).All(c=>c.RemovedAt==0)&&sameTime.Skip(2).All(c=>double.IsPositiveInfinity(c.RemovedAt)),"Equal-time press/release/press preserves declared order");
            var mixed=spellEffects.Build(new AnimationClip{Events=[new(0,"PIE.@APPLYSPELL|0x930|Anchor spell for MCO.esp",0)]},false,[new(release,1)]);
            Assert(mixed.Count==2&&mixed.All(c=>c.RemovedAt==1),"Manual release also affects an ability applied by animation annotations");
            Assert(spellEffects.Build(new(),false,[new(press,0),new(press,36)]).Count==4,"Finite passive effects may be reapplied after their record duration");
            BitConverter.GetBytes(0u).CopyTo(spellData.Bytes,16);
            var constant=spellEffects.Build(new(),false,[new(press,0),new(press,36),new(release,40)]);
            Assert(constant.Count==2&&constant.All(c=>double.IsPositiveInfinity(c.Lifetime)&&c.RemovedAt==40),"Constant abilities persist until removal without a fake three-second lifetime or duplicate spawn");
            BitConverter.GetBytes(1u).CopyTo(spellData.Bytes,16);
            var cast=new Trigger("Cast","@CAST|0x930|Anchor spell for MCO.esp","fixture");
            Assert(spellEffects.Build(new(),false,[new(cast,0),new(cast,.5)]).Count==4,"Independent casts are not deduplicated as learned abilities");
            BitConverter.GetBytes(0u).CopyTo(spellData.Bytes,8);
            var effect=spellRecords.Resolve("Anchor spell for MCO.esp",0x934)!;
            var data=effect.First("DATA")!;
            BitConverter.GetBytes(2u).CopyTo(data.Bytes,84);
            var aimed=spellEffects.Preview(new("Berserker","@CAST|0x930|Anchor spell for MCO.esp","test"),0);
            Assert(aimed.Count==1,"Aimed hit art is withheld until impact instead of appearing on cast");
            BitConverter.GetBytes(0u).CopyTo(data.Bytes,84);
            BitConverter.GetBytes(data.UInt()|0x08000000u).CopyTo(data.Bytes,0);
            Assert(spellEffects.Preview(new("Berserker","@CAST|0x930|Anchor spell for MCO.esp","test"),0).Count==1,"No Hit Effect flag suppresses self-target hit art");
        }
        string weaponEffect=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\greatsword\BerserkerWeapon.nif");
        if(!File.Exists(weaponEffect))Skip("Berserker weapon emitter checks: "+SelfTest.Missing(weaponEffect));else{
            var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(weaponEffect));
            var p=new ParticleNif(n,n.Blocks.OfType<NiParticleSystem>().Single(s=>s.Name.ToString()=="pFlames"));
            p.Seek(.5);
            Assert(p.Simulation.BirthRate(.1)>0&&p.Simulation.Particles.Count>0,"mBegin automatically transitions to mLoop for the authored weapon emitter");
            p.Seek(2);
            Assert(p.Simulation.Particles.Any(x=>x.Generation>0),"Real weapon spawn-on-death creates child particles");
            p.Animation.SelectedSequence="mBegin";p.Simulation.Reset();p.Seek(.5);
            Assert(p.Simulation.TotalBorn==0,"Explicit mBegin sequence selection does not silently substitute mLoop");
            Assert(p.Diagnostics.Any(d=>d.Contains("no alpha property"))&&p.Diagnostics.Any(d=>d.Contains("without a palette")),"Missing native alpha/palette state is reported instead of claiming visual parity");
        }
        var types=new[]{typeof(NiParticleSystem),typeof(NiPSysData),typeof(NiPSysBoxEmitter),typeof(NiPSysEmitterCtlr),typeof(NiAlphaProperty),typeof(BSEffectShaderProperty),typeof(NiFloatInterpolator),typeof(NiBoolInterpolator),typeof(NiflySharp.Bitfields.AlphaFlags),typeof(NiflySharp.Bitfields.TimeControllerFlags)};
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-api.txt"),types.SelectMany(t=>new[]{"TYPE "+t.Name}.Concat(t.GetProperties().Select(p=>p.Name+" : "+p.PropertyType.FullName))));
        File.AppendAllLines(Path.Combine(AppContext.BaseDirectory,"particle-api.txt"),typeof(NiGeometryData).GetFields(System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Select(f=>f.Name+" : "+f.FieldType));
        List<object> systems=[];
        foreach(var mod in SelfTest.TestMods)if(!SelfTest.HasMod(mod))Skip("Particle NIF audit: "+SelfTest.Missing(mod));else foreach(var path in Directory.EnumerateFiles(Path.Combine(SelfTest.Mods,mod),"*.nif",SearchOption.AllDirectories)){
            try{
                var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(path));
                foreach(var ps in n.Blocks.OfType<NiParticleSystem>()){
                    var p=new ParticleNif(n,ps);p.Seek(.2);
                    systems.Add(new{path,p.Name,p.Texture,p.Palette,p.WorldSpace,alpha=NifAnimation.Get(NifAnimation.Get(p.Alpha,"Flags"),"Value")?.ToString(),shader=p.Shader?.GetType().GetProperties().Where(x=>x.GetIndexParameters().Length==0).Select(x=>new{x.Name,value=SafeValue(x,p.Shader)}),capacity=p.Simulation.Config.Capacity,count=p.Simulation.Particles.Count,
                        emitterMeshes=p.Modifiers.OfType<NiPSysMeshEmitter>().SelectMany(e=>NifAnimation.Items(e.EmitterMeshes)).Select(r=>new{index=NifAnimation.Get(r,"Index"),type=p.Block(r)?.GetType().Name,name=NifAnimation.Get(p.Block(r),"Name")?.ToString()}),sequences=n.Blocks.OfType<NiControllerSequence>().Select(s=>new{name=s.Name.ToString(),s.StartTime,s.StopTime,s.Frequency,bindings=NifAnimation.Items(s.ControlledBlocks).Select(b=>new{
    properties=b.GetType().GetProperties().Where(x=>x.GetIndexParameters().Length==0).Select(x=>new{x.Name,value=SafeValue(x,b)}),
    interpolator=p.Block(NifAnimation.Get(b,"Interpolator"))?.GetType().Name,
    value=NifAnimation.Get(p.Block(NifAnimation.Get(b,"Interpolator")),"Value")?.ToString()
})}),settings=p.Simulation.Config,rate=p.Simulation.BirthRate(.1),active=p.Simulation.EmitterActive(.1),
                        controllers=n.Blocks.OfType<NiPSysEmitterCtlr>().Select(c=>new{modifier=c.ModifierName.ToString(),c.StartTime,c.StopTime,interp=p.Block(c.Interpolator)?.GetType().Name,value=NifAnimation.Get(p.Block(c.Interpolator),"Value")?.ToString(),target=c.Target.Index}),
                        modifiers=p.Modifiers.Select(m=>new{type=m.GetType().Name,properties=m.GetType().GetProperties().Where(x=>x.GetIndexParameters().Length==0&&!x.Name.Contains("References")).Select(x=>new{x.Name,value=SafeValue(x,m)})}),p.Diagnostics});
                }
            }catch(Exception ex){systems.Add(new{path,error=ex.ToString()});}
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"particle-audit.json"),JsonSerializer.Serialize(systems,new JsonSerializerOptions{WriteIndented=true}));
        lines.Add("Particle NIF audit entries: "+systems.Count);File.WriteAllLines(Path.Combine(AppContext.BaseDirectory,"particle-tests.txt"),lines);
    }
    static string? SafeValue(System.Reflection.PropertyInfo p,object o){try{return p.GetValue(o)?.ToString();}catch{return null;}}
    static string State(ParticleSimulation s)=>JsonSerializer.Serialize(s.Particles,new JsonSerializerOptions{IncludeFields=true});
}



