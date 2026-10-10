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

#include <unordered_map>
#include "OpenGL_MapFormats.h"
#include "DirectXTex.h"

using GLu = std::uint32_t;

struct DxgiToGlFormat {
    const char* DxgiName;
    bool IsCompressedGL;
    GLu GlInternalFormat;
    const char* GlInternalName;
    GLu GlPixelFormat;
    GLu GlPixelType;
};

static constexpr GLu GL_ZERO = 0u;
static constexpr GLu GL_ONE = 1u;

static constexpr GLu GL_DEPTH_COMPONENT = 0x1902u;
static constexpr GLu GL_RED = 0x1903u;
static constexpr GLu GL_RGB = 0x1907u;
static constexpr GLu GL_RGBA = 0x1908u;
static constexpr GLu GL_BGRA = 0x80E1u;
static constexpr GLu GL_RG = 0x8227u;
static constexpr GLu GL_DEPTH_STENCIL = 0x84F9u;

static constexpr GLu GL_BYTE = 0x1400u;
static constexpr GLu GL_UNSIGNED_BYTE = 0x1401u;
static constexpr GLu GL_SHORT = 0x1402u;
static constexpr GLu GL_UNSIGNED_SHORT = 0x1403u;
static constexpr GLu GL_INT = 0x1404u;
static constexpr GLu GL_UNSIGNED_INT = 0x1405u;
static constexpr GLu GL_FLOAT = 0x1406u;
static constexpr GLu GL_HALF_FLOAT = 0x140Bu;

static constexpr GLu GL_UNSIGNED_SHORT_5_6_5 = 0x8363u;
static constexpr GLu GL_UNSIGNED_SHORT_5_6_5_REV = 0x8364u;
static constexpr GLu GL_UNSIGNED_SHORT_4_4_4_4 = 0x8033u;
static constexpr GLu GL_UNSIGNED_SHORT_4_4_4_4_REV = 0x8365u;
static constexpr GLu GL_UNSIGNED_SHORT_5_5_5_1 = 0x8034u;
static constexpr GLu GL_UNSIGNED_SHORT_1_5_5_5_REV = 0x8366u;
static constexpr GLu GL_UNSIGNED_INT_10_10_10_2 = 0x8036u;
static constexpr GLu GL_UNSIGNED_INT_2_10_10_10_REV = 0x8368u;
static constexpr GLu GL_UNSIGNED_INT_10F_11F_11F_REV = 0x8C3Bu;
static constexpr GLu GL_UNSIGNED_INT_5_9_9_9_REV = 0x8C3Eu;
static constexpr GLu GL_UNSIGNED_INT_24_8 = 0x84FAu;
static constexpr GLu GL_FLOAT_32_UNSIGNED_INT_24_8_REV = 0x8DADu;

static constexpr GLu GL_RED_INTEGER = 0x8D94u;
static constexpr GLu GL_RG_INTEGER = 0x8228u;
static constexpr GLu GL_RGB_INTEGER = 0x8D98u;
static constexpr GLu GL_RGBA_INTEGER = 0x8D99u;

static constexpr GLu GL_R8 = 0x8229u;
static constexpr GLu GL_R16 = 0x822Au;
static constexpr GLu GL_R16F = 0x822Du;
static constexpr GLu GL_R32F = 0x822Eu;
static constexpr GLu GL_R8I = 0x8231u;
static constexpr GLu GL_R8UI = 0x8232u;
static constexpr GLu GL_R16I = 0x8233u;
static constexpr GLu GL_R16UI = 0x8234u;
static constexpr GLu GL_R32I = 0x8235u;
static constexpr GLu GL_R32UI = 0x8236u;
static constexpr GLu GL_R8_SNORM = 0x8F94u;
static constexpr GLu GL_R16_SNORM = 0x8F98u;

static constexpr GLu GL_RG8 = 0x822Bu;
static constexpr GLu GL_RG16 = 0x822Cu;
static constexpr GLu GL_RG16F = 0x822Fu;
static constexpr GLu GL_RG32F = 0x8230u;
static constexpr GLu GL_RG8I = 0x8237u;
static constexpr GLu GL_RG8UI = 0x8238u;
static constexpr GLu GL_RG16I = 0x8239u;
static constexpr GLu GL_RG16UI = 0x823Au;
static constexpr GLu GL_RG32I = 0x823Bu;
static constexpr GLu GL_RG32UI = 0x823Cu;
static constexpr GLu GL_RG8_SNORM = 0x8F95u;
static constexpr GLu GL_RG16_SNORM = 0x8F99u;

static constexpr GLu GL_RGB32F = 0x8815u;
static constexpr GLu GL_RGB32UI = 0x8D71u;
static constexpr GLu GL_RGB32I = 0x8D83u;
static constexpr GLu GL_RGB565 = 0x8D62u;
static constexpr GLu GL_R11F_G11F_B10F = 0x8C3Au;
static constexpr GLu GL_RGB9_E5 = 0x8C3Du;

static constexpr GLu GL_RGBA4 = 0x8056u;
static constexpr GLu GL_RGB5_A1 = 0x8057u;
static constexpr GLu GL_RGBA8 = 0x8058u;
static constexpr GLu GL_RGB10_A2 = 0x8059u;
static constexpr GLu GL_RGBA16 = 0x805Bu;
static constexpr GLu GL_RGBA32F = 0x8814u;
static constexpr GLu GL_RGBA16F = 0x881Au;
static constexpr GLu GL_RGBA8UI = 0x8D7Cu;
static constexpr GLu GL_RGBA16UI = 0x8D76u;
static constexpr GLu GL_RGBA32UI = 0x8D70u;
static constexpr GLu GL_RGBA8I = 0x8D8Eu;
static constexpr GLu GL_RGBA16I = 0x8D88u;
static constexpr GLu GL_RGBA32I = 0x8D82u;
static constexpr GLu GL_RGBA8_SNORM = 0x8F97u;
static constexpr GLu GL_RGBA16_SNORM = 0x8F9Bu;
static constexpr GLu GL_RGB10_A2UI = 0x906Fu;
static constexpr GLu GL_SRGB8_ALPHA8 = 0x8C43u;

static constexpr GLu GL_DEPTH_COMPONENT16 = 0x81A5u;
static constexpr GLu GL_DEPTH_COMPONENT24 = 0x81A6u;
static constexpr GLu GL_DEPTH_COMPONENT32F = 0x8CACu;
static constexpr GLu GL_DEPTH24_STENCIL8 = 0x88F0u;
static constexpr GLu GL_DEPTH32F_STENCIL8 = 0x8CADu;

static constexpr GLu GL_COMPRESSED_RGB_S3TC_DXT1_EXT = 0x83F0u;
static constexpr GLu GL_COMPRESSED_RGBA_S3TC_DXT1_EXT = 0x83F1u;
static constexpr GLu GL_COMPRESSED_RGBA_S3TC_DXT3_EXT = 0x83F2u;
static constexpr GLu GL_COMPRESSED_RGBA_S3TC_DXT5_EXT = 0x83F3u;

static constexpr GLu GL_COMPRESSED_SRGB_S3TC_DXT1_EXT = 0x8C4Cu;
static constexpr GLu GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT = 0x8C4Du;
static constexpr GLu GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT3_EXT = 0x8C4Eu;
static constexpr GLu GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT = 0x8C4Fu;

static constexpr GLu GL_COMPRESSED_RED_RGTC1 = 0x8DBBu;
static constexpr GLu GL_COMPRESSED_SIGNED_RED_RGTC1 = 0x8DBCu;
static constexpr GLu GL_COMPRESSED_RG_RGTC2 = 0x8DBDu;
static constexpr GLu GL_COMPRESSED_SIGNED_RG_RGTC2 = 0x8DBEu;

static constexpr GLu GL_COMPRESSED_RGBA_BPTC_UNORM = 0x8E8Cu;
static constexpr GLu GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM = 0x8E8Du;
static constexpr GLu GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT = 0x8E8Eu;
static constexpr GLu GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT = 0x8E8Fu;

static constexpr GLu GL_TEXTURE_SWIZZLE_R = 0x8E42u;
static constexpr GLu GL_TEXTURE_SWIZZLE_G = 0x8E43u;
static constexpr GLu GL_TEXTURE_SWIZZLE_B = 0x8E44u;
static constexpr GLu GL_TEXTURE_SWIZZLE_A = 0x8E45u;


static const std::unordered_map<int, FormatMapping> dxgiFormatMap = {
 {0,   {"DXGI_FORMAT_UNKNOWN", false, 0, "Incompatible", 0, 0}},
    {1,   {"DXGI_FORMAT_R32G32B32A32_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {2,   {"DXGI_FORMAT_R32G32B32A32_FLOAT", false, GL_RGBA32F, "GL_RGBA32F", GL_RGBA, GL_FLOAT}},
    {3,   {"DXGI_FORMAT_R32G32B32A32_UINT", false, GL_RGBA32UI, "GL_RGBA32UI", GL_RGBA_INTEGER, GL_UNSIGNED_INT}},
    {4,   {"DXGI_FORMAT_R32G32B32A32_SINT", false, GL_RGBA32I, "GL_RGBA32I", GL_RGBA_INTEGER, GL_INT}},
    {5,   {"DXGI_FORMAT_R32G32B32_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {6,   {"DXGI_FORMAT_R32G32B32_FLOAT", false, GL_RGB32F, "GL_RGB32F", GL_RGB, GL_FLOAT}},
    {7,   {"DXGI_FORMAT_R32G32B32_UINT", false, GL_RGB32UI, "GL_RGB32UI", GL_RGB_INTEGER, GL_UNSIGNED_INT}},
    {8,   {"DXGI_FORMAT_R32G32B32_SINT", false, GL_RGB32I, "GL_RGB32I", GL_RGB_INTEGER, GL_INT}},
    {9,   {"DXGI_FORMAT_R16G16B16A16_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {10,  {"DXGI_FORMAT_R16G16B16A16_FLOAT", false, GL_RGBA16F, "GL_RGBA16F", GL_RGBA, GL_HALF_FLOAT}},
    {11,  {"DXGI_FORMAT_R16G16B16A16_UNORM", false, GL_RGBA16, "GL_RGBA16", GL_RGBA, GL_UNSIGNED_SHORT}},
    {12,  {"DXGI_FORMAT_R16G16B16A16_UINT", false, GL_RGBA16UI, "GL_RGBA16UI", GL_RGBA_INTEGER, GL_UNSIGNED_SHORT}},
    {13,  {"DXGI_FORMAT_R16G16B16A16_SNORM", false, GL_RGBA16_SNORM, "GL_RGBA16_SNORM", GL_RGBA, GL_SHORT}},
    {14,  {"DXGI_FORMAT_R16G16B16A16_SINT", false, GL_RGBA16I, "GL_RGBA16I", GL_RGBA_INTEGER, GL_SHORT}},
    {15,  {"DXGI_FORMAT_R32G32_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {16,  {"DXGI_FORMAT_R32G32_FLOAT", false, GL_RG32F, "GL_RG32F", GL_RG, GL_FLOAT}},
    {17,  {"DXGI_FORMAT_R32G32_UINT", false, GL_RG32UI, "GL_RG32UI", GL_RG_INTEGER, GL_UNSIGNED_INT}},
    {18,  {"DXGI_FORMAT_R32G32_SINT", false, GL_RG32I, "GL_RG32I", GL_RG_INTEGER, GL_INT}},
    {19,  {"DXGI_FORMAT_R32G8X24_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {20,  {"DXGI_FORMAT_D32_FLOAT_S8X24_UINT", false, GL_DEPTH32F_STENCIL8, "GL_DEPTH32F_STENCIL8", GL_DEPTH_STENCIL, GL_FLOAT_32_UNSIGNED_INT_24_8_REV}},
    {21,  {"DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {22,  {"DXGI_FORMAT_X32_TYPELESS_G8X24_UINT", false, 0, "Incompatible", 0, 0}},
    {23,  {"DXGI_FORMAT_R10G10B10A2_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {24,  {"DXGI_FORMAT_R10G10B10A2_UNORM", false, GL_RGB10_A2, "GL_RGB10_A2", GL_RGBA, GL_UNSIGNED_INT_2_10_10_10_REV}},
    {25,  {"DXGI_FORMAT_R10G10B10A2_UINT", false, GL_RGB10_A2UI, "GL_RGB10_A2UI", GL_RGBA_INTEGER, GL_UNSIGNED_INT_2_10_10_10_REV}},
    {26,  {"DXGI_FORMAT_R11G11B10_FLOAT", false, GL_R11F_G11F_B10F, "GL_R11F_G11F_B10F", GL_RGB, GL_UNSIGNED_INT_10F_11F_11F_REV}},
    {27,  {"DXGI_FORMAT_R8G8B8A8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {28,  {"DXGI_FORMAT_R8G8B8A8_UNORM", false, GL_RGBA8, "GL_RGBA8", GL_RGBA, GL_UNSIGNED_BYTE}},
    {29,  {"DXGI_FORMAT_R8G8B8A8_UNORM_SRGB", false, GL_SRGB8_ALPHA8, "GL_SRGB8_ALPHA8", GL_RGBA, GL_UNSIGNED_BYTE}},
    {30,  {"DXGI_FORMAT_R8G8B8A8_UINT", false, GL_RGBA8UI, "GL_RGBA8UI", GL_RGBA_INTEGER, GL_UNSIGNED_BYTE}},
    {31,  {"DXGI_FORMAT_R8G8B8A8_SNORM", false, GL_RGBA8_SNORM, "GL_RGBA8_SNORM", GL_RGBA, GL_BYTE}},
    {32,  {"DXGI_FORMAT_R8G8B8A8_SINT", false, GL_RGBA8I, "GL_RGBA8I", GL_RGBA_INTEGER, GL_BYTE}},
    {33,  {"DXGI_FORMAT_R16G16_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {34,  {"DXGI_FORMAT_R16G16_FLOAT", false, GL_RG16F, "GL_RG16F", GL_RG, GL_HALF_FLOAT}},
    {35,  {"DXGI_FORMAT_R16G16_UNORM", false, GL_RG16, "GL_RG16", GL_RG, GL_UNSIGNED_SHORT}},
    {36,  {"DXGI_FORMAT_R16G16_UINT", false, GL_RG16UI, "GL_RG16UI", GL_RG_INTEGER, GL_UNSIGNED_SHORT}},
    {37,  {"DXGI_FORMAT_R16G16_SNORM", false, GL_RG16_SNORM, "GL_RG16_SNORM", GL_RG, GL_SHORT}},
    {38,  {"DXGI_FORMAT_R16G16_SINT", false, GL_RG16I, "GL_RG16I", GL_RG_INTEGER, GL_SHORT}},
    {39,  {"DXGI_FORMAT_R32_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {40,  {"DXGI_FORMAT_D32_FLOAT", false, GL_DEPTH_COMPONENT32F, "GL_DEPTH_COMPONENT32F", GL_DEPTH_COMPONENT, GL_FLOAT}},
    {41,  {"DXGI_FORMAT_R32_FLOAT", false, GL_R32F, "GL_R32F", GL_RED, GL_FLOAT}},
    {42,  {"DXGI_FORMAT_R32_UINT", false, GL_R32UI, "GL_R32UI", GL_RED_INTEGER, GL_UNSIGNED_INT}},
    {43,  {"DXGI_FORMAT_R32_SINT", false, GL_R32I, "GL_R32I", GL_RED_INTEGER, GL_INT}},
    {44,  {"DXGI_FORMAT_R24G8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {45,  {"DXGI_FORMAT_D24_UNORM_S8_UINT", false, GL_DEPTH24_STENCIL8, "GL_DEPTH24_STENCIL8", GL_DEPTH_STENCIL, GL_UNSIGNED_INT_24_8}},
    {46,  {"DXGI_FORMAT_R24_UNORM_X8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {47,  {"DXGI_FORMAT_X24_TYPELESS_G8_UINT", false, 0, "Incompatible", 0, 0}},
    {48,  {"DXGI_FORMAT_R8G8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {49,  {"DXGI_FORMAT_R8G8_UNORM", false, GL_RG8, "GL_RG8", GL_RG, GL_UNSIGNED_BYTE}},
    {50,  {"DXGI_FORMAT_R8G8_UINT", false, GL_RG8UI, "GL_RG8UI", GL_RG_INTEGER, GL_UNSIGNED_BYTE}},
    {51,  {"DXGI_FORMAT_R8G8_SNORM", false, GL_RG8_SNORM, "GL_RG8_SNORM", GL_RG, GL_BYTE}},
    {52,  {"DXGI_FORMAT_R8G8_SINT", false, GL_RG8I, "GL_RG8I", GL_RG_INTEGER, GL_BYTE}},
    {53,  {"DXGI_FORMAT_R16_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {54,  {"DXGI_FORMAT_R16_FLOAT", false, GL_R16F, "GL_R16F", GL_RED, GL_HALF_FLOAT}},
    {55,  {"DXGI_FORMAT_D16_UNORM", false, GL_DEPTH_COMPONENT16, "GL_DEPTH_COMPONENT16", GL_DEPTH_COMPONENT, GL_UNSIGNED_SHORT}},
    {56,  {"DXGI_FORMAT_R16_UNORM", false, GL_R16, "GL_R16", GL_RED, GL_UNSIGNED_SHORT}},
    {57,  {"DXGI_FORMAT_R16_UINT", false, GL_R16UI, "GL_R16UI", GL_RED_INTEGER, GL_UNSIGNED_SHORT}},
    {58,  {"DXGI_FORMAT_R16_SNORM", false, GL_R16_SNORM, "GL_R16_SNORM", GL_RED, GL_SHORT}},
    {59,  {"DXGI_FORMAT_R16_SINT", false, GL_R16I, "GL_R16I", GL_RED_INTEGER, GL_SHORT}},
    {60,  {"DXGI_FORMAT_R8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {61,  {"DXGI_FORMAT_R8_UNORM", false, GL_R8, "GL_R8", GL_RED, GL_UNSIGNED_BYTE}},
    {62,  {"DXGI_FORMAT_R8_UINT", false, GL_R8UI, "GL_R8UI", GL_RED_INTEGER, GL_UNSIGNED_BYTE}},
    {63,  {"DXGI_FORMAT_R8_SNORM", false, GL_R8_SNORM, "GL_R8_SNORM", GL_RED, GL_BYTE}},
    {64,  {"DXGI_FORMAT_R8_SINT", false, GL_R8I, "GL_R8I", GL_RED_INTEGER, GL_BYTE}},
    {65,  {"DXGI_FORMAT_A8_UNORM", false, GL_R8, "GL_R8", GL_RED, GL_UNSIGNED_BYTE}}, // requiere swizzle A=R, RGB=0
    {66,  {"DXGI_FORMAT_R1_UNORM", false, 0, "Incompatible", 0, 0}},
    {67,  {"DXGI_FORMAT_R9G9B9E5_SHAREDEXP", false, GL_RGB9_E5, "GL_RGB9_E5", GL_RGB, GL_UNSIGNED_INT_5_9_9_9_REV}},
    {68,  {"DXGI_FORMAT_R8G8_B8G8_UNORM", false, 0, "Incompatible", 0, 0}},
    {69,  {"DXGI_FORMAT_G8R8_G8B8_UNORM", false, 0, "Incompatible", 0, 0}},
    {70,  {"DXGI_FORMAT_BC1_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {71,  {"DXGI_FORMAT_BC1_UNORM", true, GL_COMPRESSED_RGBA_S3TC_DXT1_EXT, "GL_COMPRESSED_RGBA_S3TC_DXT1_EXT", 0, 0}},
    {72,  {"DXGI_FORMAT_BC1_UNORM_SRGB", true, GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT, "GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT1_EXT", 0, 0}},
    {73,  {"DXGI_FORMAT_BC2_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {74,  {"DXGI_FORMAT_BC2_UNORM", true, GL_COMPRESSED_RGBA_S3TC_DXT3_EXT, "GL_COMPRESSED_RGBA_S3TC_DXT3_EXT", 0, 0}},
    {75,  {"DXGI_FORMAT_BC2_UNORM_SRGB", true, GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT3_EXT, "GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT3_EXT", 0, 0}},
    {76,  {"DXGI_FORMAT_BC3_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {77,  {"DXGI_FORMAT_BC3_UNORM", true, GL_COMPRESSED_RGBA_S3TC_DXT5_EXT, "GL_COMPRESSED_RGBA_S3TC_DXT5_EXT", 0, 0}},
    {78,  {"DXGI_FORMAT_BC3_UNORM_SRGB", true, GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT, "GL_COMPRESSED_SRGB_ALPHA_S3TC_DXT5_EXT", 0, 0}},
    {79,  {"DXGI_FORMAT_BC4_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {80,  {"DXGI_FORMAT_BC4_UNORM", true, GL_COMPRESSED_RED_RGTC1, "GL_COMPRESSED_RED_RGTC1", 0, 0}},
    {81,  {"DXGI_FORMAT_BC4_SNORM", true, GL_COMPRESSED_SIGNED_RED_RGTC1, "GL_COMPRESSED_SIGNED_RED_RGTC1", 0, 0}},
    {82,  {"DXGI_FORMAT_BC5_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {83,  {"DXGI_FORMAT_BC5_UNORM", true, GL_COMPRESSED_RG_RGTC2, "GL_COMPRESSED_RG_RGTC2", 0, 0}},
    {84,  {"DXGI_FORMAT_BC5_SNORM", true, GL_COMPRESSED_SIGNED_RG_RGTC2, "GL_COMPRESSED_SIGNED_RG_RGTC2", 0, 0}},
    {85,  {"DXGI_FORMAT_B5G6R5_UNORM", false, GL_RGB565, "GL_RGB565", GL_RGB, GL_UNSIGNED_SHORT_5_6_5_REV}},
    {86,  {"DXGI_FORMAT_B5G5R5A1_UNORM", false, GL_RGB5_A1, "GL_RGB5_A1", GL_BGRA, GL_UNSIGNED_SHORT_1_5_5_5_REV}},
    {87,  {"DXGI_FORMAT_B8G8R8A8_UNORM", false, GL_RGBA8, "GL_RGBA8", GL_BGRA, GL_UNSIGNED_BYTE}},
    {88,  {"DXGI_FORMAT_B8G8R8X8_UNORM", false, GL_RGBA8, "GL_RGBA8", GL_BGRA, GL_UNSIGNED_BYTE}}, // requiere swizzle A=1
    {89,  {"DXGI_FORMAT_R10G10B10_XR_BIAS_A2_UNORM", false, 0, "Incompatible", 0, 0}},
    {90,  {"DXGI_FORMAT_B8G8R8A8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {91,  {"DXGI_FORMAT_B8G8R8A8_UNORM_SRGB", false, GL_SRGB8_ALPHA8, "GL_SRGB8_ALPHA8", GL_BGRA, GL_UNSIGNED_BYTE}},
    {92,  {"DXGI_FORMAT_B8G8R8X8_TYPELESS", false, 0, "Incompatible", 0, 0}},
    {93,  {"DXGI_FORMAT_B8G8R8X8_UNORM_SRGB", false, GL_SRGB8_ALPHA8, "GL_SRGB8_ALPHA8", GL_BGRA, GL_UNSIGNED_BYTE}}, // requiere swizzle A=1
    {94,  {"DXGI_FORMAT_BC6H_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {95,  {"DXGI_FORMAT_BC6H_UF16", true, GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT, "GL_COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT", 0, 0}},
    {96,  {"DXGI_FORMAT_BC6H_SF16", true, GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT, "GL_COMPRESSED_RGB_BPTC_SIGNED_FLOAT", 0, 0}},
    {97,  {"DXGI_FORMAT_BC7_TYPELESS", true, 0, "Incompatible", 0, 0}},
    {98,  {"DXGI_FORMAT_BC7_UNORM", true, GL_COMPRESSED_RGBA_BPTC_UNORM, "GL_COMPRESSED_RGBA_BPTC_UNORM", 0, 0}},
    {99,  {"DXGI_FORMAT_BC7_UNORM_SRGB", true, GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM, "GL_COMPRESSED_SRGB_ALPHA_BPTC_UNORM", 0, 0}},
    {100, {"DXGI_FORMAT_AYUV", false, 0, "Incompatible", 0, 0}},
    {101, {"DXGI_FORMAT_Y410", false, 0, "Incompatible", 0, 0}},
    {102, {"DXGI_FORMAT_Y416", false, 0, "Incompatible", 0, 0}},
    {103, {"DXGI_FORMAT_NV12", false, 0, "Incompatible", 0, 0}},
    {104, {"DXGI_FORMAT_P010", false, 0, "Incompatible", 0, 0}},
    {105, {"DXGI_FORMAT_P016", false, 0, "Incompatible", 0, 0}},
    {106, {"DXGI_FORMAT_420_OPAQUE", false, 0, "Incompatible", 0, 0}},
    {107, {"DXGI_FORMAT_YUY2", false, 0, "Incompatible", 0, 0}},
    {108, {"DXGI_FORMAT_Y210", false, 0, "Incompatible", 0, 0}},
    {109, {"DXGI_FORMAT_Y216", false, 0, "Incompatible", 0, 0}},
    {110, {"DXGI_FORMAT_NV11", false, 0, "Incompatible", 0, 0}},
    {111, {"DXGI_FORMAT_AI44", false, 0, "Incompatible", 0, 0}},
    {112, {"DXGI_FORMAT_IA44", false, 0, "Incompatible", 0, 0}},
    {113, {"DXGI_FORMAT_P8", false, 0, "Incompatible", 0, 0}},
    {114, {"DXGI_FORMAT_A8P8", false, 0, "Incompatible", 0, 0}},
    {115, {"DXGI_FORMAT_B4G4R4A4_UNORM", false, GL_RGBA4, "GL_RGBA4", GL_BGRA, GL_UNSIGNED_SHORT_4_4_4_4_REV}},

    // DXGI no define enumerantes oficiales 116..129

    {130, {"DXGI_FORMAT_P208", false, 0, "Incompatible", 0, 0}},
    {131, {"DXGI_FORMAT_V208", false, 0, "Incompatible", 0, 0}},
    {132, {"DXGI_FORMAT_V408", false, 0, "Incompatible", 0, 0}},

    {189, {"DXGI_FORMAT_SAMPLER_FEEDBACK_MIN_MIP_OPAQUE", false, 0, "Incompatible", 0, 0}},
    {190, {"DXGI_FORMAT_SAMPLER_FEEDBACK_MIP_REGION_USED_OPAQUE", false, 0, "Incompatible", 0, 0}},

    // DXGI moderno; no siempre aparece en wrappers viejos, pero existe oficialmente
    {191, {"DXGI_FORMAT_A4B4G4R4_UNORM", false, GL_RGBA4, "GL_RGBA4", GL_RGBA, GL_UNSIGNED_SHORT_4_4_4_4_REV}},

};



const FormatMapping* GetFormatMapping(int dxgiCode)
{
    auto it = dxgiFormatMap.find(dxgiCode);
    return (it != dxgiFormatMap.end()) ? &it->second : nullptr;
}


