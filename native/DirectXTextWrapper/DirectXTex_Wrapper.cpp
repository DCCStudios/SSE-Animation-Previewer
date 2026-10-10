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

// DirectXTex_Wrapper.cpp
// C++/CLI unified implementation: toda la lógica de DirectXTex dentro de métodos .NET
#include "DirectXTex_Wrapper.h"    // Define structs nativos y clases CLI bajo _MANAGED
#include <DirectXTex.h>
#include "OpenGL_MapFormats.h"
#include "OpenGL_MapFormats_FallBacks.h"

#include <vector>
#include <cstring>
#include <msclr/marshal.h>
#include <vcclr.h>

using namespace DirectX;
using namespace System;
using namespace System::Collections::Generic;
using namespace System::Runtime::InteropServices;
using namespace msclr;
#ifdef _MANAGED

namespace DirectXTexWrapperCLI
{
    static size_t CalculateMipExtent(size_t baseExtent, size_t mipLevel) noexcept
    {
        size_t value = baseExtent >> mipLevel;
        return value > 0 ? value : 1;
    }

    static size_t CalculateSurfaceRowCount(size_t height, DXGI_FORMAT format) noexcept
    {
        if (DirectX::IsCompressed(format))
        {
            size_t rows = (height + 3u) / 4u;
            return rows > 0 ? rows : 1;
        }

        return height > 0 ? height : 1;
    }

    // ===============================================================================================
    // ⭐ LA LEY DE "A QUE FORMATO DESCOMPRIMIR UN BCn", ESCRITA UNA VEZ, CON EL USO DECLARADO.
    //
    // ⛔ Estaba transcripta TRES veces —acá, adentro de LoadTextures y adentro de ConvertForBitmap— y
    // las tres NO decian lo mismo. Al factorizarla aparecieron DOS ejes en los que los llamadores
    // difieren de verdad. Ninguno es accidental: cada uno responde a un requisito distinto, y por eso
    // van como USO declarado y no como cuatro copias sueltas.
    //
    //   EJE 1 · sRGB. `ConvertForBitmap` SIEMPRE quiere lineal: su destino es un Bitmap .NET BGRA8
    //   lineal. Los otros dos conservan el sRGB de la fuente.
    //
    //   EJE 2 · SIGNO. La tuberia de conversion tiene que preservar SNORM porque re-encodear es una
    //   transformacion FIEL: mandar SNORM a UNORM satura la mitad negativa a 0 y ademas recuantiza
    //   (127 pasos -> 255). Los dos caminos de DECODE, en cambio, tienen que aterrizar en UNORM, porque
    //   la tabla de canales del lado administrado (`FaceTintCpuCompositor.CanalesDelFormatoDecodificado`)
    //   conoce 49/61 (R8G8_UNORM / R8_UNORM) y NO conoce 51/63 (R8G8_SNORM / R8_SNORM) — ojo con
    //   confundirlos con 50/62, que son los UINT. Y es lo que esos dos caminos ya hacian en produccion.
    //
    // ⛔⛔ EL EJE DEL SIGNO LO ENCONTRO UN REVISOR, NO MI GATE. Yo habia unificado los tres a UNORM y lo
    // declare byte-neutro apoyandome en la fixture `huecos`... que digeria
    // `ConvertLoadedTextureToDdsBytes` con salida BC3. Con salida COMPRIMIDA la llave de la tabla es el
    // formato de SALIDA, asi que el eje de la ENTRADA nunca se tocaba: el gate estaba verde midiendo
    // otra cosa. El caso que si lo ejercita es entrada BCn SNORM con salida SIN COMPRIMIR, y ahora esta
    // en la fixture.
    // ===============================================================================================
    enum class UsoDeDescompresion
    {
        PipelineDeConversion,   // re-encode: fiel — conserva sRGB y conserva el signo
        ConsumoCpuOGl,          // LoadTextures: conserva sRGB, aterriza SNORM en UNORM
        BitmapAdministrado      // ConvertForBitmap: siempre lineal, aterriza SNORM en UNORM
    };

    static DXGI_FORMAT FormatoDeDescompresion(DXGI_FORMAT srcFmt, UsoDeDescompresion uso) noexcept
    {
        const bool preservarSrgb = (uso != UsoDeDescompresion::BitmapAdministrado);
        const bool preservarSigno = (uso == UsoDeDescompresion::PipelineDeConversion);

        switch (srcFmt)
        {
        case DXGI_FORMAT_BC6H_UF16:
        case DXGI_FORMAT_BC6H_SF16:
            return DXGI_FORMAT_R16G16B16A16_FLOAT;

        case DXGI_FORMAT_BC4_UNORM:
            return DXGI_FORMAT_R8_UNORM;

        case DXGI_FORMAT_BC4_SNORM:
            return preservarSigno ? DXGI_FORMAT_R8_SNORM : DXGI_FORMAT_R8_UNORM;

        case DXGI_FORMAT_BC5_UNORM:
            return DXGI_FORMAT_R8G8_UNORM;

        case DXGI_FORMAT_BC5_SNORM:
            return preservarSigno ? DXGI_FORMAT_R8G8_SNORM : DXGI_FORMAT_R8G8_UNORM;

        case DXGI_FORMAT_BC1_UNORM:
        case DXGI_FORMAT_BC1_UNORM_SRGB:
        case DXGI_FORMAT_BC2_UNORM:
        case DXGI_FORMAT_BC2_UNORM_SRGB:
        case DXGI_FORMAT_BC3_UNORM:
        case DXGI_FORMAT_BC3_UNORM_SRGB:
        case DXGI_FORMAT_BC7_UNORM:
        case DXGI_FORMAT_BC7_UNORM_SRGB:
            return (preservarSrgb && DirectX::IsSRGB(srcFmt))
                ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
                : DXGI_FORMAT_R8G8B8A8_UNORM;

        default:
            // ⛔ El `default` tambien difiere por uso, y NO por capricho: la copia del pipeline devolvia
            // R8G8B8A8_UNORM y las de decode UNKNOWN. Para el pipeline UNKNOWN es mejor (que elija
            // DirectXTex el natural del formato) pero cambiaria bytes en un formato exotico, asi que se
            // conserva lo que cada camino hacia.
            return (uso == UsoDeDescompresion::PipelineDeConversion)
                ? DXGI_FORMAT_R8G8B8A8_UNORM
                : DXGI_FORMAT_UNKNOWN;
        }
    }

    // ===============================================================================================
    // ⛔⛔ NOTA: ACA VIVIO UNA CADENA DE MIPS NIVEL-POR-NIVEL EN PARALELO. SE SACO. LEER ANTES DE
    // VOLVER A INTENTARLO.
    //
    // El diagnostico que la motivo SIGUE SIENDO CIERTO y esta medido: generar los mips es el 63-85 % del
    // encode y corre con CPU/wall = 1,00, o sea en UN solo hilo. Con el filtro por default DirectXTex va
    // por WIC y `GenerateMipMapsUsingWIC` escala CADA NIVEL DESDE EL NIVEL 0, asi que los niveles son
    // independientes y `DirectX::Resize` por nivel da los MISMOS BYTES (verificado: 108 digestos x 5
    // corridas). Aislada, una textura sola mejoraba 1,2-2,0x.
    //
    // POR QUE SE SACO IGUAL, en orden de peso:
    //   1. NINGUN CAMINO DE PRODUCCION LA APROVECHABA. El bake corre en hilos del ThreadPool (MTA) con
    //      varias conversiones en vuelo ⇒ el reparto le daba 0 ayudantes. Y el bake interactivo sale del
    //      hilo de UI de WinForms, que es [STAThread] ⇒ tampoco. El 2x medido salia de un arnes que
    //      encodea de a una textura por vez, forma que la app no tiene.
    //   2. RIESGO DE REGRESION EN EL BAKE. Medido aislando el eje: el armado por nivel es 1,34-1,41x MAS
    //      LENTO EN SERIE que `GenerateMipMaps` (`Resize` asigna un ScratchImage por nivel, rehace el
    //      bitmap WIC de la fuente y obliga a copiar al destino; WIC escribe directo y arma el bitmap una
    //      vez). Con la maquina saturada eso se paga entero. Un reparto que a veces da 1 ayudante elige
    //      el camino mas caro sin nucleos libres para compensarlo.
    //   3. `std::thread` tira `std::system_error` si el SO no puede crear el hilo. Adentro de una funcion
    //      `noexcept` eso es `std::terminate()`: el proceso del usuario muere sin log. En x86, con dos
    //      buffers de decenas de MB vivos, encontrar 11 huecos de 1 MB para los stacks no es teorico.
    //   4. COM. `DirectX::Resize` usa una factoria WIC que DirectXTex cachea en un singleton de PROCESO.
    //      Creada desde un STA y usada desde workers MTA, se cruzan apartamentos sin proxy.
    //
    // ⇒ Si alguna vez se retoma: hace falta un POOL persistente (no `std::thread` por llamada), la
    // factoria WIC creada explicitamente desde un hilo MTA propio via `DirectX::SetWICFactory`, y un gate
    // permanente que compare los bytes del camino rapido contra los de DirectXTex CON EL PROCESO CARGADO
    // — porque si el camino depende de cuantos nucleos sobran, el bake pasa a producir bytes que dependen
    // de la carga, que es el defecto de 21-render-orden-de-dibujo-no-determinista.
    // ===============================================================================================

    static void CopyManagedSubresourceToScratchImage(
        TextureSubresource^ managedSubresource,
        size_t expectedWidth,
        size_t expectedHeight,
        DXGI_FORMAT format,
        const DirectX::Image* destinationImage)
    {
        if (managedSubresource == nullptr)
            throw gcnew ArgumentNullException("Subresources");
        if (managedSubresource->Data == nullptr)
            throw gcnew ArgumentException("Cada subrecurso debe tener Data.", "Subresources");
        if (managedSubresource->Data->Length == 0)
            throw gcnew ArgumentException("Cada subrecurso debe tener datos.", "Subresources");
        if (destinationImage == nullptr || destinationImage->pixels == nullptr)
            throw gcnew InvalidOperationException("No se pudo obtener la imagen destino para el subrecurso.");

        if (managedSubresource->Width > 0 && managedSubresource->Width != static_cast<int>(expectedWidth))
            throw gcnew ArgumentException("Width del subrecurso no coincide con el mip esperado.", "Subresources");
        if (managedSubresource->Height > 0 && managedSubresource->Height != static_cast<int>(expectedHeight))
            throw gcnew ArgumentException("Height del subrecurso no coincide con el mip esperado.", "Subresources");

        size_t tightRowPitch = 0;
        size_t tightSlicePitch = 0;
        HRESULT hr = DirectX::ComputePitch(format, expectedWidth, expectedHeight, tightRowPitch, tightSlicePitch, DirectX::CP_FLAGS_NONE);
        if (FAILED(hr))
            throw gcnew InvalidOperationException("ComputePitch fallÃ³ para el formato de entrada.");

        size_t sourceRowPitch = managedSubresource->RowPitch > 0
            ? static_cast<size_t>(managedSubresource->RowPitch)
            : tightRowPitch;
        size_t sourceSlicePitch = managedSubresource->SlicePitch > 0
            ? static_cast<size_t>(managedSubresource->SlicePitch)
            : tightSlicePitch;
        size_t sourceLength = static_cast<size_t>(managedSubresource->Data->LongLength);

        bool hasExplicitPitch = (managedSubresource->RowPitch > 0) || (managedSubresource->SlicePitch > 0);
        if (!hasExplicitPitch)
        {
            if (sourceLength != tightSlicePitch)
                throw gcnew ArgumentException("Cuando RowPitch/SlicePitch no se informan, el subrecurso debe venir tight-packed.", "Subresources");
        }
        else
        {
            if (sourceLength < sourceSlicePitch)
                throw gcnew ArgumentException("Data es mÃ¡s chico que SlicePitch para el subrecurso.", "Subresources");
        }

        if (sourceRowPitch < tightRowPitch)
            throw gcnew ArgumentException("RowPitch del subrecurso es menor que el mÃ­nimo requerido.", "Subresources");

        size_t rowCount = CalculateSurfaceRowCount(expectedHeight, format);
        if (sourceSlicePitch < (sourceRowPitch * rowCount))
            throw gcnew ArgumentException("SlicePitch del subrecurso no alcanza para contener todas las filas.", "Subresources");

        if (destinationImage->rowPitch < tightRowPitch || destinationImage->slicePitch < tightSlicePitch)
            throw gcnew InvalidOperationException("El buffer destino no tiene el pitch esperado.");

        pin_ptr<Byte> pinnedSource = &managedSubresource->Data[0];
        const auto srcBytes = reinterpret_cast<const uint8_t*>(pinnedSource);
        auto dstBytes = const_cast<uint8_t*>(destinationImage->pixels);

        for (size_t row = 0; row < rowCount; ++row)
        {
            std::memcpy(
                dstBytes + (row * destinationImage->rowPitch),
                srcBytes + (row * sourceRowPitch),
                tightRowPitch);
        }
    }

    static TextureSubresource^ CreateManagedSubresource(
        const DirectX::Image* image,
        int mipLevel,
        int arrayIndex)
    {
        if (image == nullptr || image->pixels == nullptr)
            throw gcnew InvalidOperationException("Subrecurso de salida invÃ¡lido.");
        if (image->slicePitch > static_cast<size_t>(Int32::MaxValue))
            throw gcnew InvalidOperationException("El subrecurso de salida excede el tamaÃ±o mÃ¡ximo soportado por .NET.");

        int sizeBytes = static_cast<int>(image->slicePitch);
        auto managedData = gcnew array<Byte>(sizeBytes);
        Marshal::Copy(IntPtr(const_cast<uint8_t*>(image->pixels)), managedData, 0, sizeBytes);

        return gcnew TextureSubresource(
            managedData,
            static_cast<int>(image->width),
            static_cast<int>(image->height),
            static_cast<int>(image->rowPitch),
            static_cast<int>(image->slicePitch),
            mipLevel,
            arrayIndex);
    }

    array<System::Byte>^ Loader::EncodeDDSHeader(
        int dxgiFormat,
        int width,
        int height,
        int arraySize,
        int mipLevels,
        bool isCubemap
    )
    {
        using namespace DirectX;

        if (width <= 0 || height <= 0) throw gcnew System::ArgumentOutOfRangeException("dimensiones inválidas");
        if (arraySize <= 0) arraySize = 1;
        if (mipLevels <= 0) mipLevels = 1;

        TexMetadata meta = {};
        meta.width = static_cast<size_t>(width);
        meta.height = static_cast<size_t>(height);
        meta.depth = 1;
        meta.arraySize = static_cast<size_t>(arraySize);
        meta.mipLevels = static_cast<size_t>(mipLevels);
        meta.miscFlags = isCubemap ? TEX_MISC_TEXTURECUBE : 0;
        meta.miscFlags2 = 0;
        meta.format = static_cast<DXGI_FORMAT>(dxgiFormat);
        meta.dimension = TEX_DIMENSION_TEXTURE2D; // FO4 DX10 ? 2D

        size_t required = 0;
        HRESULT hr = DirectX::EncodeDDSHeader(meta, DDS_FLAGS_NONE, nullptr, 0, required);
        if (FAILED(hr) || required == 0)
            throw gcnew System::InvalidOperationException("EncodeDDSHeader (consulta tamaño) falló");

        std::vector<uint8_t> header(required);
        hr = DirectX::EncodeDDSHeader(meta, DDS_FLAGS_NONE, header.data(), header.size(), required);
        if (FAILED(hr))
            throw gcnew System::InvalidOperationException("EncodeDDSHeader (escritura) falló");

        auto managed = gcnew array<System::Byte>(static_cast<int>(header.size()));
        System::Runtime::InteropServices::Marshal::Copy(
            System::IntPtr(header.data()), managed, 0, static_cast<int>(header.size())
        );
        return managed;
    }

// -----------------------
// Constructores CLI
// -----------------------
    TextureLevel::TextureLevel(array<Byte>^ data, int width, int height)
        : Data(data), Width(width), Height(height)
    {}

    TextureLoaded::TextureLoaded()
    {
        Levels = gcnew List<TextureLevel^>(0);
    }

    TextureSubresource::TextureSubresource()
        : Data(gcnew array<Byte>(0)), Width(0), Height(0), RowPitch(0), SlicePitch(0), MipLevel(-1), ArrayIndex(-1)
    {}

    TextureSubresource::TextureSubresource(array<Byte>^ data, int width, int height, int rowPitch, int slicePitch, int mipLevel, int arrayIndex)
        : Data(data), Width(width), Height(height), RowPitch(rowPitch), SlicePitch(slicePitch), MipLevel(mipLevel), ArrayIndex(arrayIndex)
    {}

    TextureConversionRequest::TextureConversionRequest()
        : Subresources(gcnew array<TextureSubresource^>(0)),
        Width(0), Height(0),
        InputDxgiFormat(static_cast<int>(DXGI_FORMAT_UNKNOWN)),
        OutputDxgiFormat(static_cast<int>(DXGI_FORMAT_UNKNOWN)),
        MipLevels(1), ArraySize(1),
        IsCubemap(false),
        AutoGenerateMipMaps(false),
        FilterFlags(static_cast<int>(TEX_FILTER_DEFAULT)),
        CompressFlags(static_cast<int>(TEX_COMPRESS_DEFAULT)),
        AlphaThreshold(TEX_THRESHOLD_DEFAULT)
    {}

    TextureConversionResult::TextureConversionResult()
        : Subresources(gcnew array<TextureSubresource^>(0)),
        Width(0), Height(0), DxgiFormat(static_cast<int>(DXGI_FORMAT_UNKNOWN)),
        MipLevels(0), ArraySize(0), IsCubemap(false)
    {}


    // -----------------------
    // DdsMetadata constructor + Loader::GetDdsMetadata
      // -----------------------
        DdsMetadata::DdsMetadata()
        : Width(0), Height(0), MipCount(0), DxgiFormat(0),
        Faces(0), IsCubemap(false), ArraySize(0), HeaderSize(0), Loaded(false)
    {
    }

     // -----------------------
    // Loader: Metadata
    // -----------------------
        DdsMetadata^ Loader::GetDdsMetadata(array<Byte>^ ddsBytes) {
            auto out = gcnew DdsMetadata();
            out->Loaded = false;
            if (ddsBytes == nullptr || ddsBytes->Length < 4) return out;

            pin_ptr<Byte> pData = &ddsBytes[0];
            DirectX::TexMetadata meta = {};                
                HRESULT hr = DirectX::GetMetadataFromDDSMemory(
                    reinterpret_cast<const uint8_t*>(pData),
                    static_cast<size_t>(ddsBytes->Length),
                    DirectX::DDS_FLAGS_NONE, meta
                );
            if (FAILED(hr)) return out;

            out->Width = static_cast<int>(meta.width);
            out->Height = static_cast<int>(meta.height);
            out->MipCount = static_cast<int>(meta.mipLevels);
            out->DxgiFormat = static_cast<int>(meta.format);
            out->IsCubemap = meta.IsCubemap();
            out->Faces = out->IsCubemap ? 6 : 1;
            // El arraySize CRUDO de DirectXTex: para un cubemap vale 6 (o 6*N), para una 2D suelta 1.
            // `Faces` es derivado y pierde el caso del Texture2DArray; los consumidores que necesitan
            // saber "¿esto es una sola imagen?" tienen que mirar ESTE campo.
            out->ArraySize = static_cast<int>(meta.arraySize);

            bool hasDx10 = (ddsBytes->Length >= 88) &&
                (ddsBytes[84] == 'D' && ddsBytes[85] == 'X' &&
                    ddsBytes[86] == '1' && ddsBytes[87] == '0');
            out->HeaderSize = hasDx10 ? 148 : 128;
            out->Loaded = true;
            return out;
        }

    // -----------------------
    // Loader: LoadTextures
    // -----------------------
    // Recorta un ScratchImage a UN nivel de mip, conservando todas las caras. Devuelve false si el
    // nivel no existe o si no se pudo reservar. Los bytes del nivel salen VERBATIM: lo unico que se
    // pierde son los otros niveles, que es exactamente el punto.
    static bool RecortarAUnNivel(DirectX::ScratchImage& imagen, size_t nivel, DirectX::ScratchImage& salida)
    {
        const TexMetadata& md = imagen.GetMetadata();
        if (nivel >= md.mipLevels || md.depth != 1)
            return false;

        TexMetadata mdSel = md;
        mdSel.width = CalculateMipExtent(md.width, nivel);
        mdSel.height = CalculateMipExtent(md.height, nivel);
        mdSel.mipLevels = 1;

        if (FAILED(salida.Initialize(mdSel)))
            return false;

        for (size_t cara = 0; cara < md.arraySize; ++cara)
        {
            const Image* src = imagen.GetImage(nivel, cara, 0);
            const Image* dst = salida.GetImage(0, cara, 0);
            if (!src || !src->pixels || !dst || !dst->pixels || src->slicePitch != dst->slicePitch)
            {
                salida.Release();
                return false;
            }
            std::memcpy(dst->pixels, src->pixels, src->slicePitch);
        }
        return true;
    }

    List<TextureLoaded^>^ Loader::LoadTextures(array<array<Byte>^>^ ddsFiles, bool useCompress, bool forceOpenGL)
    {
        return LoadTextures(ddsFiles, useCompress, forceOpenGL, -1);
    }

    List<TextureLoaded^>^ Loader::LoadTextures(array<array<Byte>^>^ ddsFiles, bool useCompress, bool forceOpenGL, int onlyMipLevel)
    {
        if (ddsFiles == nullptr || ddsFiles->Length == 0)
            throw gcnew ArgumentException("ddsFiles debe contener al menos una textura");

        auto output = gcnew List<TextureLoaded^>(ddsFiles->Length);

        for (int t = 0; t < ddsFiles->Length; ++t)
        {
            auto texfb = gcnew TextureLoaded();
            texfb->Loaded = false;        // por defecto
            texfb->DxgiCodeOriginal = -1; // valores seguros por defecto
            texfb->DxgiCodeFinal = -1;

            auto dataArr = ddsFiles[t];
            if (dataArr == nullptr || dataArr->Length == 0)
            {
                output->Add(texfb);
                continue;
            }

            // Carga DDS
            pin_ptr<Byte> pData = &dataArr[0];
            ScratchImage image;
            HRESULT hr = DirectX::LoadFromDDSMemory(
                reinterpret_cast<const uint8_t*>(pData),
                static_cast<size_t>(dataArr->Length),
                DDS_FLAGS_NONE, nullptr, image
            );
            if (FAILED(hr))
            {
                output->Add(texfb);
                continue;
            }

            const TexMetadata* meta = &image.GetMetadata();
            int dxgiOrig = static_cast<int>(meta->format);
            int dxgiFinal = static_cast<int>(meta->format);
            bool compressed = IsCompressed(meta->format);

            // Determinar compatibilidad OpenGL
            const FormatMapping* fmap = GetFormatMapping(dxgiFinal);
            bool compatibleGL = fmap && fmap->glInternalFormat && (!compressed || useCompress);
            bool needsDecompress = (compressed && !useCompress) || (forceOpenGL && !compatibleGL);

            // ⛔ EL RANGO DE `onlyMipLevel` SE MIRA ACA, ANTES DE DESCOMPRIMIR. El chequeo vivia abajo, tras
            // el Decompress: un nivel fuera de rango pagaba la cadena BCn ENTERA y recien despues devolvia
            // Loaded=false, o sea el peor camino de CPU y memoria justo para la entrada invalida.
            // ⛔ NO ES EL ARREGLO DE UN BUG OBSERVADO, y conviene que quede escrito: el unico llamador
            // (`FaceTintCpuCompositor.DecodeDds`) deriva el nivel de `GetDdsMetadata`, que usa el MISMO
            // parser y los MISMOS flags (`GetMetadataFromDDSMemory` / `LoadFromDDSMemory`, ambos
            // DDS_FLAGS_NONE) ⇒ el indice SIEMPRE cae en rango, y un payload truncado ya muere antes, en el
            // LoadFromDDSMemory. Esto es el contrato de una API publica, que cualquiera puede llamar con un
            // numero arbitrario.
            // ⛔ TIENE QUE IR ANTES del `finalImg = std::move(image)` de mas abajo: despues, `meta` sigue
            // apuntando al metadata de `image`, que el move dejo en CERO, y `mipLevels == 0` rechazaria
            // TODOS los niveles.
            if (onlyMipLevel >= 0 && static_cast<size_t>(onlyMipLevel) >= meta->mipLevels)
            {
                output->Add(texfb);
                continue;
            }

            // ⭐ RECORTE A UN NIVEL, Y SOLO SI VA A DESCOMPRIMIR.
            // Lo caro es la descompresion BCn, asi que el recorte tiene que pasar ANTES de ella: recortar
            // al final no ahorra nada. Pero recortar cuando NO hay descompresion es peor que no hacerlo:
            // el recorte COPIA el nivel y no ahorra ningun trabajo. MEDIDO: con un BGRA8 sin comprimir el
            // decode empeoraba 0,74-0,80x haciendolo siempre. Para ese caso el ahorro esta del otro lado
            // —emitir un solo nivel administrado en vez de la cadena entera— y eso se hace mas abajo.
            bool recortado = false;
            if (onlyMipLevel >= 0 && needsDecompress)
            {
                ScratchImage recorte;
                if (RecortarAUnNivel(image, static_cast<size_t>(onlyMipLevel), recorte))
                {
                    image = std::move(recorte);
                    meta = &image.GetMetadata();
                    recortado = true;
                }
            }

            // Imagen final
            ScratchImage finalImg;
            if (!needsDecompress)
            {
                finalImg = std::move(image);
                // Tras mover, refrescar SIEMPRE metadata/flags
                meta = &finalImg.GetMetadata();
                dxgiFinal = static_cast<int>(meta->format);
                compressed = IsCompressed(meta->format);
                fmap = GetFormatMapping(dxgiFinal);
                compatibleGL = fmap && fmap->glInternalFormat && (!compressed || useCompress);
            }
            else
            {
                // Una sola ley para los tres caminos (ver FormatoDeDescompresion).
                // Este camino CONSERVA el sRGB de la fuente: alimenta al render y al re-encode.
                DXGI_FORMAT decompFmt = FormatoDeDescompresion(meta->format, UsoDeDescompresion::ConsumoCpuOGl);

                hr = DirectX::Decompress(
                    image.GetImages(), image.GetImageCount(), *meta,
                    decompFmt,           // <- NUNCA pasar un formato BC aquí
                    finalImg
                );
                if (FAILED(hr))
                {
                    output->Add(texfb);
                    continue;
                }

                // Refrescar metadata/flags tras descomprimir
                meta = &finalImg.GetMetadata();
                dxgiFinal = static_cast<int>(meta->format);
                compressed = IsCompressed(meta->format);
                fmap = GetFormatMapping(dxgiFinal);
                compatibleGL = fmap && fmap->glInternalFormat && (!compressed || useCompress);
            }

            // Convertir forzado a OpenGL (si aún no es compatible)
            if (forceOpenGL && !compatibleGL)
            {
                int fallbackCode = ResolveFallback(dxgiFinal);
                // ⛔⛔ EL DESTINO NO PUEDE SER EL MISMO ScratchImage QUE LA FUENTE.
                // DirectX::Convert arranca con `result.Initialize(...)`, e Initialize hace `Release()`:
                // libera el array de Image Y el bloque de pixeles. Pasando `finalImg` como destino, el
                // `finalImg.GetImages()` que ya se evaluo como argumento queda colgando y la conversion
                // lee memoria liberada (use-after-free). Hoy la rama es inalcanzable —los formatos sin
                // mapeo GL son TYPELESS/YUV y esos mueren antes, en el Decompress— pero es una mina:
                // el dia que alguien agregue un formato al mapeo, revienta el heap sin sintoma claro.
                ScratchImage convertedImg;
                hr = DirectX::Convert(
                    finalImg.GetImages(), finalImg.GetImageCount(), *meta,
                    static_cast<DXGI_FORMAT>(fallbackCode),
                    TEX_FILTER_DEFAULT, 0.0f,
                    convertedImg
                );
                if (FAILED(hr))
                {
                    output->Add(texfb);
                    continue;
                }

                finalImg = std::move(convertedImg);

                // Refrescar metadata tras Convert
                meta = &finalImg.GetMetadata();
                dxgiFinal = static_cast<int>(meta->format);
                fmap = GetFormatMapping(dxgiFinal);
            }

            // Construir objeto administrado
            auto tex = gcnew TextureLoaded();
            tex->DxgiCodeOriginal = dxgiOrig;
            tex->DxgiCodeFinal = static_cast<int>(meta->format);
            fmap = GetFormatMapping(tex->DxgiCodeFinal);
            if (!fmap)
            {
                output->Add(texfb);
                continue;
            }

            tex->GlInternalFormat = fmap->glInternalFormat;
            tex->GlPixelFormat = fmap->glPixelFormat;
            tex->GlPixelType = fmap->glPixelType;
            tex->IsCompressedGL = fmap->isCompressed;
            tex->IsCubemap = meta->IsCubemap() != 0;
            tex->Loaded = true;
            const int nivelesDisponibles = max(1, static_cast<int>(finalImg.GetMetadata().mipLevels));

            // ⛔⛔ UN DDS VOLUMETRICO SALIA COMO UNA 2D VALIDA, CON UNA SOLA SLICE. DirectXTex pone
            // arraySize=1 y depth=N para un volumen, asi que `Faces` da 1 y NINGUNA guarda de VB lo ve
            // (`TextureLoaded` no expone `depth`). El loop de abajo hace `GetImage(m, f, 0)` = slice 0 y
            // nada mas, y devolvia `Loaded = true`: el consumidor recibia la primera lamina de un LUT 3D
            // creyendo que era la textura entera. Esto SOLO se puede cerrar acá.
            if (finalImg.GetMetadata().depth != 1)
            {
                output->Add(texfb);
                continue;
            }

            tex->Faces = static_cast<int>(finalImg.GetMetadata().arraySize); // cast explícito

            // Que niveles se MATERIALIZAN como arrays administrados. Si se pidio uno solo y no hubo
            // recorte previo (porque no habia descompresion que ahorrar), el ahorro esta justamente aca:
            // no asignar ni copiar los mips que el consumidor va a descartar.
            int mipIni = 0;
            int mipFin = nivelesDisponibles - 1;
            if (onlyMipLevel >= 0 && !recortado)
            {
                // ⛔ SIN DEGRADAR EN SILENCIO. Antes, con un nivel fuera de rango, no se cumplia ninguna
                // rama y se emitia LA CADENA ENTERA con Miplevels = N. El consumidor que confia en el
                // contrato del .h lee Levels(0) creyendo que es el nivel pedido y recibe el mip 0: una
                // textura con el detalle equivocado, que en un bake no se ve hasta comparar pixeles.
                //
                // ⛔ PERO SE SEÑALA COMO TODOS LOS DEMAS FALLOS DE ESTE LOOP, no con una excepcion.
                // Esto es un loop POR ARCHIVO y `onlyMipLevel` es UN parametro para el lote entero: si un
                // archivo del medio tiene menos mips que el nivel pedido, tirar mata TODO lo ya decodificado
                // y le devuelve al llamador una lista vacia por culpa de un solo archivo. Los otros seis
                // caminos de fallo de esta misma funcion hacen `output->Add(texfb); continue;` — habia dos
                // politicas de fallo conviviendo. Ademas el unico llamador (`FaceTintCpuCompositor.DecodeDds`)
                // termina en `Catch : Return Nothing`, asi que la excepcion no evitaba nada: cambiaba "mip
                // equivocado" por "textura entera perdida y sin log". Con `Loaded = false` el consumidor se
                // entera por el camino que ya chequea.
                //
                // ⛔ HOY ESTA RAMA ES INALCANZABLE, y queda de RED. El rechazo real vive arriba, antes del
                // Decompress. Es inalcanzable por construccion: llegar aca exige `!recortado`, y sin recorte
                // ni `Decompress` ni `Convert` cambian `mipLevels`, asi que `nivelesDisponibles` es el mismo
                // numero que ya se comparo contra la fuente. Se conserva porque el orden de los pasos de esta
                // funcion cambio dos veces: si alguien vuelve a mover el recorte, esto es lo que evita que un
                // nivel fuera de rango salga como mip 0 con `Miplevels` mintiendo. La POLITICA de fallo
                // (Loaded=false y no una excepcion) esta explicada arriba y sigue valiendo para los dos.
                if (onlyMipLevel >= nivelesDisponibles)
                {
                    output->Add(texfb);
                    continue;
                }
                mipIni = onlyMipLevel;
                mipFin = onlyMipLevel;
            }
            tex->Miplevels = mipFin - mipIni + 1;

            // Extraer primero todas las caras del nivel más bajo pedido, luego las del siguiente, etc.
            bool levelesOk = true;
            for (int m = mipIni; m <= mipFin && levelesOk; ++m)
            {
                for (int f = 0; f < tex->Faces; ++f)
                {
                    const Image* img = finalImg.GetImage(m, f, 0); // slice=0 (si quieres 3D, iterar depth)
                    if (!img || !img->pixels)
                    {
                        levelesOk = false;
                        break;
                    }

                    // ⛔ El tamaño lo da DirectXTex en `slicePitch`, calculado por ComputePitch para el
                    // formato real. Recalcularlo acá con una tabla de blockSize propia era duplicar la ley
                    // y la copia era INCOMPLETA: BC1_TYPELESS/BC4_TYPELESS caian en el `default` de 16 bytes
                    // por bloque siendo de 8, o sea se pedia el DOBLE y se leia fuera del buffer nativo.
                    // Para todos los formatos que la tabla SI cubria, el valor es identico (rowPitch*filas).
                    // ⛔ NO se puede saltear un nivel: `Miplevels` y `Faces` ya estan fijados, y dejar
                    // `Levels` corto hace que todo consumidor que indexe `m * Faces + f` lea la cara
                    // EQUIVOCADA en vez de recibir un IndexOutOfRange. Si un subrecurso no entra en un
                    // array de .NET, la textura entera no se puede representar: se descarta.
                    if (img->slicePitch > static_cast<size_t>(Int32::MaxValue))
                    {
                        levelesOk = false;
                        break;
                    }
                    int sizeBytes = static_cast<int>(img->slicePitch);

                    auto managedData = gcnew array<Byte>(sizeBytes);
                    Marshal::Copy(
                        IntPtr(const_cast<uint8_t*>(img->pixels)),
                        managedData, 0, sizeBytes
                    );

                    tex->Levels->Add(gcnew TextureLevel(
                        managedData,
                        static_cast<int>(img->width),
                        static_cast<int>(img->height)
                    ));
                }
            }

            if (!levelesOk)
            {
                output->Add(texfb);
                continue;
            }

            output->Add(tex);
        }

        return output;
    }



    // -----------------------
    // Loader: ConvertForBitmap
    // -----------------------
    TextureLoaded^ Loader::ConvertForBitmap(array<Byte>^ ddsFile)
    {
        if (ddsFile == nullptr || ddsFile->Length == 0)
            throw gcnew ArgumentException("ddsFile inválido");

        // Cargar DDS
        pin_ptr<Byte> pData = &ddsFile[0];
        DirectX::ScratchImage image;
        HRESULT hr = DirectX::LoadFromDDSMemory(
            reinterpret_cast<const uint8_t*>(pData),
            static_cast<size_t>(ddsFile->Length),
            DirectX::DDS_FLAGS_NONE, nullptr, image
        );
        if (FAILED(hr))
            throw gcnew InvalidOperationException("No se pudo cargar DDS para bitmap");

        const DirectX::TexMetadata* meta = &image.GetMetadata();
        DirectX::ScratchImage finalImg;

        // Queremos un bitmap compatible con .NET: BGRA8 lineal (no sRGB)
        const DXGI_FORMAT targetFmt = DXGI_FORMAT_B8G8R8A8_UNORM;
        const DXGI_FORMAT srcFmt = meta->format;

        // ⭐ SOLO EL NIVEL 0. Esta funcion devuelve un unico Level (mip 0, cara 0) y ya lo decia su
        // nombre, pero descomprimia y convertia la CADENA ENTERA para despues tirarla: en un DDS con
        // mips eso es ~33 % de trabajo de decode de mas, mas la memoria de toda la cadena.
        // Decompress y Convert son por-imagen independientes (recorren `srcImages` de a una), asi que
        // pasarles solo la imagen 0 da EXACTAMENTE los mismos bytes para esa imagen.
        const DirectX::Image* src0 = image.GetImage(0, 0, 0);
        if (!src0 || !src0->pixels)
            throw gcnew InvalidOperationException("El DDS no tiene nivel 0 utilizable");

        // Si está comprimida, descomprimir al formato adecuado según BCn
        if (IsCompressed(srcFmt))
        {
            // Una sola ley para los tres caminos (ver FormatoDeDescompresion).
            // ⛔ preservarSrgb = FALSE a proposito: el destino es un Bitmap .NET BGRA8 LINEAL, asi que
            // una fuente sRGB tiene que salir en el formato lineal. Es el unico eje en el que este
            // camino difiere de los otros dos, y por eso es un parametro y no otra copia de la tabla.
            const DXGI_FORMAT decompFmt = FormatoDeDescompresion(srcFmt, UsoDeDescompresion::BitmapAdministrado);

            hr = DirectX::Decompress(*src0, decompFmt, finalImg);
            if (FAILED(hr))
                throw gcnew InvalidOperationException("No se pudo descomprimir DDS");

            // Si lo descomprimido no es aún BGRA8, convertir
            const DirectX::Image* dec0 = finalImg.GetImage(0, 0, 0);
            if (!dec0 || !dec0->pixels)
                throw gcnew InvalidOperationException("La descompresión no dejó nivel 0");
            if (finalImg.GetMetadata().format != targetFmt)
            {
                DirectX::ScratchImage tmp;
                hr = DirectX::Convert(*dec0, targetFmt, DirectX::TEX_FILTER_DEFAULT, 0.0f, tmp);
                if (FAILED(hr))
                    throw gcnew InvalidOperationException("Error al convertir DDS a BGRA8");

                finalImg = std::move(tmp);
            }
        }
        else if (srcFmt != targetFmt)
        {
            // No comprimida pero con formato distinto -> convertir a BGRA8
            hr = DirectX::Convert(*src0, targetFmt, DirectX::TEX_FILTER_DEFAULT, 0.0f, finalImg);
            if (FAILED(hr))
                throw gcnew InvalidOperationException("Error en conversión a BGRA8");
        }
        else
        {
            // Ya está en BGRA8
            finalImg = std::move(image);
        }

        // Extraer datos del primer mip/face/slice
        const DirectX::Image* img = finalImg.GetImage(0, 0, 0);
        if (!img || !img->pixels)
            throw gcnew InvalidOperationException("Imagen final inválida");

        int sizeBytes = static_cast<int>(img->slicePitch);
        auto managedData = gcnew array<Byte>(sizeBytes);
        System::Runtime::InteropServices::Marshal::Copy(
            IntPtr(const_cast<uint8_t*>(img->pixels)), managedData, 0, sizeBytes
        );

        // Construir resultado administrado
        auto tex = gcnew TextureLoaded();
        tex->DxgiCodeOriginal = static_cast<int>(srcFmt);                         // formato original del DDS cargado
        tex->DxgiCodeFinal = static_cast<int>(finalImg.GetMetadata().format);  // BGRA8_UNORM
        tex->IsCubemap = false;
        tex->Loaded = true;
        tex->GlInternalFormat = 0;
        tex->GlPixelFormat = 0;
        tex->GlPixelType = 0;
        tex->Miplevels = 1;
        tex->Faces = 1;

        tex->Levels->Add(gcnew TextureLevel(
            managedData,
            static_cast<int>(img->width),
            static_cast<int>(img->height)
        ));

        return tex;
    }

    // -----------------------------------------------------------------------------------------------
    // LA TUBERIA DE CONVERSION, UNA SOLA VEZ.
    // Valida el request, copia los subrecursos administrados a un ScratchImage, y aplica en orden:
    // descompresion (si hace falta) -> generacion de mips -> formato canonico -> compresion/conversion.
    // Deja el resultado en `workingImage`.
    //
    // ⛔ Existe como funcion aparte para que la ley este escrita UNA vez: la comparten
    // `ConvertSubresources` (que devuelve un subrecurso administrado por mip/cara) y
    // `ConvertSubresourcesToDds` (que devuelve el DDS entero en UN solo buffer). Si se duplicara, los
    // dos caminos podrian divergir en el filtro, el formato intermedio o el orden de los pasos, y la
    // divergencia solo se veria como una diferencia de bytes en el bake.
    // -----------------------------------------------------------------------------------------------
    static void RunConversionPipeline(TextureConversionRequest^ request, DirectX::ScratchImage& workingImage)
    {
        if (request == nullptr)
            throw gcnew ArgumentNullException("request");
        if (request->Width <= 0 || request->Height <= 0)
            throw gcnew ArgumentOutOfRangeException("request", "Width and Height must be > 0.");
        if (request->ArraySize <= 0)
            throw gcnew ArgumentOutOfRangeException("request", "ArraySize must be > 0.");
        if (request->IsCubemap && (request->ArraySize % 6) != 0)
            throw gcnew ArgumentException("ArraySize must be a multiple of 6 for cubemaps.", "request");

        bool autoGenerateMipMaps = request->AutoGenerateMipMaps;
        size_t requestedMipLevels = 0;
        if (autoGenerateMipMaps)
        {
            if (request->MipLevels < 0)
                throw gcnew ArgumentOutOfRangeException("request", "MipLevels must be >= 0 when AutoGenerateMipMaps is enabled.");
            requestedMipLevels = request->MipLevels > 0 ? static_cast<size_t>(request->MipLevels) : 0u;
        }
        else
        {
            if (request->MipLevels <= 0)
                throw gcnew ArgumentOutOfRangeException("request", "MipLevels must be > 0.");
            requestedMipLevels = static_cast<size_t>(request->MipLevels);
        }

        int inputMipCount = autoGenerateMipMaps ? 1 : request->MipLevels;
        long long expectedCount64 = static_cast<long long>(inputMipCount) * static_cast<long long>(request->ArraySize);
        if (expectedCount64 <= 0 || expectedCount64 > static_cast<long long>(Int32::MaxValue))
            throw gcnew ArgumentException("The total number of subresources is invalid.", "request");

        int expectedCount = static_cast<int>(expectedCount64);
        if (request->Subresources == nullptr || request->Subresources->Length != expectedCount)
        {
            if (autoGenerateMipMaps)
                throw gcnew ArgumentException("Subresources must contain exactly ArraySize base subresources when AutoGenerateMipMaps is enabled.", "request");
            throw gcnew ArgumentException("Subresources must contain exactly MipLevels * ArraySize elements.", "request");
        }

        DXGI_FORMAT inputFormat = static_cast<DXGI_FORMAT>(request->InputDxgiFormat);
        DXGI_FORMAT outputFormat = static_cast<DXGI_FORMAT>(request->OutputDxgiFormat);
        if (inputFormat == DXGI_FORMAT_UNKNOWN)
            throw gcnew ArgumentException("InputDxgiFormat cannot be DXGI_FORMAT_UNKNOWN.", "request");
        if (outputFormat == DXGI_FORMAT_UNKNOWN)
            throw gcnew ArgumentException("OutputDxgiFormat cannot be DXGI_FORMAT_UNKNOWN.", "request");
        if (DirectX::IsTypeless(inputFormat) || DirectX::IsTypeless(outputFormat))
            throw gcnew ArgumentException("Typeless formats are not supported by ConvertSubresources.", "request");

        TexMetadata sourceMetadata = {};
        sourceMetadata.width = static_cast<size_t>(request->Width);
        sourceMetadata.height = static_cast<size_t>(request->Height);
        sourceMetadata.depth = 1;
        sourceMetadata.arraySize = static_cast<size_t>(request->ArraySize);
        sourceMetadata.mipLevels = static_cast<size_t>(inputMipCount);
        sourceMetadata.miscFlags = request->IsCubemap ? TEX_MISC_TEXTURECUBE : 0;
        sourceMetadata.miscFlags2 = 0;
        sourceMetadata.format = inputFormat;
        sourceMetadata.dimension = TEX_DIMENSION_TEXTURE2D;

        ScratchImage sourceImage;
        HRESULT hr = sourceImage.Initialize(sourceMetadata);
        if (FAILED(hr))
            throw gcnew InvalidOperationException("Unable to initialize the source texture for conversion.");

        for (int mip = 0; mip < inputMipCount; ++mip)
        {
            size_t mipWidth = CalculateMipExtent(sourceMetadata.width, static_cast<size_t>(mip));
            size_t mipHeight = CalculateMipExtent(sourceMetadata.height, static_cast<size_t>(mip));

            for (int item = 0; item < request->ArraySize; ++item)
            {
                int index = (mip * request->ArraySize) + item;
                auto managedSubresource = request->Subresources[index];
                if (managedSubresource == nullptr)
                    throw gcnew ArgumentException("Subresources contains a null element.", "request");
                if (managedSubresource->MipLevel >= 0 && managedSubresource->MipLevel != mip)
                    throw gcnew ArgumentException("Subresource MipLevel does not match its position.", "request");
                if (managedSubresource->ArrayIndex >= 0 && managedSubresource->ArrayIndex != item)
                    throw gcnew ArgumentException("Subresource ArrayIndex does not match its position.", "request");

                auto destinationImage = sourceImage.GetImage(static_cast<size_t>(mip), static_cast<size_t>(item), 0);
                CopyManagedSubresourceToScratchImage(managedSubresource, mipWidth, mipHeight, inputFormat, destinationImage);
            }
        }

        workingImage = std::move(sourceImage);
        bool willGenerateMipMaps = autoGenerateMipMaps && (requestedMipLevels != 1u);

        if (DirectX::IsCompressed(inputFormat) && ((inputFormat != outputFormat) || willGenerateMipMaps))
        {
            // Misma ley, distinta LLAVE: si la salida es comprimida hay que aterrizar en el formato
            // canonico que ESA compresion consume; si no, en el natural de la ENTRADA. Antes eran dos
            // funciones y una de ellas se contradecia con la otra en el mismo ternario.
            const DXGI_FORMAT llave = DirectX::IsCompressed(outputFormat) ? outputFormat : inputFormat;
            const DXGI_FORMAT decompressFormat = FormatoDeDescompresion(llave, UsoDeDescompresion::PipelineDeConversion);

            ScratchImage decompressedImage;
            hr = DirectX::Decompress(
                workingImage.GetImages(),
                workingImage.GetImageCount(),
                workingImage.GetMetadata(),
                decompressFormat,
                decompressedImage);
            if (FAILED(hr))
                throw gcnew InvalidOperationException("Unable to decompress the input texture.");

            workingImage = std::move(decompressedImage);
        }

        if (willGenerateMipMaps)
        {
            ScratchImage mipmappedImage;
            hr = DirectX::GenerateMipMaps(
                workingImage.GetImages(),
                workingImage.GetImageCount(),
                workingImage.GetMetadata(),
                static_cast<TEX_FILTER_FLAGS>(request->FilterFlags),
                requestedMipLevels,
                mipmappedImage);
            if (FAILED(hr))
                throw gcnew InvalidOperationException("Unable to auto-generate mipmaps for the requested texture.");

            workingImage = std::move(mipmappedImage);
        }

        if (DirectX::IsCompressed(outputFormat))
        {
            if (workingImage.GetMetadata().format != outputFormat)
            {
                DXGI_FORMAT canonicalFormat = FormatoDeDescompresion(outputFormat, UsoDeDescompresion::PipelineDeConversion);
                if (workingImage.GetMetadata().format != canonicalFormat)
                {
                    ScratchImage canonicalImage;
                    hr = DirectX::Convert(
                        workingImage.GetImages(),
                        workingImage.GetImageCount(),
                        workingImage.GetMetadata(),
                        canonicalFormat,
                        static_cast<TEX_FILTER_FLAGS>(request->FilterFlags),
                        request->AlphaThreshold,
                        canonicalImage);
                    if (FAILED(hr))
                        throw gcnew InvalidOperationException("Unable to convert to the intermediate format needed for compression.");

                    workingImage = std::move(canonicalImage);
                }

                ScratchImage compressedImage;
                hr = DirectX::Compress(
                    workingImage.GetImages(),
                    workingImage.GetImageCount(),
                    workingImage.GetMetadata(),
                    outputFormat,
                    static_cast<TEX_COMPRESS_FLAGS>(request->CompressFlags),
                    request->AlphaThreshold,
                    compressedImage);
                if (FAILED(hr))
                    throw gcnew InvalidOperationException("Unable to compress to the requested output format.");

                workingImage = std::move(compressedImage);
            }
        }
        else if (workingImage.GetMetadata().format != outputFormat)
        {
            ScratchImage convertedImage;
            hr = DirectX::Convert(
                workingImage.GetImages(),
                workingImage.GetImageCount(),
                workingImage.GetMetadata(),
                outputFormat,
                static_cast<TEX_FILTER_FLAGS>(request->FilterFlags),
                request->AlphaThreshold,
                convertedImage);
            if (FAILED(hr))
                throw gcnew InvalidOperationException("Unable to convert to the requested output format.");

            workingImage = std::move(convertedImage);
        }
    }

    TextureConversionResult^ Loader::ConvertSubresources(TextureConversionRequest^ request)
    {
        ScratchImage workingImage;
        RunConversionPipeline(request, workingImage);

        const TexMetadata& resultMetadata = workingImage.GetMetadata();
        unsigned long long resultCount64 = static_cast<unsigned long long>(resultMetadata.mipLevels) * static_cast<unsigned long long>(resultMetadata.arraySize);
        if (resultCount64 > static_cast<unsigned long long>(Int32::MaxValue))
            throw gcnew InvalidOperationException("The resulting texture exceeds the range supported by .NET.");
        if (resultMetadata.width > static_cast<size_t>(Int32::MaxValue) || resultMetadata.height > static_cast<size_t>(Int32::MaxValue))
            throw gcnew InvalidOperationException("The resulting texture exceeds the range supported by .NET.");

        int resultCount = static_cast<int>(resultCount64);
        auto result = gcnew TextureConversionResult();
        result->Width = static_cast<int>(resultMetadata.width);
        result->Height = static_cast<int>(resultMetadata.height);
        result->DxgiFormat = static_cast<int>(resultMetadata.format);
        result->MipLevels = static_cast<int>(resultMetadata.mipLevels);
        result->ArraySize = static_cast<int>(resultMetadata.arraySize);
        result->IsCubemap = resultMetadata.IsCubemap() != 0;
        result->Subresources = gcnew array<TextureSubresource^>(resultCount);

        int writeIndex = 0;
        for (int mip = 0; mip < result->MipLevels; ++mip)
        {
            for (int item = 0; item < result->ArraySize; ++item)
            {
                auto image = workingImage.GetImage(static_cast<size_t>(mip), static_cast<size_t>(item), 0);
                result->Subresources[writeIndex] = CreateManagedSubresource(image, mip, item);
                ++writeIndex;
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------------------------------
    // Loader::ConvertSubresourcesToDds
    //
    // Misma tuberia que ConvertSubresources, pero devuelve el DDS COMPLETO (header + payload) en UN
    // solo array administrado. Es el camino para el consumidor que lo unico que quiere es el archivo.
    //
    // Por que existe (MEDIDO, no argumentado): la version por subrecursos obliga al llamador a hacer
    // el mismo payload TRES veces en memoria administrada —un array por mip, la concatenacion, y el
    // DDS final— y las tres son LOH para una textura de cara. Con 4096² BGRA8 el camino de produccion
    // asignaba 320 MB por encode; este asigna solo el DDS.
    //
    // ⛔ ORDEN DEL PAYLOAD: array-major y despues mip, que es lo que dice el formato DDS y lo que
    // escribe DirectX::SaveToDDSMemory (`for item { for level }`). El camino por subrecursos emite
    // mip-major, asi que para ArraySize > 1 (cubemaps) los dos ordenes NO coinciden: el de aca es el
    // correcto. Con ArraySize == 1 —todos los caminos de la app— son el MISMO byte a byte.
    // -----------------------------------------------------------------------------------------------
    array<Byte>^ Loader::ConvertSubresourcesToDds(TextureConversionRequest^ request)
    {
        ScratchImage workingImage;
        RunConversionPipeline(request, workingImage);

        const TexMetadata& md = workingImage.GetMetadata();

        size_t headerSize = 0;
        HRESULT hr = DirectX::EncodeDDSHeader(md, DDS_FLAGS_NONE, nullptr, 0, headerSize);
        if (FAILED(hr) || headerSize == 0)
            throw gcnew InvalidOperationException("EncodeDDSHeader (consulta tamaño) falló");

        unsigned long long total = static_cast<unsigned long long>(headerSize);
        for (size_t item = 0; item < md.arraySize; ++item)
        {
            for (size_t level = 0; level < md.mipLevels; ++level)
            {
                const Image* img = workingImage.GetImage(level, item, 0);
                if (!img || !img->pixels)
                    throw gcnew InvalidOperationException("Falta un subrecurso en el resultado de la conversión.");
                total += static_cast<unsigned long long>(img->slicePitch);
            }
        }
        if (total > static_cast<unsigned long long>(Int32::MaxValue))
            throw gcnew InvalidOperationException("El DDS resultante excede el tamaño máximo de un array de .NET.");

        auto ddsBytes = gcnew array<Byte>(static_cast<int>(total));
        pin_ptr<Byte> pinned = &ddsBytes[0];
        auto dst = reinterpret_cast<uint8_t*>(pinned);

        // `escrito` aparte de `headerSize`: el parametro de salida de EncodeDDSHeader no puede ser la
        // misma variable que su capacidad si despues se quiere seguir usando la capacidad.
        size_t escrito = 0;
        hr = DirectX::EncodeDDSHeader(md, DDS_FLAGS_NONE, dst, headerSize, escrito);
        if (FAILED(hr) || escrito != headerSize)
            throw gcnew InvalidOperationException("EncodeDDSHeader (escritura) falló");

        size_t offset = headerSize;
        for (size_t item = 0; item < md.arraySize; ++item)
        {
            for (size_t level = 0; level < md.mipLevels; ++level)
            {
                const Image* img = workingImage.GetImage(level, item, 0);
                if (!img || !img->pixels)
                    throw gcnew InvalidOperationException("Falta un subrecurso en el resultado de la conversión.");
                std::memcpy(dst + offset, img->pixels, img->slicePitch);
                offset += img->slicePitch;
            }
        }

        return ddsBytes;
    }




} // namespace DirectXTexWrapperCLI

#endif // _MANAGED




