namespace SSEPreview;

// AE 1.6.1170: BSFadeNode helper 0x14147B110 and property fade 0x14147C470.
internal static class RefractionFade
{
    internal static float NormalizedDistance(float distance,float cameraLodAdjust=1,float categoryScale=0)
    {
        if(!float.IsFinite(distance)||distance<0||!float.IsFinite(cameraLodAdjust)||cameraLodAdjust<=0||!float.IsFinite(categoryScale))throw new ArgumentOutOfRangeException(nameof(distance));
        float multiplier=categoryScale>0?cameraLodAdjust/categoryScale:.2f;
        return distance*multiplier*BitConverter.Int32BitsToSingle(0x3911a2b4);
    }
    internal static (bool Render,float Strength) At(float normalizedDistance)
    {
        if(!float.IsFinite(normalizedDistance))throw new ArgumentOutOfRangeException(nameof(normalizedDistance));
        if(normalizedDistance>.03f)return(false,0);
        if(normalizedDistance<=.025f)return(true,1);
        return(true,Math.Clamp((normalizedDistance-.03f)/(.025f-.03f),0,1));
    }
    internal static void Tests(Action<bool,string> check)
    {
        check(NormalizedDistance(7200,1,1)==1,"Refraction distance uses the native 1/7200 float constant");
        check(NormalizedDistance(1000,2,4)==NormalizedDistance(500,1,1),"Refraction camera LOD and positive category scale normalize distance");
        check(NormalizedDistance(1000,8,0)==NormalizedDistance(1000,1,-1),"Nonpositive native fade categories use the fixed 0.2 multiplier");
        check(At(.025f)==(true,1)&&Math.Abs(At(.0275f).Strength-.5f)<1e-6,"Refraction fade retains full strength then falls through the native interval");
        check(At(.03f)==(true,0)&&At(float.BitIncrement(.03f))==(false,0),"Refraction keeps the zero-strength boundary pass and omits values above it");
        check(At(NormalizedDistance(800)).Strength==1&&!At(NormalizedDistance(1200)).Render,"Default category preserves nearby refraction and removes distant passes");
    }
}
