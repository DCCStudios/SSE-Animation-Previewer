using System.Numerics;

namespace SSEPreview;

/// <summary>Deterministic force integration in the particle system's simulation coordinates.</summary>
public static class ParticleForces
{
    // AE 1.6.1170 RVA DD7650: strength * 1.6 plus independent per-axis
    // turbulence * scale * 500. The caller supplies repeatable signed random samples.
    public static Vector3 Gravity(Vector3 position,Matrix4x4 frame,Vector3 localAxis,
        float strength,float decay,bool spherical,float turbulence=0,float turbulenceScale=1,Vector3 randomSigned=default)
        =>Gravity(position,frame.Translation,Unit(Vector3.TransformNormal(localAxis,frame)),strength,decay,spherical,turbulence,turbulenceScale,randomSigned);
    static Vector3 Unit(Vector3 v){float length=v.Length();return float.IsFinite(length)&&length>1e-6f?v/length:Vector3.Zero;}
    // Resolved-frame form: origin and unit axis are computed once per step by the caller.
    public static Vector3 Gravity(Vector3 position,Vector3 origin,Vector3 axis,
        float strength,float decay,bool spherical,float turbulence,float turbulenceScale,Vector3 randomSigned)
    {
        if(!float.IsFinite(strength)||!float.IsFinite(decay)||!float.IsFinite(turbulence)||!float.IsFinite(turbulenceScale))return Vector3.Zero;
        var displacement=origin-position;
        float distance=spherical?displacement.Length():Math.Abs(Vector3.Dot(displacement,axis));
        // Native spherical + decay + turbulence branch uses the transformed axis
        // for the underlying force (DD7E32), while retaining radial attenuation.
        var direction=spherical&&(decay==0||turbulence==0)?Unit(displacement):axis;
        float magnitude=1.6f*strength*(decay==0?1:MathF.Exp(-decay*distance));
        var noise=Vector3.Clamp(randomSigned,-Vector3.One,Vector3.One)*(turbulence*turbulenceScale*500);
        var result=direction*magnitude+noise;
        return float.IsFinite(result.X)&&float.IsFinite(result.Y)&&float.IsFinite(result.Z)?result:Vector3.Zero;
    }
    // AE 1.6.1170 inheritance initialization, RVA 0xDFACA0:
    // variation is relative to the multiplier; only the lower endpoint is clamped.
    public static float InheritanceFactor(float multiplier,float variation,float random)
    {
        if(!float.IsFinite(multiplier)||!float.IsFinite(variation)||!float.IsFinite(random))return 0;
        float spread=multiplier*variation;
        float low=Math.Clamp(multiplier-spread,0,float.MaxValue),high=multiplier+spread;
        return low+(high-low)*Math.Clamp(random,0,1);
    }
    // NifXML defines RangeFalloff as the outer distance, not an additional radius.
    // AE 1.6.1170 RVA DE7990 removes min(1, percentage * falloff * dt * 30) of axial velocity.
    public static Vector3 Drag(Vector3 velocity,Vector3 position,Matrix4x4 frame,Vector3 localAxis,
        float percentage,float range,float outerRange,float dt)
    {
        var axis=Vector3.TransformNormal(localAxis,frame);
        float length=axis.Length();
        if(!float.IsFinite(length)||length<1e-7f)return velocity;
        return Drag(velocity,position,Vector3.Transform(Vector3.Zero,frame),axis/length,percentage,range,outerRange,dt);
    }
    // Resolved-frame form: the caller supplies the world origin and unit axis once per step.
    public static Vector3 Drag(Vector3 velocity,Vector3 position,Vector3 origin,Vector3 axis,
        float percentage,float range,float outerRange,float dt)
    {
        if(dt<=0||percentage<=0||!float.IsFinite(percentage))return velocity;
        double distance=Vector3.Distance(position,origin),weight;
        if(range<0||outerRange<0)return velocity;
        if(distance<=range)weight=1;
        else if(outerRange<=range||distance>=outerRange)weight=0;
        else weight=(outerRange-distance)/(outerRange-range);
        float removed=(float)Math.Clamp(percentage*weight*dt*30,0,1);
        return velocity-axis*(Vector3.Dot(velocity,axis)*removed);
    }
    // AE 1.6.1170 bomb loop (RVA 0xDE23A0): full-distance cutoff,
    // then symmetry-specific linear attenuation or expf(-distance/decay).
    public static Vector3 Bomb(Vector3 position,Matrix4x4 frame,Vector3 localAxis,
        float acceleration,float decay,int decayType,int symmetry)
    {
        if(!float.IsFinite(acceleration)||!float.IsFinite(decay)||symmetry is <0 or >2||decayType is <0 or >2)return Vector3.Zero;
        var displacement=position-frame.Translation;
        // AE checks full distance before projecting onto cylindrical/planar symmetry.
        if(decayType!=0&&(decay<=0||displacement.Length()>decay))return Vector3.Zero;
        if(symmetry!=0){
            var axis=Vector3.TransformNormal(localAxis,frame);
            float length=axis.Length();if(!float.IsFinite(length)||length<1e-7f)return Vector3.Zero;
            axis/=length;var axial=axis*Vector3.Dot(displacement,axis);
            displacement=symmetry==1?displacement-axial:axial;
            if(symmetry==2&&displacement.LengthSquared()<1e-14f)return axis*acceleration;
        }
        float distance=displacement.Length();if(!float.IsFinite(distance)||distance<1e-7f)return Vector3.Zero;
        float attenuation=1;
        if(decayType!=0){
            if(decay<=0)return Vector3.Zero;
            attenuation=decayType==1?Math.Max(0,1-distance/decay):MathF.Exp(-distance/decay);
        }
        return displacement*(acceleration*attenuation/distance);
    }}
