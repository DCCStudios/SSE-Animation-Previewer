using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
namespace SSEPreview;

// Skin output is in NIF world space. The caller applies effect placement or the
// particle-system inverse once, after sampling the deformed surface.
internal static class ParticleSkin
{
    internal static void Bind(Nifcontent_Class_Manolo nif,INiShape shape,ParticleMesh surface,Func<object?,Matrix4x4> world)
    {
        var renderable=new NifRenderableShape(nif,shape,nif.Blocks.IndexOf((NiAVObject)shape));
        var geometry=renderable.Geometry;var skin=geometry.GetSkinning();
        var positions=geometry.GetVertexPositions();var normals=geometry.GetNormals();
        var references=skin.BoneRefIndices??[];
        if(references.Length==0||references.Any(i=>i<0||i>=nif.Blocks.Count||nif.Blocks[i] is not NiNode))
            throw new InvalidDataException("missing or invalid bone palette (indices are not compacted)");
        if(renderable.ShapeBones.Count!=references.Length||renderable.ShapeBoneTransforms.Count!=references.Length)
            throw new InvalidDataException("bone palette and bind-transform counts differ");
        if(skin.VertexCount!=positions.Count||skin.WeightsPerVertex<=0||skin.WeightsPerVertex>32||skin.BoneIndices==null||skin.BoneWeights==null||skin.BoneIndices.Length!=positions.Count*skin.WeightsPerVertex||skin.BoneWeights.Length!=skin.BoneIndices.Length)
            throw new InvalidDataException("missing or inconsistent vertex influence arrays");
        var weights=new float[skin.BoneWeights.Length];
        for(int vertex=0;vertex<positions.Count;vertex++){
            float total=0;int start=vertex*skin.WeightsPerVertex;
            for(int j=0;j<skin.WeightsPerVertex;j++){
                int slot=start+j;float weight=(float)skin.BoneWeights[slot];
                if(!float.IsFinite(weight)||weight<0||(weight>0&&skin.BoneIndices[slot]>=references.Length))
                    throw new InvalidDataException("invalid vertex weight or weighted bone index");
                total+=weight;weights[slot]=weight;
            }
            if(!float.IsFinite(total)||total<=0)throw new InvalidDataException("vertex has no positive bone weights");
            for(int j=0;j<skin.WeightsPerVertex;j++)weights[start+j]/=total;
        }
        var bind=renderable.ShapeBoneTransforms.Select(t=>ParticleNif.Matrix(t.ToMatrix4())).ToArray();
        var matrices=new Matrix4x4[references.Length];
        surface.SamplesWorldSpace=true;
        // Evaluate(time) has already updated this NIF's controller hierarchy. Rebuild
        // at each sample so seeks and other systems sharing the NIF cannot stale it.
        surface.Prepare=()=>{for(int i=0;i<matrices.Length;i++)matrices[i]=bind[i]*world(nif.Blocks[references[i]]);};
        surface.Deform=vertex=>{
            Matrix4x4 blended=default;int start=vertex*skin.WeightsPerVertex;
            for(int j=0;j<skin.WeightsPerVertex;j++){int slot=start+j;if(weights[slot]>0)blended+=matrices[skin.BoneIndices[slot]]*weights[slot];}
            var position=Vector3.Transform(positions[vertex],blended);
            var normal=normals.Count==positions.Count&&Matrix4x4.Invert(blended,out var inverse)?Vector3.TransformNormal(normals[vertex],Matrix4x4.Transpose(inverse)):Vector3.Zero;
            return(position,normal);
        };
    }
}
