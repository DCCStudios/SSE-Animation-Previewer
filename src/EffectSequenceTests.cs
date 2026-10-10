using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using NiflySharp.Enums;
using NiflySharp.Bitfields;
namespace SSEPreview;
internal static class EffectSequenceTests
{
    public static string Create(string directory)
    {
        var n=new NifFile();if(n.Load(ParticleFixture.Create(directory))!=0)throw new InvalidDataException("Cannot load sequence fixture source");
        var root=n.Blocks.OfType<NiNode>().First();
        root.ExtraDataList.AddBlockRef(n.AddBlock(new BSBehaviorGraphExtraData{BehaviourGraphFile=new(@"Magic\BeginLoopEnd.hkx")}));
        var shader=n.Blocks.OfType<BSEffectShaderProperty>().Single();
        var controller=new BSEffectShaderPropertyFloatController{Target=new(n.Blocks.IndexOf(shader)),Frequency=1,StartTime=0,StopTime=10,
            Flags=new TimeControllerFlags{Value=76},ControlledVariable=EffectShaderControlledVariable.AlphaTransparency,
            Interpolator=new(n.AddBlock(new NiBlendFloatInterpolator()))};
        int ci=n.AddBlock(controller);shader.Controller=new(ci);
        var manager=new NiControllerManager{Target=new(n.Blocks.IndexOf(root)),Frequency=1,ControllerSequences=new()};
        int mi=n.AddBlock(manager);root.Controller=new(mi);
        foreach(var name in new[]{"mBegin","mLoop","mEnd"}){
            bool end=name=="mEnd";float length=end?.4f:name=="mBegin"?.25f:1;
            var keys=new NiTextKeyExtraData{NumTextKeys=1,TextKeys=[new Key<NiStringRef>{Time=length,Value=new(end?"EndAnim":"end")}]};
            var interp=new NiFloatInterpolator{Value=1};
            if(end)interp.Data=new(n.AddBlock(new NiFloatData{Data=new KeyGroup<float>{NumKeys=2,Interpolation=(KeyType)1,
                Keys=[new Key<float>{Time=0,Value=1},new Key<float>{Time=length,Value=0}]}}));
            var sequence=new NiControllerSequence{Name=new(name),Manager=new(mi),StartTime=0,StopTime=length,Frequency=1,CycleType=(CycleType)(name=="mLoop"?0:2),
                TextKeys_NiBlockRef_NTKED=new(n.AddBlock(keys)),NumControlledBlocks=1,ControlledBlocks=[new ControlledBlock{Controller=new(ci),Interpolator=new(n.AddBlock(interp)),
                    NodeName=new("Authored fountain"),PropertyType=new("BSEffectShaderProperty"),ControllerType=new("BSEffectShaderPropertyFloatController")}]};
            manager.ControllerSequences.AddBlockRef(n.AddBlock(sequence));
        }
        string path=Path.Combine(directory,@"meshes\previewer\sequence-end.nif");
        if(n.Save(path)!=0)throw new InvalidDataException("Cannot save ending sequence fixture");return path;
    }
    public static void Run()
    {
        EffectGraphInspection.Run();
        var folder=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\verification\effect-graph"));
        var graph=EffectSequenceGraph.Parse(File.ReadAllBytes(Path.Combine(folder,"beginloopendbehavior.hkx")));
        List<string> log=[];
        void Check(bool ok,string message){log.Add((ok?"PASS ":"FAIL ")+message);File.WriteAllLines(Path.Combine(folder,"tests.txt"),log);if(!ok)throw new InvalidDataException(message);}
        IEnumerable<(double,string)> Keys(string s)=>s=="mBegin"?[(.25,"end")]:s=="mEnd"?[(.4,"EndAnim")]:[];
        Check(graph.At(.1,1,Keys).Sequence=="mBegin","Vanilla graph selects its declared starting sequence");
        Check(graph.At(.5,1,Keys).Sequence=="mLoop","Authored end event transitions begin to loop");
        Check(graph.At(1.2,1,Keys) is {Sequence:"mEnd",Finished:false},"StopEffect selects the actual transition target");
        Check(graph.At(1.5,1,Keys).Finished,"EndAnim finishes the effect before the native watchdog");
        Check(graph.At(1,.1,Keys).Sequence=="mLoop","StopEffect during begin is ignored when no authored transition accepts it");
        Check(graph.At(3.11,.1,Keys).Finished,"Ignored stop event still reaches the three-second native timeout");
        Check(graph.At(1.2,1,s=>s=="mBegin"?[(.25,"end")]:[]).Sequence=="mEnd","Missing end animation does not silently replay the loop");
        Check(!graph.At(4,1,s=>s=="mBegin"?[(.25,"end")]:[]).Finished&&graph.At(4.001,1,s=>s=="mBegin"?[(.25,"end")]:[]).Finished,"Native watchdog uses a strict greater-than boundary");
        var changedBytes=File.ReadAllBytes(Path.Combine(folder,"beginloopendbehavior.hkx"));
        var stopText=System.Text.Encoding.ASCII.GetBytes("StopEffect");
        int stopIndex=changedBytes.AsSpan().IndexOf(stopText);
        System.Text.Encoding.ASCII.GetBytes("HideEffect").CopyTo(changedBytes,stopIndex);
        var changedGraph=EffectSequenceGraph.Parse(changedBytes);
        Check(changedGraph.At(1.2,1,Keys).Finished,"Loaded transition event names are respected instead of assuming every graph accepts StopEffect");
        string weapon=Path.Combine(SelfTest.Mods,@"Anchor MCO Animations v2 - Cloud Fenrir\meshes\magiceffects\greatsword\BerserkerWeapon.nif");
        var realNif=new Nifcontent_Class_Manolo();realNif.Load_Manolo(File.ReadAllBytes(weapon));
        var real=new ParticleNif(realNif,realNif.Blocks.OfType<NiParticleSystem>().Single(p=>p.Name.ToString()=="pFlames"));
        real.Animation.Behavior=graph;real.Animation.StopAt=1;
        real.Seek(.5);
        Check(real.Animation.Phase(.5)?.Sequence=="mLoop"&&real.Simulation.Particles.Count>0,"Real Anchor weapon emission follows its referenced begin/loop graph");
        Check(real.Animation.Phase(1.1)?.Sequence=="mEnd"&&real.Animation.Finished(4.001),"Real Anchor missing mEnd reaches native teardown timeout");
        string fixture=Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,@"..\..\..\..\particle-fixtures")));
        var n=new Nifcontent_Class_Manolo();n.Load_Manolo(File.ReadAllBytes(fixture));
        var animation=new NifAnimation(n){Behavior=graph,StopAt=1};
        var shader=n.Blocks.OfType<BSEffectShaderProperty>().Single();
        File.WriteAllLines(Path.Combine(folder,"nif-sequences.txt"),n.Blocks.OfType<NiControllerSequence>().Select(s=>s.Name+" keys="+s.TextKeys_NiBlockRef_NTKED?.Index+" bindings="+s.ControlledBlocks?.Count)
            .Concat(n.Blocks.OfType<NiTextKeyExtraData>().SelectMany(t=>t.TextKeys.Select(k=>k.Time+" "+k.Value))));
        animation.Evaluate(.5);File.AppendAllText(Path.Combine(folder,"nif-sequences.txt"),"\n"+animation.Phase(.5)+" alpha="+shader.BaseColor.A);
        Check(animation.Phase(.5)?.Sequence=="mLoop"&&shader.BaseColor.A==1,"Serialized NIF text keys drive the loop and its alpha");
        animation.Evaluate(1.2);Check(Math.Abs(shader.BaseColor.A-.5)<.001,"Authored ending alpha interpolates at local ending time");
        Check(animation.Finished(1.5),"Serialized ending text key completes the effect");
        File.WriteAllLines(Path.Combine(folder,"nif-sequences.txt"),n.Blocks.OfType<NiControllerSequence>().Select(s=>s.Name+" keys="+s.TextKeys_NiBlockRef_NTKED?.Index+" bindings="+s.ControlledBlocks?.Count)
            .Concat(n.Blocks.OfType<NiTextKeyExtraData>().SelectMany(t=>t.TextKeys.Select(k=>k.Time+" "+k.Value))));
        animation.Evaluate(.5);File.AppendAllText(Path.Combine(folder,"nif-sequences.txt"),"\n"+animation.Phase(.5)+" alpha="+shader.BaseColor.A);
        Check(shader.BaseColor.A==1&&!animation.Finished(.5),"Backward seek reconstructs the pre-ending loop");
        animation.SelectedSequence="mLoop";animation.Evaluate(5);
        Check(!animation.Finished(5)&&shader.BaseColor.A==1,"Explicit sequence inspection overrides automatic graph teardown");
    }
}
