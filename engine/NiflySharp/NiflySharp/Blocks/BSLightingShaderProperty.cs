using NiflySharp.Enums;
using NiflySharp.Stream;
using NiflySharp.Structs;
using System.Collections.Generic;
using System.Numerics;
using static NiflySharp.Helpers.ShaderHelper;

namespace NiflySharp.Blocks
{
    public partial class BSLightingShaderProperty : BSShaderProperty, INiShader
    {
        public bool HasTextureSet => !_textureSet?.IsEmpty() ?? false;
        public NiBlockRef<BSShaderTextureSet> TextureSetRef { get => _textureSet; set => _textureSet = value; }

        public TexClampMode TextureClampMode { get => _textureClampMode; set => _textureClampMode = value; }

        public BSShaderType155 ShaderType_FO76_SF { get => _shaderType_BSST155; set => _shaderType_BSST155 = value; }

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

        public string RootMaterialName
        {
            get => _rootMaterial?.String;
            set
            {
                if (_rootMaterial != null)
                    _rootMaterial.String = value;
            }
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
        /// Glow color (no alpha with BSLightingShaderProperty)
        /// </summary>
        public Color4 EmissiveColor
        {
            get => new(_emissiveColor.R, _emissiveColor.G, _emissiveColor.B, 0.0f);
            set => _emissiveColor = new Color3(value.R, value.G, value.B);
        }

        /// <summary>
        /// Multiplied emissive colors
        /// </summary>
        public float EmissiveMultiple { get => _emissiveMultiple; set => _emissiveMultiple = value; }

        /// <summary>
        /// The material opacity (1=opaque). Greater than 1.0 is used to affect alpha falloff i.e. staying opaque longer based on vertex alpha and alpha mask.
        /// </summary>
        public float Alpha { get => _alpha; set => _alpha = value; }

        /// <summary>
        /// The amount of distortion. **Not based on physically accurate refractive index** (0=none)
        /// </summary>
        public float RefractionStrength { get => _refractionStrength; set => _refractionStrength = value; }

        /// <summary>
        /// The material specular power, or glossiness (before FO4).
        /// </summary>
        public float Glossiness { get => _glossiness; set => _glossiness = value; }

        /// <summary>
        /// The base roughness, multiplied by the smoothness map (starting with FO4).
        /// </summary>
        public float Smoothness { get => _smoothness; set => _smoothness = value; }

        /// <summary>
        /// Adds a colored highlight.
        /// </summary>
        public Color3 SpecularColor
        {
            get => new(_specularColor.R, _specularColor.G, _specularColor.B);
            set => _specularColor = new Color3(value.R, value.G, value.B);
        }

        /// <summary>
        /// Brightness of specular highlight. (0=not visible)
        /// </summary>
        public float SpecularStrength { get => _specularStrength; set => _specularStrength = value; }

        /// <summary>
        /// Controls strength for various envmap/backlight/rim/softlight lighting effects (before FO4).
        /// </summary>
        public float Softlight { get => _lightingEffect1; set => _lightingEffect1 = value; }

        /// <summary>
        /// Subsurface Rolloff (starting with FO4).
        /// </summary>
        public float SubsurfaceRolloff { get => _subsurfaceRolloff; set => _subsurfaceRolloff = value; }

        /// <summary>
        /// Rimlight Power
        /// </summary>
        public float RimlightPower
        {
            get
            {
                return Type == ShaderGameType.FO4 ? _rimlightPower : _lightingEffect2;
            }
            set
            {
                if (Type == ShaderGameType.FO4)
                    _rimlightPower = value;
                else
                    _lightingEffect2 = value;
            }
        }

        /// <summary>
        /// Backlight Power (starting with FO4).
        /// </summary>
        public float BacklightPower { get => _backlightPower; set => _backlightPower = value; }

        /// <summary>
        /// Grayscale To Palette Scale (starting with FO4).
        /// </summary>
        public float GrayscaleToPaletteScale { get => _grayscaletoPaletteScale; set => _grayscaletoPaletteScale = value; }

        /// <summary>
        /// Fresnel Power (starting with FO4).
        /// </summary>
        public float FresnelPower { get => _fresnelPower; set => _fresnelPower = value; }

        /// <summary>
        /// Wetness properties (starting with FO4).
        /// </summary>
        public BSSPWetnessParams Wetness { get => _wetness; set => _wetness = value; }

        /// <summary>
        /// Luminance properties (starting with FO76).
        /// </summary>
        public BSSPLuminanceParams Luminance { get => _luminance; set => _luminance = value; }

        /// <summary>
        /// Do Translucency (starting with FO76).
        /// </summary>
        public bool DoTranslucency { get => _doTranslucency.GetValueOrDefault(); set => _doTranslucency = value; }

        /// <summary>
        /// Translucency properties (starting with FO76).
        /// </summary>
        public BSSPTranslucencyParams Translucency { get => _translucency; set => _translucency = value; }

        /// <summary>
        /// Scales the intensity of the environment/cube map.
        /// </summary>
        public new float EnvironmentMapScale { get => _environmentMapScale_fl; set => _environmentMapScale_fl = value; }

        /// <summary>
        /// Use Screen Space Reflections (starting with FO4).
        /// </summary>
        public bool UseScreenSpaceReflections { get => _useScreenSpaceReflections.GetValueOrDefault(); set => _useScreenSpaceReflections = value; }

        /// <summary>
        /// Wetness Control: Use SSR (starting with FO4).
        /// </summary>
        public bool WetnessControl_UseSSR { get => _wetnessControl_UseSSR.GetValueOrDefault(); set => _wetnessControl_UseSSR = value; }

        /// <summary>
        /// Tints the base texture. Overridden by game settings.
        /// This property is only used until FO4.
        /// </summary>
        public Color3 SkinTintColor { get => _skinTintColor_C3; set => _skinTintColor_C3 = value; }

        /// <summary>
        /// Skin Tint Alpha.
        /// This property is only used in FO4.
        /// </summary>
        public float SkinTintAlpha { get => _skinTintAlpha; set => _skinTintAlpha = value; }

        /// <summary>
        /// This property is only used starting FO76.
        /// </summary>
        public Color4 SkinTintColor_FO76 { get => _skinTintColor_C4; set => _skinTintColor_C4 = value; }

        /// <summary>
        /// Hair Tint Color
        /// </summary>
        public Color3 HairTintColor { get => _hairTintColor; set => _hairTintColor = value; }

        /// <summary>
        /// Max Passes for parallax occlusion.
        /// </summary>
        public float MaxPasses { get => _maxPasses; set => _maxPasses = value; }

        /// <summary>
        /// Scale for parallax occlusion.
        /// </summary>
        public float Scale { get => _scale; set => _scale = value; }

        /// <summary>
        /// How far from the surface the inner layer appears to be.
        /// </summary>
        public float ParallaxInnerLayerThickness { get => _parallaxInnerLayerThickness; set => _parallaxInnerLayerThickness = value; }

        /// <summary>
        /// Depth of inner parallax layer effect.
        /// </summary>
        public float ParallaxRefractionScale { get => _parallaxRefractionScale; set => _parallaxRefractionScale = value; }

        /// <summary>
        /// Scales the inner parallax layer texture.
        /// </summary>
        public TexCoord ParallaxInnerLayerTextureScale { get => _parallaxInnerLayerTextureScale; set => _parallaxInnerLayerTextureScale = value; }

        /// <summary>
        /// How strong the environment/cube map is.
        /// </summary>
        public float ParallaxEnvmapStrength { get => _parallaxEnvmapStrength; set => _parallaxEnvmapStrength = value; }

        /// <summary>
        /// CK lists "snow material" when used.
        /// </summary>
        public Vector4 SparkleParameters { get => _sparkleParameters; set => _sparkleParameters = value; }

        /// <summary>
        /// Eye cubemap scale
        /// </summary>
        public float EyeCubemapScale { get => _eyeCubemapScale; set => _eyeCubemapScale = value; }

        /// <summary>
        /// Offset to set center for left eye cubemap
        /// </summary>
        public Vector3 LeftEyeReflectionCenter { get => _leftEyeReflectionCenter; set => _leftEyeReflectionCenter = value; }

        /// <summary>
        /// Offset to set center for right eye cubemap
        /// </summary>
        public Vector3 RightEyeReflectionCenter { get => _rightEyeReflectionCenter; set => _rightEyeReflectionCenter = value; }

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

            _shaderType_BSST155 = (BSShaderType155)_shaderType;
        }
    }
}
