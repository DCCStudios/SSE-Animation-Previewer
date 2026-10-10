using System.Collections;
using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using NiflySharp.Structs;
using M4=OpenTK.Mathematics.Matrix4;

namespace SSEPreview;
// Evaluate authored controllers from their absolute local time. Scrubbing is deterministic.
public sealed class NifAnimation
{
    readonly Nifcontent_Class_Manolo nif;
    readonly List<NiTimeController> controllers;
    readonly NiTimeController[] authoredControllers;
    readonly NifLagControllers lag;
    readonly NifBillboards billboards;
    public Matrix4x4 RenderWorld(INiShape shape,Matrix4x4 placement,OrbitCamera camera)=>billboards.Any?billboards.World((NiAVObject)shape,placement,camera):ParticleNif.Matrix(World(shape))*placement;
    public Func<double,Matrix4x4> Placement = _=>Matrix4x4.Identity;
    public void ResetMotion()=>lag.Reset();
    internal long ReplayedMotionSteps=>lag.ReplayedSteps;
    public List<string> Diagnostics {get;}=[];
    public NifAnimation(Nifcontent_Class_Manolo nif)
    {
        this.nif=nif;controllers=nif.Blocks.OfType<NiTimeController>().ToList();
        // Particle emitters/modifiers are sampled by ParticleNif, not applied to NIF nodes here.
        authoredControllers=controllers.Where(c=>c is NiTransformController or BSEffectShaderPropertyFloatController or BSEffectShaderPropertyColorController or BSLightingShaderPropertyFloatController or NiVisController or BSNiAlphaPropertyTestRefController).ToArray();
        foreach(var c in controllers)
            if(c is not NiTransformController and not BSEffectShaderPropertyFloatController and not BSEffectShaderPropertyColorController and not BSLightingShaderPropertyFloatController and not NiVisController and not BSNiAlphaPropertyTestRefController and not NiControllerManager and not NiMultiTargetTransformController and not BSLagBoneController and not BSPSysMultiTargetEmitterCtlr &&
               !c.GetType().Name.StartsWith("NiPSys"))
                Diagnostics.Add("Unsupported NIF controller: "+c.GetType().Name);
        lag=new(nif,Diagnostics);billboards=new(nif,Diagnostics);

    }
    // Called for every modifier field of every live particle each 120 Hz step; a GetProperty lookup per call
    // dominated simulation time on dense systems, so accessors are compiled once per (type, member).
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type,string),Func<object,object?>?> accessors=new();
    static Func<object,object?>? Accessor(Type type,string name)=>accessors.GetOrAdd((type,name),key=>{
        var (t,n)=key;var parameter=System.Linq.Expressions.Expression.Parameter(typeof(object));
        System.Linq.Expressions.Expression? body=null;
        var property=t.GetProperty(n);
        if(property!=null&&property.GetIndexParameters().Length==0&&property.GetMethod!=null)body=System.Linq.Expressions.Expression.Property(System.Linq.Expressions.Expression.Convert(parameter,t),property);
        else if(t.GetField(n) is {} field)body=System.Linq.Expressions.Expression.Field(System.Linq.Expressions.Expression.Convert(parameter,t),field);
        if(body==null)return null;
        return System.Linq.Expressions.Expression.Lambda<Func<object,object?>>(System.Linq.Expressions.Expression.Convert(body,typeof(object)),parameter).Compile();
    });
    public static object? Get(object? o,string p){if(o==null)return null;var get=Accessor(o.GetType(),p);if(get==null)return null;try{return get(o);}catch{return null;}}
    public static float Number(object? o,float fallback=0) {try{return o is NiBool nb?(nb.Value?1:0):o==null?fallback:Convert.ToSingle(o);}catch{return fallback;}}
    public static object[] Items(object? o) => o is NiRefArray refs?refs.References.Cast<object>().ToArray():o is IEnumerable e?e.Cast<object>().ToArray():[];
    public static object? Sample(object? group,double t)
    {
        var keys=Items(Get(group,"Keys"));if(keys.Length==0)return null;
        var type=Convert.ToInt32(Get(group,"Interpolation") ?? 1);return SampleKeys(keys,t,type);
    }
    public static object? SampleKeys(object[] keys,double t,int type=1)
    {
        if(keys.Length==0)return null;
        int hi=Array.FindIndex(keys,k=>Number(Get(k,"Time"))>=t);
        if(hi==0)return Get(keys[0],"Value");if(hi<0)return Get(keys[^1],"Value");
        object a=keys[hi-1],b=keys[hi];float ta=Number(Get(a,"Time")),tb=Number(Get(b,"Time")),u=(float)Math.Clamp((t-ta)/Math.Max(1e-7,tb-ta),0,1);
        object? va=Get(a,"Value"),vb=Get(b,"Value");
        if(type==5)return t==tb?vb:va;
        if(va is Quaternion qa && vb is Quaternion qb)return Quaternion.Slerp(qa,qb,u);
        float h00=2*u*u*u-3*u*u+1,h10=u*u*u-2*u*u+u,h01=-2*u*u*u+3*u*u,h11=u*u*u-u*u;
        if(va is float fa && vb is float fb) {
            if(type==2)return h00*fa+h10*Number(Get(a,"Forward"))+h01*fb+h11*Number(Get(b,"Backward"));
            return fa+(fb-fa)*u;
        }
        if(va is Vector3 pa && vb is Vector3 pb) {
            if(type==2)return h00*pa+h10*(Get(a,"Forward") is Vector3 af?af:Vector3.Zero)+h01*pb+h11*(Get(b,"Backward") is Vector3 bf?bf:Vector3.Zero);
            return Vector3.Lerp(pa,pb,u);
        }
        if(va is Color4 ca && vb is Color4 cb)return new Color4 { R=ca.R+(cb.R-ca.R)*u,G=ca.G+(cb.G-ca.G)*u,B=ca.B+(cb.B-ca.B)*u,A=ca.A+(cb.A-ca.A)*u };
        return va;
    }
    public object? Block(object? reference)=> reference is INiRef r?nif.GetBlock(r):null;
    public string? SelectedSequence;
    public EffectSequenceGraph? Behavior;
    public double StopAt=double.PositiveInfinity;
    IEnumerable<(double Time,string Event)> SequenceEvents(string name)
    {
        var sequence=nif.Blocks.OfType<NiControllerSequence>().FirstOrDefault(s=>s.Name.ToString()==name);
        if(sequence==null||sequence.Frequency<=0)yield break;
        var text=Block(sequence.TextKeys_NiBlockRef_NTKED);
        foreach(var key in Items(Get(text,"TextKeys"))){
            double time=(Number(Get(key,"Time"))-sequence.StartTime)/sequence.Frequency;
            foreach(var ev in (Get(key,"Value")?.ToString()??"").Split('\n',StringSplitOptions.RemoveEmptyEntries))
                yield return (time,ev.Trim());
        }
    }
    public EffectSequenceGraph.Phase? Phase(double age)=>SelectedSequence==null?Behavior?.At(age,StopAt,SequenceEvents):null;
    public bool Finished(double age)=>Phase(age)?.Finished??false;
    public string[] SequenceNames=>nif.Blocks.OfType<NiControllerSequence>().Select(s=>s.Name.ToString()).ToArray();
    public double AuthoredDuration=>controllers.Where(c=>float.IsFinite(c.StopTime)&&c.StopTime>0).Select(c=>(double)c.StopTime).DefaultIfEmpty(0).Max();
    public object? ResolveInterpolator(NiTimeController controller,string property,double age,out double time)
    {
        time=ControllerTime(controller,age);
        var original=Block(Get(controller,property));
        if(original==null||!original.GetType().Name.StartsWith("NiBlend"))return original;
        var sequence=Sequence(age,out time);if(sequence==null)return original;
        string id=property=="VisibilityInterpolator"?"EmitterActive":"BirthRate";
        foreach(var binding in Items(Get(sequence,"ControlledBlocks"))){
            var boundController=Block(Get(binding,"Controller"));
            bool match=ReferenceEquals(boundController,controller);
            if(!match){
                string node=Get(binding,"NodeName")?.ToString()??"",type=Get(binding,"ControllerType")?.ToString()??"",cid=Get(binding,"ControllerID")?.ToString()??"";
                match=node==(Get(Block(controller.Target),"Name")?.ToString()??"")&&type==controller.GetType().Name&&(cid.Length==0||cid==(Get(controller,"ModifierName")?.ToString()??""));
            }
            string iid=Get(binding,"InterpolatorID")?.ToString()??"";
            if(match&&(controller is not NiPSysEmitterCtlr||iid==id||iid.Length==0))return Block(Get(binding,"Interpolator"));
        }
        return original;
    }
    static bool IsPhase(string name,string phase)=>name.EndsWith(phase,StringComparison.OrdinalIgnoreCase);
    NiControllerSequence? Sequence(double age,out double time)
    {
        var sequences=nif.Blocks.OfType<NiControllerSequence>().ToArray();
        var phase=Phase(age);
        if(phase?.Finished==true){time=age;return null;}
        if(phase!=null)age=phase.LocalAge;
        var sequence=sequences.FirstOrDefault(s=>s.Name.ToString().Equals(phase?.Sequence??SelectedSequence,StringComparison.OrdinalIgnoreCase));
        if(phase!=null&&sequence==null){time=age;return null;}
        // Vanilla casting art (Magic\CastingWithIntro) names its phases mIntro/mIdle; without the behavior graph the
        // automatic path treats Intro like Begin and Idle like Loop so the art keeps animating instead of clamping.
        sequence??=sequences.FirstOrDefault(s=>IsPhase(s.Name.ToString(),"Start")||IsPhase(s.Name.ToString(),"Begin"))??sequences.FirstOrDefault(s=>IsPhase(s.Name.ToString(),"Intro"))??sequences.FirstOrDefault();
        if(sequence==null){time=age;return null;}
        var loop=sequences.FirstOrDefault(s=>IsPhase(s.Name.ToString(),"Loop"))??sequences.FirstOrDefault(s=>IsPhase(s.Name.ToString(),"Idle"));
        if(phase==null&&SelectedSequence==null&&loop!=null&&sequence!=loop&&age>=Math.Max(0,sequence.StopTime-sequence.StartTime)/Math.Max(.000001,sequence.Frequency)){age-=Math.Max(0,sequence.StopTime-sequence.StartTime)/Math.Max(.000001,sequence.Frequency);sequence=loop;}
        double duration=sequence.StopTime-sequence.StartTime;
        time=sequence.StartTime+age*sequence.Frequency;
        if(duration>0){int cycle=(int)sequence.CycleType;if(cycle==0)time=sequence.StartTime+(time-sequence.StartTime)%duration;else if(cycle==1){double p=(time-sequence.StartTime)%(duration*2);time=sequence.StartTime+(p<=duration?p:duration*2-p);}else time=Math.Clamp(time,sequence.StartTime,sequence.StopTime);}
        return sequence;
    }
    object? Interpolate(object? interpolator,double time)
    {
        if(interpolator==null)return null;
        var data=Block(Get(interpolator,"Data"));return Sample(Get(data,"Data"),time) ?? Get(interpolator,"Value");
    }
    public static double ControllerTime(NiTimeController c,double age)
    {
        double t=age*c.Frequency+c.Phase,start=c.StartTime,end=c.StopTime,length=end-start;
        int cycle=Convert.ToInt32(Get(c.Flags,"CycleType") ?? 2);
        if(length<=0 || !double.IsFinite(length))return start;
        if(cycle==0)return start+((t-start)%length+length)%length;
        if(cycle==1) {double p=((t-start)%(length*2)+length*2)%(length*2);return start+(p<=length?p:length*2-p);}
        return Math.Clamp(t,start,end);
    }
    void ApplyTransform(NiAVObject node,object? interpolator,double t)
    {
                var data=Block(Get(interpolator,"Data"));var tr=Get(interpolator,"Transform");
                object? p=Sample(Get(data,"Translations"),t) ?? Get(tr,"Translation");
                if(p is Vector3 pos && pos.X>-1e30f)node.Translation=pos;
                object? s=Sample(Get(data,"Scales"),t) ?? Get(tr,"Scale");if(s is float scale&&scale>-1e30f)node.Scale=scale;
                var q=SampleKeys(Items(Get(data,"QuaternionKeys")),t) ?? Get(tr,"Rotation");
                if(q is Quaternion rot && rot.X>-1e30f && rot.LengthSquared()>.01f) {
                    var m=Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rot));
                    node.Rotation=new Matrix33 {M11=m.M11,M12=m.M12,M13=m.M13,M21=m.M21,M22=m.M22,M23=m.M23,M31=m.M31,M32=m.M32,M33=m.M33};
                }
                var xyz=Items(Get(data,"XYZRotations"));
                if(xyz.Length==3) {
                    var m=Matrix4x4.CreateRotationX(Number(Sample(xyz[0],t)))*Matrix4x4.CreateRotationY(Number(Sample(xyz[1],t)))*Matrix4x4.CreateRotationZ(Number(Sample(xyz[2],t)));
                    node.Rotation=new Matrix33 {M11=m.M11,M12=m.M12,M13=m.M13,M21=m.M21,M22=m.M22,M23=m.M23,M31=m.M31,M32=m.M32,M33=m.M33};
                }
    }
    // NiTimeController flags: bit 0 = APP_INIT (time counted from when the effect starts), bit 3 = active.
    // One-shot effects are saved as APP_INIT with the active bit clear (flags 69: every manager-less vanilla
    // impact emitter, e.g. icicleimpact01.nif) and are started when the game spawns the effect. A preview begins
    // at effect time zero, so APP_INIT controllers count as started. APP_TIME controllers saved inactive (68)
    // stay off. That the engine sets the active bit at spawn is inferred from Gamebryo's StartAnimations, not
    // read from the 1.6.1170 executable.
    internal static bool Started(uint flags)=>(flags&8)!=0||(flags&1)!=0;
    internal bool SampleTemplateVisibility(uint flags,IEnumerable<NiVisController> chain,double age)
    {
        bool visible=(flags&1)==0;
        foreach(var controller in chain)if(Started(Convert.ToUInt16(Get(controller.Flags,"Value")))){
            var value=Interpolate(Block(Get(controller,"Interpolator")),ControllerTime(controller,age));
            if(value!=null)visible=Number(value)>0;
        }
        return visible;
    }
    internal Matrix4x4 SampleTemplateTransform(NiAVObject authored,IEnumerable<NiTransformController> chain,double age)
    {
        // A private temporary target prevents one particle's age from changing shared NIF state.
        var target=new NiNode{Translation=authored.Translation,Rotation=authored.Rotation,Scale=authored.Scale};
        foreach(var c in chain)if(Started(Convert.ToUInt16(Get(c.Flags,"Value"))))
            ApplyTransform(target,Block(Get(c,"Interpolator")),ControllerTime(c,age));
        return ParticleNif.Matrix(new Transform_Class{Translation=target.Translation,Rotation=target.Rotation,Scale=target.Scale}.ToMatrix4());
    }
    public void Evaluate(double age)
    {
        parentWorlds.Clear();
        if(lag.Any)lag.Evaluate(age,SelectedSequence,Behavior,StopAt,Placement,EvaluateAuthored);else EvaluateAuthored(age);
    }
    void EvaluateAuthored(double age)
    {
        var sequence=Sequence(age,out double sequenceTime);
        if(sequence!=null)foreach(var binding in Items(Get(sequence,"ControlledBlocks"))){
            var controller=Block(Get(binding,"Controller"));
            string type=Get(binding,"ControllerType")?.ToString()??"";
            if(controller is not NiMultiTargetTransformController&&!(controller==null&&type=="NiTransformController"))continue;
            string name=Get(binding,"NodeName")?.ToString()??"";
            var node=nif.Blocks.OfType<NiAVObject>().FirstOrDefault(n=>n.Name.ToString()==name);
            if(node!=null)ApplyTransform(node,Block(Get(binding,"Interpolator")),sequenceTime);
        }
        visTracks??=BuildVisTracks();
        foreach(var track in visTracks){
            double vt=ControllerTime(track.Controller,age);var times=track.Times;float value;
            if(times.Length==0)value=track.Fallback;
            else{
                // Same rule as SampleKeys for non-interpolated values: first key at or after t; constant keys switch on the key time.
                int hi=-1;for(int i=0;i<times.Length;i++)if(times[i]>=vt){hi=i;break;}
                value=hi==0?track.Values[0]:hi<0?track.Values[^1]:track.Constant&&vt==times[hi]?track.Values[hi]:track.Values[hi-1];
            }
            if(float.IsNaN(value))continue;
            track.Target.Flags_ui=(track.Target.Flags_ui&~1u)|(value>0?0u:1u);
        }
        foreach(var c in authoredControllers) {
            if(fastVis.Contains(c))continue;
            var target=Block(c.Target);var interpolator=ResolveInterpolator(c,"Interpolator",age,out double t);
            if(c is BSNiAlphaPropertyTestRefController&&target is NiAlphaProperty alpha){
                float threshold=Number(Interpolate(interpolator,t),float.NaN);
                if(float.IsFinite(threshold))alpha.Threshold=(byte)Math.Clamp(threshold,0,255);
            }
            else if(c is NiVisController&&target is NiAVObject visibleNode){
                var value=Interpolate(interpolator,t);
                if(value!=null){bool visible=Number(value)>0;visibleNode.Flags_ui=(visibleNode.Flags_ui&~1u)|(visible?0u:1u);}
            }
            else if(c is NiTransformController && target is NiAVObject node) {
                ApplyTransform(node,interpolator,t);
            }
            else if(c is BSEffectShaderPropertyFloatController && target is BSEffectShaderProperty effect) {
                float v=Number(Interpolate(interpolator,t),float.NaN);if(!float.IsFinite(v)||v < -1e30f)continue;
                var variable=Get(c,"ControlledVariable")?.ToString() ?? "";
                if(variable=="AlphaTransparency"){var color=effect.BaseColor;color.A=v;effect.BaseColor=color;}
                if(variable=="EmissiveMultiple")effect.BaseColorScale=v;
                if(variable=="FalloffStartAngle")effect.FalloffStartAngle=v;
                if(variable=="FalloffStopAngle")effect.FalloffStopAngle=v;
                if(variable=="FalloffStartOpacity")effect.FalloffStartOpacity=v;
                if(variable=="FalloffStopOpacity")effect.FalloffStopOpacity=v;
                if(variable is "UOffset" or "VOffset") {var uv=effect.UVOffset;if(variable=="UOffset")uv.U=v;else uv.V=v;effect.UVOffset=uv;}
                if(variable is "UScale" or "VScale") {var uv=effect.UVScale;if(variable=="UScale")uv.U=v;else uv.V=v;effect.UVScale=uv;}
            }
            else if(c is BSEffectShaderPropertyColorController && target is BSEffectShaderProperty es && Interpolate(interpolator,t) is Vector3 color) {var col=es.BaseColor;col.R=color.X;col.G=color.Y;col.B=color.Z;es.BaseColor=col;}
            else if(c is BSLightingShaderPropertyFloatController && target is BSLightingShaderProperty lit) {
                float v=Number(Interpolate(interpolator,t),float.NaN);if(!float.IsFinite(v)||v < -1e30f)continue;
                switch(Get(c,"ControlledVariable")?.ToString()) {
                    case "Alpha":case "AlphaTransparency":lit.Alpha=v;break;
                    case "EmissiveMultiple":lit.EmissiveMultiple=v;break;
                    case "UOffset":{var uv=lit.UVOffset;uv.U=v;lit.UVOffset=uv;break;}
                    case "VOffset":{var uv=lit.UVOffset;uv.V=v;lit.UVOffset=uv;break;}
                    case "UScale":{var uv=lit.UVScale;uv.U=v;lit.UVScale=uv;break;}
                    case "VScale":{var uv=lit.UVScale;uv.V=v;lit.UVScale=uv;break;}
                }
            }
        }
    }
    // Visibility controllers with their own bool key data (no blend interpolator) are flattened to arrays once;
    // a baked flipbook effect has one per shape and the generic path boxes every key on every frame.
    sealed record VisTrack(NiTimeController Controller,NiAVObject Target,float[] Times,float[] Values,bool Constant,float Fallback);
    VisTrack[]? visTracks;readonly HashSet<NiTimeController> fastVis=new(ReferenceEqualityComparer.Instance);
    VisTrack[] BuildVisTracks()
    {
        var list=new List<VisTrack>();
        foreach(var c in authoredControllers){
            if(c is not NiVisController||Block(c.Target) is not NiAVObject target)continue;
            var interpolator=Block(Get(c,"Interpolator"));
            if(interpolator is not NiBoolInterpolator)continue;
            var group=Get(Block(Get(interpolator,"Data")),"Data");var keys=Items(Get(group,"Keys"));
            object? fallback=Get(interpolator,"Value");
            list.Add(new(c,target,keys.Select(k=>Number(Get(k,"Time"))).ToArray(),keys.Select(k=>Number(Get(k,"Value"))).ToArray(),
                Convert.ToInt32(Get(group,"Interpolation")??1)==5,fallback==null?float.NaN:Number(fallback)));
            fastVis.Add(c);
        }
        return list.ToArray();
    }
    // NifFile.GetParentNode scans every block per call. Baked effects carry thousands of shapes that are
    // each tested every frame, so the first parent of every child is recorded once (same first-match rule).
    Dictionary<object,NiNode>? parents;
    readonly Dictionary<NiNode,M4> parentWorlds=new(ReferenceEqualityComparer.Instance);
    NiNode? Parent(object child)
    {
        if(parents==null){
            parents=new(ReferenceEqualityComparer.Instance);
            foreach(var node in nif.Blocks.OfType<NiNode>())foreach(int index in node.Children.Indices)
                if(index>=0&&index<nif.Blocks.Count)parents.TryAdd(nif.Blocks[index],node);
        }
        return parents.GetValueOrDefault(child);
    }
    public bool Visible(INiShape shape)=>VisibleObject(shape,shape.Flags_ui);
    public bool VisibleNode(NiAVObject shape)=>VisibleObject(shape,shape.Flags_ui);
    bool VisibleObject(object shape,uint flags)
    {
        if((flags&1)!=0)return false;
        var parent=Parent(shape);int guard=0;
        while(parent!=null&&guard++<256){
            if((parent.Flags_ui&1)!=0)return false;
            parent=Parent(parent);
        }
        return true;
    }
    public M4 World(INiShape shape)
    {
        var local=new Transform_Class(shape);var parent=Parent(shape);
        if(parent==null)return local.ToMatrix4();
        // Node transforms only change inside Evaluate, so a parent's world matrix is resolved once per evaluation.
        if(!parentWorlds.TryGetValue(parent,out var parentWorld))parentWorlds[parent]=parentWorld=Transform_Class.GetGlobalTransform(parent,nif).ToMatrix4();
        return local.ToMatrix4()*parentWorld;
    }
}
