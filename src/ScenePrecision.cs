using FO4_Base_Library;
using NiflySharp.Blocks;
using OpenTK.Mathematics;
using M4=OpenTK.Mathematics.Matrix4;
using M4d=OpenTK.Mathematics.Matrix4d;
using M3d=OpenTK.Mathematics.Matrix3d;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Precision attack collisions over the scene: the capsules a clip's Collision_* annotations create, drawn on their
// bones at the current pose. Red is the attack capsule, blue the recoil capsule, as in Precision's own debug view.
public sealed partial class Scene
{
    readonly SmpDebugDraw precisionDraw=new();
    public static readonly Color4 PrecisionAttack=new(1f,.22f,.18f,.95f),PrecisionRecoil=new(.3f,.45f,1f,.9f);
    bool showPrecision;
    public bool ShowPrecision{get=>showPrecision;set{showPrecision=value;view.UpdateRequired=true;view.Invalidate();}}
    // Precision sizes the player's capsules with the fPlayerAttack* multipliers and its own recoil switch.
    bool precisionAsPlayer=true;
    public bool PrecisionAsPlayer{get=>precisionAsPlayer;set{precisionAsPlayer=value;precisionTimeline=null;view.UpdateRequired=true;view.Invalidate();}}
    // Whether the attack playing at a scene time is a power attack (Duration(0) then lasts longer). Set per clip.
    Func<double,bool> precisionPowerAttack=_=>false;
    public Func<double,bool> PrecisionPowerAttack{get=>precisionPowerAttack;set{precisionPowerAttack=value;precisionTimeline=null;}}
    // Capsule collision shapes on skeleton bones, in Havok units, keyed by node name.
    readonly Dictionary<string,(V3d A,V3d B,double Radius)> boneCapsules=new(StringComparer.OrdinalIgnoreCase);
    public int BoneCapsules=>boneCapsules.Count;
    PrecisionSettings? precisionSettings;long precisionSettingsRevision=-1;
    PrecisionTimeline? precisionTimeline;(AnimationClip?,int) precisionKey;
    double? weaponMeshLength;
    public int PrecisionLines=>precisionDraw.LastLines;
    public readonly record struct PrecisionCapsule(PrecisionCollision Collision,bool Recoil,V3d A,V3d B,double Radius,string Detail);

    void ReadBoneCapsules(string path)
    {
        boneCapsules.Clear();precisionTimeline=null;
        try{
            var nif=new Nifcontent_Class_Manolo();nif.Load_Manolo(assets.Read(path));
            foreach(var node in nif.Blocks.OfType<NiNode>()){
                string name=node.Name?.String??"";
                if(name.Length==0||node.CollisionObject==null||node.CollisionObject.IsEmpty())continue;
                // Precision reads the shape straight off the rigid body and only uses it when it is a capsule.
                if(nif.GetBlock(node.CollisionObject) is bhkNiCollisionObject collision&&nif.GetBlock(collision.Body) is bhkWorldObject body&&nif.GetBlock(body.Shape) is bhkCapsuleShape capsule)
                    boneCapsules[name]=(new V3d(capsule.FirstPoint.X,capsule.FirstPoint.Y,capsule.FirstPoint.Z),new V3d(capsule.SecondPoint.X,capsule.SecondPoint.Y,capsule.SecondPoint.Z),capsule.Radius);
            }
        }catch(Exception ex){Diagnostics.Add("Skeleton collision capsules unavailable for Precision: "+ex.Message);}
    }
    public PrecisionSettings PrecisionConfig{get{
        if(precisionSettings==null||precisionSettingsRevision!=assets.Revision){precisionSettings=PrecisionSettings.Load(assets);precisionSettingsRevision=assets.Revision;precisionTimeline=null;}
        return precisionSettings;
    }}
    // Precision finds the node with GetObjectByName, which ignores case.
    HierarchiBone_class? PrecisionBone(string node)
    {
        if(Skeleton.SkeletonDictionary.TryGetValue(node,out var bone))return bone;
        foreach(var (name,candidate) in Skeleton.SkeletonDictionary)if(name.Equals(node,StringComparison.OrdinalIgnoreCase))return candidate;
        return null;
    }
    public PrecisionTimeline? Precision{get{
        var settings=PrecisionConfig;
        if(Clip==null)return null;
        var key=(Clip,Clip.Events.Count);
        if(precisionTimeline==null||!key.Equals(precisionKey)){
            precisionTimeline=PrecisionTimeline.Build(Clip.Events,settings,precisionAsPlayer,precisionPowerAttack,n=>PrecisionBone(n)!=null);precisionKey=key;
        }
        return precisionTimeline;
    }}
    // GetWeaponMeshLength on the equipped weapon model: geometry bounds through the node chain up to the model root,
    // skipping hidden objects, billboards and particles. Effect-shader shapes are left out as well; Precision skips
    // those that do not write depth, which covers the glow cards weapons carry.
    public double WeaponMeshLength()
    {
        if(weaponMeshLength is {} cached)return cached;
        var bound=PrecisionGeometry.MergedBound(WeaponBoundSpheres());weaponBound=bound;
        return (weaponMeshLength=bound.Any?bound.Radius+Math.Abs(bound.Center.Y):0).Value;
    }
    (bool Any,V3d Center,double Radius) weaponBound;
    internal string WeaponBoundText{get{WeaponMeshLength();return FormattableString.Invariant($"any {weaponBound.Any}, centre ({weaponBound.Center.X:F1}, {weaponBound.Center.Y:F1}, {weaponBound.Center.Z:F1}), radius {weaponBound.Radius:F1}");}}
    // The weapon node axis the blade runs along, as the FF7 Limit Gauge plugin finds it: the dominant axis of the
    // weapon bound's centre, +Y when the bound says nothing. 0..2 = +X +Y +Z, 3..5 = -X -Y -Z.
    public int WeaponBladeAxis()
    {
        WeaponMeshLength();
        if(!weaponBound.Any||!(weaponBound.Radius>8)||!(weaponBound.Center.Length>5))return 1;
        double[] parts=[weaponBound.Center.X,weaponBound.Center.Y,weaponBound.Center.Z];int best=0;
        for(int i=1;i<3;i++)if(Math.Abs(parts[i])>Math.Abs(parts[best]))best=i;
        return best+(parts[best]<0?3:0);
    }
    // Row-vector matrix that takes a NIF authored in blade space (origin at the grip, blade along +Z) to the weapon
    // node's space: X, Y, Z go to x, y, z with z on the blade axis (the plugin's toBlade).
    public System.Numerics.Matrix4x4 BladeMatrix()
    {
        int axis=WeaponBladeAxis();
        var z=System.Numerics.Vector3.Zero;float sign=axis<3?1:-1;
        if(axis%3==0)z.X=sign;else if(axis%3==1)z.Y=sign;else z.Z=sign;
        var x=axis%3==0?System.Numerics.Vector3.UnitY:System.Numerics.Vector3.UnitX;
        var y=System.Numerics.Vector3.Cross(z,x);x=System.Numerics.Vector3.Cross(y,z);
        return new(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,0,0,0,1);
    }
    List<(V3d,double)> WeaponBoundSpheres()
    {
        var spheres=new List<(V3d,double)>();
        foreach(var nif in weaponShapes.Select(s=>modelOwners.GetValueOrDefault(s)).OfType<Nifcontent_Class_Manolo>().Distinct()){
            var parents=new Dictionary<NiAVObject,NiNode>(ReferenceEqualityComparer.Instance);
            foreach(var node in nif.Blocks.OfType<NiNode>())foreach(var reference in node.Children.References)if(nif.GetBlock(reference) is NiAVObject child)parents.TryAdd(child,node);
            foreach(var shape in weaponShapes.Where(s=>modelOwners.GetValueOrDefault(s)==nif)){
                if(shape.NifShape is not NiAVObject geometry||shape.NifShape is not NiflySharp.INiShape data||shape.NifShader is BSEffectShaderProperty)continue;
                var chain=new List<NiAVObject>{geometry};
                for(var parent=parents.GetValueOrDefault(geometry);parent!=null;parent=parents.GetValueOrDefault(parent))chain.Add(parent);
                if(chain.Any(o=>((o.Flags_ui|o.Flags_us)&1)!=0||o is NiBillboardNode))continue;
                var bounds=data.Bounds;var center=new V3d(bounds.Center.X,bounds.Center.Y,bounds.Center.Z);double radius=bounds.Radius;
                foreach(var o in chain){
                    // NIF rotations are stored transposed relative to the game's NiMatrix3 [row, column] layout.
                    var m=o.Rotation;var rotate=new M3d(m.M11,m.M21,m.M31,m.M12,m.M22,m.M32,m.M13,m.M23,m.M33);
                    center=PrecisionGeometry.Apply(rotate,center*o.Scale)+new V3d(o.Translation.X,o.Translation.Y,o.Translation.Z);radius*=o.Scale;
                }
                spheres.Add((center,radius));
            }
        }
        return spheres;
    }
    // Every capsule alive at a time, placed on the skeleton's current pose and the actor's root motion.
    public List<PrecisionCapsule> PrecisionCapsulesAt(double time)
    {
        var list=new List<PrecisionCapsule>();var timeline=Precision;if(timeline==null)return list;
        var settings=PrecisionConfig;var actor=ActorWorld(time);
        foreach(var c in timeline.Collisions){
            if(!c.ActiveAt(time)||PrecisionBone(c.Definition.Node) is not {} bone)continue;
            var world=bone.GetGlobalTransform.ToMatrix4d()*actor;
            var shape=PrecisionShape(c,bone);
            list.Add(Place(c,false,shape,world));
            if(c.RecoilAt(time))list.Add(Place(c,true,PrecisionGeometry.Recoil(settings,c.Definition,shape.Radius),world));
        }
        return list;
    }
    PrecisionGeometry.Capsule PrecisionShape(PrecisionCollision c,HierarchiBone_class bone)
    {
        var settings=PrecisionConfig;
        if(c.Definition.IsWeapon){
            // The length comes from the model hanging under WEAPON when the collision starts. SHIELD has no
            // left-hand model here, so it takes fMinWeaponLength as Precision does for an empty node.
            bool inHand=c.Definition.Node=="WEAPON"&&WeaponBoneNameAt(c.Start).Equals("Weapon",StringComparison.OrdinalIgnoreCase);
            return PrecisionGeometry.Weapon(settings,c.Definition,inHand?WeaponMeshLength():0,precisionAsPlayer);
        }
        return PrecisionGeometry.Node(settings,c.Definition,boneCapsules.TryGetValue(bone.BoneName,out var capsule)?capsule:null,precisionAsPlayer);
    }
    public string PrecisionShapeDetail(PrecisionCollision c)=>PrecisionBone(c.Definition.Node) is {} bone?PrecisionShape(c,bone).Detail:"node missing";
    // The attack node is a child of the bone: its translation goes through the bone's full transform, its rotation
    // through the bone's rotation. Havok bodies do not scale, so the bone's scale stays off the capsule.
    static PrecisionCapsule Place(PrecisionCollision c,bool recoil,PrecisionGeometry.Capsule shape,M4d world)
    {
        var origin=V3d.TransformPosition(shape.Translate,world);
        var x=new V3d(world.M11,world.M12,world.M13).Normalized();var y=new V3d(world.M21,world.M22,world.M23).Normalized();var z=new V3d(world.M31,world.M32,world.M33).Normalized();
        V3d To(V3d local){var u=PrecisionGeometry.Apply(shape.Rotate,local);return origin+x*u.X+y*u.Y+z*u.Z;}
        return new(c,recoil,To(shape.A),To(shape.B),shape.Radius,shape.Detail);
    }
    void DrawPrecisionOverlay(M4 projection)
    {
        if(!showPrecision||Clip==null)return;
        precisionDraw.Begin();
        foreach(var capsule in PrecisionCapsulesAt(Time))precisionDraw.Capsule(capsule.A,capsule.B,capsule.Radius,capsule.Recoil?PrecisionRecoil:PrecisionAttack);
        precisionDraw.End();
        precisionDraw.Draw(projection,view.camera);
    }
}
