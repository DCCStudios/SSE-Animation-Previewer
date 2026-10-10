namespace SSEPreview;

public static class ParticleLod
{
    // BSPSysLODModifier AE RVA 1511440. Input is normalized fade-node distance,
    // not world-space units. A fixed input avoids guessing runtime fade settings.
    public static (float Emission,float Size) Evaluate(float distance,float begin,float end,float endEmission,float endSize)
    {
        if(!float.IsFinite(distance)||!float.IsFinite(begin)||!float.IsFinite(end)||!float.IsFinite(endEmission)||!float.IsFinite(endSize))return (1,1);
        if(distance<=begin)return (1,1);
        float fraction=end<=begin||distance>end?1:(distance-begin)/(end-begin);
        return (Math.Max(0,1+(endEmission-1)*fraction),Math.Max(0,1+(endSize-1)*fraction));
    }
}