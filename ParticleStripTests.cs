using System.Numerics;
using FO4_Base_Library;
using NiflySharp.Blocks;
using NiflySharp;
using NiflySharp.Enums;
using NiflySharp.Structs;
namespace SSEPreview;
internal static class ParticleStripTests
{
    internal static string Fixture(string directory)
    {
        var nif=new NifFile();if(nif.Load(ParticleFixture.Create(directory))!=0)throw new InvalidDataException("Cannot load strip fixture source");
        var old=nif.Blocks.OfType<NiParticleSystem>().Single();int index=nif.Blocks.IndexOf(old);
        var data=new BSStripPSysData{HasRadii=true,HasSizes=true,HasRotationAngles=true,AspectRatio=1,MaxPointCount=30,StartCapSize=0,EndCapSize=0,DoZPrepass=false};
        typeof(NiGeometryData).GetField("_bSMaxVertices",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.SetValue(data,(ushort)512);
        var strip=new BSStripParticleSystem{Name=new("Original crossed strips"),Scale=1,Rotation=old.Rotation,WorldSpace=true,Modifiers=old.Modifiers,Controller=old.Controller,ShaderPropertyRef=old.ShaderPropertyRef,AlphaPropertyRef=old.AlphaPropertyRef,Data_NiBlockRef_NPSD=new(nif.AddBlock(data))};
        if(!nif.ReplaceBlock(index,strip))throw new InvalidDataException("Cannot replace fixture particle system");
        strip.Modifiers.AddBlockRef(nif.AddBlock(new BSPSysStripUpdateModifier{Name=new("Strip update"),Active=true,Target=new(index),Order=(NiPSysModifierOrder)8000,UpdateDeltaTime=1f/30}));
        var emitter=nif.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.Declination=1;emitter.DeclinationVariation=.2f;emitter.LifeSpan=1.5f;emitter.LifeSpanVariation=0;emitter.InitialRadius=5;
        string file=Path.Combine(directory,@"meshes\previewer\crossed-strips.nif");if(nif.Save(file)!=0)throw new InvalidDataException("Cannot save crossed-strip fixture");return file;
    }
    internal static string RetentionFixture(string directory)
    {
        var nif=new NifFile();if(nif.Load(Fixture(directory))!=0)throw new InvalidDataException("Cannot load strip retention source");
        var emitter=nif.Blocks.OfType<NiPSysBoxEmitter>().Single();emitter.LifeSpan=.3f;emitter.LifeSpanVariation=0;
        var visibility=new NiBoolData{Data=new KeyGroup<byte>{NumKeys=2,Interpolation=(KeyType)5,Keys=[new(){Time=0,Value=1},new(){Time=.1f,Value=0}]}};
        nif.Blocks.OfType<NiPSysEmitterCtlr>().Single().VisibilityInterpolator=new(nif.AddBlock(new NiBoolInterpolator{Data=new(nif.AddBlock(visibility))}));
        string file=Path.Combine(directory,@"meshes\previewer\strip-retention.nif");if(nif.Save(file)!=0)throw new InvalidDataException("Cannot save strip retention fixture");return file;
    }
    internal static void Run(Action<bool,string> check)
    {
        var saved=new Nifcontent_Class_Manolo();saved.Load_Manolo(File.ReadAllBytes(Fixture(Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures"))));
        var authored=new ParticleNif(saved,saved.Blocks.OfType<BSStripParticleSystem>().Single(),21);authored.Seek(.5);
        check(authored.Simulation.Particles.Any(p=>p.Strip!=null&&ParticleStripGeometry.Triangles(p.Strip,new(0,0,1,1)).Any()),"Saved original crossed-strip NIF reloads and produces strip triangles");
        var retentionNif=new Nifcontent_Class_Manolo();retentionNif.Load_Manolo(File.ReadAllBytes(RetentionFixture(Path.Combine(ParticleTests.VerificationProjectRoot(),"particle-fixtures"))));
        var retentionSystem=new ParticleNif(retentionNif,retentionNif.Blocks.OfType<BSStripParticleSystem>().Single(),29);retentionSystem.Seek(.5);
        check(retentionSystem.Simulation.Particles.Count>0&&retentionSystem.Simulation.Particles.All(p=>p.Age>p.Life)&&retentionSystem.Simulation.Particles.Any(p=>p.Strip!.Points.Count>1),"Saved burst fixture keeps visible strip tails after every emitting particle expires");
        retentionSystem.Seek(1);check(retentionSystem.Simulation.Particles.Count==0,"Saved burst fixture removes all strips after their histories drain");
        var history=new ParticleStripHistory(6);
        check(history.Capacity==9&&Math.Abs(history.PointLifetime-.1f)<1e-6f,"Strip loader adds three working slots and derives point lifetime at 60 Hz from authored count");
        void Add(float t)=>history.Sample(t,new(t,0,0),new(t,1,1,1),2,true);
        Add(0);Add(.005f);Add(.01f);
        check(history.Points.Count==2&&history.Points[1].Time==.01f,"Strip keeps two initial points and replaces its provisional head between commits");
        Add(.03f);check(history.Points.Count==3&&history.Points[1].Time==.01f,"Strip commits a new head after strictly more than one sixtieth of a second");
        Add(.09f);Add(.12f);
        check(history.Points.Count==4&&history.Points[0].Time==.01f&&Math.Abs(history.TailFraction-.5f)<1e-5f,"Strip trims expired points and retains the partially clipped oldest segment");
        check(history.Points[0].Center.X==.01f&&history.Points[0].Color.X==.01f&&history.Points[0].Radius==2,"Strip clipping retains original point center, color and radius for geometry interpolation");
        var clippedBounds=ParticleStripGeometry.BoundsVertices(history).ToArray();
        var clippedTriangles=ParticleStripGeometry.Triangles(history,new(0,0,1,1)).Select(v=>v.Position).Distinct().ToArray();
        check(clippedBounds.ToHashSet().SetEquals(clippedTriangles)&&clippedBounds.Min(v=>v.X)>.019f,"Strip bounds match rendered vertices including the clipped tail rather than expired sample centers");
        int segments=history.SegmentCount;history.Sample(.3f,Vector3.Zero,Vector4.One,1,false);
        check(history.Points.Count==0&&history.SegmentCount==segments,"Expired strip tails drain without adding points and retain the native segment-count high-water mark");
        check(!ParticleStripGeometry.BoundsVertices(history).Any(),"Drained strip history contributes no phantom framing or refraction bounds");
        var full=new ParticleStripHistory(1);full.Sample(0,Vector3.Zero,Vector4.One,1,true);full.Sample(.001f,Vector3.Zero,Vector4.One,1,true);full.Sample(.018f,Vector3.Zero,Vector4.One,1,true);
        check(full.Points.Count<=full.Capacity-1,"Strip history reserves the native spare ring-buffer slot");
        var straight=new ParticleStripHistory(60);straight.Sample(0,Vector3.Zero,Vector4.One,4,true);straight.Sample(.02f,new(0,0,10),Vector4.One,4,true);straight.Sample(.04f,new(0,0,20),Vector4.One,4,true);
        var vertices=ParticleStripGeometry.Triangles(straight,new(0,0,1,1)).ToArray();
        check(vertices.Length==24&&straight.Points.All(p=>Math.Abs(p.SideA.Length()-1)<1e-5f&&Math.Abs(p.SideB.Length()-1)<1e-5f&&Math.Abs(Vector3.Dot(p.SideA,p.SideB))<1e-5f),"Straight strip produces two perpendicular sheets with four vertices per cross section");
        check(straight.Points.All(p=>Math.Abs(Vector3.Distance(p.Vertex(0),p.Vertex(1))-4)<1e-5f)&&vertices.Min(v=>v.UV.X)==0&&vertices.Max(v=>v.UV.X)==1,"Strip cross-section width uses half-radius offsets and texture U spans the segment-count interval");
        straight.Sample(.06f,new(10,0,30),Vector4.One,4,true);var bend=straight.Points[^2];
        check(Math.Abs(Vector3.Dot(bend.SideA,bend.Direction))<1e-4f&&Math.Abs(Vector3.Dot(bend.SideB,bend.Direction))<1e-4f&&bend.Direction.X>0,"Bent strip transports both side vectors onto its smoothed direction");
        ParticleSimulation Retained(int capacity=10)=>new(new(){Capacity=capacity,Life=.04f,Speed=20},17){BirthRate=_=>120,EmissionEnabled=t=>t<ParticleSimulation.Step,Initialize=(p,t)=>p.Strip=new(6),Update=(p,dt,t)=>{p.Position+=p.Velocity*dt;p.Strip!.Sample((float)t,p.Position,p.Color,p.Radius,p.Age<=p.Life);}};
        var retained=Retained();retained.Seek(.06);var expired=retained.Particles.Single();
        check(expired.Age>expired.Life&&expired.Strip!.Points.Count>1,"Expired strip particle stays allocated while its existing tail still has segments");
        float newest=expired.Strip!.Points[^1].Time;retained.Seek(.09);
        check(expired.Strip.Points[^1].Time==newest&&expired.Age>.07f,"Retained strip continues aging without adding new points beyond lifespan");
        string RetainedState()=>string.Join(";",retained.Particles.Select(p=>$"{p.Age}|{p.Position}|{p.Strip!.TailFraction}|"+string.Join("/",p.Strip.Points)));
        var retainedState=RetainedState();retained.Seek(.02);retained.Seek(.09);check(RetainedState()==retainedState,"Deferred strip removal reproduces exact age and clipped history after rewind");
        retained.Seek(.3);check(retained.Particles.Count==0&&retained.TotalBorn==1,"Strip particle releases its allocation when the tail drains without extending its emitting lifetime");
        var capacityCase=Retained(1);capacityCase.EmissionEnabled=_=>true;capacityCase.Seek(.08);
        check(capacityCase.TotalBorn==1&&capacityCase.CapacityDrops>0,"Retained strip tails continue consuming system capacity until removal");
        var spawning=Retained(100);spawning.DeathSpawn=(p,t)=>new(){Generations=1,Minimum=1,Maximum=1,Life=1};spawning.Seek(.08);
        check(spawning.TotalBorn>2&&spawning.Particles.Count(p=>p.Generation==0)==1,"Strip age/death retries its spawn-before-removal path while the parent tail is retained");
        var frozen=Retained();frozen.AgeEnabled=_=>false;frozen.Seek(.2);check(frozen.Particles.Single().Age==0&&frozen.Particles.Single().Strip!.Points[^1].Time>.19f,"Disabled age modifier preserves normal strip sampling rather than forcing lifetime expiry");
        string file=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\FF7Fenrir\vortex_BodyStart.nif");
        if(File.Exists(file)){
            var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(File.ReadAllBytes(file));
            var ps=nif.Blocks.OfType<BSStripParticleSystem>().First();var system=new ParticleNif(nif,ps,51);system.Seek(.5);
            check(system.Simulation.Particles.Any(p=>p.Strip?.Points.Count>1),"Real Anchor vortex particle modifier captures multi-point strip histories");
            string State()=>string.Join(";",system.Simulation.Particles.Select(p=>p.Strip==null?"none":$"{p.Strip.TailFraction}|{p.Strip.SegmentCount}|"+string.Join("/",p.Strip.Points)));
            var state=State();system.Seek(.1);system.Seek(.5);check(State()==state,"Real Anchor strip center histories reproduce after backward seek");
            check(system.Diagnostics.Any(d=>d.Contains("crossed surfaces")&&d.Contains("unfinished")),"Crossed-strip diagnostics retain explicit unfinished geometry and lifecycle cases");
        }
    }
}
