using System.Numerics;
using FO4_Base_Library;
using NiflySharp.Blocks;
using NiflySharp.Structs;

namespace SSEPreview;

// AE 1.6.1170 BSLagBoneController::Update, RVA E3EBC0. Fixed-step replay makes seeking deterministic.
internal sealed class NifLagControllers
{
    const double Step=1d/120;
    readonly Nifcontent_Class_Manolo nif;
    readonly Entry[] entries;
    readonly Dictionary<NiAVObject,NiNode> parents=[];
    long frame=-1;
    readonly Dictionary<long,Snapshot[]> history=[];
    readonly Queue<long> historyOrder=[];
    internal long ReplayedSteps {get;private set;}
    readonly record struct Snapshot(Vector3 Position,Matrix33 Rotation,float Scale,Matrix4x4 World);
    string? sequence;
    object? behavior;
    double stop;
    sealed class Entry(BSLagBoneController controller,NiNode node,NiNode parent)
    {
        public readonly BSLagBoneController Controller=controller;
        public readonly NiNode Node=node,Parent=parent;
        public readonly Vector3 RestPosition=node.Translation;
        public readonly Matrix33 RestRotation=node.Rotation;
        public readonly float RestScale=node.Scale;
        public Vector3 Position=node.Translation;
        public Matrix33 Rotation=node.Rotation;
        public float Scale=node.Scale;
        public Matrix4x4 PreviousWorld;
    }
    public NifLagControllers(Nifcontent_Class_Manolo nif,List<string> diagnostics)
    {
        this.nif=nif;
        foreach(var parent in nif.Blocks.OfType<NiNode>())
            foreach(var reference in parent.Children.References)
                if(nif.GetBlock(reference) is NiAVObject child)parents.TryAdd(child,parent);
        int Depth(NiAVObject node){int depth=0;var seen=new HashSet<NiAVObject>();while(parents.TryGetValue(node,out var p)&&seen.Add(node)){depth++;node=p;}return depth;}
        var list=new List<Entry>();
        foreach(var controller in nif.Blocks.OfType<BSLagBoneController>()){
            if(nif.GetBlock(controller.Target) is NiNode node&&parents.TryGetValue(node,out var parent))list.Add(new(controller,node,parent));
            else diagnostics.Add("BSLagBoneController: target has no supported parent node; lag is not simulated");
        }
        entries=list.OrderBy(e=>Depth(e.Node)).ToArray();
        if(entries.Length>0)diagnostics.Add("BSLagBoneController: native translation/distance and polynomial quaternion blend with deterministic 120 Hz replay; matrix-to-quaternion conversion and game frame cadence remain unverified");
    }
    internal bool Any=>entries.Length>0;
    internal void Reset(){frame=-1;history.Clear();historyOrder.Clear();}
    static Matrix4x4 Local(NiAVObject node)=>ParticleNif.Matrix(new Transform_Class{Rotation=node.Rotation,Translation=node.Translation,Scale=node.Scale}.ToMatrix4());
    Matrix4x4 World(NiAVObject node,Matrix4x4 placement)
    {
        var result=Local(node);var seen=new HashSet<NiAVObject>{node};
        while(parents.TryGetValue(node,out var parent)&&seen.Add(parent)){result*=Local(parent);node=parent;}
        return result*placement;
    }
    static Matrix33 RotationMatrix(Quaternion q)
    {
        var m=Matrix4x4.CreateFromQuaternion(q);
        return new(){M11=m.M11,M12=m.M12,M13=m.M13,M21=m.M21,M22=m.M22,M23=m.M23,M31=m.M31,M32=m.M32,M33=m.M33};
    }
    // Native branch deliberately uses distance squared versus (velocity * dt)^2, then a proportional step.
    internal static Vector3 AdvancePosition(Vector3 previous,Vector3 desired,float velocity,float maximumDistance,float dt)
    {
        if(velocity<=0||dt<=0)return previous;
        var delta=desired-previous;float square=delta.LengthSquared(),amount=velocity*dt;
        if(amount*amount>square)return desired;
        if(square>maximumDistance*maximumDistance){
            var correction=delta*((MathF.Sqrt(square)-maximumDistance)/MathF.Sqrt(square));
            delta=(delta-correction)*amount+correction;
        }else delta*=amount;
        return previous+delta;
    }
    // AE 1.6.1170 RVA D340E0: polynomial-adjusted normalized interpolation.
    // Unlike Quaternion.Slerp, this helper does not flip a negative-dot endpoint.
    internal static Quaternion BlendRotation(Quaternion from,Quaternion to,float amount)
    {
        float dot=((from.X*to.X+from.W*to.W)+from.Y*to.Y)+from.Z*to.Z;
        float correction=1-BitConverter.Int32BitsToSingle(0x3f52a2d1)*dot;
        correction=correction*correction*BitConverter.Int32BitsToSingle(0x3f15e2d1);
        float u=amount<=.5f?amount:1-amount;
        float weight=(((2*u-3)*(correction*u)+1)+correction)*u;
        if(amount>.5f)weight=1-weight;
        var result=from+(to-from)*weight;
        float square=((result.W*result.W+result.X*result.X)+result.Y*result.Y)+result.Z*result.Z;
        // A malformed antipodal midpoint is singular in the native routine.
        // Keep rendering finite instead of sending NaNs to the vertex buffer.
        return float.IsFinite(square)&&square>1e-20f?result*(1/MathF.Sqrt(square)):from;
    }
    // NIF transforms store signed uniform scale separately from rotation. General
    // affine decomposition may assign a reflection to one axis and invent a turn.
    internal static bool RotationFromUniformTransform(Matrix4x4 transform,out Quaternion rotation)
    {
        rotation=Quaternion.Identity;
        float magnitude=new Vector3(transform.M11,transform.M12,transform.M13).Length();
        float determinant=transform.GetDeterminant();
        if(!float.IsFinite(magnitude)||magnitude<1e-10f||!float.IsFinite(determinant)||determinant==0)return false;
        float scale=determinant<0?-magnitude:magnitude;
        var m=new Matrix4x4(transform.M11/scale,transform.M12/scale,transform.M13/scale,0,
            transform.M21/scale,transform.M22/scale,transform.M23/scale,0,
            transform.M31/scale,transform.M32/scale,transform.M33/scale,0,0,0,0,1);
        var q=Quaternion.CreateFromRotationMatrix(m);
        float square=q.LengthSquared();
        if(!float.IsFinite(square)||square<1e-10f)return false;
        rotation=Quaternion.Normalize(q);return true;
    }
    void Restore()
    {
        foreach(var e in entries){e.Node.Translation=e.Position;e.Node.Rotation=e.Rotation;e.Node.Scale=e.Scale;}
    }
    void Save(Matrix4x4 placement)
    {
        foreach(var e in entries){e.Position=e.Node.Translation;e.Rotation=e.Node.Rotation;e.Scale=e.Node.Scale;e.PreviousWorld=World(e.Node,placement);}
    }
    void Update(double dt,Matrix4x4 placement)
    {
        foreach(var e in entries){
            if(!NifAnimation.Started(e.Controller.Flags.Value))continue;
            var parent=World(e.Parent,placement);
            if(!Matrix4x4.Invert(parent,out var inverse))continue;
            var desired=Vector3.Transform(e.RestPosition,parent);
            // The native update skips both translation and rotation when the target displacement is zero.
            if(desired==e.PreviousWorld.Translation)continue;
            var previous=e.PreviousWorld;
            if(e.Controller.LinearVelocity>0){
                previous.Translation=AdvancePosition(previous.Translation,desired,e.Controller.LinearVelocity,e.Controller.MaximumDistance,(float)dt);
                e.Node.Translation=Vector3.Transform(previous.Translation,inverse);
            }
            if(e.Controller.LinearRotation>0&&RotationFromUniformTransform(previous*inverse,out var from)){
                var rest=new Matrix4x4(e.RestRotation.M11,e.RestRotation.M12,e.RestRotation.M13,0,e.RestRotation.M21,e.RestRotation.M22,e.RestRotation.M23,0,e.RestRotation.M31,e.RestRotation.M32,e.RestRotation.M33,0,0,0,0,1);
                var to=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(rest));
                e.Node.Rotation=RotationMatrix(BlendRotation(Quaternion.Normalize(from),to,Math.Clamp((float)dt*e.Controller.LinearRotation,0,1)));
            }
        }
    }
    void Remember()
    {
        if(history.ContainsKey(frame))return;
        history[frame]=entries.Select(e=>new Snapshot(e.Position,e.Rotation,e.Scale,e.PreviousWorld)).ToArray();historyOrder.Enqueue(frame);
        while(historyOrder.Count>512)history.Remove(historyOrder.Dequeue());
    }
    bool Recall(long target)
    {
        if(!history.TryGetValue(target,out var saved))return false;
        for(int i=0;i<entries.Length;i++){var e=entries[i];var s=saved[i];e.Position=s.Position;e.Rotation=s.Rotation;e.Scale=s.Scale;e.PreviousWorld=s.World;}
        frame=target;return true;
    }
    internal void Evaluate(double age,string? selected,object? graph,double stopAt,Func<double,Matrix4x4> placement,Action<double> authored)
    {
        if(!double.IsFinite(age)||age<0){authored(age);return;}
        if(selected!=sequence||!ReferenceEquals(graph,behavior)||stopAt!=stop){Reset();sequence=selected;behavior=graph;stop=stopAt;}
        long target=(long)Math.Floor(age/Step+1e-9);
        if(frame>=0&&target!=frame)Recall(target);
        if(frame<0||target<frame){
            Reset();
            foreach(var e in entries){e.Node.Translation=e.RestPosition;e.Node.Rotation=e.RestRotation;e.Node.Scale=e.RestScale;}
            authored(0);Save(placement(0));frame=0;Remember();
        }
        while(frame<target){double time=(frame+1)*Step;Restore();authored(time);var p=placement(time);Update(Step,p);Save(p);frame++;ReplayedSteps++;Remember();}
        Restore();authored(age);
        double remainder=age-frame*Step;
        if(remainder>1e-9)Update(remainder,placement(age));else Restore();
    }
}