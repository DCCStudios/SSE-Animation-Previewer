using NiflySharp.Enums;
using NiflySharp.Stream;
using NiflySharp.Structs;
using System.Collections.Generic;
using static NiflySharp.Helpers.ShaderHelper;

namespace NiflySharp.Blocks
{
    public partial class BSSkyShaderProperty : BSShaderProperty, INiShader
    {
        /// <summary>
        /// Used in Skyrim
        /// </summary>
        public SkyrimShaderPropertyFlags1 ShaderFlags_SSPF1
        {
            get => _shaderFlags1;
            set => _shaderFlags1 = value;
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

        public SkyObjectType SkyObjectType { get => _skyObjectType; set => _skyObjectType = value; }

        public new void BeforeSync(NiStreamReversible stream)
        {
            _shaderFlags = 0;
            _shaderFlags2 = 0;

            if (stream.Version.StreamVersion < 130)
            {
                Type = ShaderGameType.SK;
            }

            if (stream.Version.StreamVersion >= 130)
            {
                Type = ShaderGameType.FO4;
                _shaderFlags1 = 0;
                _shaderFlags2_SSPF2 = 0;

                if (stream.Version.StreamVersion == 155)
                {
                    Type = ShaderGameType.FO76SF;
                }
            }
        }
    }
}
