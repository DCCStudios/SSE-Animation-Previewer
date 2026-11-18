using FO4_Base_Library;
using NiflySharp.Blocks;

namespace SSEPreview;

public static class MaterialOverrides
{
    public static int Apply(Nifcontent_Class_Manolo nif,string[]? allTextures=null,string[]? skinTextures=null)
    {
        int changed=0;
        foreach(var shader in nif.Blocks.OfType<BSLightingShaderProperty>().ToArray()){
            var textures=allTextures??(shader.IsTypeSkinTint?skinTextures:null);
            if(textures==null)continue;
            // Detach the texture set: an untouched armor shader may share the original block.
            var replacement=new BSShaderTextureSet {
                Textures=textures.Select(t=>new NiflySharp.NiString4(t)).ToList()
            };
            shader.TextureSetRef=new(nif.AddBlock(replacement));
            changed++;
        }
        return changed;
    }
    /// <summary>Rewrites the eight texture slots of every face shader (BSLightingShaderType 4, the FaceGen head) in
    /// the NIF: slot 3 is the detail map and slot 6 the face tint. The edit receives a copy and returns the new slots.</summary>
    public static int EditFace(Nifcontent_Class_Manolo nif,Func<string[],string[]> edit)
    {
        int changed=0;
        foreach(var shader in nif.Blocks.OfType<BSLightingShaderProperty>().ToArray()){
            if(!shader.IsTypeFaceTint)continue;
            var current=(nif.GetBlock(shader.TextureSetRef) as BSShaderTextureSet)?.Textures.Select(t=>t.Content??"").ToArray()??[];
            var slots=new string[8];for(int i=0;i<8;i++)slots[i]=i<current.Length?current[i]:"";
            shader.TextureSetRef=new(nif.AddBlock(new BSShaderTextureSet{Textures=edit(slots).Select(t=>new NiflySharp.NiString4(t)).ToList()}));
            changed++;
        }
        return changed;
    }
}