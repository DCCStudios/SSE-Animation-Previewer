using FO4_Base_Library;
using FO4_Base_Library.Havok.Canon;
using NiflySharp.Blocks;
namespace SSEPreview;

// A deliberately validated subset: flat Gamebryo-sequence state machines with
// unconditional, immediate event transitions. Unsupported graphs remain explicit.
public sealed class EffectSequenceGraph
{
    public sealed record State(int Id,string Sequence,Dictionary<string,int> Transitions,string[] ExitEvents);
    public sealed record Phase(string? Sequence,double LocalAge,bool Finished);
    readonly Dictionary<int,State> states;
    readonly int initial;
    readonly HashSet<string> events;
    public IEnumerable<string> Sequences=>states.Values.Select(s=>s.Sequence).Distinct();
    EffectSequenceGraph(int initial,Dictionary<int,State> states,IEnumerable<string> events){this.initial=initial;this.states=states;this.events=new(events,StringComparer.Ordinal);}
    static Dictionary<string,object> Fields(HkxObjectGraph_Class graph,object? reference)
    {
        if(reference is not HkxVirtualObjectGraph_Class obj)throw new InvalidDataException("Missing behavior object");
        return HavokObjetoGenerico.Leer(graph,obj)??throw new InvalidDataException("Unsupported behavior object "+obj.ClassName);
    }
    static object? Field(Dictionary<string,object> fields,string name)=>fields.GetValueOrDefault(name);
    static int Int(object? value)=>Convert.ToInt32(value);
    public static EffectSequenceGraph Parse(byte[] bytes)
    {
        var graph=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(bytes));
        var machines=graph.GetObjectsByClassName("hkbStateMachine").ToArray();
        if(machines.Length!=1)throw new InvalidDataException("Only one flat effect state machine is supported");
        var machine=Fields(graph,machines[0]);
        if(Field(machine,"wildcardTransitions")!=null||Int(Field(machine,"startStateMode"))!=0)
            throw new InvalidDataException("Effect graph uses unsupported state selection or wildcard transitions");
        var strings=Fields(graph,graph.GetObjectsByClassName("hkbBehaviorGraphStringData").Single());
        var events=NifAnimation.Items(Field(strings,"eventNames")).Select(x=>x.ToString()??"").ToArray();
        string Event(int id)=>id>=0&&id<events.Length?events[id]:throw new InvalidDataException("Invalid effect event id");
        Dictionary<int,State> states=[];
        foreach(var obj in graph.GetObjectsByClassName("hkbStateMachineStateInfo")){
            var f=Fields(graph,obj);
            if(!Convert.ToBoolean(Field(f,"enable")))throw new InvalidDataException("Disabled effect state");
            if(Field(f,"generator") is not HkxVirtualObjectGraph_Class generator||generator.ClassName!="BGSGamebryoSequenceGenerator")
                throw new InvalidDataException("Effect state is not a Gamebryo sequence");
            var gen=Fields(graph,generator);
            if(Int(Field(gen,"eUseTimePercentage"))!=0||Int(Field(gen,"eBlendModeFunction"))!=0||NifAnimation.Number(Field(gen,"fPercent"),1)!=1)throw new InvalidDataException("Percentage-driven effect sequence");
            var sequence=Field(gen,"pSequence")?.ToString()??"";
            Dictionary<string,int> transitions=new(StringComparer.Ordinal);
            if(Field(f,"transitions") is {} tr)foreach(var item in NifAnimation.Items(Field(Fields(graph,tr),"transitions"))){
                if(NifAnimation.Get(item,"Condition")!=null||(Int(NifAnimation.Get(item,"Flags"))&~256)!=0)
                    throw new InvalidDataException("Conditional or timed effect transition");
                if(NifAnimation.Get(item,"Transition") is {} blend){
                    var bf=Fields(graph,blend);
                    if(NifAnimation.Number(Field(bf,"duration"))!=0||NifAnimation.Number(Field(bf,"toGeneratorStartTimeFraction"))!=0)
                        throw new InvalidDataException("Blended effect transition");
                }
                if(!transitions.TryAdd(Event(Int(NifAnimation.Get(item,"EventId"))),Int(NifAnimation.Get(item,"ToStateId"))))
                    throw new InvalidDataException("Ambiguous effect transition");
            }
            if(Field(f,"enterNotifyEvents")!=null)throw new InvalidDataException("Effect state entry events are not supported");
            string[] exits=Field(f,"exitNotifyEvents") is {} exit?NifAnimation.Items(Field(Fields(graph,exit),"events")).Select(e=>Event(Int(NifAnimation.Get(e,"Id")))).ToArray():[];
            if(exits.Any(e=>e!="EndAnim"))throw new InvalidDataException("Unsupported effect state exit event");
            int id=Int(Field(f,"stateId"));states.Add(id,new(id,sequence,transitions,exits));
        }
        int initial=Int(Field(machine,"startStateId"));
        if(!states.ContainsKey(initial)||states.Values.Any(s=>s.Transitions.Values.Any(id=>!states.ContainsKey(id))))
            throw new InvalidDataException("Effect transition targets an absent state");
        return new(initial,states,events);
    }
    public static EffectSequenceGraph? Load(Assets assets,string path)
    {
        // NIF paths are relative to meshes; dependency paths are relative to the project directory.
        path=Assets.Normalize(path);if(!path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))path=@"meshes\"+path;
        string projectDirectory=Path.GetDirectoryName(path)??"meshes";
        for(int depth=0;depth<3;depth++){
            path=Assets.Normalize(path);if(!path.StartsWith(@"meshes\",StringComparison.OrdinalIgnoreCase))path=Path.Combine(projectDirectory,path);
            var bytes=assets.Read(path);
            var graph=HkxObjectGraphParser_Class.BuildGraph(HkxPackfileParser_Class.Parse(bytes));
            if(graph.GetObjectsByClassName("hkbStateMachine").Any())return Parse(bytes);
            var project=graph.GetObjectsByClassName("hkbProjectStringData").FirstOrDefault();
            if(project!=null){
                var names=NifAnimation.Items(Field(Fields(graph,project),"characterFilenames"));
                if(names.Length!=1)throw new InvalidDataException("Effect project has multiple characters");
                path=names[0].ToString()!;continue;
            }
            var character=graph.GetObjectsByClassName("hkbCharacterStringData").FirstOrDefault();
            if(character!=null){path=Field(Fields(graph,character),"behaviorFilename")?.ToString()??"";continue;}
            throw new InvalidDataException("Unsupported effect graph root");
        }
        throw new InvalidDataException("Effect graph dependency chain is too deep");
    }
    public Phase At(double age,double stopAt,Func<string,IEnumerable<(double Time,string Event)>> keys)
    {
        var state=states[initial];double entered=0;bool sentStop=false;
        for(int iteration=0;iteration<256;iteration++){
            if(double.IsFinite(stopAt)&&age>stopAt+3)return new(null,0,true); // AE teardown watchdog.
            var next=keys(state.Sequence).Where(k=>k.Time>=0&&events.Contains(k.Event)&&(k.Event=="EndAnim"||state.Transitions.ContainsKey(k.Event)))
                .OrderBy(k=>k.Time).FirstOrDefault((Time:double.PositiveInfinity,Event:""));
            double eventTime=entered+next.Time;
            bool stopping=!sentStop&&stopAt<=eventTime;
            if(stopping)eventTime=stopAt;
            if(eventTime>age||!double.IsFinite(eventTime))return new(state.Sequence,Math.Max(0,age-entered),false);
            string ev=stopping?"StopEffect":next.Event;
            if(stopping){sentStop=true;if(!events.Contains("StopEffect"))return new(null,0,true);}
            if(ev=="EndAnim")return new(null,0,true);
            if(state.Transitions.TryGetValue(ev,out int id)){
                if(state.ExitEvents.Contains("EndAnim"))return new(null,0,true);
                state=states[id];entered=eventTime;
            }else if(stopping){
                // StopEffect can be ignored in an authored state (e.g. mBegin).
                // Process its normal next transition, but never resend the stop event.
            }else return new(state.Sequence,Math.Max(0,age-entered),false);
        }
        throw new InvalidDataException("Effect graph exceeded the immediate-transition limit");
    }
}
