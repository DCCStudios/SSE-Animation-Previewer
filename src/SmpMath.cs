using BMatrix=BulletSharp.Math.Matrix;
using BVector=BulletSharp.Math.Vector3;
using M4d=OpenTK.Mathematics.Matrix4d;
using Qd=OpenTK.Mathematics.Quaterniond;
using V3d=OpenTK.Mathematics.Vector3d;

namespace SSEPreview;

// Rotation, origin and uniform scale, composed the way FSMP's btQsTransform composes them: a*b applies b first.
// Matrices in this application are row-vector matrices (v' = v·M), the same layout BulletSharp marshals.
public readonly struct SmpTransform
{
    public readonly Qd Rotation;public readonly V3d Origin;public readonly double Scale;
    public SmpTransform(Qd rotation,V3d origin,double scale=1){Rotation=rotation;Origin=origin;Scale=scale;}
    public static readonly SmpTransform Identity=new(Qd.Identity,V3d.Zero);
    public static V3d Rotate(Qd q,V3d v)=>V3d.Transform(v,q);
    public static SmpTransform operator*(SmpTransform a,SmpTransform b)=>new(Qd.Normalize(a.Rotation*b.Rotation),a.Origin+Rotate(a.Rotation,b.Origin*a.Scale),a.Scale*b.Scale);
    public V3d Apply(V3d point)=>Origin+Rotate(Rotation,point*Scale);
    public SmpTransform Inverse(){var r=Qd.Invert(Rotation);double s=1/Scale;return new(r,Rotate(r,-Origin*s),s);}
    // btQsTransform::asTransform drops the scale and keeps rotation and origin.
    public SmpTransform Rigid=>new(Rotation,Origin);
    public SmpTransform WithScale(double scale)=>new(Rotation,Origin,scale);
    public M4d ToMatrix()=>M4d.CreateScale(Scale)*M4d.CreateFromQuaternion(Rotation)*M4d.CreateTranslation(Origin);
    public BMatrix ToBullet(){var m=M4d.CreateFromQuaternion(Rotation);return new BMatrix{M11=m.M11,M12=m.M12,M13=m.M13,M21=m.M21,M22=m.M22,M23=m.M23,M31=m.M31,M32=m.M32,M33=m.M33,M41=Origin.X,M42=Origin.Y,M43=Origin.Z,M44=1};}
    public static SmpTransform From(M4d m)
    {
        double scale=m.Row0.Xyz.Length;if(!(scale>1e-12)||!double.IsFinite(scale))scale=1;
        var rotation=m.ExtractRotation();
        if(!double.IsFinite(rotation.W)||rotation.LengthSquared<1e-12)rotation=Qd.Identity;
        return new(Qd.Normalize(rotation),m.Row3.Xyz,scale);
    }
    public static SmpTransform From(BMatrix m)=>From(new M4d(m.M11,m.M12,m.M13,0,m.M21,m.M22,m.M23,0,m.M31,m.M32,m.M33,0,m.M41,m.M42,m.M43,1));
    public static BVector Bullet(V3d v)=>new(v.X,v.Y,v.Z);
    public static V3d Vector(BVector v)=>new(v.X,v.Y,v.Z);
    public bool Finite=>double.IsFinite(Origin.X)&&double.IsFinite(Origin.Y)&&double.IsFinite(Origin.Z)&&double.IsFinite(Rotation.X)&&double.IsFinite(Rotation.Y)&&double.IsFinite(Rotation.Z)&&double.IsFinite(Rotation.W)&&double.IsFinite(Scale);
}

// Axis-aligned box used by the collider tree; an invalidated box collides with nothing.
public struct SmpAabb
{
    public V3d Min,Max;
    public static SmpAabb Empty=>new(){Min=new(double.MaxValue),Max=new(-double.MaxValue)};
    public readonly bool CollideWith(in SmpAabb other)=>!(other.Max.X<Min.X||other.Max.Y<Min.Y||other.Max.Z<Min.Z||Max.X<other.Min.X||Max.Y<other.Min.Y||Max.Z<other.Min.Z);
    public void Merge(in SmpAabb other){Min=V3d.ComponentMin(Min,other.Min);Max=V3d.ComponentMax(Max,other.Max);}
    public readonly bool Valid=>Min.X<=Max.X;
}
