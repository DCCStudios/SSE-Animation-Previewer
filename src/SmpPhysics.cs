using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using BulletSharp;
using BMatrix=BulletSharp.Math.Matrix;
using BVector=BulletSharp.Math.Vector3;
using M4d=OpenTK.Mathematics.Matrix4d;
using Qd=OpenTK.Mathematics.Quaterniond;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// HDT-SMP simulation, following FSMP (hdtSkyrimSystem, hdtSkyrimBone, hdtSkinnedMeshWorld, hdtSkyrimPhysicsWorld).
// Bones named in a physics XML become Bullet rigid bodies with no shape. Mass 0 makes a bone kinematic: it follows
// the animated skeleton and drags the simulated bones through their constraints. Collision comes from SmpCollision
// and enters the solver as hand-built contact manifolds, as it does in the plugin.

// A scene node a bone reads its animated transform from and writes its simulated transform to.
public interface ISmpNode
{
    string Name {get;}
    int Depth {get;}
    // World matrix (row vectors) including the actor's placement in the scene.
    M4d World {get;}
    void Write(M4d world);
    // Drops whatever Write stored, returning the node to its animated transform.
    void Clear();
}

// Skinned collision mesh data, independent of the NIF reader so the solver can be tested with synthetic meshes.
// Weights and BoneIndices hold four entries per vertex; Triangles holds three vertex indices per face.
public sealed record SmpMeshSource(string Name,string[] BoneNames,SmpTransform[] SkinToBone,(V3d Center,double Radius)[] BoneBounds,V3d[] Positions,float[] Weights,int[] BoneIndices,int[] Triangles,ISmpNode?[]? FallbackNodes=null);

public sealed class SmpSolverSettings
{
    public int Iterations=10,MinFps=60,MaxSubSteps=4;public double Erp=.2;
    public string Origin="FSMP defaults";
    // SKSE\Plugins\hdtSkinnedMeshConfigs\configs.json, "solver" section, with FSMP's own clamps.
    public static SmpSolverSettings FromConfig(string json,string origin)
    {
        var settings=new SmpSolverSettings{Origin=origin};
        using var document=System.Text.Json.JsonDocument.Parse(json,new System.Text.Json.JsonDocumentOptions{AllowTrailingCommas=true,CommentHandling=System.Text.Json.JsonCommentHandling.Skip});
        if(!document.RootElement.TryGetProperty("solver",out var solver))return settings;
        if(solver.TryGetProperty("numIterations",out var iterations)&&iterations.TryGetInt32(out int n))settings.Iterations=Math.Clamp(n,4,128);
        if(solver.TryGetProperty("erp",out var erp)&&erp.TryGetDouble(out double e))settings.Erp=Math.Clamp(e,.01,1);
        if(solver.TryGetProperty("min-fps",out var fps)&&fps.TryGetInt32(out int f))settings.MinFps=Math.Clamp(f,1,300);
        if(solver.TryGetProperty("maxSubSteps",out var steps)&&steps.TryGetInt32(out int s))settings.MaxSubSteps=Math.Clamp(s,1,60);
        return settings;
    }
    public override string ToString()=>$"{Iterations} iterations, erp {Erp:0.###}, min-fps {MinFps}, {MaxSubSteps} substeps ({Origin})";
}

public sealed class SmpBone
{
    public required string Name;public required ISmpNode Node;public required RigidBody Rig;
    public bool Kinematic;
    // centerOfMassTransform and its inverse; identity in almost every XML.
    public SmpTransform LocalToRig=SmpTransform.Identity,RigToLocal=SmpTransform.Identity;
    // The transform collision reads. During a step it holds the predicted pose, after a step the written one.
    public SmpTransform Current=SmpTransform.Identity;
    public double MarginMultiplier=1,GravityFactor=1,LinearDamping,AngularDamping;
    internal BVector FrameLinear,FrameAngular;
    public int Depth=>Node.Depth;

    BMatrix destination;
    public void ReadTransform(double timeStep,bool reset){Sample();Apply(timeStep,reset);}
    // Reads the node. Returns how far a kinematic bone is from where the simulation last left it.
    public double Sample()
    {
        double oldScale=Current.Scale;Current=SmpTransform.From(Node.World);double newScale=Current.Scale;
        if(Math.Abs(newScale-oldScale)>1e-6){
            double factor=oldScale/newScale;
            if(!Kinematic){
                double factor3=factor*factor*factor,factor5=factor3*factor*factor;var inertia=Rig.InvInertiaDiagLocal;
                Rig.SetMassProps(1/(Rig.InvMass*factor3),new BVector(1,1,1));Rig.InvInertiaDiagLocal=inertia*factor5;Rig.UpdateInertiaTensor();
            }
            LocalToRig=new(LocalToRig.Rotation,LocalToRig.Origin/factor);RigToLocal=new(RigToLocal.Rotation,RigToLocal.Origin/factor);
        }
        destination=(Current.Rigid*LocalToRig).ToBullet();
        if(!Kinematic)return 0;
        var at=Rig.WorldTransform;double dx=destination.M41-at.M41,dy=destination.M42-at.M42,dz=destination.M43-at.M43;
        return Math.Sqrt(dx*dx+dy*dy+dz*dz);
    }
    public void Apply(double timeStep,bool reset)
    {
        var dest=destination;
        if(reset){
            Rig.WorldTransform=dest;Rig.InterpolationWorldTransform=dest;
            Rig.LinearVelocity=BVector.Zero;Rig.AngularVelocity=BVector.Zero;Rig.InterpolationLinearVelocity=BVector.Zero;Rig.InterpolationAngularVelocity=BVector.Zero;
            Rig.UpdateInertiaTensor();FrameLinear=FrameAngular=BVector.Zero;
        }else if(Kinematic){
            // The animation moved this bone; it crosses the distance at constant velocity over the frame's substeps.
            var current=Rig.WorldTransform;
            TransformUtil.CalculateVelocity(ref current,ref dest,timeStep,out FrameLinear,out FrameAngular);
        }
    }
    V3d written;
    // Writes the simulated pose to the node. Returns how far the bone moved since it was last written.
    public double WriteTransform()
    {
        var transform=SmpTransform.From(Rig.WorldTransform)*RigToLocal;
        Current=new(transform.Rotation,transform.Origin,Current.Scale);
        Node.Write(Current.ToMatrix());
        double moved=(transform.Origin-written).Length;written=transform.Origin;
        return moved;
    }
}

// stiffspring-constraint keeps two bone origins near their rest distance. FSMP implements it as a custom solver
// row, which a managed binding cannot add to Bullet, so the same row is solved on its own before each step.
public sealed class SmpStiffSpring
{
    public required SmpBone A,B;
    public double MinDistance,MaxDistance,Equilibrium,Stiffness,Damping,OldDiff;
    public void Apply(double dt,double erp,int iterations)
    {
        var a=(A.RigToLocal*SmpTransform.From(A.Rig.WorldTransform)).Origin;var b=(B.RigToLocal*SmpTransform.From(B.Rig.WorldTransform)).Origin;
        var between=a-b;double distance=between.Length;
        if(distance<1e-7)return;
        var dir=between/distance;double fps=1/dt,invMass=A.Rig.InvMass+B.Rig.InvMass;
        if(invMass<=0)return;
        double relative=V3d.Dot(dir,SmpTransform.Vector(A.Rig.LinearVelocity)-SmpTransform.Vector(B.Rig.LinearVelocity));
        double target,lower,upper;
        if(distance<MinDistance||distance>MaxDistance){
            // Same right-hand side and impulse bounds as the plugin's limit row, signs included.
            target=fps*erp*(distance<MinDistance?distance-MinDistance:distance-MaxDistance);
            if(MinDistance==MaxDistance){lower=double.NegativeInfinity;upper=double.PositiveInfinity;}
            else if(distance>MaxDistance){lower=double.NegativeInfinity;upper=0;}
            else{lower=0;upper=double.PositiveInfinity;}
        }else{
            double delta=distance-Equilibrium,velocity=(delta-OldDiff)*fps,force=(delta+OldDiff)*.5*Stiffness,friction=Damping*velocity;
            force+=force*friction<0?Math.Clamp(friction,-Math.Abs(force),Math.Abs(force)):friction;
            double targetVelocity=fps/iterations*force,maxImpulse=Math.Abs(force)/fps;
            target=MotorFactor(distance,MinDistance,MaxDistance,targetVelocity,fps*erp)*targetVelocity*invMass;
            lower=-maxImpulse;upper=maxImpulse;OldDiff=delta;
        }
        double impulse=Math.Clamp((target-relative)/invMass,lower,upper);
        if(impulse==0||!double.IsFinite(impulse))return;
        A.Rig.LinearVelocity+=SmpTransform.Bullet(dir*(impulse*A.Rig.InvMass));B.Rig.LinearVelocity-=SmpTransform.Bullet(dir*(impulse*B.Rig.InvMass));
    }
    // btTypedConstraint::getMotorFactor
    static double MotorFactor(double pos,double low,double high,double velocity,double timeFactor)
    {
        if(low>high)return 1;
        if(low==high)return 0;
        double delta=velocity/timeFactor;
        if(delta<0)return pos>=low&&pos+delta<low?(low-pos)/delta:pos<low?0:1;
        if(delta>0)return pos<=high&&pos+delta>high?(high-pos)/delta:pos>high?0:1;
        return 0;
    }
}

public sealed class SmpSystem
{
    public string Name="";
    public List<SmpBone> Bones=[];
    public readonly List<SmpBody> Bodies=[];
    public readonly List<TypedConstraint> Constraints=[];
    public readonly List<SmpStiffSpring> StiffSprings=[];
    // Bone pairs joined by a constraint, for the debug overlay.
    public readonly List<(SmpBone A,SmpBone B)> Links=[];
    internal readonly List<(Generic6DofSpring2Constraint Joint,SmpBone A,SmpBone B,SmpTransform FrameA,SmpTransform FrameB,SmpSystemBuilder.GenericTemplate Template)> Generic=[];
    internal readonly List<(ConeTwistConstraint Joint,SmpBone A,SmpBone B,SmpTransform FrameA,SmpTransform FrameB)> Cones=[];
    internal bool Initialized;
    public int DynamicBones=>Bones.Count(b=>!b.Kinematic);
}

// Reads one physics XML in document order, as FSMP does: templates change as the file goes on, and a bone takes
// whatever the unnamed default template holds at the point it is created.
public sealed class SmpSystemBuilder
{
    sealed class BoneTemplate
    {
        public double Mass,LinearDamping,AngularDamping,Friction=.5,RollingFriction,Restitution,MarginMultiplier=1,GravityFactor=1;
        public V3d Inertia;public SmpTransform CenterOfMass=SmpTransform.Identity;
        public BoneTemplate Clone()=>(BoneTemplate)MemberwiseClone();
    }
    internal enum FrameType {InA,InB,InLerp}
    internal sealed class GenericTemplate
    {
        public FrameType FrameType=FrameType.InB;public bool UseLinearReferenceFrameA;public SmpTransform Frame=SmpTransform.Identity;
        public V3d LinearLower=new(1),LinearUpper=new(-1),AngularLower=new(1),AngularUpper=new(-1);
        public V3d LinearStiffness,AngularStiffness,LinearDamping,AngularDamping,LinearEquilibrium,AngularEquilibrium,LinearBounce,AngularBounce;
        public bool LinearSprings=true,AngularSprings=true,LinearStiffnessLimited=true,AngularStiffnessLimited=true,SpringDampingLimited=true;
        public bool LinearMotors,AngularMotors,LinearServo,AngularServo;
        public V3d LinearNonHookeanDamping,AngularNonHookeanDamping,LinearNonHookeanStiffness,AngularNonHookeanStiffness;
        public V3d LinearTargetVelocity,AngularTargetVelocity,LinearMaxMotorForce,AngularMaxMotorForce;
        public double MotorErp=.9,MotorCfm,StopErp=.2,StopCfm;
        public GenericTemplate Clone()=>(GenericTemplate)MemberwiseClone();
    }
    sealed class StiffTemplate {public double MinFactor=1,MaxFactor=1,Stiffness,Damping,Equilibrium=.5;public StiffTemplate Clone()=>(StiffTemplate)MemberwiseClone();}
    sealed class ConeTemplate
    {
        public SmpTransform Frame=SmpTransform.Identity;public FrameType FrameType=FrameType.InB;
        public double Swing1,Swing2,Twist,Softness=1,Bias=.3,Relaxation=1;
        public ConeTemplate Clone()=>(ConeTemplate)MemberwiseClone();
    }

    readonly SmpSystem system=new();
    readonly Func<string,ISmpNode?> resolve;readonly Func<string,SmpMeshSource?> mesh;readonly CollisionShape emptyShape;readonly List<string> log;
    readonly Dictionary<string,SmpBone> index=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,BoneTemplate> boneTemplates=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,GenericTemplate> genericTemplates=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,StiffTemplate> stiffTemplates=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,ConeTemplate> coneTemplates=new(StringComparer.OrdinalIgnoreCase);
    readonly List<(SmpBody Body,bool AutoGenerate)> deferred=[];
    readonly HashSet<string> reported=[];
    SmpSystemBuilder(Func<string,ISmpNode?> resolve,Func<string,SmpMeshSource?> mesh,CollisionShape emptyShape,List<string> log){this.resolve=resolve;this.mesh=mesh;this.emptyShape=emptyShape;this.log=log;}

    void Warn(XElement? at,string message)
    {
        string line=at is IXmlLineInfo info&&info.HasLineInfo()?$" (line {info.LineNumber})":"";
        string text=$"{system.Name}{line}: {message}";
        if(reported.Add(text))log.Add(text);
    }

    // Returns null when the XML cannot produce a system; the reason is appended to the log.
    public static SmpSystem? Build(string name,string xml,Func<string,ISmpNode?> resolve,Func<string,SmpMeshSource?> mesh,CollisionShape emptyShape,List<string> log)
    {
        var builder=new SmpSystemBuilder(resolve,mesh,emptyShape,log);builder.system.Name=name;
        try{
            var document=XDocument.Parse(xml.TrimStart('﻿'),LoadOptions.SetLineInfo);
            var root=document.Root;
            if(root==null||root.Name.LocalName!="system"){log.Add($"{name}: root element is <{root?.Name.LocalName}>, not <system>; FSMP loads no physics from this file.");return null;}
            if(root.Descendants().Any(e=>e.Name.LocalName is "pattern" or "pattern-default")){log.Add($"{name}: <pattern> macros are not expanded by the previewer; no physics built for this file.");builder.Release();return null;}
            builder.ReadSystem(root);
        }
        catch(XmlException ex){log.Add($"{name}: XML is not well formed (line {ex.LineNumber}): {ex.Message}");builder.Release();return null;}
        catch(SmpParseException ex){log.Add($"{name}: {ex.Message}. FSMP stops reading at an error like this and builds no physics for the item.");builder.Release();return null;}
        if(builder.system.Bones.Count==0){log.Add($"{name}: no bone in the XML matches a node on this actor; nothing to simulate.");return null;}
        return builder.system;
    }
    void Release(){foreach(var c in system.Constraints)c.Dispose();foreach(var bone in system.Bones)bone.Rig.Dispose();}

    sealed class SmpParseException(string message):Exception(message);
    static string Line(XElement e)=>e is IXmlLineInfo info&&info.HasLineInfo()?$"line {info.LineNumber}":"unknown line";
    static string Name(XElement e)=>e.Name.LocalName;
    static string Text(XElement e)=>e.Nodes().OfType<XText>().FirstOrDefault()?.Value??"";
    static double Float(string text,XElement at)
    {
        string s=text.Trim();int comma=s.IndexOf(',');if(comma>=0)s=s[..comma]+"."+s[(comma+1)..];
        if(s.StartsWith('+'))s=s[1..];
        if(!double.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out double value))throw new SmpParseException($"<{Name(at)}> at {Line(at)} is not a number: '{text.Trim()}'");
        return value;
    }
    static double Float(XElement e)=>Float(Text(e),e);
    static int Int(XElement e)
    {
        string s=Text(e).Trim();if(s.StartsWith('+'))s=s[1..];
        try{return s.StartsWith("0x")?Convert.ToInt32(s[2..],16):s.Length>1&&s[0]=='0'?Convert.ToInt32(s[1..],8):int.Parse(s,CultureInfo.InvariantCulture);}
        catch(Exception ex) when(ex is FormatException or OverflowException or ArgumentException){throw new SmpParseException($"<{Name(e)}> at {Line(e)} is not an integer: '{s}'");}
    }
    static bool Bool(XElement e)=>Text(e).Trim() switch{"true" or "1"=>true,"false" or "0"=>false,var other=>throw new SmpParseException($"<{Name(e)}> at {Line(e)} is not a boolean: '{other}'")};
    static string Attribute(XElement e,string name)=>e.Attribute(name)?.Value??throw new SmpParseException($"<{Name(e)}> at {Line(e)} is missing attribute {name}");
    static V3d Vector(XElement e)=>new(Float(Attribute(e,"x"),e),Float(Attribute(e,"y"),e),Float(Attribute(e,"z"),e));
    static SmpTransform Transform(XElement e)
    {
        var rotation=Qd.Identity;var origin=V3d.Zero;
        foreach(var child in e.Elements()){
            if(Name(child)=="basis"){
                var q=new Qd(Float(Attribute(child,"x"),child),Float(Attribute(child,"y"),child),Float(Attribute(child,"z"),child),Float(Attribute(child,"w"),child));
                rotation=q.LengthSquared<1e-12?Qd.Identity:Qd.Normalize(q);
            }else if(Name(child)=="basis-axis-angle"){
                var axis=Vector(child);double angle=Float(Attribute(child,"angle"),child);
                if(axis.LengthSquared<1e-12){axis=V3d.UnitX;angle=0;}
                rotation=Qd.FromAxisAngle(axis.Normalized(),angle);
            }else if(Name(child)=="origin")origin=Vector(child);
        }
        return new(rotation,origin);
    }

    BoneTemplate BoneDefault(string name)=>boneTemplates.TryGetValue(name,out var found)?found:boneTemplates.TryGetValue("",out var unnamed)?unnamed:(boneTemplates[""]=new());
    GenericTemplate GenericDefault(string name)=>genericTemplates.TryGetValue(name,out var found)?found:genericTemplates.TryGetValue("",out var unnamed)?unnamed:(genericTemplates[""]=new());
    StiffTemplate StiffDefault(string name)=>stiffTemplates.TryGetValue(name,out var found)?found:stiffTemplates.TryGetValue("",out var unnamed)?unnamed:(stiffTemplates[""]=new());
    ConeTemplate ConeDefault(string name)=>coneTemplates.TryGetValue(name,out var found)?found:coneTemplates.TryGetValue("",out var unnamed)?unnamed:(coneTemplates[""]=new());
    static string Optional(XElement e,string name)=>e.Attribute(name)?.Value??"";

    void ReadSystem(XElement root)
    {
        foreach(var e in root.Elements()){
            switch(Name(e)){
                case "bone":ReadBone(e);break;
                case "bone-default":{var template=BoneDefault(Optional(e,"extends")).Clone();ReadBoneTemplate(e,template);boneTemplates[Optional(e,"name")]=template;break;}
                case "per-vertex-shape":ReadShape(e,false);break;
                case "per-triangle-shape":ReadShape(e,true);break;
                case "constraint-group":
                    foreach(var child in e.Elements())if(!ReadConstraintElement(child))Warn(child,$"unknown element <{Name(child)}> skipped");
                    break;
                case "shape":Warn(e,"<shape> defines a rigid-body collision shape. FSMP adds bone bodies to the world with collision group 0, so these shapes never touch anything; the previewer skips them.");break;
                default:if(!ReadConstraintElement(e))Warn(e,$"unknown element <{Name(e)}> skipped");break;
            }
        }
        // Shapes finish after the whole file is read, so a bone made dynamic late still counts.
        foreach(var (body,autoGenerate) in deferred){if(autoGenerate)body.Shape.AutoGenerate();body.FinishBuild();}
        system.Bones=system.Bones.OrderBy(b=>b.Depth).ToList();
    }
    bool ReadConstraintElement(XElement e)
    {
        switch(Name(e)){
            case "generic-constraint":ReadGeneric(e);return true;
            case "stiffspring-constraint":ReadStiff(e);return true;
            case "conetwist-constraint":ReadCone(e);return true;
            case "generic-constraint-default":{var template=GenericDefault(Optional(e,"extends")).Clone();ReadGenericTemplate(e,template);genericTemplates[Optional(e,"name")]=template;return true;}
            case "stiffspring-constraint-default":{var template=StiffDefault(Optional(e,"extends")).Clone();ReadStiffTemplate(e,template);stiffTemplates[Optional(e,"name")]=template;return true;}
            case "conetwist-constraint-default":{var template=ConeDefault(Optional(e,"extends")).Clone();ReadConeTemplate(e,template);coneTemplates[Optional(e,"name")]=template;return true;}
            default:return false;
        }
    }
    void ReadBoneTemplate(XElement e,BoneTemplate t)
    {
        foreach(var c in e.Elements()){
            switch(Name(c)){
                case "mass":t.Mass=Float(c);break;
                case "inertia":t.Inertia=Vector(c);break;
                case "centerOfMassTransform":t.CenterOfMass=Transform(c);break;
                case "linearDamping":t.LinearDamping=Float(c);break;
                case "angularDamping":t.AngularDamping=Float(c);break;
                case "friction":t.Friction=Float(c);break;
                case "rollingFriction":t.RollingFriction=Float(c);break;
                case "restitution":t.Restitution=Float(c);break;
                case "margin-multiplier":t.MarginMultiplier=Float(c);break;
                case "gravity-factor":t.GravityFactor=Math.Clamp(Float(c),0,1);break;
                case "wind-factor":Float(c);break;
                case "collision-filter":Int(c);break;
                case "shape":Warn(c,"bone <shape> skipped: FSMP adds bone bodies with collision group 0, so a rigid-body shape never collides.");break;
                // Bone-to-bone filters only apply to rigid-body shape pairs, which never form (see <shape>).
                case "can-collide-with-bone" or "no-collide-with-bone":break;
                default:Warn(c,$"unknown element <{Name(c)}> skipped");break;
            }
        }
    }
    void ReadBone(XElement e)
    {
        string name=Attribute(e,"name");
        if(index.ContainsKey(name)){Warn(e,$"bone {name} already exists; second definition skipped");return;}
        CreateBone(name,Optional(e,"template"),e,e);
    }
    SmpBone? CreateBone(string name,string templateName,XElement? read,XElement? at)
    {
        var node=resolve(name);
        if(node==null){Warn(at,$"node {name} does not exist on this actor; no bone created");return null;}
        var template=BoneDefault(templateName).Clone();
        if(read!=null)ReadBoneTemplate(read,template);
        var bone=NewBone(node,template);
        bone.LocalToRig=template.CenterOfMass;bone.RigToLocal=template.CenterOfMass.Inverse();
        bone.MarginMultiplier=template.MarginMultiplier;bone.GravityFactor=template.GravityFactor;
        bone.ReadTransform(0,true);
        return bone;
    }
    SmpBone NewBone(ISmpNode node,BoneTemplate template)
    {
        using var info=new RigidBodyConstructionInfo(template.Mass,null,emptyShape,SmpTransform.Bullet(template.Inertia)){
            LinearDamping=template.LinearDamping,AngularDamping=template.AngularDamping,Friction=template.Friction,RollingFriction=template.RollingFriction,Restitution=template.Restitution};
        var rig=new RigidBody(info);
        bool kinematic=template.Mass==0;
        rig.CollisionFlags=kinematic?CollisionFlags.KinematicObject:CollisionFlags.None;
        var bone=new SmpBone{Name=node.Name,Node=node,Rig=rig,Kinematic=kinematic,LinearDamping=template.LinearDamping,AngularDamping=template.AngularDamping};
        system.Bones.Add(bone);index[node.Name]=bone;
        return bone;
    }
    SmpBone? GetOrCreateBone(string name,XElement at)
    {
        if(index.TryGetValue(name,out var bone))return bone;
        Warn(at,$"bone {name} used before being created; created with the current default template");
        return CreateBone(name,"",null,at);
    }

    SmpBody? GenerateBody(XElement e,string name)
    {
        var source=mesh(name);
        if(source==null||source.Positions.Length==0){Warn(e,$"shape {name} is not a skinned shape in this model; no collision built for it. The XML binds shapes by name, so a renamed shape loses its physics without any error in game.");return null;}
        var body=new SmpBody{Name=name,System=system};
        for(int k=0;k<source.BoneNames.Length;k++){
            if(!index.TryGetValue(source.BoneNames[k],out var bone)){
                // A skin bone the XML never declared takes the current default template's mass and nothing else.
                var node=resolve(source.BoneNames[k])??source.FallbackNodes?[k];
                if(node==null){Warn(e,$"shape {name}: skin bone {source.BoneNames[k]} is not on this actor; shape skipped");return null;}
                bone=NewBone(node,BoneDefault(""));
                Warn(e,$"shape {name}: bone {source.BoneNames[k]} has no <bone> line and was created {(bone.Kinematic?"kinematic":"dynamic")} from the default template in force at this shape");
            }
            body.SkinnedBones.Add(new SmpSkinnedBone{Bone=bone,VertexToBone=source.SkinToBone[k],IsKinematic=bone.Kinematic});
        }
        body.BoneBounds=source.BoneBounds;
        body.SkinPos=(V3d[])source.Positions.Clone();body.Weights=(float[])source.Weights.Clone();body.BoneIndex=(int[])source.BoneIndices.Clone();
        for(int i=0;i<body.BoneIndex.Length;i++)if(body.BoneIndex[i]<0||body.BoneIndex[i]>=body.SkinnedBones.Count){body.BoneIndex[i]=0;body.Weights[i]=0;}
        body.SortWeights();
        return body;
    }
    void ReadShape(XElement e,bool triangles)
    {
        string name=Attribute(e,"name");var body=GenerateBody(e,name);
        if(body==null)return;
        var shape=body.Shape=new SmpShape(body,triangles);
        if(triangles){
            var source=mesh(name)!;
            for(int t=0;t+2<source.Triangles.Length;t+=3)shape.AddTriangle(source.Triangles[t],source.Triangles[t+1],source.Triangles[t+2]);
        }
        foreach(var c in e.Elements()){
            switch(Name(c)){
                case "priority":Warn(c,"<priority> is deprecated and ignored by FSMP");break;
                case "margin":shape.Margin=Float(c);break;
                case "prenetration" or "penetration" when triangles:shape.Penetration=Float(c);break;
                case "shared":
                    body.Shared=Text(c).Trim() switch{"public"=>SmpShared.Public,"internal"=>SmpShared.Internal,"external"=>SmpShared.External,"private"=>SmpShared.Private,_=>SmpShared.Public};
                    if(Text(c).Trim() is not ("public" or "internal" or "external" or "private"))Warn(c,"unknown <shared> value; FSMP uses public");
                    break;
                case "tag":body.Tags.Add(Text(c));break;
                case "can-collide-with-tag":body.CanCollideWithTags.Add(Text(c));break;
                case "no-collide-with-tag":body.NoCollideWithTags.Add(Text(c));break;
                case "can-collide-with-bone":{if(GetOrCreateBone(Text(c),c) is {} bone)body.CanCollideWithBones.Add(bone);break;}
                case "no-collide-with-bone":{if(GetOrCreateBone(Text(c),c) is {} bone)body.NoCollideWithBones.Add(bone);break;}
                case "weight-threshold":{
                    string boneName=Attribute(c,"bone");float threshold=(float)Float(c);
                    foreach(var skinned in body.SkinnedBones){
                        if(!skinned.Bone.Name.Equals(boneName,StringComparison.OrdinalIgnoreCase))continue;
                        skinned.WeightThreshold=threshold;
                        // A per-vertex shape stops at the first matching bone, a per-triangle shape sets every match.
                        if(!triangles)break;
                    }
                    break;
                }
                case "disable-tag":body.DisableTag=Text(c);break;
                case "disable-priority":body.DisablePriority=Int(c);break;
                default:Warn(c,$"unknown element <{Name(c)}> skipped");break;
            }
        }
        deferred.Add((body,!triangles));
        system.Bodies.Add(body);
    }

    bool ParseFrame(XElement c,ref FrameType type,ref SmpTransform frame)
    {
        switch(Name(c)){
            case "frameInA":type=FrameType.InA;frame=Transform(c);return true;
            case "frameInB":type=FrameType.InB;frame=Transform(c);return true;
            case "frameInLerp":{
                // The two lerp factors travel in the frame origin's x and y, as in the plugin.
                double translation=0,rotation=0;
                foreach(var child in c.Elements()){
                    if(Name(child)=="translationLerp")translation=Float(child);else if(Name(child)=="rotationLerp")rotation=Float(child);
                    else Warn(child,$"unknown element <{Name(child)}> skipped");
                }
                type=FrameType.InLerp;frame=new(Qd.Identity,new V3d(translation,rotation,0));return true;
            }
            default:return false;
        }
    }
    void ReadGenericTemplate(XElement e,GenericTemplate t)
    {
        foreach(var c in e.Elements()){
            if(ParseFrame(c,ref t.FrameType,ref t.Frame))continue;
            switch(Name(c)){
                case "enableLinearSprings":t.LinearSprings=Bool(c);break;
                case "enableAngularSprings":t.AngularSprings=Bool(c);break;
                case "linearStiffnessLimited":t.LinearStiffnessLimited=Bool(c);break;
                case "angularStiffnessLimited":t.AngularStiffnessLimited=Bool(c);break;
                case "springDampingLimited":t.SpringDampingLimited=Bool(c);break;
                case "linearNonHookeanDamping":t.LinearNonHookeanDamping=Vector(c);break;
                case "angularNonHookeanDamping":t.AngularNonHookeanDamping=Vector(c);break;
                case "linearNonHookeanStiffness":t.LinearNonHookeanStiffness=Vector(c);break;
                case "angularNonHookeanStiffness":t.AngularNonHookeanStiffness=Vector(c);break;
                case "linearMotors":t.LinearMotors=Bool(c);break;
                case "angularMotors":t.AngularMotors=Bool(c);break;
                case "linearServoMotors":t.LinearServo=Bool(c);break;
                case "angularServoMotors":t.AngularServo=Bool(c);break;
                case "linearTargetVelocity":t.LinearTargetVelocity=Vector(c);break;
                case "angularTargetVelocity":t.AngularTargetVelocity=Vector(c);break;
                case "linearMaxMotorForce":t.LinearMaxMotorForce=Vector(c);break;
                case "angularMaxMotorForce":t.AngularMaxMotorForce=Vector(c);break;
                case "stopERP":t.StopErp=Float(c);break;
                case "stopCFM":t.StopCfm=Float(c);break;
                case "motorERP":t.MotorErp=Float(c);break;
                case "motorCFM":t.MotorCfm=Float(c);break;
                case "useLinearReferenceFrameA":t.UseLinearReferenceFrameA=Bool(c);break;
                case "linearLowerLimit":t.LinearLower=Vector(c);break;
                case "linearUpperLimit":t.LinearUpper=Vector(c);break;
                case "angularLowerLimit":t.AngularLower=Vector(c);break;
                case "angularUpperLimit":t.AngularUpper=Vector(c);break;
                case "linearStiffness":t.LinearStiffness=Vector(c);break;
                case "angularStiffness":t.AngularStiffness=Vector(c);break;
                case "linearDamping":t.LinearDamping=Vector(c);break;
                case "angularDamping":t.AngularDamping=Vector(c);break;
                case "linearEquilibrium":t.LinearEquilibrium=Vector(c);break;
                case "angularEquilibrium":t.AngularEquilibrium=Vector(c);break;
                case "linearBounce":t.LinearBounce=Vector(c);break;
                case "angularBounce":t.AngularBounce=Vector(c);break;
                default:Warn(c,$"unknown element <{Name(c)}> skipped");break;
            }
        }
    }
    bool FindBones(XElement e,out SmpBone a,out SmpBone b)
    {
        string nameA=Attribute(e,"bodyA"),nameB=Attribute(e,"bodyB");a=b=null!;
        if(!index.TryGetValue(nameA,out var first)){Warn(e,$"constraint {nameA} <-> {nameB}: bone for bodyA does not exist yet; created with the current default template");first=CreateBone(nameA,"",null,e);if(first==null)return false;}
        if(!index.TryGetValue(nameB,out var second)){Warn(e,$"constraint {nameA} <-> {nameB}: bone for bodyB does not exist yet; created with the current default template");second=CreateBone(nameB,"",null,e);if(second==null)return false;}
        if(first==second){Warn(e,$"constraint between the same bone {nameA}; skipped");return false;}
        if(first.Kinematic&&second.Kinematic){Warn(e,$"constraint between two kinematic bones {nameA} <-> {nameB}; skipped");return false;}
        a=first;b=second;return true;
    }
    static void CalcFrame(FrameType type,SmpTransform frame,SmpTransform trA,SmpTransform trB,out SmpTransform frameA,out SmpTransform frameB)
    {
        SmpTransform world;
        switch(type){
            case FrameType.InA:frameA=frame;world=trA*frame;frameB=(trB.Inverse()*world).Rigid;break;
            case FrameType.InB:frameB=frame;world=trB*frame;frameA=(trA.Inverse()*world).Rigid;break;
            default:
                world=new(Qd.Slerp(trA.Rotation,trB.Rotation,frame.Origin.Y),V3d.Lerp(trA.Origin,trB.Origin,frame.Origin.X));
                frameA=(trA.Inverse()*world).Rigid;frameB=(trB.Inverse()*world).Rigid;break;
        }
    }
    void ReadGeneric(XElement e)
    {
        if(!FindBones(e,out var a,out var b))return;
        var t=GenericDefault(Optional(e,"template")).Clone();ReadGenericTemplate(e,t);
        CalcFrame(t.FrameType,t.Frame,a.Current,b.Current,out var frameA,out var frameB);
        // useLinearReferenceFrameA swaps which bone is Bullet's body A.
        if(t.UseLinearReferenceFrameA){(a,b)=(b,a);(frameA,frameB)=(frameB,frameA);}
        var joint=new Generic6DofSpring2Constraint(a.Rig,b.Rig,(a.RigToLocal*frameA).ToBullet(),(b.RigToLocal*frameB).ToBullet(),RotateOrder.XYZ);
        for(int i=0;i<6;i++)joint.EnableSpring(i,true);
        ApplyGeneric(joint,t,1,1);
        if(t.LinearNonHookeanDamping!=V3d.Zero||t.AngularNonHookeanDamping!=V3d.Zero||t.LinearNonHookeanStiffness!=V3d.Zero||t.AngularNonHookeanStiffness!=V3d.Zero)
            Warn(e,"non-Hookean spring terms need FSMP's modified Bullet and are not simulated; the spring stays linear here");
        system.Constraints.Add(joint);system.Links.Add((a,b));system.Generic.Add((joint,a,b,frameA,frameB,t));
    }
    // linear scales lengths and equilibrium, the stiffness factors follow FSMP's scaleConstraint (cube and fifth power).
    internal static void ApplyGeneric(Generic6DofSpring2Constraint joint,GenericTemplate t,double linear,double angularStiffness)
    {
        double cube=linear*linear*linear;
        joint.LinearLowerLimit=SmpTransform.Bullet(t.LinearLower*linear);joint.LinearUpperLimit=SmpTransform.Bullet(t.LinearUpper*linear);
        joint.AngularLowerLimit=SmpTransform.Bullet(t.AngularLower);joint.AngularUpperLimit=SmpTransform.Bullet(t.AngularUpper);
        for(int i=0;i<3;i++){
            joint.SetStiffness(i,t.LinearStiffness[i]*cube,t.LinearStiffnessLimited);joint.SetStiffness(i+3,t.AngularStiffness[i]*angularStiffness,t.AngularStiffnessLimited);
            joint.SetDamping(i,t.LinearDamping[i],t.SpringDampingLimited);joint.SetDamping(i+3,t.AngularDamping[i],t.SpringDampingLimited);
            joint.SetEquilibriumPoint(i,t.LinearEquilibrium[i]*linear);joint.SetEquilibriumPoint(i+3,t.AngularEquilibrium[i]);
            joint.EnableSpring(i,t.LinearSprings);joint.EnableSpring(i+3,t.AngularSprings);
            joint.EnableMotor(i,t.LinearMotors);joint.EnableMotor(i+3,t.AngularMotors);
            joint.SetServo(i,t.LinearServo);joint.SetServo(i+3,t.AngularServo);
            joint.SetServoTarget(i,t.LinearEquilibrium[i]*linear);joint.SetServoTarget(i+3,t.AngularEquilibrium[i]);
            joint.SetTargetVelocity(i,t.LinearTargetVelocity[i]);joint.SetTargetVelocity(i+3,t.AngularTargetVelocity[i]);
            joint.SetMaxMotorForce(i,t.LinearMaxMotorForce[i]);joint.SetMaxMotorForce(i+3,t.AngularMaxMotorForce[i]);
            joint.SetParam(ConstraintParam.Erp,t.MotorErp,i);joint.SetParam(ConstraintParam.Cfm,t.MotorCfm,i);
            joint.SetParam(ConstraintParam.StopErp,t.StopErp,i);joint.SetParam(ConstraintParam.StopCfm,t.StopCfm,i);
            var motor=joint.GetRotationalLimitMotor(i);
            motor.MotorErp=t.MotorErp;motor.MotorCfm=t.MotorCfm;motor.StopErp=t.StopErp;motor.StopCfm=t.StopCfm;motor.Bounce=t.AngularBounce[i];
        }
        joint.TranslationalLimitMotor.Bounce=SmpTransform.Bullet(t.LinearBounce);
    }
    void ReadStiffTemplate(XElement e,StiffTemplate t)
    {
        foreach(var c in e.Elements()){
            switch(Name(c)){
                case "minDistanceFactor":t.MinFactor=Math.Max(Float(c),0);break;
                case "maxDistanceFactor":t.MaxFactor=Math.Max(Float(c),0);break;
                case "stiffness":t.Stiffness=Math.Max(Float(c),0);break;
                case "damping":t.Damping=Math.Max(Float(c),0);break;
                case "equilibrium":t.Equilibrium=Math.Clamp(Float(c),0,1);break;
                default:Warn(c,$"unknown element <{Name(c)}> skipped");break;
            }
        }
    }
    void ReadStiff(XElement e)
    {
        if(!FindBones(e,out var a,out var b))return;
        var t=StiffDefault(Optional(e,"template")).Clone();ReadStiffTemplate(e,t);
        double distance=(a.Current.Origin-b.Current.Origin).Length,min=distance*t.MinFactor,max=distance*t.MaxFactor;
        system.StiffSprings.Add(new SmpStiffSpring{A=a,B=b,MinDistance=min,MaxDistance=max,Stiffness=t.Stiffness,Damping=t.Damping,Equilibrium=min*t.Equilibrium+max*(1-t.Equilibrium)});
        system.Links.Add((a,b));
        Warn(e,"stiffspring-constraint is approximated: FSMP solves it as a custom row inside Bullet's solver, the previewer solves the same row on its own before each step");
    }
    void ReadConeTemplate(XElement e,ConeTemplate t)
    {
        foreach(var c in e.Elements()){
            if(ParseFrame(c,ref t.FrameType,ref t.Frame))continue;
            switch(Name(c)){
                case "swingSpan1" or "coneLimit" or "limitZ":t.Swing1=Math.Max(Float(c),0);break;
                case "swingSpan2" or "planeLimit" or "limitY":t.Swing2=Math.Max(Float(c),0);break;
                case "twistSpan" or "twistLimit" or "limitX":t.Twist=Math.Max(Float(c),0);break;
                case "limitSoftness":t.Softness=Math.Clamp(Float(c),0,1);break;
                case "biasFactor":t.Bias=Math.Clamp(Float(c),0,1);break;
                case "relaxationFactor":t.Relaxation=Math.Clamp(Float(c),0,1);break;
                default:Warn(c,$"unknown element <{Name(c)}> skipped");break;
            }
        }
    }
    void ReadCone(XElement e)
    {
        if(!FindBones(e,out var a,out var b))return;
        var t=ConeDefault(Optional(e,"template")).Clone();ReadConeTemplate(e,t);
        CalcFrame(t.FrameType,t.Frame,a.Current,b.Current,out var frameA,out var frameB);
        var joint=new ConeTwistConstraint(a.Rig,b.Rig,(a.RigToLocal*frameA).ToBullet(),(b.RigToLocal*frameB).ToBullet());
        joint.EnableMotor(false);joint.SetLimit(t.Swing1,t.Swing2,t.Twist,t.Softness,t.Bias,t.Relaxation);
        system.Constraints.Add(joint);system.Links.Add((a,b));system.Cones.Add((joint,a,b,frameA,frameB));
    }
}

public sealed class SmpWorld:IDisposable
{
    // Bullet works in Skyrim units; gravity is 9.8 m/s² at 1 unit = 1.425 cm.
    public const double ScaleSkyrim=1/0.01425;
    readonly DefaultCollisionConfiguration configuration=new();readonly CollisionDispatcher dispatcher;readonly DbvtBroadphase broadphase=new();
    readonly SequentialImpulseConstraintSolver solver=new();readonly DiscreteDynamicsWorld world;
    public readonly EmptyShape EmptyShape=new();
    public readonly List<SmpSystem> Systems=[];
    public readonly SmpSolverSettings Settings;
    public readonly List<SmpContact> Contacts=[];
    public readonly List<string> Diagnostics=[];
    double averageInterval,accumulated;bool resetPending=true;long step;
    readonly List<SmpBody> bodies=[];readonly List<PersistentManifold> manifolds=[];
    public int LastSubSteps {get;private set;}
    public double LastStepMs {get;private set;}
    public double LastCollisionMs {get;private set;}
    // Time per phase of the last update, summed over its substeps.
    public double LastReadMs,LastPredictMs,LastSkinMs,LastPairsMs,LastSolveMs,LastWriteMs;
    static double Since(long start)=>System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    public long Steps=>step;
    public int LastContacts {get;private set;}
    public int PeakContacts {get;private set;}
    public int Resets {get;private set;}
    public double GravityScale=1;
    // Largest distance any simulated bone moved in the last update; near zero once everything hangs still.
    public double LastMotion {get;private set;}=double.MaxValue;
    // The debug overlay needs every body skinned, not only the ones with a collision partner.
    public bool UpdateAllBodies;
    // Called once per update before simulated bones are written back, parents first.
    public Action? BeforeWrite;

    public SmpWorld(SmpSolverSettings settings)
    {
        Settings=settings;
        dispatcher=new CollisionDispatcher(configuration);
        world=new DiscreteDynamicsWorld(dispatcher,broadphase,solver,configuration){Gravity=new BVector(0,0,-9.8*ScaleSkyrim)};
        var info=world.SolverInfo;
        info.Friction=0;info.SplitImpulse=1;info.SplitImpulsePenetrationThreshold=-.01;info.Erp2=.15;info.GlobalCfm=.001;
        info.RestitutionVelocityThreshold=.2;info.SolverMode=SolverModes.Simd;info.LeastSquaresResidualThreshold=.0001;
        info.NumIterations=settings.Iterations;info.Erp=settings.Erp;
        // One island: contacts built by hand are not in the broadphase, so Bullet's island builder cannot see them.
        world.SimulationIslandManager.SplitIslands=false;
        averageInterval=1d/settings.MinFps;
    }
    public void Add(SmpSystem system)
    {
        Systems.Add(system);bodies.AddRange(system.Bodies);
        foreach(var bone in system.Bones){
            bone.Rig.ActivationState=ActivationState.DisableDeactivation;
            world.AddRigidBody(bone.Rig,0,0);
        }
        foreach(var constraint in system.Constraints)world.AddConstraint(constraint,true);
        ApplyGravity();
        system.Initialized=false;
    }
    void ApplyGravity()
    {
        foreach(var system in Systems)foreach(var bone in system.Bones)if(!bone.Kinematic)bone.Rig.Gravity=world.Gravity*(bone.GravityFactor*GravityScale);
    }
    // Next update re-reads every bone from its node with zero velocity. Simulated bones keep the pose they had
    // relative to their parent, as an FSMP reset does.
    public void RequestReset()=>resetPending=true;
    // Moves the whole simulation rigidly, velocities included: the actor jumped (a root-motion loop wrapped) but
    // nothing moved relative to it.
    public void Carry(M4d delta)
    {
        var rigid=SmpTransform.From(delta).Rigid;var move=rigid.ToBullet();var turn=rigid.ToBullet();turn.M41=turn.M42=turn.M43=0;
        foreach(var system in Systems)foreach(var bone in system.Bones){
            var rig=bone.Rig;rig.WorldTransform=rig.WorldTransform*move;rig.InterpolationWorldTransform=rig.WorldTransform;
            rig.LinearVelocity=BVector.TransformCoordinate(rig.LinearVelocity,turn);rig.AngularVelocity=BVector.TransformCoordinate(rig.AngularVelocity,turn);
        }
    }
    // Largest distance a kinematic bone had to cover in the last update, and the recent norm for ordinary frames.
    public double LastKinematicGap {get;private set;}
    double usualGap;

    // One frame of FSMP's doUpdate. Returns true when the simulation advanced and bones were written.
    // jump marks a frame that follows a seek or a loop wrap. If the kinematic bones then sit much farther from the
    // simulation than they move in an ordinary frame, the pose itself jumped and the simulation is reset instead of
    // being whipped across the gap.
    public bool Update(double interval,bool jump=false)
    {
        if(Systems.Count==0||!(interval>0))return false;
        double timeTick=1d/Settings.MinFps;
        accumulated+=interval;averageInterval+=(interval-averageInterval)*.125;
        double tick=Math.Min(averageInterval,timeTick);
        if(!(accumulated*2>tick))return false;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        double remaining=Math.Min(accumulated,tick*Settings.MaxSubSteps);
        LastReadMs=LastPredictMs=LastSkinMs=LastPairsMs=LastSolveMs=LastWriteMs=0;long phase=System.Diagnostics.Stopwatch.GetTimestamp();
        double gap=0;
        foreach(var system in Systems)foreach(var bone in system.Bones)gap=Math.Max(gap,bone.Sample());
        LastKinematicGap=gap;
        if(jump&&gap>Math.Max(8,usualGap*3))resetPending=true;
        bool reset=resetPending;resetPending=false;if(reset)Resets++;
        if(!jump&&!reset)usualGap=Math.Max(usualGap*.98,gap);
        foreach(var system in Systems){
            bool systemReset=reset||!system.Initialized;system.Initialized=true;
            foreach(var bone in system.Bones)bone.Apply(remaining,systemReset);
            ScaleConstraints(system);
        }
        SmpCollision.UpdateActiveState(bodies);
        LastSubSteps=0;LastCollisionMs=0;LastReadMs+=Since(phase);
        while(remaining>tick){Step(tick);remaining-=tick;}
        if(remaining>1d/300)Step(remaining);
        accumulated=0;
        BeforeWrite?.Invoke();
        if(!Healthy()){
            Diagnostics.Add("HDT-SMP: the simulation produced a non-finite transform and was reset. A constraint or mass in the XML is likely unstable at this frame rate.");
            if(Diagnostics.Count>20)Diagnostics.RemoveRange(0,Diagnostics.Count-20);
            foreach(var system in Systems)foreach(var bone in system.Bones)bone.Node.Clear();
            foreach(var system in Systems)foreach(var bone in system.Bones)bone.ReadTransform(0,true);
            LastStepMs=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            return true;
        }
        double motion=0;phase=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var system in Systems)foreach(var bone in system.Bones)if(!bone.Kinematic)motion=Math.Max(motion,bone.WriteTransform());
        LastMotion=motion;LastWriteMs+=Since(phase);
        LastStepMs=System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return true;
    }
    bool Healthy()
    {
        foreach(var system in Systems)foreach(var bone in system.Bones){
            if(bone.Kinematic)continue;
            var m=bone.Rig.WorldTransform;
            if(!double.IsFinite(m.M41+m.M42+m.M43+m.M11+m.M22+m.M33)||Math.Abs(m.M41)>1e7||Math.Abs(m.M42)>1e7||Math.Abs(m.M43)>1e7)return false;
        }
        return true;
    }
    // FSMP rescales constraint frames and springs when a bone's world scale differs from the scale they were built at.
    readonly Dictionary<TypedConstraint,(double A,double B)> constraintScales=[];
    void ScaleConstraints(SmpSystem system)
    {
        foreach(var (joint,a,b,frameA,frameB,template) in system.Generic){
            var old=constraintScales.GetValueOrDefault(joint,(1,1));double sa=a.Current.Scale,sb=b.Current.Scale;
            if(Math.Abs(sa-old.Item1)<1e-6&&Math.Abs(sb-old.Item2)<1e-6)continue;
            constraintScales[joint]=(sa,sb);
            double w0=a.Rig.InvMass,w1=b.Rig.InvMass,factor=(sa*w0+sb*w1)/(w0+w1);
            joint.SetFrames((a.RigToLocal*new SmpTransform(frameA.Rotation,frameA.Origin*sa)).ToBullet(),(b.RigToLocal*new SmpTransform(frameB.Rotation,frameB.Origin*sb)).ToBullet());
            SmpSystemBuilder.ApplyGeneric(joint,template,factor,Math.Pow(factor,5));
        }
        foreach(var (joint,a,b,frameA,frameB) in system.Cones){
            var old=constraintScales.GetValueOrDefault(joint,(1,1));double sa=a.Current.Scale,sb=b.Current.Scale;
            if(Math.Abs(sa-old.Item1)<1e-6&&Math.Abs(sb-old.Item2)<1e-6)continue;
            constraintScales[joint]=(sa,sb);
            joint.SetFrames((a.RigToLocal*new SmpTransform(frameA.Rotation,frameA.Origin*sa)).ToBullet(),(b.RigToLocal*new SmpTransform(frameB.Rotation,frameB.Origin*sb)).ToBullet());
        }
    }

    void Step(double dt)
    {
        step++;LastSubSteps++;long phase=System.Diagnostics.Stopwatch.GetTimestamp();
        // Predicted pose for collision: FSMP damps and integrates every body into its interpolation transform
        // before detecting contacts, and reads the bone from that transform.
        foreach(var system in Systems)foreach(var bone in system.Bones){
            var rig=bone.Rig;var start=rig.WorldTransform;BMatrix predicted;
            if(bone.Kinematic){
                TransformUtil.IntegrateTransform(ref start,ref bone.FrameLinear,ref bone.FrameAngular,dt,out predicted);
                // Bullet derives a kinematic body's velocity from interpolation -> world over the step. Placing the
                // interpolation transform one step behind keeps the body at its start pose during the solve and
                // gives the solver the frame velocity, which is how the plugin's world treats kinematic bones.
                var backLinear=-bone.FrameLinear;var backAngular=-bone.FrameAngular;
                TransformUtil.IntegrateTransform(ref start,ref backLinear,ref backAngular,dt,out var behind);
                rig.InterpolationWorldTransform=behind;
            }else{
                var linear=rig.LinearVelocity*Math.Pow(1-bone.LinearDamping,dt);var angular=rig.AngularVelocity*Math.Pow(1-bone.AngularDamping,dt);
                TransformUtil.IntegrateTransform(ref start,ref linear,ref angular,dt,out predicted);
            }
            // FSMP multiplies rigToLocal on the left here and on the right everywhere else. The two agree whenever
            // centerOfMassTransform is identity; the order is kept so an offset centre of mass collides as in game.
            var t=bone.RigToLocal*SmpTransform.From(predicted);
            bone.Current=new(t.Rotation,t.Origin,bone.Current.Scale);
        }
        LastPredictMs+=Since(phase);
        long collisionStart=System.Diagnostics.Stopwatch.GetTimestamp();
        Contacts.Clear();Collide();
        LastCollisionMs+=System.Diagnostics.Stopwatch.GetElapsedTime(collisionStart).TotalMilliseconds;
        LastContacts=Contacts.Count;PeakContacts=Math.Max(PeakContacts,Contacts.Count);
        foreach(var contact in Contacts){
            var a=contact.A.Rig;var b=contact.B.Rig;
            var worldA=SmpTransform.Bullet(contact.WorldA);var worldB=SmpTransform.Bullet(contact.WorldB);
            var localA=BVector.TransformCoordinate(worldA,BMatrix.Invert(a.WorldTransform));var localB=BVector.TransformCoordinate(worldB,BMatrix.Invert(b.WorldTransform));
            using var point=new ManifoldPoint(localA,localB,SmpTransform.Bullet(contact.Normal),contact.Depth);
            point.PositionWorldOnA=worldA;point.PositionWorldOnB=worldB;
            point.CombinedFriction=a.Friction*b.Friction;point.CombinedRestitution=a.Restitution*b.Restitution;point.CombinedRollingFriction=a.RollingFriction*b.RollingFriction;
            var manifold=dispatcher.GetNewManifold(a,b);manifold.AddManifoldPoint(point);manifolds.Add(manifold);
        }
        phase=System.Diagnostics.Stopwatch.GetTimestamp();
        foreach(var system in Systems)foreach(var spring in system.StiffSprings)spring.Apply(dt,Settings.Erp,Settings.Iterations);
        world.StepSimulation(dt,0,dt);
        LastSolveMs+=Since(phase);
        // FSMP clears every manifold after the solve; contacts are rebuilt from scratch each step.
        foreach(var manifold in manifolds)dispatcher.ReleaseManifold(manifold);
        manifolds.Clear();
        foreach(var system in Systems)foreach(var bone in system.Bones){
            if(!bone.Kinematic)continue;
            var rig=bone.Rig;var start=rig.WorldTransform;
            TransformUtil.IntegrateTransform(ref start,ref bone.FrameLinear,ref bone.FrameAngular,dt,out var next);
            rig.WorldTransform=next;rig.InterpolationWorldTransform=next;
        }
    }
    // Verification switch: SSE_SMP_UNOPTIMIZED=1 restores the original order (skin every body, then test pairs one by one)
    // for timing comparisons. Results are identical either way.
    internal static readonly bool Unoptimized=Environment.GetEnvironmentVariable("SSE_SMP_UNOPTIMIZED")=="1";
    readonly List<SmpBody> active=[],skinned=[];readonly List<(SmpBody A,SmpBody B)> bodyPairs=[];readonly List<List<SmpContact>> pairContacts=[];
    void Collide()
    {
        if(Unoptimized){CollideSequential();return;}
        long skin=System.Diagnostics.Stopwatch.GetTimestamp();
        // Bodies that can meet a partner get a broadphase box. Bodies with more than ten colliders take it from their
        // bone bounding spheres, as FSMP does, so their vertices are skinned only if a pair's boxes overlap.
        active.Clear();skinned.Clear();bodyPairs.Clear();
        foreach(var body in bodies){
            bool needed=false;
            foreach(var other in bodies)if(other!=body&&SmpCollision.NeedsCollision(body,other)){needed=true;break;}
            if(needed){if(body.UseBoundingSpheres)body.UpdateBounds();else body.Update(step);active.Add(body);}
            else if(UpdateAllBodies&&!body.Disabled)skinned.Add(body);
        }
        for(int i=0;i<active.Count;i++)for(int j=i+1;j<active.Count;j++){
            var a=active[i];var b=active[j];
            if(!SmpCollision.NeedsCollision(a,b)||!a.Broadphase.CollideWith(b.Broadphase))continue;
            bodyPairs.Add((a,b));
            if(!skinned.Contains(a))skinned.Add(a);if(!skinned.Contains(b))skinned.Add(b);
        }
        // The debug overlay draws every body.
        if(UpdateAllBodies)foreach(var body in active)if(!skinned.Contains(body))skinned.Add(body);
        // Skinning is independent per body; a body skinned above returns at once.
        if(skinned.Count>1)Parallel.ForEach(skinned,body=>body.Update(step));else foreach(var body in skinned)body.Update(step);
        foreach(var (a,b) in bodyPairs)if(a.Shape.Triangles&&b.Shape.Triangles){a.Shape.VertexCollision!.Update(step);b.Shape.VertexCollision!.Update(step);}
        LastSkinMs+=Since(skin);
        long pair=System.Diagnostics.Stopwatch.GetTimestamp();
        // Body pairs in parallel, each into its own list; the lists are appended in pair order.
        while(pairContacts.Count<bodyPairs.Count)pairContacts.Add([]);
        for(int k=0;k<bodyPairs.Count;k++)pairContacts[k].Clear();
        if(bodyPairs.Count>1)Parallel.For(0,bodyPairs.Count,k=>SmpCollision.Collide(bodyPairs[k].A,bodyPairs[k].B,step,pairContacts[k]));
        else if(bodyPairs.Count==1)SmpCollision.Collide(bodyPairs[0].A,bodyPairs[0].B,step,pairContacts[0]);
        for(int k=0;k<bodyPairs.Count;k++)Contacts.AddRange(pairContacts[k]);
        LastPairsMs+=Since(pair);
    }
    void CollideSequential()
    {
        active.Clear();
        foreach(var body in bodies){
            bool needed=false;
            foreach(var other in bodies)if(other!=body&&SmpCollision.NeedsCollision(body,other)){needed=true;break;}
            long skin=System.Diagnostics.Stopwatch.GetTimestamp();
            if(needed){body.Update(step);active.Add(body);}else if(UpdateAllBodies&&!body.Disabled)body.Update(step);
            LastSkinMs+=Since(skin);
        }
        for(int i=0;i<active.Count;i++)for(int j=i+1;j<active.Count;j++){
            var a=active[i];var b=active[j];
            if(!SmpCollision.NeedsCollision(a,b)||!a.Broadphase.CollideWith(b.Broadphase))continue;
            long pair=System.Diagnostics.Stopwatch.GetTimestamp();
            if(a.Shape.Triangles&&b.Shape.Triangles){a.Shape.VertexCollision!.Update(step);b.Shape.VertexCollision!.Update(step);}
            SmpCollision.Collide(a,b,step,Contacts,sequential:true);LastPairsMs+=Since(pair);
        }
    }
    public void Dispose()
    {
        foreach(var manifold in manifolds)dispatcher.ReleaseManifold(manifold);
        manifolds.Clear();
        foreach(var system in Systems){
            foreach(var constraint in system.Constraints){world.RemoveConstraint(constraint);constraint.Dispose();}
            foreach(var bone in system.Bones){world.RemoveRigidBody(bone.Rig);bone.Rig.Dispose();}
        }
        Systems.Clear();bodies.Clear();
        world.Dispose();solver.Dispose();broadphase.Dispose();dispatcher.Dispose();configuration.Dispose();EmptyShape.Dispose();
    }
}
