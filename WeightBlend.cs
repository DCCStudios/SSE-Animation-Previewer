using FO4_Base_Library;
using OpenTK.Mathematics;

namespace SSEPreview;

// Skyrim body weight: armor addons and race skins ship as _0.nif (weight 0) and _1.nif (weight 100)
// pairs with identical topology, and the game interpolates vertex positions by weight/100. This is
// a base-geometry provider for the renderer: for each registered shape it rewrites the pre-skin NIF
// local vertices from a pristine copy of the loaded mesh and its counterpart, so morphs, skinning
// and normals then run on the blended base like any other mesh.
public sealed class WeightBlend : IBaseGeometryProvider
{
    sealed record Pair(Vector3d[]? Loaded,Vector3d[] Counterpart,bool LoadedIsHeavy){public Vector3d[]? Loaded{get;set;}=Loaded;}
    readonly Dictionary<IRenderableShape,Pair> pairs=new(ReferenceEqualityComparer.Instance);
    float weight=100;
    public float Weight{get=>weight;set=>weight=Math.Clamp(value,0,100);}
    public int Count=>pairs.Count;
    public IEnumerable<IRenderableShape> Shapes=>pairs.Keys;
    public void Clear()=>pairs.Clear();
    // Returns the counterpart path for a weighted mesh path, or null when the name carries no weight suffix.
    public static string? CounterpartPath(string path,out bool loadedIsHeavy)
    {
        loadedIsHeavy=false;
        if(path.EndsWith("_1.nif",StringComparison.OrdinalIgnoreCase)){loadedIsHeavy=true;return path[..^6]+"_0.nif";}
        if(path.EndsWith("_0.nif",StringComparison.OrdinalIgnoreCase))return path[..^6]+"_1.nif";
        return null;
    }
    // Pairs each loaded shape with the same-named, same-vertex-count shape of the counterpart NIF.
    public void Register(IEnumerable<IRenderableShape> loaded,Nifcontent_Class_Manolo counterpart,bool loadedIsHeavy,List<string> diagnostics,string path)
    {
        var others=NifRenderableShape.FromNif(counterpart).GroupBy(s=>s.ShapeName,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        foreach(var shape in loaded){
            if(!others.TryGetValue(shape.ShapeName,out var other)){diagnostics.Add($"{path}: weight counterpart lacks shape {shape.ShapeName}; it stays at its loaded weight.");continue;}
            var geometry=SkinningHelper.ExtractSkinnedGeometry(other,false,false);
            var vertices=geometry.NifLocalVertices;
            if(vertices==null||vertices.Length!=shape.Geometry.VertexCount){diagnostics.Add($"{path}: weight counterpart shape {shape.ShapeName} has a different vertex count; it stays at its loaded weight.");continue;}
            pairs[shape]=new(null,vertices,loadedIsHeavy);
        }
    }
    // Pre-skin vertices at the current weight, for consumers outside the renderer such as HDT-SMP collision
    // meshes, which are built from shapes that are never drawn. Null when the shape has no weight pair.
    public Vector3d[]? Blended(IRenderableShape shape)
    {
        if(!pairs.TryGetValue(shape,out var pair))return null;
        var loaded=pair.Loaded??shape.Geometry.GetVertexPositions().Select(v=>new Vector3d(v.X,v.Y,v.Z)).ToArray();
        if(loaded.Length!=pair.Counterpart.Length)return null;
        var heavy=pair.LoadedIsHeavy?loaded:pair.Counterpart;var light=pair.LoadedIsHeavy?pair.Counterpart:loaded;
        double t=weight/100.0;var result=new Vector3d[loaded.Length];
        for(int i=0;i<result.Length;i++)result[i]=light[i]+(heavy[i]-light[i])*t;
        return result;
    }
    public bool TryProvideBaseGeometry(IRenderableShape shape,ref SkinnedGeometry geom)
    {
        if(!pairs.TryGetValue(shape,out var pair)||geom.NifLocalVertices==null)return false;
        // The first call sees the loaded mesh untouched; keep that as the pristine end of the blend.
        pair.Loaded??=geom.NifLocalVertices.ToArray();
        if(pair.Loaded.Length!=geom.NifLocalVertices.Length)return false;
        var heavy=pair.LoadedIsHeavy?pair.Loaded:pair.Counterpart;var light=pair.LoadedIsHeavy?pair.Counterpart:pair.Loaded;
        double t=weight/100.0;var target=geom.NifLocalVertices;
        for(int i=0;i<target.Length;i++)target[i]=light[i]+(heavy[i]-light[i])*t;
        return true;
    }
}
