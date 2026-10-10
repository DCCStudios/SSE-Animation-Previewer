using NiflySharp.Enums;
using NiflySharp.Stream;
using NiflySharp.Structs;
using System.Collections.Generic;
using static NiflySharp.Helpers.ShaderHelper;

namespace NiflySharp.Blocks
{
    public partial class BSEffectShaderProperty : BSShaderProperty, INiShader
    {
        /// <summary>
        /// Used in Skyrim
        /// </summary>
        public SkyrimShaderPropertyFlags1 ShaderFlags_SSPF1
        {
            get => _shaderFlags1_SSPF1;
            set => _shaderFlags1_SSPF1 = value;
        }
        /// <summary>
        /// Used in Skyrim
        /// </summary>
        public SkyrimShaderPropertyFlags2 ShaderFlags_SSPF2
        {
            get => _shaderFlags2_SSPF2;
            set => _shaderFlags2_SSPF2 = value;
        }

        /// <summary>
        /// Used in Fallout 4
        /// </summary>
        public Fallout4ShaderPropertyFlags1 ShaderFlags_F4SPF1
        {
            get => _shaderFlags1_F4SPF1;
            set => _shaderFlags1_F4SPF1 = value;
        }
        /// <summary>
        /// Used in Fallout 4
        /// </summary>
        public Fallout4ShaderPropertyFlags2 ShaderFlags_F4SPF2
        {
            get => _shaderFlags2_F4SPF2;
            set => _shaderFlags2_F4SPF2 = value;
        }

        /// <summary>
        /// Used in Fallout 76 and later
        /// </summary>
        public List<BSShaderCRC32> ShaderFlagsList1
        {
            get => _sF1;
            set => _sF1 = value;
        }
        /// <summary>
        /// Used in Fallout 76 and later
        /// </summary>
        public List<BSShaderCRC32> ShaderFlagsList2
        {
            get => _sF2;
            set => _sF2 = value;
        }
        
        /// <summary>
        /// Offset UVs
        /// </summary>
        public TexCoord UVOffset { get => _uVOffset; set => _uVOffset = value; }

        /// <summary>
        /// Offset UV Scale to repeat tiling textures.
        /// </summary>
        public TexCoord UVScale { get => _uVScale; set => _uVScale = value; }

        /// <summary>
        /// Points to an external texture.
        /// </summary>
        public NiString4 SourceTexture { get => _sourceTexture; set => _sourceTexture = value; }

        /// <summary>
        /// How to handle texture borders.
        /// </summary>
        public byte TextureClampMode { get => _textureClampMode; set => _textureClampMode = value; }

        /// <summary>
        /// Lighting Influence
        /// </summary>
        public byte LightingInfluence { get => _lightingInfluence; set => _lightingInfluence = value; }

        /// <summary>
        /// Environment Map Min LOD
        /// </summary>
        public byte EnvMapMinLOD { get => _envMapMinLOD; set => _envMapMinLOD = value; }

        /// <summary>
        /// At this cosine of angle falloff will be equal to Falloff Start Opacity
        /// </summary>
        public float FalloffStartAngle { get => _falloffStartAngle; set => _falloffStartAngle = value; }

        /// <summary>
        /// At this cosine of angle falloff will be equal to Falloff Stop Opacity
        /// </summary>
        public float FalloffStopAngle { get => _falloffStopAngle; set => _falloffStopAngle = value; }

        /// <summary>
        /// Alpha falloff multiplier at start angle
        /// </summary>
        public float FalloffStartOpacity { get => _falloffStartOpacity; set => _falloffStartOpacity = value; }

        /// <summary>
        /// Alpha falloff multiplier at end angle
        /// </summary>
        public float FalloffStopOpacity { get => _falloffStopOpacity; set => _falloffStopOpacity = value; }

        /// <summary>
        /// Refraction Power (starting with FO76).
        /// </summary>
        public float RefractionPower { get => _refractionPower; set => _refractionPower = value; }

        /// <summary>
        /// Base color
        /// </summary>
        public Color4 BaseColor { get => _baseColor; set => _baseColor = value; }

        /// <summary>
        /// Multiplier for Base Color (RGB part)
        /// </summary>
        public float BaseColorScale { get => _baseColorScale; set => _baseColorScale = value; }

        /// <summary>
        /// Soft Falloff Depth
        /// </summary>
        public float SoftFalloffDepth { get => _softFalloffDepth; set => _softFalloffDepth = value; }

        /// <summary>
        /// Points to an external texture, used as palette for shader flag GreyscaleToPaletteColor/GreyscaleToPaletteAlpha.
        /// </summary>
        public NiString4 GreyscaleTexture { get => _greyscaleTexture; set => _greyscaleTexture = value; }

        /// <summary>
        /// Environment Map Texture (starting with FO4).
        /// </summary>
        public NiString4 EnvMapTexture { get => _envMapTexture; set => _envMapTexture = value; }

        /// <summary>
        /// Normal Texture (starting with FO4).
        /// </summary>
        public NiString4 NormalTexture { get => _normalTexture; set => _normalTexture = value; }

        /// <summary>
        /// Environment Mask Texture (starting with FO4).
        /// </summary>
        public NiString4 EnvMaskTexture { get => _envMaskTexture; set => _envMaskTexture = value; }

        /// <summary>
        /// Environment Map Scale (starting with FO4).
        /// </summary>
        public new float EnvironmentMapScale { get => _environmentMapScale_fl; set => _environmentMapScale_fl = value; }

        /// <summary>
        /// Reflectance Texture (starting with FO76).
        /// </summary>
        public NiString4 ReflectanceTexture { get => _reflectanceTexture; set => _reflectanceTexture = value; }

        /// <summary>
        /// Lighting Texture (starting with FO76).
        /// </summary>
        public NiString4 LightingTexture { get => _lightingTexture; set => _lightingTexture = value; }

        /// <summary>
        /// Emittance Color (starting with FO76).
        /// </summary>
        public Color3 EmittanceColor { get => _emittanceColor; set => _emittanceColor = value; }

        /// <summary>
        /// Emit Gradient Texture (starting with FO76).
        /// </summary>
        public NiString4 EmitGradientTexture { get => _emitGradientTexture; set => _emitGradientTexture = value; }

        /// <summary>
        /// Luminance properties (starting with FO76).
        /// </summary>
        public BSSPLuminanceParams Luminance { get => _luminance; set => _luminance = value; }

        public new void BeforeSync(NiStreamReversible stream)
        {
            _shaderFlags = 0;
            _shaderFlags2 = 0;

            if (stream.Version.StreamVersion < 130)
            {
                Type = ShaderGameType.SK;
                _shaderFlags1_F4SPF1 = 0;
                _shaderFlags2_F4SPF2 = 0;
            }

            if (stream.Version.StreamVersion >= 130)
            {
                Type = ShaderGameType.FO4;
                _shaderFlags1_SSPF1 = 0;
                _shaderFlags2_SSPF2 = 0;

                if (stream.Version.StreamVersion == 155)
                {
                    Type = ShaderGameType.FO76SF;
                    _shaderFlags1_F4SPF1 = 0;
                    _shaderFlags2_F4SPF2 = 0;
                }
            }
        }
    }
}
