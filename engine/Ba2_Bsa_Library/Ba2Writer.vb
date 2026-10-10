Option Strict On
Imports System.IO
Imports System.Text
Imports K4os.Compression.LZ4.Streams
Imports K4os.Compression.LZ4

Namespace BethesdaArchive.Core


    Public Class Ba2WriterCommon

        ' ====== Presets ZLIB, equivalentes a cabeceras típicas ======
        Public Enum ZlibPreset
            Fast = 0        ' cabecera 0x78 0x01 ; DeflateStream Fastest
            [Default] = 1   ' cabecera 0x78 0x9C ; DeflateStream Optimal  (COMPORTAMIENTO ACTUAL)
            Max = 2         ' cabecera 0x78 0xDA ; DeflateStream Optimal (sesgo a mejor compresión)
        End Enum
        ' ====== Formatos de compresión admitidos por BA2 ======
        Public Enum CompressionFormat
            Zip = 0   ' zlib/deflate (FO4, FO76, SF v2)
            Lz4 = 3   ' LZ4 raw block (solo BA2 v3)
        End Enum
        ' ====== Hashing BA2 exacto ======
        Private Shared ReadOnly Crc32Table As UInteger() = BuildCrc32Table()
        Friend Shared Sub Fo4SplitPath(norm As String, ByRef parent As String, ByRef stem As String, ByRef extNoDot As String)
            ' 1) Normalizar y recortar separadores extremos
            Dim relSlash As String = PathUtil.NormalizeSlash(norm).Trim(Correct_Path_separator, InCorrect_Path_separator)
            ' 2) Dividir en dir / archivo
            Dim dir As String = "", fileOnly As String = ""
            PathUtil.SplitDirFile(relSlash, dir, fileOnly)
            ' 3) Parent con separador FO4 (Correct_Path_separator)
            parent = dir.Replace(InCorrect_Path_separator, Correct_Path_separator)
            ' 4) stem y ext (sin el punto)
            stem = System.IO.Path.GetFileNameWithoutExtension(fileOnly)
            Dim ext As String = System.IO.Path.GetExtension(fileOnly)
            If Not String.IsNullOrEmpty(ext) AndAlso ext.StartsWith("."c) Then
                extNoDot = ext.Substring(1)
            Else
                extNoDot = ""
            End If
        End Sub

        Friend Shared Function Crc32Ascii(text As String) As UInteger
            Return Crc32Bytes(Encoding.ASCII.GetBytes(text))
        End Function

        ''' <summary>
        ''' Hash FO4 de name/dir para los records del BA2. El engine busca cada archivo DENTRO del
        ''' BA2 hasheando el path pedido y comparándolo contra HashFile/HashDir del record — así que
        ''' DEBE usar exactamente este algoritmo, o todo lookup falla y el archive no aporta nada
        ''' (síntoma: material y textura no cargan, sólo loose anda). Es CRC32 con la tabla zlib
        ''' (poly 0xEDB88320) pero con init = 0 y SIN complemento final, sobre los bytes ASCII en
        ''' minúscula. NO es el CRC32 zip estándar (init 0xFFFFFFFF + Not crc) que hace Crc32Bytes
        ''' para el diff de payload. Verificado 5/5 contra vanilla (barnacle/default/keys/materials/
        ''' gasolinecanister) y dirs anidados de Creation Club.
        ''' <para>⛔ ES <b>Public</b> A PROPOSITO, y es la unica de este archivo que lo es. El mismo
        ''' trio (hash del nombre, extension, hash de la carpeta) con el que un BA2 indexa es lo que
        ''' escribe el bloque de Model Information de un record — o sea que esta funcion es una LEY
        ''' COMPARTIDA entre el empaquetador de archives y el escritor de plugins. La alternativa era
        ''' copiarla en `FO4_Base_Library`, y dos copias de la funcion que decide si una entrada
        ''' apunta al archivo correcto es exactamente como se separan. No expone estado ni nada del
        ''' writer: es pura sobre un string.</para>
        ''' </summary>
        Public Shared Function Fo4PathHash(text As String) As UInteger
            ' ⛔ NO `Encoding.ASCII`: reemplaza TODO byte > 127 por `?` (0x3F), así que un nombre con
            ' acento se hashea como si fuera otro nombre — mientras la TABLA DE NOMBRES del archive se
            ' escribe en UTF-8 (`Public Property Encoding As Encoding = Encoding.UTF8`). El canon hashea
            ' sobre bytes ANSI; Latin1 los reproduce byte a byte en el rango que importa y, a diferencia
            ' de ASCII, no destruye nada.
            ' LATENTE, medido: 0 de 585.791 nombres del corpus tienen un byte alto ⇒ CERO bytes de
            ' diferencia hoy. Alcanza UN archivo suelto con acento para dispararlo.
            Dim bytes = Encoding.Latin1.GetBytes(text.ToLowerInvariant())
            Dim crc As UInteger = 0UI
            For Each bt In bytes
                Dim idx = (crc Xor bt) And &HFFUI
                crc = (crc >> 8) Xor Crc32Table(CInt(idx))
            Next
            Return crc
        End Function

        ''' <summary>Standard zip CRC32 (poly 0xEDB88320) over arbitrary bytes. Public for external
        ''' use (e.g. ArchivePackager diff: hash a VirtualEntry.Data once, compare against CRC32 of
        ''' an existing entry's decompressed payload).</summary>
        Public Shared Function Crc32Bytes(bytes As Byte()) As UInteger
            Dim crc As UInteger = &HFFFFFFFFUI
            If bytes Is Nothing Then Return Not crc
            For Each bt In bytes
                Dim idx = (crc Xor bt) And &HFFUI
                crc = (crc >> 8) Xor Crc32Table(CInt(idx))
            Next
            Return Not crc
        End Function

        ''' <summary>
        ''' Seeks <paramref name="source"/> to <paramref name="offset"/> and copies exactly
        ''' <paramref name="count"/> bytes into <paramref name="dest"/> using a 64 KB reusable
        ''' buffer. Throws EndOfStreamException on short reads. Used by writers when a
        ''' VirtualEntry.PayloadSource is set, so the chunk is moved between archives without
        ''' allocating a managed buffer for the whole payload.
        ''' </summary>
        Friend Shared Sub StreamCopyExact(source As IO.Stream, offset As Long, count As Long, dest As IO.Stream)
            If count <= 0 Then Return
            source.Position = offset
            Dim buf(65535) As Byte
            Dim remaining As Long = count
            While remaining > 0
                Dim toRead As Integer = CInt(Math.Min(CLng(buf.Length), remaining))
                Dim n = source.Read(buf, 0, toRead)
                If n <= 0 Then Throw New IO.EndOfStreamException($"StreamCopyExact: short read at offset {source.Position} (need {remaining} more).")
                dest.Write(buf, 0, n)
                remaining -= n
            End While
        End Sub

        Friend Shared Function PackExt(ext As String) As UInteger
            ext = ext.ToLowerInvariant()
            Dim b0 As Byte = 0, b1 As Byte = 0, b2 As Byte = 0, b3 As Byte = 0
            If ext.Length > 0 Then b0 = CByte(AscW(ext(0)) And &HFF)
            If ext.Length > 1 Then b1 = CByte(AscW(ext(1)) And &HFF)
            If ext.Length > 2 Then b2 = CByte(AscW(ext(2)) And &HFF)
            If ext.Length > 3 Then b3 = CByte(AscW(ext(3)) And &HFF)
            Return CUInt(b0 Or (CUInt(b1) << 8) Or (CUInt(b2) << 16) Or (CUInt(b3) << 24))
        End Function
        Friend Shared Function BuildCrc32Table() As UInteger()
            Const poly As UInteger = &HEDB88320UI
            Dim t(255) As UInteger
            For i = 0 To 255
                Dim c As UInteger = CUInt(i)
                For j = 0 To 7
                    If (c And 1UI) <> 0UI Then
                        c = poly Xor (c >> 1)
                    Else
                        c >>= 1
                    End If
                Next
                t(i) = c
            Next
            Return t
        End Function

        ' ====== helpers ======
        Friend Shared Sub WriteAscii(s As Stream, txt As String)
            Dim b = Encoding.ASCII.GetBytes(txt)
            s.Write(b, 0, b.Length)
        End Sub
        Friend Shared Sub WriteU16(s As Stream, v As UShort)
            Dim b = BitConverter.GetBytes(v) : s.Write(b, 0, 2)
        End Sub
        Friend Shared Sub WriteU32(s As Stream, v As UInteger)
            Dim b = BitConverter.GetBytes(v) : s.Write(b, 0, 4)
        End Sub
        Friend Shared Sub WriteU64(s As Stream, v As ULong)
            Dim b = BitConverter.GetBytes(v) : s.Write(b, 0, 8)
        End Sub

        ' LZ4 RAW (bloque "naked", sin frame) compatible con BA2 v3
        Friend Shared Function CompressLz4(data As Byte()) As Byte()
            If data Is Nothing OrElse data.Length = 0 Then Return Array.Empty(Of Byte)()
            Dim srcLen As Integer = data.Length
            Dim maxOut As Integer = LZ4Codec.MaximumOutputSize(srcLen)
            Dim dst As Byte() = New Byte(maxOut - 1) {}
            ' Nota: usar la sobrecarga sin 'level' para máxima compatibilidad entre versiones del paquete.
            Dim written As Integer = LZ4Codec.Encode(data, 0, srcLen, dst, 0, dst.Length)
            If written <= 0 Then Return Array.Empty(Of Byte)()
            If written = dst.Length Then Return dst
            ReDim Preserve dst(written - 1)
            Return dst
        End Function
        Friend Shared Function CompressLz4Frame(data As Byte()) As Byte()
            If data Is Nothing OrElse data.Length = 0 Then Return Array.Empty(Of Byte)()
            Using ms As New MemoryStream()
                Using lz As Stream = LZ4Stream.Encode(ms, New LZ4EncoderSettings() With {.CompressionLevel = K4os.Compression.LZ4.LZ4Level.L12_MAX}, leaveOpen:=True)
                    lz.Write(data, 0, data.Length)
                End Using
                Return ms.ToArray()
            End Using
        End Function


        ' ZLIB wrapper sobre DEFLATE raw de .NET
        ' Compat: overload sin preset (mantiene el comportamiento actual).
        Friend Shared Function CompressZlib(data As Byte()) As Byte()
            Return CompressZlib(data, ZlibPreset.Default)
        End Function

        ' ZLIB con preset (CMF/FLG típicos) y Adler-32 del original
        Friend Shared Function CompressZlib(data As Byte(), preset As ZlibPreset) As Byte()
            If data Is Nothing OrElse data.Length = 0 Then Return Array.Empty(Of Byte)()

            ' ⛔ UN SOLO BUFFER PARA TODO EL ZLIB. Escribir a dos MemoryStream sin capacidad (uno
            ' para el deflate, otro para agregarle cabecera+cola) hace CUATRO pasadas del tamaño
            ' completo: cada `New MemoryStream()` sin capacidad duplica al crecer, y cada
            ' `.ToArray()` copia entero — dos fuentes de basura de LOH por descarte. Acá el deflate
            ' se escribe DIRECTO adentro del buffer final, después de los 2 bytes de cabecera, y la
            ' cola del Adler se agrega al final: un solo array, sin copias intermedias. Los bytes de
            ' salida son los mismos — es el mismo stream zlib.
            Dim cmfPre As Byte = &H78
            Dim flgPre As Byte
            Select Case preset
                Case ZlibPreset.Fast : flgPre = &H1
                Case ZlibPreset.Max : flgPre = &HDA
                Case Else : flgPre = &H9C
            End Select

            ' Capacidad inicial: el comprimido casi nunca supera al original, y si lo supera el
            ' MemoryStream crece solo. Arrancar en el tamaño del origen evita las duplicaciones del caso
            ' normal sin castigar el patologico.
            Using outMs As New MemoryStream(data.Length + 16)
                outMs.WriteByte(cmfPre)
                outMs.WriteByte(flgPre)

                Dim level As IO.Compression.CompressionLevel
                Select Case preset
                    Case ZlibPreset.Fast
                        level = Compression.CompressionLevel.Fastest
                    Case ZlibPreset.Max
                        level = Compression.CompressionLevel.SmallestSize
                    Case Else
                        level = Compression.CompressionLevel.Optimal
                End Select

                Using ds As New IO.Compression.DeflateStream(outMs, level, True)
                    ds.Write(data, 0, data.Length)
                End Using

                ' Adler-32 del ORIGINAL (no del comprimido), big-endian, como cola del stream zlib.
                Dim adler As UInteger = Adler32(data)
                outMs.WriteByte(CByte((adler >> 24) And &HFFUI))
                outMs.WriteByte(CByte((adler >> 16) And &HFFUI))
                outMs.WriteByte(CByte((adler >> 8) And &HFFUI))
                outMs.WriteByte(CByte(adler And &HFFUI))
                Return outMs.ToArray()
            End Using
        End Function

        Private Shared Function Adler32(data As Byte()) As UInteger
            Const MOD_ADLER As UInteger = 65521UI
            ' ⛔ El `Mod` va por BLOQUE, no por byte: hacerlo por byte son DOS divisiones por byte —
            ' 44 millones de divisiones para una textura de 22 MB. 5552 es el máximo de iteraciones
            ' que `b` aguanta sin desbordar 32 bits partiendo de a<65521 y bytes de 255 — es la
            ' constante clásica de zlib (NMAX), no un número elegido a ojo. El resultado es el mismo.
            Dim a As UInteger = 1UI
            Dim b As UInteger = 0UI
            Const NMAX As Integer = 5552
            Dim i As Integer = 0
            While i < data.Length
                Dim n = Math.Min(NMAX, data.Length - i)
                Dim fin = i + n - 1
                While i <= fin
                    a += data(i)
                    b += a
                    i += 1
                End While
                a = a Mod MOD_ADLER
                b = b Mod MOD_ADLER
            End While
            Return (b << 16) Or a
        End Function
        ' ====== Helper para escribir el "compression_format" v3 ======
        Friend Shared Sub WriteCompressionFormatV3(s As Stream, fmt As CompressionFormat)
            ' BA2 v3: 0 = zip, 3 = lz4
            Dim code As UInteger = If(fmt = CompressionFormat.Lz4, 3UI, 0UI)
            WriteU32(s, code)
        End Sub

        ' ====== Validación de versión/compresión compartida (GNRL + DX10) ======
        ''' <summary>
        ''' Valida Version (1/2/3/7/8) y la regla "LZ4 solo en v3", idéntica para GNRL y DX10.
        ''' No escribe ningún byte. <paramref name="typeTag"/> ("GNRL"/"DX10") se usa solo para
        ''' construir el mensaje de excepción con el mismo texto que tenía cada writer.
        ''' </summary>
        Friend Shared Sub ValidateVersionAndCompression(version As UInteger, compressionFormat As CompressionFormat, typeTag As String)
            If version <> 1UI AndAlso version <> 2UI AndAlso version <> 3UI AndAlso version <> 7UI AndAlso version <> 8UI Then
                Throw New InvalidDataException(typeTag & " admite Version=1,2,3,7,8.")
            End If
            If compressionFormat = CompressionFormat.Lz4 AndAlso version <> 3UI Then
                Throw New InvalidDataException("LZ4 solo válido con BA2 Version=3.")
            End If
        End Sub

        ' ====== Cabecera BA2 compartida (preamble v1/v2/v3/v7/v8) ======
        ''' <summary>
        ''' Escribe el preámbulo de cabecera BA2 común a GNRL y DX10 y devuelve por
        ''' <paramref name="nameTableOffsetPatchPos"/> la posición del u64 NameTableOffset que el
        ''' writer debe parchear al final. Secuencia EXACTA de bytes:
        '''   [BTDX][u32 version][btdxType 4 bytes][u32 fileCount][u64 NameTableOffset=0 (patch)]
        '''   (v2/v3) [u64 1]   (v3) [u32 compression_format]
        ''' El orden, tipos, tamaños y endianness son los mismos que tenían los dos writers inline.
        ''' </summary>
        Friend Shared Sub WriteBa2Header(output As Stream,
                                         btdxType As String,
                                         fileCount As UInteger,
                                         version As UInteger,
                                         compressionFormat As CompressionFormat,
                                         ByRef nameTableOffsetPatchPos As Long)
            WriteAscii(output, "BTDX")
            WriteU32(output, version)
            WriteAscii(output, btdxType)
            WriteU32(output, fileCount)
            nameTableOffsetPatchPos = output.Position
            WriteU64(output, 0UL)                 ' NameTableOffset (patch)
            ' v2/v3: campo extra u64(1)
            If version = 2UI OrElse version = 3UI Then
                WriteU64(output, 1UL)
            End If
            ' v3: u32 compression_format (0=zip, 3=lz4)
            If version = 3UI Then
                WriteCompressionFormatV3(output, compressionFormat)
            End If
        End Sub

        ' ====== Cola: tabla de nombres / string table (UTF-8, u16 length) ======
        ''' <summary>
        ''' Escribe la string table opcional (idéntica en GNRL y DX10) y parchea el u64
        ''' NameTableOffset en <paramref name="nameTableOffsetPatchPos"/>. Cada nombre se emite como
        ''' [u16 longitud][bytes UTF-8 (enc)] usando la ruta completa Directory\FileName ya
        ''' normalizada. Cuando <paramref name="includeStrings"/> es False no escribe nada y deja el
        ''' NameTableOffset en 0 (tal como lo escribió WriteBa2Header). Las posiciones se restauran
        ''' al final para no alterar el cursor de salida.
        ''' </summary>
        Friend Shared Sub WriteStringTable(output As Stream,
                                           includeStrings As Boolean,
                                           enc As Encoding,
                                           names As IEnumerable(Of String),
                                           nameTableOffsetPatchPos As Long)
            If Not includeStrings Then
                ' Omitir string table → mantener NameTableOffset=0 (ya escrito) y no emitir nombres.
                Return
            End If
            Dim nameTableOffset As ULong = CULng(output.Position)
            For Each full In names
                Dim nb = enc.GetBytes(full)
                If nb.Length > UShort.MaxValue Then Throw New InvalidDataException("Nombre demasiado largo para string table (u16).")
                WriteU16(output, CUShort(nb.Length))
                output.Write(nb, 0, nb.Length)
            Next
            Dim savePos As Long = output.Position
            output.Position = nameTableOffsetPatchPos
            WriteU64(output, nameTableOffset)
            output.Position = savePos
        End Sub

    End Class

    ''' <summary>
    ''' Escritor de BA2 (Fallout 4) DX10 (texturas).
    ''' Estructura v3 (compression=3=LZ4).
    ''' </summary>
    ''' 
    Public NotInheritable Class Ba2WriterDX10
        Public Shared Event Writed()

        Public NotInheritable Class Options
            Public Property Encoding As Encoding = Encoding.UTF8
            ' FO4 admite v1, v7 y v8; default NG:
            Public Property Version As UInteger = 8UI
            Public Property IncludeStrings As Boolean = True
            Public Property ZlibPreset As Ba2WriterCommon.ZlibPreset = Ba2WriterCommon.ZlibPreset.Default
            ' Sólo tiene efecto con Version=3 (LZ4 raw). En el resto de las versiones DX10
            ' siempre comprime con ZLIB sin importar este valor.
            Public Property CompressionFormat As Ba2WriterCommon.CompressionFormat = Ba2WriterCommon.CompressionFormat.Zip

        End Class

        ''' <summary>
        ''' Construye el BA2 (DX10) completo en memoria.
        ''' </summary>
        Public Shared Function Build(entries As IEnumerable(Of VirtualEntry), opts As Options) As Byte()
            Using ms As New MemoryStream()
                Write(ms, entries, opts)
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>
        ''' Escribe un BA2 DX10 (FO4 v1/v7/v8) en el stream de salida.
        ''' Cada VirtualEntry.Data debe ser el payload DDS SIN cabecera (ver Dx10Importer.FromDdsBytes);
        ''' pasar el DDS completo (con magic "DDS ") lanza InvalidDataException.
        ''' Compresión: ZLIB (RFC1950). Si comp >= raw => store (CompressedSize = 0).
        ''' </summary>
        Public Shared Sub Write(output As Stream, entries As IEnumerable(Of VirtualEntry), opts As Options)
            If output Is Nothing OrElse Not output.CanWrite Then Throw New ArgumentException("Stream inválido.")
            ArgumentNullException.ThrowIfNull(entries)
            If opts Is Nothing Then opts = New Options()

            ' Validación versión/compresión (DX10 soporta v1,7,8 y también v2/v3 según C++; LZ4 solo v3).
            Ba2WriterCommon.ValidateVersionAndCompression(opts.Version, opts.CompressionFormat, "DX10")

            Dim enc = If(opts.Encoding, Encoding.UTF8)
            Dim list = entries.ToList()
            If list.Count = 0 Then Throw New ArgumentException("No hay entradas.")

            ' Normalizar rutas lógicas
            For Each ve In list
                ve.Directory = PathUtil.NormalizeSlash(ve.Directory)
            Next

            ' ====== Preparar metadata + payloads ======
            Dim perFile = New List(Of FileMeta)(list.Count)
            Dim iFile As Integer = 0
            For Each ve In list
                Dim raw As Byte()

                If ve.PreCompressed Then
                    raw = Array.Empty(Of Byte)()      ' no se usa cuando hay pass-through
                Else
                    raw = If(ve.Data, Array.Empty(Of Byte)())
                    ' Contract: VirtualEntry.Data for BA2 DX10 must be the stripped DDS payload
                    ' (mip data only, no header). Use Dx10Importer.FromDdsBytes() or
                    ' Dx10Importer.SplitDdsBytes() to produce a correct VirtualEntry.
                    If Dx10Importer.HasDdsMagic(raw) Then
                        Throw New InvalidDataException(
                            "BA2 DX10: VirtualEntry.Data must be the stripped DDS payload, not the full DDS file. " &
                            "Use Dx10Importer.FromDdsBytes() or Dx10Importer.SplitDdsBytes() before passing.")
                    End If
                End If

                ' Validate DX10 metadata (always required, regardless of PreCompressed). The caller
                ' is responsible for populating these fields from the source DDS header.
                If ve.Width <= 0 OrElse ve.Height <= 0 Then Throw New InvalidDataException("DX10: Width/Height inválidos.")
                If ve.MipCount <= 0 Then Throw New InvalidDataException("DX10: MipCount inválido.")
                If ve.Faces <= 0 Then Throw New InvalidDataException("DX10: Faces inválido.")
                If ve.DxgiFormat < 0 OrElse ve.DxgiFormat > 255 Then Throw New InvalidDataException("DX10: DxgiFormat inválido (0..255).")
                ' No hay "faces" que derivar acá: la entrada DX10 del BA2 no tiene campo de tamaño de
                ' array. Sólo lleva `Dx10_Flags` (1 = cubemap, ver abajo) más MipCount, formato y
                ' TileMode — un Texture2DArray de N elementos no es representable en este formato;
                ' es un límite del contenedor, no algo que corregir.

                ' Hashes
                Dim relNorm As String = PathUtil.JoinDirFile(ve.Directory, ve.FileName)
                Dim norm = PathUtil.NormalizeSlash(relNorm)
                Dim parent As String = "", stem As String = "", extNoDot As String = ""
                Ba2WriterCommon.Fo4SplitPath(norm, parent, stem, extNoDot)

                ' Header DX10 por archivo.
                ' BA2-006 — single-chunk-cubre-todos-los-mips (supuesto del modelo single-chunk):
                '   este writer emite SIEMPRE 1 chunk que cubre los mips 0..MipCount-1, así que
                '   Chunk_MipFirst=0 / Chunk_MipLast=MipCount-1 (abajo) es correcto para los tres
                '   sub-paths: fresh-compress arma el chunk desde el DDS completo; pass-through y
                '   PreCompressed sólo se alimentan de archivos que ESTA librería escribió →
                '   single-chunk-all-mips (ver BA2-014). VirtualEntry NO transporta el rango de mips
                '   del chunk fuente, así que un entry DX10 single-chunk EXTERNO cuyo chunk cubriera un
                '   SUB-RANGO (p.ej. mips 0..K con K<MipCount-1, patrón de streaming) sería re-etiquetado
                '   aquí como 0..MipCount-1 (corrupción del rango). Hoy ese path es inalcanzable (BA2-014:
                '   no se ingieren archivos externos multi/sub-chunk). Si en el futuro se ingieren, hay que
                '   propagar Chunk_MipFirst/MipLast del chunk fuente por VirtualEntry ANTES de confiar en
                '   el pass-through; no extender el modelo ahora (caso no alcanzable).
                Dim fm As New FileMeta With {
                    .Index = iFile,
                    .HashFile = Ba2WriterCommon.Fo4PathHash(stem),
                    .HashDir = Ba2WriterCommon.Fo4PathHash(parent),
                    .HashExt = Ba2WriterCommon.PackExt(extNoDot),
                    .ModIndex = 0,
                    .ChunkCount = 1,
                    .ChunkHeaderSize = CUShort(24), ' DX10: 24 bytes (incluye sentinel)
                    .Dx10_Width = CUShort(ve.Width),
                    .Dx10_Height = CUShort(ve.Height),
                    .Dx10_MipCount = CByte(ve.MipCount),
                    .Dx10_DxgiFormatU8 = CByte(ve.DxgiFormat And &HFF),
                    .Dx10_Flags = If(ve.IsCubemap, CByte(1), CByte(0)),
                    .Dx10_TileMode = CByte(8), ' como en el C++
                    .Chunk_MipFirst = 0US,
                    .Chunk_MipLast = CUShort(ve.MipCount - 1)
                }

                If ve.PayloadSource IsNot Nothing Then
                    ' Stream-copy from another archive — same Version + CompressionFormat assumed.
                    Dim ps = ve.PayloadSource
                    fm.Chunk_DecompSize = CUInt(ps.DecompSize)
                    fm.Chunk_CompSize = If(ps.IsCompressed, CUInt(ps.Length), 0UI)
                    fm.Chunk_CompData = Nothing
                    fm.Chunk_PayloadSrc = ps
                ElseIf ve.PreCompressed Then
                    ' Pass-through: caller supplies chunk bytes already formatted for this archive's
                    ' Version/CompressionFormat. Sizes come from the source chunk header verbatim.
                    fm.Chunk_DecompSize = ve.PreCompressedDecompSize
                    fm.Chunk_CompSize = ve.PreCompressedCompSize
                    fm.Chunk_CompData = If(ve.PreCompressedBytes, Array.Empty(Of Byte)())
                Else
                    ' raw is the stripped DDS payload (mips concatenated). Compress directly.
                    ' (raw.Length is Integer, so it can never exceed Integer.MaxValue — no size guard needed here.)
                    fm.Chunk_DecompSize = CUInt(raw.Length)

                    ' ZLIB bien formado (CMF/FLG + Adler-32). Si no mejora => store.
                    ' Compresión según Version/CompressionFormat:
                    ' - v3 + LZ4 => LZ4 raw
                    ' - resto => ZLIB
                    Dim comp As Byte()
                    If opts.Version = 3UI AndAlso opts.CompressionFormat = Ba2WriterCommon.CompressionFormat.Lz4 Then
                        comp = Ba2WriterCommon.CompressLz4(raw)
                    Else
                        comp = Ba2WriterCommon.CompressZlib(raw, opts.ZlibPreset)
                    End If
                    Dim useComp As Boolean = comp IsNot Nothing AndAlso comp.Length > 0 AndAlso comp.Length < raw.Length
                    fm.Chunk_CompSize = CUInt(If(useComp, comp.Length, 0))
                    fm.Chunk_CompData = If(useComp, comp, raw)
                End If

                fm.FileName = ve.FileName
                fm.Directory = ve.Directory

                perFile.Add(fm)
                iFile += 1
                RaiseEvent Writed()
            Next

            ' ====== Header BA2 (v1/v2/v3/v7/v8) ======
            ' Preámbulo compartido GNRL/DX10. Solo cambia el tag de tipo ("DX10") y el conteo.
            Dim posStringTableOffset As Long
            Ba2WriterCommon.WriteBa2Header(output, "DX10", CUInt(perFile.Count), CUInt(opts.Version), opts.CompressionFormat, posStringTableOffset)

            ' ====== File headers + chunk entries (DX10) ======
            Dim patchOffsetPositions As New List(Of Long)(perFile.Count)
            For Each fm In perFile
                ' 1) Hashes
                Ba2WriterCommon.WriteU32(output, fm.HashFile)
                Ba2WriterCommon.WriteU32(output, fm.HashExt)
                Ba2WriterCommon.WriteU32(output, fm.HashDir)

                ' 2) Mini header por archivo
                output.WriteByte(fm.ModIndex)                     ' 0
                output.WriteByte(fm.ChunkCount)                   ' 1
                Ba2WriterCommon.WriteU16(output, fm.ChunkHeaderSize) ' 24 (DX10)

                ' header_t DX10 (orden FO4: h, w, mip_count, format, flags, tile_mode)
                Ba2WriterCommon.WriteU16(output, fm.Dx10_Height)
                Ba2WriterCommon.WriteU16(output, fm.Dx10_Width)
                output.WriteByte(fm.Dx10_MipCount)
                output.WriteByte(fm.Dx10_DxgiFormatU8)
                output.WriteByte(fm.Dx10_Flags)
                output.WriteByte(fm.Dx10_TileMode)

                ' 3) Chunk header (24 bytes, sentinel incluido)
                patchOffsetPositions.Add(output.Position)         ' apunta al u64 Offset
                Ba2WriterCommon.WriteU64(output, 0UL)             ' Offset absoluto (patch)
                Ba2WriterCommon.WriteU32(output, fm.Chunk_CompSize) ' 0 si store
                Ba2WriterCommon.WriteU32(output, fm.Chunk_DecompSize)
                Ba2WriterCommon.WriteU16(output, fm.Chunk_MipFirst)
                Ba2WriterCommon.WriteU16(output, fm.Chunk_MipLast)
                Ba2WriterCommon.WriteU32(output, &HBAADF00DUI)    ' sentinel dentro de los 24
            Next

            ' ====== Data chunks + parcheo offsets ======
            For i = 0 To perFile.Count - 1
                Dim fm = perFile(i)
                Dim off As ULong = CULng(output.Position)

                ' Patch del offset absoluto
                Dim save = output.Position
                output.Position = patchOffsetPositions(i)
                Ba2WriterCommon.WriteU64(output, off)
                output.Position = save

                ' Escribir payload: PayloadSrc (stream-copy) > Chunk_CompData (in-memory).
                If fm.Chunk_PayloadSrc IsNot Nothing Then
                    Ba2WriterCommon.StreamCopyExact(fm.Chunk_PayloadSrc.SourceStream, fm.Chunk_PayloadSrc.Offset, fm.Chunk_PayloadSrc.Length, output)
                Else
                    Dim cd = fm.Chunk_CompData
                    If cd IsNot Nothing AndAlso cd.Length > 0 Then
                        output.Write(cd, 0, cd.Length)
                    End If
                ' ⛔ SOLTAR EL PAYLOAD APENAS SE ESCRIBIO. El writer comprime TODO en una primera pasada
                ' (acumulando en `perFile`/`metas`) y recien escribe en la segunda, asi que sin esto los
                ' bytes comprimidos de las N entradas viven a la vez hasta terminar el archivo. Empaquetando
                ' un bake de 2000 diffuse 4096² eso es un pico de decenas de GB — OOM garantizada en 8 GB.
                ' Soltarlo aca corta el pico a lo que ya se escribio; el arreglo de fondo es comprimir a
                ' temporales y usar PayloadSrc, que es el camino de stream-copy que esta justo arriba.
                ' ⚠️ EL ALIVIO ES PARCIAL, no total: esto suelta la referencia del `FileMeta`, y eso libera
                ' de verdad SOLO cuando el array lo produjo el compresor. En el camino `store` y cuando la
                ' entrada trae `PreCompressedBytes`, el array sigue siendo el del `VirtualEntry` del
                ' llamador y anular este campo no baja un byte — ahi el dueño de la memoria es quien armo
                ' la lista de entradas, no el writer.
                    fm.Chunk_CompData = Nothing
                End If
            Next

            ' ====== String table (UTF-8, u16 length) opcional ======
            ' Cola compartida GNRL/DX10: emite [u16 len][bytes] por nombre y parchea el NameTableOffset.
            ' Las rutas se proyectan en el MISMO orden de iteración (perFile) que tenía el bucle inline.
            Ba2WriterCommon.WriteStringTable(output, opts.IncludeStrings, enc,
                                             perFile.Select(Function(fm) PathUtil.JoinDirFile(fm.Directory, fm.FileName)),
                                             posStringTableOffset)

        End Sub

        ' ====== estructuras ======
        Private NotInheritable Class FileMeta
            Public Index As Integer
            Public HashFile As UInteger
            Public HashExt As UInteger
            Public HashDir As UInteger
            Public ModIndex As Byte
            Public ChunkCount As Byte
            Public ChunkHeaderSize As UShort
            Public Dx10_Width As UShort
            Public Dx10_Height As UShort
            Public Dx10_MipCount As Byte
            Public Dx10_DxgiFormatU8 As Byte
            Public Dx10_Flags As Byte
            Public Dx10_TileMode As Byte
            Public Chunk_CompSize As UInteger
            Public Chunk_DecompSize As UInteger
            Public Chunk_MipFirst As UShort
            Public Chunk_MipLast As UShort
            Public Chunk_CompData As Byte()                    ' bytes to emit when Chunk_PayloadSrc is null
            Public Chunk_PayloadSrc As PayloadStreamSource    ' stream-copy source (mutually exclusive with Chunk_CompData)
            Public FileName As String
            Public Directory As String
        End Class
    End Class

    Public NotInheritable Class Ba2WriterGNRL
        Public Shared Event Writed()

        Public NotInheritable Class Options
            Public Property Encoding As Encoding = Encoding.UTF8
            Public Property Version As UInteger = 8UI
            Public Property IncludeStrings As Boolean = True
            Public Property ZlibPreset As Ba2WriterCommon.ZlibPreset = Ba2WriterCommon.ZlibPreset.Default
            Public Property CompressionFormat As Ba2WriterCommon.CompressionFormat = Ba2WriterCommon.CompressionFormat.Zip

        End Class

        ''' <summary>Construye el BA2 GNRL en memoria.</summary>
        Public Shared Function Build(entries As IEnumerable(Of VirtualEntry), opts As Options) As Byte()
            Using ms As New MemoryStream()
                Write(ms, entries, opts)
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>
        ''' Escribe un BA2 GNRL v8 (FO4 NG) en <paramref name="output"/>.
        ''' Cada VirtualEntry usa .Directory/.FileName/.Data. (No requiere metadata DX10).
        ''' Compresión: zlib/Deflate. Si no comprime mejor, se guarda sin comprimir (CompSize=0).
        ''' </summary>
        Public Shared Sub Write(output As Stream, entries As IEnumerable(Of VirtualEntry), opts As Options)
            If output Is Nothing OrElse Not output.CanWrite Then Throw New ArgumentException("Stream inválido.", NameOf(output))
            ArgumentNullException.ThrowIfNull(entries)
            If opts Is Nothing Then opts = New Options()

            Dim enc = If(opts.Encoding, Encoding.UTF8)
            Dim list = entries.ToList()
            If list.Count = 0 Then Throw New InvalidDataException("No hay entradas (GNRL).")

            ' Normalizar paths lógicos
            For Each ve In list
                ve.Directory = NormalizeSlash(ve.Directory)
            Next

            ' ===== Preparar metadatos y compresión por archivo (zlib) =====
            Dim metas As New List(Of FileMeta)(list.Count)
            For i As Integer = 0 To list.Count - 1
                Dim ve = list(i)
                Dim rel = JoinDirFile(ve.Directory, ve.FileName)
                Dim norm = PathUtil.NormalizeSlash(rel)

                Dim parent As String = "", stem As String = "", extNoDot As String = ""
                Ba2WriterCommon.Fo4SplitPath(norm, parent, stem, extNoDot)

                ' Three pass-through / compress paths:
                '   1) PayloadSource: stream-copy from another archive, no managed payload buffer.
                '   2) PreCompressed (in-memory): caller supplies the chunk bytes already encoded.
                '   3) Default: compress ve.Data with the writer's configured codec.
                ' Paths 1 and 2 require the source archive to have used the same Version + format.
                Dim fm As FileMeta
                If ve.PayloadSource IsNot Nothing Then
                    Dim ps = ve.PayloadSource
                    fm = New FileMeta With {
                        .Index = i,
                        .HashFile = Ba2WriterCommon.Fo4PathHash(stem),
                        .HashExt = Ba2WriterCommon.PackExt(extNoDot),
                        .HashDir = Ba2WriterCommon.Fo4PathHash(parent),
                        .ModIndex = 0,
                        .ChunkCount = 1,
                        .ChunkHeaderSize = CUShort(16),
                        .CompSize = If(ps.IsCompressed, CUInt(ps.Length), 0UI),
                        .DecompSize = CUInt(ps.DecompSize),
                        .Directory = ve.Directory,
                        .FileName = ve.FileName,
                        .CompData = Nothing,
                        .PayloadSrc = ps
                    }
                ElseIf ve.PreCompressed Then
                    Dim pc As Byte() = If(ve.PreCompressedBytes, Array.Empty(Of Byte)())
                    fm = New FileMeta With {
                        .Index = i,
                        .HashFile = Ba2WriterCommon.Fo4PathHash(stem),
                        .HashExt = Ba2WriterCommon.PackExt(extNoDot),
                        .HashDir = Ba2WriterCommon.Fo4PathHash(parent),
                        .ModIndex = 0,
                        .ChunkCount = 1,
                        .ChunkHeaderSize = CUShort(16),
                        .CompSize = ve.PreCompressedCompSize,
                        .DecompSize = ve.PreCompressedDecompSize,
                        .Directory = ve.Directory,
                        .FileName = ve.FileName,
                        .CompData = pc
                    }
                Else
                    Dim raw As Byte() = If(ve.Data, Array.Empty(Of Byte)())
                    ' (raw.Length is Integer, so it can never exceed Integer.MaxValue — no size guard needed here.)

                    ' Compresión según Version/CompressionFormat:
                    ' - v3 + LZ4 => LZ4 raw
                    ' - resto => ZLIB
                    Dim comp As Byte()
                    If opts.Version = 3UI AndAlso opts.CompressionFormat = Ba2WriterCommon.CompressionFormat.Lz4 Then
                        comp = Ba2WriterCommon.CompressLz4(raw)
                    Else
                        comp = Ba2WriterCommon.CompressZlib(raw, opts.ZlibPreset)
                    End If

                    Dim useComp As Boolean = comp IsNot Nothing AndAlso comp.Length > 0 AndAlso comp.Length < raw.Length

                    fm = New FileMeta With {
                        .Index = i,
                        .HashFile = Ba2WriterCommon.Fo4PathHash(stem),
                        .HashExt = Ba2WriterCommon.PackExt(extNoDot),
                        .HashDir = Ba2WriterCommon.Fo4PathHash(parent),
                        .ModIndex = 0,
                        .ChunkCount = 1,
                        .ChunkHeaderSize = CUShort(16),              ' GNRL: 16 bytes; el sentinel se escribe aparte
                        .CompSize = CUInt(If(useComp, comp.Length, 0)),
                        .DecompSize = CUInt(raw.Length),
                        .Directory = ve.Directory,
                        .FileName = ve.FileName,
                        .CompData = If(useComp, comp, raw)           ' lo que realmente se escribirá en payload
                    }
                End If
                metas.Add(fm)
                RaiseEvent Writed()
            Next

            ' ===== Header BA2 (v1/v2/v3/v7/v8) =====
            Ba2WriterCommon.ValidateVersionAndCompression(opts.Version, opts.CompressionFormat, "GNRL")

            ' Preámbulo compartido GNRL/DX10. Solo cambia el tag de tipo ("GNRL") y el conteo.
            Dim posNameTable As Long
            Ba2WriterCommon.WriteBa2Header(output, "GNRL", CUInt(metas.Count), opts.Version, opts.CompressionFormat, posNameTable)

            ' ===== File headers + chunk headers =====
            Dim patchOffsets As New List(Of Long)(metas.Count)
            For Each fm In metas
                ' per-file header (hashes + mini header GNRL)
                Ba2WriterCommon.WriteU32(output, fm.HashFile)
                Ba2WriterCommon.WriteU32(output, fm.HashExt)
                Ba2WriterCommon.WriteU32(output, fm.HashDir)
                output.WriteByte(fm.ModIndex)                     ' 0
                output.WriteByte(fm.ChunkCount)                   ' 1
                Ba2WriterCommon.WriteU16(output, fm.ChunkHeaderSize)  ' 16

                ' chunk header base (16 bytes) + sentinel (4) inmediatamente después
                patchOffsets.Add(output.Position)
                Ba2WriterCommon.WriteU64(output, 0UL)             ' Offset absoluto (patch)
                Ba2WriterCommon.WriteU32(output, fm.CompSize)     ' CompressedSize (0 si sin comprimir)
                Ba2WriterCommon.WriteU32(output, fm.DecompSize)   ' DecompressedSize
                Ba2WriterCommon.WriteU32(output, &HBAADF00DUI)    ' Sentinel (no cuenta dentro de los 16)
            Next

            ' ===== Payloads + parcheo de offsets absolutos =====
            For i = 0 To metas.Count - 1
                Dim fm = metas(i)
                Dim absOff As ULong = CULng(output.Position)

                ' Parchear offset absoluto del chunk
                Dim save = output.Position
                output.Position = patchOffsets(i)
                Ba2WriterCommon.WriteU64(output, absOff)
                output.Position = save

                ' Escribir los datos reales: PayloadSrc (stream-copy) > CompData (in-memory bytes).
                If fm.PayloadSrc IsNot Nothing Then
                    Ba2WriterCommon.StreamCopyExact(fm.PayloadSrc.SourceStream, fm.PayloadSrc.Offset, fm.PayloadSrc.Length, output)
                ElseIf fm.CompData IsNot Nothing AndAlso fm.CompData.Length > 0 Then
                    output.Write(fm.CompData, 0, fm.CompData.Length)
                End If
                ' ⛔ SOLTAR EL PAYLOAD APENAS SE ESCRIBIO. El writer comprime TODO en una primera pasada
                ' (acumulando en `perFile`/`metas`) y recien escribe en la segunda, asi que sin esto los
                ' bytes comprimidos de las N entradas viven a la vez hasta terminar el archivo. Empaquetando
                ' un bake de 2000 diffuse 4096² eso es un pico de decenas de GB — OOM garantizada en 8 GB.
                ' Soltarlo aca corta el pico a lo que ya se escribio; el arreglo de fondo es comprimir a
                ' temporales y usar PayloadSrc, que es el camino de stream-copy que esta justo arriba.
                ' ⚠️ EL ALIVIO ES PARCIAL, no total: esto suelta la referencia del `FileMeta`, y eso libera
                ' de verdad SOLO cuando el array lo produjo el compresor. En el camino `store` y cuando la
                ' entrada trae `PreCompressedBytes`, el array sigue siendo el del `VirtualEntry` del
                ' llamador y anular este campo no baja un byte — ahi el dueño de la memoria es quien armo
                ' la lista de entradas, no el writer.
                fm.CompData = Nothing
            Next

            ' ===== Name table (UTF-8, u16 length) opcional =====
            ' Cola compartida GNRL/DX10: emite [u16 len][bytes] por nombre y parchea el NameTableOffset.
            ' Las rutas se proyectan en el MISMO orden de iteración (metas) que tenía el bucle inline.
            Ba2WriterCommon.WriteStringTable(output, opts.IncludeStrings, enc,
                                             metas.Select(Function(fm) PathUtil.JoinDirFile(fm.Directory, fm.FileName)),
                                             posNameTable)

        End Sub

        ' ===== estructuras internas =====
        Private NotInheritable Class FileMeta
            Public Index As Integer
            Public HashFile As UInteger
            Public HashExt As UInteger
            Public HashDir As UInteger
            Public ModIndex As Byte
            Public ChunkCount As Byte
            Public ChunkHeaderSize As UShort
            Public ChunkOffset As ULong         ' set during single-pass write; back-patched into the reserved file header block
            Public CompSize As UInteger
            Public DecompSize As UInteger
            Public Directory As String
            Public FileName As String
            Public CompData As Byte()           ' bytes to emit when PayloadSrc is null
            Public PayloadSrc As PayloadStreamSource ' stream-copy source (mutually exclusive with CompData)
        End Class


    End Class


End Namespace

