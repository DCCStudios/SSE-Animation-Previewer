namespace SSEPreview;
internal static class ParticleAspect
{
    internal static float AtSpeed(float first,float second,float speed1,float speed2,float projectedSpeed,bool enabled)
    {
        if(!float.IsFinite(first)||first<=0)first=1;
        if(!enabled)return first;
        if(!float.IsFinite(second)||second<=0||!float.IsFinite(speed1)||!float.IsFinite(speed2)||speed1==speed2||!float.IsFinite(projectedSpeed))return first;
        // AE E51BA1..E51BDE: upper comparison followed by lower clamp (also for reversed limits).
        float speed=projectedSpeed>speed2?speed2:Math.Max(projectedSpeed,speed1);
        float ratio=first+(speed-speed1)/(speed2-speed1)*(second-first);
        return float.IsFinite(ratio)&&ratio>0?ratio:first;
    }
}
