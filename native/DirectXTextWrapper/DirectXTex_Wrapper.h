// DirectXTexWrapper — C++/CLI wrapper over Microsoft DirectXTex.
// Copyright (C) 2025  ManoloV02  <https://github.com/MANOLOV02>
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program.
// If not, see <https://www.gnu.org/licenses/>.
//
// Wraps DirectXTex, (C) Microsoft Corporation, used under the MIT License.
// See README.md and LICENSE.

#pragma once

// DirectXTex_Wrapper.h - C++/CLI wrapper declarations

#include <cstdint>
#include "OpenGL_MapFormats.h"     // Mappings for GL formats
#include "DirectXTex.h"            // Core DirectXTex API

#ifdef _MANAGED

#include <vcclr.h>
#include <msclr/marshal_cppstd.h>
using namespace System;
using namespace System::Collections::Generic;
using namespace System::Runtime::InteropServices;

namespace DirectXTexWrapperCLI
{

    /// <summary>Managed representation of a single mipmap level.</summary>
    public ref class TextureLevel
    {
    public:
        array<Byte>^ Data;
        int Width;
        int Height;
        TextureLevel(array<Byte>^ data, int width, int height);
    };

    /// <summary>Managed representation of a loaded texture.</summary>
    public ref class TextureLoaded
    {
    public:
        List<TextureLevel^>^ Levels;
        bool IsCompressedGL;
        bool Loaded;
        bool IsCubemap;
        int DxgiCodeOriginal;
        int DxgiCodeFinal;
        int Faces;
        int Miplevels;
        unsigned int GlInternalFormat;
        unsigned int GlPixelFormat;
        unsigned int GlPixelType;
        TextureLoaded();
    };

    /// <summary>
    /// Managed representation of an explicit texture subresource.
    /// Used by ConvertSubresources to remove ambiguity around mipmaps and array faces.
    /// </summary>
    public ref class TextureSubresource
    {
    public:
        array<Byte>^ Data;
        int Width;
        int Height;
        int RowPitch;
        int SlicePitch;
        int MipLevel;
        int ArrayIndex;
        TextureSubresource();
        TextureSubresource(array<Byte>^ data, int width, int height, int rowPitch, int slicePitch, int mipLevel, int arrayIndex);
    };

    /// <summary>
    /// Explicit conversion request for subresource-based texture conversions.
    /// Supports Texture2D, Texture2DArray and Cubemap.
    /// Subresources must be ordered as mip-major and then array/face-major.
    /// If AutoGenerateMipMaps is true, only the base subresources are required and MipLevels can be 0 for the full chain.
    /// </summary>
    public ref class TextureConversionRequest
    {
    public:
        array<TextureSubresource^>^ Subresources;
        int Width;
        int Height;
        int InputDxgiFormat;
        int OutputDxgiFormat;
        int MipLevels;
        int ArraySize;
        bool IsCubemap;
        bool AutoGenerateMipMaps;
        int FilterFlags;
        int CompressFlags;
        float AlphaThreshold;
        TextureConversionRequest();
    };

    /// <summary>
    /// Managed conversion result returned by ConvertSubresources.
    /// Output data is returned tight-packed and in mip-major order.
    /// </summary>
    public ref class TextureConversionResult
    {
    public:
        array<TextureSubresource^>^ Subresources;
        int Width;
        int Height;
        int DxgiFormat;
        int MipLevels;
        int ArraySize;
        bool IsCubemap;
        TextureConversionResult();
    };

    public ref class DdsMetadata
    {
    public:
        int Width;
        int Height;
        int MipCount;
        int DxgiFormat;
        int Faces;
        bool IsCubemap;
        /// <summary>Cuantos elementos tiene el array de texturas (6 en un cubemap, 1 en una 2D suelta).
        /// <para>⛔ EXISTE PORQUE `Faces` NO ALCANZA: se calcula como `IsCubemap ? 6 : 1`, asi que para un
        /// Texture2DArray de N elementos vale 1 y una guarda del tipo "no es cubemap y Faces &lt;= 1" da
        /// verdadero — o sea, promete excluir arrays y no excluye ninguno. Este es el dato real de
        /// `TexMetadata::arraySize`.</para></summary>
        int ArraySize;
        int HeaderSize;
        bool Loaded;
        DdsMetadata();
    };

    /// <summary>Loader methods replacing P/Invoke.</summary>
    public ref class Loader
    {
    public:
        static List<TextureLoaded^>^ LoadTextures(array<array<Byte>^>^ ddsFiles, bool useCompress, bool forceOpenGL);
        /// <summary>Igual que LoadTextures, pero decodificando UN SOLO nivel de mip.
        /// <para><paramref name="onlyMipLevel"/> &lt; 0 = la cadena entera (idéntico a la sobrecarga de
        /// tres argumentos). Con un nivel concreto, la descompresión BCn se hace sólo sobre ese nivel:
        /// el consumidor que elige un mip y descarta los demás pagaba entre 25 % y 29 % de trabajo de
        /// más, medido con Tools/TexCodecPerfProbe.</para>
        /// <para>El resultado trae ese nivel en <c>Levels[0..Faces-1]</c> y <c>Miplevels = 1</c>.</para>
        /// <para>⛔ Si el nivel NO existe en un archivo del lote, ESE archivo vuelve con
        /// <c>Loaded = false</c> — no se tira, y los demás del lote se decodifican igual. Es la misma
        /// señal que usan todos los otros caminos de fallo de la función.</para></summary>
        static List<TextureLoaded^>^ LoadTextures(array<array<Byte>^>^ ddsFiles, bool useCompress, bool forceOpenGL, int onlyMipLevel);
        static TextureLoaded^ ConvertForBitmap(array<Byte>^ ddsFile);
        static TextureConversionResult^ ConvertSubresources(TextureConversionRequest^ request);
        /// <summary>Misma conversión que ConvertSubresources pero devuelve el DDS completo
        /// (header + payload) en UN solo array, sin materializar un array por subrecurso ni la
        /// concatenación intermedia. Payload en el orden del formato DDS: array-major, después mip.</summary>
        static array<System::Byte>^ ConvertSubresourcesToDds(TextureConversionRequest^ request);
        static array<System::Byte>^ EncodeDDSHeader(int dxgiFormat, int width, int height, int arraySize, int mipLevels, bool isCubemap);
        static DdsMetadata^ GetDdsMetadata(array<Byte>^ ddsBytes);   // ← agregar acá

    };

} // namespace DirectXTexWrapperCLI

#endif // _MANAGED
