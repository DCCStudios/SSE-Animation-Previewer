using System.Numerics;
using NiflySharp.Structs;
namespace SSEPreview;

// Sampling is isolated from NIF/controller traversal so geometric invariants can be tested.
public sealed class ParticleMesh
{
    readonly Vector3[] vertices,normals;
    internal Func<int,(Vector3 Position,Vector3 Normal)>? Deform;
    internal Action? Prepare;
    internal bool SamplesWorldSpace;
    readonly Triangle[] faces;
    readonly (int A,int B)[] edges;
    public ParticleMesh(Vector3[] vertices,Vector3[] normals,Triangle[] faces)
    {
        this.vertices=vertices;this.normals=normals;
        this.faces=faces.Where(t=>t.V1<vertices.Length&&t.V2<vertices.Length&&t.V3<vertices.Length).ToArray();
        edges=this.faces.SelectMany(t=>new[]{Pair(t.V1,t.V2),Pair(t.V2,t.V3),Pair(t.V3,t.V1)}).Distinct().ToArray();
    }
    static (int,int) Pair(int a,int b)=>a<b?(a,b):(b,a);
    public bool Valid(int mode)=>vertices.Length>0&&(mode==0||(mode is 1 or 3?faces.Length>0:edges.Length>0));
    // AE rand() supplies 15-bit integers. Keep our deterministic seed but match its sample grid.
    internal static int Random15(Func<float> random)=>Math.Clamp((int)(random()*32768),0,32767);
    internal static Vector3 RandomVelocityDirection(Func<float> random)
    {
        // AE DEEF8F..DEEFEF normalizes three independently sampled signed components.
        var value=new Vector3(Random15(random)/32767f*2-1,Random15(random)/32767f*2-1,Random15(random)/32767f*2-1);
        return value.LengthSquared()>0?Vector3.Normalize(value):Vector3.Zero;
    }
    internal static float SurfaceFraction(float first,float second)
    {
        // AE 1.6.1170 RVA DEEA37..DEEAF2 uses one inverse-square-root iteration.
        int bits=unchecked(0x5f3759df-(BitConverter.SingleToInt32Bits(first)>>1));
        float estimate=BitConverter.Int32BitsToSingle(bits);
        float root=(1.5f-.5f*first*estimate*estimate)*estimate*first;
        return second*root;
    }
    public (Vector3 Position,Vector3 Normal) Sample(int mode,Func<float> random)
    {
        Prepare?.Invoke();
        var sampled=Deform==null?null:new Dictionary<int,(Vector3 Position,Vector3 Normal)>();
        (Vector3 Position,Vector3 Normal) Vertex(int i){if(sampled!.TryGetValue(i,out var value))return value;return sampled[i]=Deform?.Invoke(i)??(vertices[i],normals.Length==vertices.Length?normals[i]:Vector3.Zero);}
        Vector3 Position(int i)=>Deform==null?vertices[i]:Vertex(i).Position;
        int Pick(int count)=>Math.Min(count-1,(int)(random()*count));
        Vector3 Normal(int i)=>Deform==null?(normals.Length==vertices.Length?normals[i]:Vector3.Zero):Vertex(i).Normal;
        Vector3 p,n;
        if(mode==0){int i=Pick(vertices.Length);p=Position(i);n=Normal(i);}
        else if(mode is 1 or 3){
            var t=faces[Random15(random)%faces.Length];
            float a=1f/3,b=a,c=a;
            if(mode==3){float fraction=SurfaceFraction(Random15(random)/32767f,Random15(random)/32767f);a=0;b=1-fraction;c=fraction;}
            p=Position(t.V1)*a+Position(t.V2)*b+Position(t.V3)*c;
            // Native face emission uses the averaged face-vertex normals even in surface mode.
            n=(Normal(t.V1)+Normal(t.V2)+Normal(t.V3))/3;
            if(n.LengthSquared()<1e-12f)n=Vector3.Cross(Position(t.V2)-Position(t.V1),Position(t.V3)-Position(t.V1));
        }else{
            var edge=edges[Pick(edges.Length)];float t=mode==2?.5f:random();
            p=Vector3.Lerp(Position(edge.A),Position(edge.B),t);n=Vector3.Lerp(Normal(edge.A),Normal(edge.B),t);
        }
        return(p,n.LengthSquared()>1e-12f?Vector3.Normalize(n):Vector3.UnitZ);
    }
}