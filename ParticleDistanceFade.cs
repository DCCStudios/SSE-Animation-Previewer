namespace SSEPreview;
internal static class ParticleDistanceFade
{
    // AE D40BA0 reads NiParticles +160/+162/+164/+166 in this serialized order.
    // Preserve native comparisons: FarBegin is the outer cutoff despite its name.
    internal static float At(float distance,ushort farBegin,ushort farEnd,ushort nearBegin,ushort nearEnd)
    {
        if(distance>=farBegin&&farEnd>0)return 0;
        if(distance<=nearEnd)return 0;
        if(distance>=farEnd&&farEnd>0)return 1-(distance-farEnd)/(farBegin-farEnd);
        if(distance>nearBegin)return 1;
        return (distance-nearEnd)/(nearBegin-nearEnd);
    }
}
