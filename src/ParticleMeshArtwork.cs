using System.Numerics;
using FO4_Base_Library;
using NiflySharp;
using NiflySharp.Blocks;
using static SSEPreview.NifAnimation;
namespace SSEPreview;
internal sealed class ParticleMeshArtwork
{
    internal sealed record Vertex(Vector3 Position,Vector2 UV,Vector4 Color);
    internal sealed record Part(int Generation,int TemplateCount,Matrix4x4 RootRotation,Vertex[] Vertices,BSEffectShaderProperty Shader,NiAlphaProperty? Alpha,string Texture,string Palette,Func<double,Matrix4x4> LocalTransform,Func<double,bool> Visible);
    internal readonly List<Part> Parts=[];
    internal readonly bool HasRotation;
    internal static IEnumerable<NiAVObject> Descendants(FO4_Base_Library.Nifcontent_Class_Manolo nif,NiAVObject root)
    {
        var pending=new Stack<NiAVObject>();var seen=new HashSet<NiAVObject>();pending.Push(root);
        while(pending.TryPop(out var node)){if(!seen.Add(node))continue;yield return node;if(node is NiNode parent)foreach(var reference in parent.Children.References)if(nif.GetBlock(reference) is NiAVObject child)pending.Push(child);}
    }
    internal static HashSet<NiAVObject> TemplateObjects(Nifcontent_Class_Manolo nif)=>nif.Blocks.OfType<NiPSysMeshUpdateModifier>()
        .SelectMany(m=>m.Meshes?.References??[]).Select(r=>nif.GetBlock(r)).OfType<NiAVObject>().SelectMany(r=>Descendants(nif,r)).ToHashSet();
    internal ParticleMeshArtwork(ParticleNif system)
    {
        var nif=system.Nif;var animation=new NifAnimation(nif);
        if(system.Data is not NiMeshPSysData){system.Diagnostics.Add(system.Name+": mesh particle data block is missing or has the wrong type; geometry is suppressed");return;}
        HasRotation=Get(system.Data,"HasRotationAxes") is NiBool axes&&axes.Value&&Get(system.Data,"HasRotationAngles") is NiBool angles&&angles.Value;
        var modifier=system.Modifiers.OfType<NiPSysMeshUpdateModifier>().FirstOrDefault();
        if(modifier==null){system.Diagnostics.Add(system.Name+": mesh particles have no mesh-update template list; geometry is suppressed");return;}
        if(modifier.GetType()!=typeof(NiPSysMeshUpdateModifier)||!modifier.Active.Value){system.Diagnostics.Add(system.Name+": Havok or inactive mesh-update modifiers are not supported; geometry is suppressed");return;}
        IEnumerable<NiTimeController> Controllers(NiAVObject node){
            var seen=new HashSet<NiTimeController>();var controller=nif.GetBlock(node.Controller) as NiTimeController;
            while(controller!=null&&seen.Add(controller)){yield return controller;controller=nif.GetBlock(controller.NextController) as NiTimeController;}
        }
        var roots=(modifier.Meshes?.References??[]).Select(r=>nif.GetBlock(r) as NiAVObject).ToArray();
        for(int generation=0;generation<roots.Length;generation++){
            var root=roots[generation];if(root==null)continue;
            var objects=Descendants(nif,root).ToArray();
            if(objects.Any(o=>Controllers(o).Any(c=>!(c is NiVisController&&animation.Block(Get(c,"Interpolator")) is NiBoolInterpolator)&&!(c is NiTransformController&&!ReferenceEquals(o,root)&&animation.Block(Get(c,"Interpolator")) is NiTransformInterpolator)))){
                system.Diagnostics.Add(system.Name+": unsupported mesh-particle controller on template "+root.Name+" is not yet supported; this template is suppressed");continue;
            }
            var rootRotation=ParticleNif.Matrix(new Transform_Class{Rotation=root.Rotation,Scale=1}.ToMatrix4());
            foreach(var shape in objects.OfType<INiShape>()){
                if(shape is NiParticleSystem){system.Diagnostics.Add(system.Name+": nested particle templates are unsupported");continue;}
                if(shape.IsSkinned){system.Diagnostics.Add(system.Name+": skinned mesh-particle template is unsupported");continue;}
                if(nif.GetBlock(shape.ShaderPropertyRef) is not BSEffectShaderProperty shader){system.Diagnostics.Add(system.Name+": mesh-particle template requires an effect shader; other materials remain unsupported");continue;}
                if(nif.GetBlock(shader.Controller)!=null){system.Diagnostics.Add(system.Name+": animated mesh-particle shader is unsupported; this part is suppressed");continue;}
                var renderable=new NifRenderableShape(nif,shape,nif.Blocks.IndexOf((NiAVObject)shape));var geometry=renderable.Geometry;
                var positions=geometry.GetVertexPositions();var uvs=geometry.GetUVs();var colors=geometry.GetVertexColors();
                var chain=new List<(NiAVObject Authored,NiTransformController[] Controllers)>();NiAVObject? node=(NiAVObject)shape;var seen=new HashSet<NiAVObject>();
                while(node!=null&&!ReferenceEquals(node,root)&&seen.Add(node)){
                    chain.Add((new NiNode{Rotation=node.Rotation,Translation=node.Translation,Scale=node.Scale},Controllers(node).OfType<NiTransformController>().ToArray()));node=nif.GetParentNode(node) as NiAVObject;
                }
                var visibility=new List<(uint Flags,NiVisController[] Controllers)>();node=(NiAVObject)shape;seen.Clear();
                while(node!=null&&seen.Add(node)){
                    visibility.Add((node.Flags_ui,Controllers(node).OfType<NiVisController>().ToArray()));
                    if(ReferenceEquals(node,root))break;node=nif.GetParentNode(node) as NiAVObject;
                }
                List<Vertex> vertices=[];
                foreach(var triangle in geometry.GetTriangles()){
                    int[] indices=[triangle.V1,triangle.V2,triangle.V3];if(indices.Any(i=>i>=positions.Count))continue;
                    foreach(int i in indices)vertices.Add(new(positions[i],i<uvs.Count?new(uvs[i].U,uvs[i].V):Vector2.Zero,i<colors.Count&&((uint)shader.ShaderFlags_SSPF2&32)!=0?new(colors[i].R,colors[i].G,colors[i].B,colors[i].A):Vector4.One));
                }
                Parts.Add(new(generation,roots.Length,rootRotation,vertices.ToArray(),shader,nif.GetBlock(shape.AlphaPropertyRef) as NiAlphaProperty,Assets.GamePath(shader.SourceTexture.Content),Assets.GamePath(shader.GreyscaleTexture.Content),age=>{var local=Matrix4x4.Identity;foreach(var item in chain)local*=animation.SampleTemplateTransform(item.Authored,item.Controllers,age);return local;},age=>visibility.All(item=>animation.SampleTemplateVisibility(item.Flags,item.Controllers,age))));
            }
        }
        system.Diagnostics.Add(system.Name+": effect-shader mesh particles render by generation with descendant transforms and template visibility at particle age; root transform/shader animation, skinned/lit templates, native pooling, Havok and controller inheritance remain unfinished");
    }
    internal static IEnumerable<(Vector3 Position,Vector2 UV,Vector4 Color)> Vertices(Part part,ParticleSimulation.Particle particle,bool hasRotation,Matrix4x4 systemTransform)
    {
        if(!part.Visible(particle.Age))yield break;
        var placement=part.LocalTransform(particle.Age)*ParticleMeshTransform.At(particle,hasRotation,part.RootRotation)*systemTransform;
        // Mesh update does not overwrite template vertex colors with billboard tint.
        foreach(var vertex in part.Vertices)yield return(Vector3.Transform(vertex.Position,placement),vertex.UV,vertex.Color);
    }
}
