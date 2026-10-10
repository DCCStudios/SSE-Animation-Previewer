using System.Numerics;
namespace SSEPreview;

// Center/sample history for BSStripPSysData. Cross-section vertices and native
// render scheduling are separate: this is not a billboard-to-ribbon approximation.
public sealed class ParticleStripHistory
{
    public sealed record Point(float Time,Vector3 Center,Vector4 Color,float Radius)
    {
        public Vector3 Direction,SideA,SideB;
        public Vector3 Vertex(int lane)=>Center+(lane<2?SideB:SideA)*(lane%2==0?.5f:-.5f)*Radius;
    }
    static Vector3 Unit(Vector3 v)=>v.Length()>1e-6f?Vector3.Normalize(v):Vector3.Zero;
    static void Frame(Point point,Point? previous,Vector3? next)
    {
        if(next==null){if(previous!=null){point.Direction=previous.Direction;point.SideA=previous.SideA;point.SideB=previous.SideB;}return;}
        var outgoing=Unit(next.Value-point.Center);
        if(previous==null){
            point.Direction=outgoing;
            point.SideB=Unit(Vector3.Cross(new(0,BitConverter.Int32BitsToSingle(0x3dcbc89b),BitConverter.Int32BitsToSingle(0x3f7ebac2)),outgoing));
            point.SideA=Unit(Vector3.Cross(point.SideB,outgoing));return;
        }
        point.Direction=Unit(outgoing+previous.Direction);
        var axis=Unit(Vector3.Cross(previous.Direction,point.Direction));
        float angle=MathF.Acos(Math.Clamp(Vector3.Dot(previous.Direction,point.Direction),-1,1));
        var rotation=Matrix4x4.CreateFromAxisAngle(axis,angle);
        point.SideA=Vector3.TransformNormal(previous.SideA,rotation);point.SideB=Vector3.TransformNormal(previous.SideB,rotation);
    }
    readonly List<Point> points=[];
    public IReadOnlyList<Point> Points=>points;
    public int Capacity {get;}
    public float PointLifetime {get;}
    public int SegmentCount {get;private set;}
    public float TailFraction {get;private set;}
    float lastCommittedTime;
    public ParticleStripHistory(int authoredPointCount)
    {
        if(authoredPointCount is <0 or >65532)throw new ArgumentOutOfRangeException(nameof(authoredPointCount));
        // DF8245 adds three working slots; DF8250 derives time from the authored count.
        Capacity=authoredPointCount+3;PointLifetime=authoredPointCount*(1f/60);
    }
    public void Sample(float time,Vector3 center,Vector4 color,float radius,bool alive)
    {
        if(!float.IsFinite(time)||!float.IsFinite(radius))throw new ArgumentException("Strip sample time and radius must be finite");
        if(points.Count>0&&time<points[^1].Time)throw new ArgumentException("Reset the particle before sampling an earlier time");
        if(alive){
            // DF87D7..DF8837: retain a provisional head between 60 Hz commits.
            // The first two samples always append; a full buffer replaces its head.
            bool append=(time-lastCommittedTime>1f/60||points.Count<2)&&points.Count<Capacity-1;
            if(append)lastCommittedTime=time;
            else if(points.Count>0)points.RemoveAt(points.Count-1);
            if(points.Count>0)Frame(points[^1],points.Count>1?points[^2]:null,center);
            var point=new Point(time,center,color,radius);Frame(point,points.Count>0?points[^1]:null,null);points.Add(point);
            SegmentCount=Math.Max(SegmentCount,points.Count);
        }
        TailFraction=0;
        // DF8AD0..DF8BA6 keeps the oldest crossing segment and clips its geometry
        // to the cutoff. Original sample attributes stay intact for interpolation.
        while(points.Count>0&&time-points[0].Time>PointLifetime){
            if(points.Count<2||time-points[1].Time>PointLifetime){points.RemoveAt(0);continue;}
            float duration=points[1].Time-points[0].Time;
            TailFraction=duration>0?Math.Clamp((time-PointLifetime-points[0].Time)/duration,0,1):0;
            break;
        }
    }
}
