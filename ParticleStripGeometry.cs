using System.Numerics;
namespace SSEPreview;
internal static class ParticleStripGeometry
{
    internal readonly record struct Vertex(Vector3 Position,Vector4 Color,Vector2 UV);
    internal static Vector3 Position(ParticleStripHistory history,int i,int lane)
    {
        var position=history.Points[i].Vertex(lane);
        return i==0&&history.TailFraction>0
            ?Vector3.Lerp(position,history.Points[1].Vertex(lane),history.TailFraction):position;
    }
    internal static IEnumerable<Vector3> BoundsVertices(ParticleStripHistory history)
    {
        if(history.Points.Count<2||history.SegmentCount<2)yield break;
        for(int i=0;i<history.Points.Count;i++)for(int lane=0;lane<4;lane++)yield return Position(history,i,lane);
    }
    internal static IEnumerable<Vertex> Triangles(ParticleStripHistory history,Vector4 uv)
    {
        var points=history.Points;if(points.Count<2||history.SegmentCount<2)yield break;
        Vertex V(int i,int lane){
            var point=points[i];var position=Position(history,i,lane);
            return new(position,point.Color,new(uv.X+(uv.Z-uv.X)*i/(history.SegmentCount-1),lane%2==0?uv.Y:uv.W));
        }
        // Native E51F00 emits one strip for lanes 0/1, then another for lanes 2/3.
        for(int pair=0;pair<4;pair+=2)for(int i=0;i<points.Count-1;i++){
            yield return V(i,pair);yield return V(i,pair+1);yield return V(i+1,pair);
            yield return V(i,pair+1);yield return V(i+1,pair+1);yield return V(i+1,pair);
        }
    }
}
