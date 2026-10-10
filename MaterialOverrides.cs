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
}