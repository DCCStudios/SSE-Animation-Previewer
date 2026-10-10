namespace SSEPreview;
internal static class ReferenceTimelineTests
{
    internal static void Run(Action<bool,string> check)
    {
        var records=new Records();var assets=new Assets();var diagnostics=new List<string>();
        var art=new GameRecord{Kind="ARTO",Plugin="fixture.esp",Key="fixture.esp|000001"};
        art.Fields.Add(new("MODL",System.Text.Encoding.UTF8.GetBytes(@"previewer\timeline.nif")));records.All[art.Key]=art;
        var reference=new GameRecord{Kind="RFCT",Plugin="fixture.esp",Key="fixture.esp|000002"};var data=new byte[12];BitConverter.GetBytes(1u).CopyTo(data,0);reference.Fields.Add(new("DATA",data));records.All[reference.Key]=reference;
        assets.External[@"meshes\previewer\timeline.nif"]="fixture";
        List<EffectCue> Build(params TimedReferenceEffect[] actions)=>ReferenceEffectTimeline.Build(assets,records,actions,diagnostics);
        var cues=Build(new(reference.Key,2,true),new(reference.Key,.5),new(reference.Key,1),new(reference.Key,3));
        check(cues.Count==3&&cues[0].RemovedAt==2&&cues[1].RemovedAt==2&&double.IsPositiveInfinity(cues[2].RemovedAt),"Reference-effect Stop ends all preceding instances but not later plays, independent of edit order");
        check(cues.All(c=>c.WorldSpace&&c.ArtLifecycle&&!c.FollowActor),"Manual reference effects use a fixed world origin and an explicit art lifecycle independent of actor following");
        check(!cues[0].ParticlesVisible(-.1)&&cues[0].ParticlesVisible(1.49)&&!cues[0].ParticlesVisible(1.5),"Reference effects without a behavior graph disappear at their exact stop time");
        check(double.IsPositiveInfinity(Build(new(reference.Key,1,true),new(reference.Key,1))[0].RemovedAt),"Equal-time stop then play preserves the new instance");
        check(Build(new(reference.Key,1),new(reference.Key,1,true))[0].RemovedAt==1,"Equal-time play then stop suppresses that instance");
        var effects=new Effects(assets,records);
        check(effects.Build(new AnimationClip(),false,references:new[]{new TimedReferenceEffect(reference.Key,.2)}).Single().Time==.2,"Reference-effect actions are included by the animation effect builder");
        check(Build(new TimedReferenceEffect("missing|000001",0)).Count==0&&diagnostics.Any(d=>d.StartsWith("Missing reference effect:")),"Missing reference-effect records are diagnosed instead of guessed");
        check(Build(new(reference.Key,double.NaN),new(reference.Key,-1)).Count==0,"Nonfinite and negative reference-effect action times are rejected");
        var positioned=Build(new(reference.Key,.2,Target:1,Position:new(-180,20,30),Heading:MathF.PI/2),
            new(reference.Key,.2,Target:2,Position:new(180,20,30)),new(reference.Key.ToUpperInvariant(),1,true,Target:1));
        check(positioned.Count==2&&positioned[0].RemovedAt==1&&double.IsPositiveInfinity(positioned[1].RemovedAt),"Reference-effect Stop is scoped to its target and resolves effect keys case-insensitively");
        check(positioned[0].LocalPlacement(0).Translation==new System.Numerics.Vector3(-180,20,30)&&positioned[1].Offset.X==180,"Reference effects retain independent world target positions");
        var direction=System.Numerics.Vector3.TransformNormal(System.Numerics.Vector3.UnitX,positioned[0].LocalPlacement(0));
        check(System.Numerics.Vector3.Distance(direction,System.Numerics.Vector3.UnitY)<1e-6,"Reference-effect target yaw rotates local artwork without rotating its world position");
        check(Build(new(reference.Key,0,Target:0),new(reference.Key,0,Position:new(float.NaN,0,0)),new(reference.Key,0,Heading:float.PositiveInfinity)).Count==0,"Invalid target identities, positions and headings cannot enter the renderer");
        var following=Build(new TimedReferenceEffect(reference.Key,.2,FollowCharacter:true)).Single();
        check(following.FollowActor&&!following.WorldSpace&&following.ArtLifecycle,"Following reference targets enable historical actor placement while retaining the art lifecycle");
        assets.External.Clear();check(Build(new TimedReferenceEffect(reference.Key,0)).Count==0,"Missing art files cannot create reference-effect timeline cues");
    }
}
