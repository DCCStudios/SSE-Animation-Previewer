using System.Numerics;
namespace SSEPreview;
public static class ProjectileMotion
{
    // AE 1.6.1170 RVA 0x7E9790/0x7EABA0 use this exact single-precision constant.
    public static readonly float GravityUnits=BitConverter.Int32BitsToSingle(0x442B983C);
    public static float GravityFactor(float authored,int type)=>(type&2)!=0?1:authored;
    public static bool IsBallistic(int type)=>type is 1 or 2 or 64;
    public static readonly float PitchLimit=BitConverter.Int32BitsToSingle(0x3FC6D3F2);
    public static float ModelElevation(float speed,float gravity,int type,double time,float elevation=0)
    {
        if(!IsBallistic(type)||time<=0||!(speed>0)||!float.IsFinite(speed)||!float.IsFinite(gravity)||!float.IsFinite(elevation))return elevation;
        double acceleration=GravityFactor(gravity,type)*GravityUnits;
        if(acceleration==0)return elevation;
        double horizontal=speed*Math.Cos(elevation),vertical=speed*Math.Sin(elevation);
        // Native gravity adds an unsigned turning angle to TESObjectREFR pitch
        // (positive downward), clamped to +/-89 degrees. Integrate that turn
        // along our continuous trajectory, not the presentation frame clock.
        double turn=Math.Abs(Math.Atan2(vertical-acceleration*Math.Max(0,time),horizontal)-Math.Atan2(vertical,horizontal));
        return (float)Math.Clamp(elevation-turn,-PitchLimit,PitchLimit);
    }
    public static Vector3 Displacement(float speed,float gravity,int type,double time,float elevation=0)
    {
        double t=Math.Max(0,time),acceleration=GravityFactor(gravity,type)*GravityUnits;
        return new(0,(float)(speed*Math.Cos(elevation)*t),(float)(speed*Math.Sin(elevation)*t-.5*acceleration*t*t));
    }
    // Continuous trajectory integral. Native gameplay advances in discrete frames;
    // this preview deliberately remains independent of the presentation frame rate.
    public static double Distance(float speed,float gravity,int type,double time,float elevation=0)
    {
        double t=Math.Max(0,time),a=GravityFactor(gravity,type)*GravityUnits;
        double horizontal=speed*Math.Cos(elevation),vertical=speed*Math.Sin(elevation);
        if(a==0)return Math.Abs(speed)*t;
        double h=Math.Abs(horizontal);
        if(Math.Abs(a*t)<1e-6*Math.Max(Math.Abs(speed),1)){
            double Velocity(double at)=>Math.Sqrt(h*h+(vertical-a*at)*(vertical-a*at));
            return t*(Velocity(0)+4*Velocity(t*.5)+Velocity(t))/6;
        }
        double Primitive(double z)=>h<1e-10?.5*z*Math.Abs(z):
            .5*(z*Math.Sqrt(h*h+z*z)+h*h*Math.Asinh(z/h));
        return Math.Max(0,(Primitive(vertical)-Primitive(vertical-a*t))/a);
    }
    public static double RangeTime(float speed,float gravity,int type,float range,float elevation=0)
    {
        if(!float.IsFinite(range)||range<0)return double.PositiveInfinity;
        if(range==0)return 0;
        double v=Math.Abs(speed),a=Math.Abs(GravityFactor(gravity,type)*GravityUnits);
        if(!double.IsFinite(v)||!double.IsFinite(a))return double.PositiveInfinity;
        if(a==0)return v>0?range/v:double.PositiveInfinity;
        double hi=Math.Max(1e-6,range/Math.Max(v,1));
        for(int i=0;i<64&&Distance(speed,gravity,type,hi,elevation)<range;i++)hi*=2;
        double lo=0;
        for(int i=0;i<64;i++){
            double mid=(lo+hi)*.5;
            if(Distance(speed,gravity,type,mid,elevation)<range)lo=mid;else hi=mid;
        }
        return (lo+hi)*.5;
    }
}
