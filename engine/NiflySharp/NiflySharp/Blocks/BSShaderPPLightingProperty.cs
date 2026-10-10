using NiflySharp.Structs;

namespace NiflySharp.Blocks
{
    public partial class BSShaderPPLightingProperty : BSShaderLightingProperty, INiShader
    {
        public bool HasTextureSet => !_textureSet?.IsEmpty() ?? false;
        public NiBlockRef<BSShaderTextureSet> TextureSetRef { get => _textureSet; set => _textureSet = value; }

        /// <summary>
        /// The amount of distortion. **Not based on physically accurate refractive index** (0=none) (0-1)
        /// </summary>
        public float RefractionStrength { get => _refractionStrength; set => _refractionStrength = value; }

        /// <summary>
        /// Rate of texture movement for refraction shader.
        /// </summary>
        public int RefractionFirePeriod { get => _refractionFirePeriod; set => _refractionFirePeriod = value; }

        /// <summary>
        /// The number of passes the parallax shader can apply.
        /// </summary>
        public float ParallaxMaxPasses { get => _parallaxMaxPasses; set => _parallaxMaxPasses = value; }

        /// <summary>
        /// The strength of the parallax.
        /// </summary>
        public float ParallaxScale { get => _parallaxScale; set => _parallaxScale = value; }

        /// <summary>
        /// Glow color and alpha
        /// </summary>
        public Color4 EmissiveColor { get => _emissiveColor; set => _emissiveColor = value; }
    }
}
