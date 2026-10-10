using System.Numerics;

namespace SSEPreview;

// Swept point contacts avoid tunnelling through small colliders between fixed ticks.
// Collider dimensions are evaluated in their authored local coordinate system.
public static class ParticleCollision
{
    public readonly record struct Hit(float Fraction,Vector3 Normal);
    public static Hit? Sweep(Vector3 start,Vector3 end,Matrix4x4 frame,bool sphere,
        float radius,float width,float height,Vector3 xAxis,Vector3 yAxis)
    {
        if(!Matrix4x4.Invert(frame,out var inverse))return null;
        Vector3 a=Vector3.Transform(start,inverse),b=Vector3.Transform(end,inverse),d=b-a;
        float fraction;Vector3 normal;
        if(sphere){
            if(!(radius>0)||!float.IsFinite(radius))return null;
            float aa=Vector3.Dot(d,d),bb=Vector3.Dot(a,d),cc=Vector3.Dot(a,a)-radius*radius;
            if(aa<1e-16f)return null;
            float discriminant=bb*bb-aa*cc;if(discriminant<0)return null;
            float root=MathF.Sqrt(discriminant);
            float near=(-bb-root)/aa,far=(-bb+root)/aa;
            fraction=near>=0?near:far;
            if(fraction<0||fraction>1)return null;
            normal=a+d*fraction;
        }else{
            normal=Vector3.Cross(xAxis,yAxis);
            if(normal.LengthSquared()<1e-12f||width<0||height<0)return null;
            normal=Vector3.Normalize(normal);
            float divisor=Vector3.Dot(d,normal);
            if(Math.Abs(divisor)<1e-10f)return null;
            fraction=-Vector3.Dot(a,normal)/divisor;
            if(fraction<0||fraction>1)return null;
            var at=a+d*fraction;
            float xx=Vector3.Dot(xAxis,xAxis),yy=Vector3.Dot(yAxis,yAxis),xy=Vector3.Dot(xAxis,yAxis);
            float determinant=xx*yy-xy*xy;if(determinant<1e-12f)return null;
            float ax=Vector3.Dot(at,xAxis),ay=Vector3.Dot(at,yAxis);
            float x=(ax*yy-ay*xy)/determinant,y=(ay*xx-ax*xy)/determinant;
            if(Math.Abs(x)>width*.5f||Math.Abs(y)>height*.5f)return null;
        }
        normal=Vector3.TransformNormal(normal,Matrix4x4.Transpose(inverse));
        if(normal.LengthSquared()<1e-12f)return null;
        normal=Vector3.Normalize(normal);
        if(Vector3.Dot(end-start,normal)>0)normal=-normal;
        return new(fraction,normal);
    }
    public static Vector3 Reflect(Vector3 velocity,Vector3 normal,float bounce) =>
        velocity-(1+Math.Max(0,float.IsFinite(bounce)?bounce:0))*Vector3.Dot(velocity,normal)*normal;
}