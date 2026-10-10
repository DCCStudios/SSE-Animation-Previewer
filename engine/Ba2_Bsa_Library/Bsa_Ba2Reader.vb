Option Strict On
Imports System.IO
Imports System.Runtime
Imports System.Text
Imports DirectXTexWrapperCLI ' Loader.EncodeDDSHeader(...)
Imports ICSharpCode.SharpZipLib.Zip.Compression
Imports K4os.Compression.LZ4.Streams

' ======== API pública ========
Namespace BethesdaArchive.Core

    ''' <summary>Techo del pre-alloc del MemoryStream de extraccion. Cota del FORMATO, no de la maquina:
    ''' ninguna textura de FO4/SSE se acerca (4096x4096 BC7 con mips ~22 MB). Por encima de esto el
    ''' `DecompressedSize` del header no es creible y se deja crecer el stream solo.</summary>
    Friend Module Ba2ReaderLimits
        Friend Const MaxPreAllocBytes As Long = 256L * 1024L * 1024L
        ''' <summary>Cota SUPERIOR DEMOSTRABLE de expansión de un bloque comprimido, en veces.
        ''' <para>NO es "un margen holgado": es el máximo del FORMATO. Un bloque deflate puede
        ''' expandirse a lo sumo 1032:1 (una secuencia de 258 bytes de match codificada en ~2 bits ⇒
        ''' 258/0,25). LZ4 tiene un tope menor (~255:1), así que 1032 los cubre a los dos.</para>
        ''' <para>⚠️ Un límite fijo tipo ×32 NO alcanza: un chunk de DDS uniforme (facetint plano,
        ''' _s negro, mip 1x1) comprime muchísimo más que 32:1 y ese techo apagaría el pre-alloc
        ''' justo en esos casos. El número correcto lo fija el formato, no se elige a ojo.</para></summary>
        Friend Const MaxBlockExpansion As Long = 1032L
    End Module
    Public Class BethesdaReader
        Implements IDisposable

        Public ReadOnly Property EntriesFiles As IReadOnlyList(Of ArchiveEntry)

        Private ReadOnly _impl As IArchiveImpl
        Private ReadOnly _entries As List(Of ArchiveEntry)

        ''' <summary>Abre BSA (SSE v105) o BA2 (FO4 GNRL/DX10).</summary>
        Public Sub New(fs As Stream, Optional encoding As Encoding = Nothing)
            If encoding Is Nothing Then encoding = Encoding.UTF8
            _impl = DetectAndOpen(fs, encoding)
            _entries = _impl.ListEntries()
            EntriesFiles = _entries.AsReadOnly()
        End Sub

        ''' <summary>a) Lista expuesta en .EntriesFiles; b) descomprime en memoria por índice.</summary>
        Public Function ExtractToMemory(index As Integer) As Byte()
            If index < 0 OrElse index >= _entries.Count Then Throw New ArgumentOutOfRangeException(NameOf(index))
            Return _impl.ExtractByIndex(index)
        End Function

        ''' <summary>
        ''' Returns the raw compressed payload of an entry without decompressing it. Intended for
        ''' stream-copy scenarios (e.g. WM Pack incremental rewrite reusing unchanged entries from
        ''' a previous archive). Throws NotSupportedException if the source entry has a layout the
        ''' caller cannot replay verbatim (e.g. multi-chunk BA2, non-default tile mode).
        ''' </summary>
        Public Function ExtractCompressedPayload(index As Integer) As RawCompressedEntry
            If index < 0 OrElse index >= _entries.Count Then Throw New ArgumentOutOfRangeException(NameOf(index))
            Return _impl.ExtractRawByIndex(index)
        End Function

        ''' <summary>
        ''' Builds a PayloadStreamSource pointing at this reader's underlying stream, so a writer
        ''' can stream-copy the entry's payload bytes without buffering them in a managed array.
        ''' For BA2 entries this is metadata-only (no I/O). For BSA compressed entries it reads
        ''' the 4-byte u32 decompSize prefix from disk (single tiny read per entry).
        ''' The returned source is valid only while THIS BethesdaReader's stream stays open and
        ''' un-seeked by other operations. The writer that consumes it is responsible for
        ''' sequencing reads (one entry at a time).
        ''' </summary>
        Public Function GetPayloadSource(index As Integer) As PayloadStreamSource
            If index < 0 OrElse index >= _entries.Count Then Throw New ArgumentOutOfRangeException(NameOf(index))
            Return _impl.GetPayloadSourceByIndex(index)
        End Function

        ''' <summary>BA2 header version (1/2/3/7/8) when this is a BA2 archive; Nothing for BSA.
        ''' The packager uses it to force a rewrite when the on-disk version differs from the
        ''' requested one even though the content is byte-identical.</summary>
        Public ReadOnly Property Ba2HeaderVersion As UInteger?
            Get
                Return _impl.Ba2HeaderVersion
            End Get
        End Property

        Private Shared Function DetectAndOpen(fs As Stream, enc As Encoding) As IArchiveImpl
            If Not fs.CanSeek Then Throw New ArgumentException("El Stream debe soportar Seek")
            Dim start = fs.Position
            ' BA2-016: single 4-byte magic read, then compare both ways — detection only, no bytes
            ' are written. BA2 "BTDX" = 42 54 44 58; BSA "BSA\0" = 42 53 41 00 (u32 LE 0x00415342).
            Using br As New BinaryReader(fs, enc, leaveOpen:=True)
                If fs.Length - fs.Position >= 4 Then
                    Dim magicBytes = br.ReadBytes(4)
                    fs.Position = start
                    If Encoding.ASCII.GetString(magicBytes) = "BTDX" Then Return New Ba2Impl(fs, enc)
                    If BitConverter.ToUInt32(magicBytes, 0) = &H415342UI Then Return New BsaImpl(fs, enc)
                End If
            End Using
            Throw New InvalidDataException("Formato no reconocido (BSA SSE o BA2 FO4).")
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _impl?.Dispose()
        End Sub
    End Class

    Public Class ArchiveEntry
        Public Property Index As Integer
        Public Property Directory As String
        Public Property FileName As String
        ' Total decompressed payload size, populated from data already parsed during Open() —
        ' no extra I/O. Semantics:
        '   - BA2 GNRL/DX10: sum of chunk DecompressedSize fields (always known).
        '   - BSA non-compressed: deduced from sizeField minus the BSTRING overhead.
        '   - BSA compressed: NOT KNOWN here (the per-entry u32 decompSize lives at the start
        '     of the payload, which Open() doesn't read). Reported as 0 in that case; callers
        '     that need an exact number must call ExtractCompressedPayload or ExtractToMemory.
        Public Property DecompressedSize As Long
        ' Stored record hashes as they exist on disk — consumed by ArchivePackager.ComputeDiff to
        ' detect archives written with a stale/wrong hash algorithm and force a rewrite even when the
        ' payloads are byte-identical. BA2: 32-bit FO4 name/dir hash (0 for BSA). BSA: 64-bit TES4
        ' file/folder hash (0 for BA2).
        Public Property Ba2NameHash As UInteger
        Public Property Ba2DirHash As UInteger
        Public Property BsaFileHash As ULong
        Public Property BsaDirHash As ULong
        Public ReadOnly Property FullPath As String
            Get
                If String.IsNullOrEmpty(Directory) Then Return FileName
                Return Directory.TrimEnd(Correct_Path_separator, InCorrect_Path_separator) & Correct_Path_separator & FileName
            End Get
        End Property
    End Class

    Friend Interface IArchiveImpl
        Inherits IDisposable
        Function ListEntries() As List(Of ArchiveEntry)
        Function ExtractByIndex(index As Integer) As Byte()
        Function ExtractRawByIndex(index As Integer) As RawCompressedEntry
        Function GetPayloadSourceByIndex(index As Integer) As PayloadStreamSource
        ''' <summary>BA2 header version (1/2/3/7/8) for BA2 archives; Nothing for BSA (whose v105
        ''' is a BSA version, not comparable to a BA2 version).</summary>
        ReadOnly Property Ba2HeaderVersion As UInteger?
    End Interface

    ' ============ DECOMPRESSERS =============
    Friend Module Lz4Strict
        ''' <summary>
        ''' Descomprime LZ4 de forma determinista.
        ''' - Si el buffer comienza con magic de FRAME (0x184D2204) o SKIPPABLE (0x184D2A50..5F), procesa como FRAME:
        '''   soporta múltiples frames concatenados y bloques skippables.
        ''' - En caso contrario, trata el buffer como RAW BLOCK.
        ''' En ambos modos exige que la salida tenga exactamente 'expected' bytes.
        ''' </summary>
        Friend Function Lz4DecompressStrict(packed As Byte(), expected As Integer) As Byte()
            If expected <= 0 Then
                Throw New InvalidDataException("LZ4: 'expected' debe ser > 0 para decodificación estricta.")
            End If
            If packed Is Nothing OrElse packed.Length = 0 Then
                Throw New InvalidDataException("LZ4: buffer vacío.")
            End If

            ' ---- Detección de FRAME / SKIPPABLE por magic ----
            Dim isFrame As Boolean = False
            Dim isSkippableStart As Boolean = False
            If packed.Length >= 4 Then
                Dim b0 = packed(0) : Dim b1 = packed(1) : Dim b2 = packed(2) : Dim b3 = packed(3)
                isFrame = (b0 = &H4 AndAlso b1 = &H22 AndAlso b2 = &H4D AndAlso b3 = &H18) ' 04-22-4D-18
                isSkippableStart = (b1 = &H2A AndAlso b2 = &H4D AndAlso b3 = &H18 AndAlso b0 >= &H50 AndAlso b0 <= &H5F) ' 5x-2A-4D-18
            End If

            If isFrame OrElse isSkippableStart Then
                ' --------- FRAME path (múltiples frames + skippables) ---------
                Using msIn As New MemoryStream(packed, writable:=False),
                  msOut As New MemoryStream(expected)

                    While msIn.Position < msIn.Length
                        If msIn.Length - msIn.Position < 4 Then Exit While

                        ' Peek 4 bytes
                        Dim savePos = msIn.Position
                        Dim h0 = msIn.ReadByte() : Dim h1 = msIn.ReadByte() : Dim h2 = msIn.ReadByte() : Dim h3 = msIn.ReadByte()
                        msIn.Position = savePos
                        If h0 < 0 OrElse h1 < 0 OrElse h2 < 0 OrElse h3 < 0 Then Exit While

                        Dim hdr0 = CByte(h0) : Dim hdr1 = CByte(h1) : Dim hdr2 = CByte(h2) : Dim hdr3 = CByte(h3)
                        Dim frame = (hdr0 = &H4 AndAlso hdr1 = &H22 AndAlso hdr2 = &H4D AndAlso hdr3 = &H18)
                        Dim skippable = (hdr1 = &H2A AndAlso hdr2 = &H4D AndAlso hdr3 = &H18 AndAlso hdr0 >= &H50 AndAlso hdr0 <= &H5F)

                        If skippable Then
                            ' magic + length (u32 LE) + payload skippable
                            msIn.Position += 4
                            If msIn.Length - msIn.Position < 4 Then Throw New InvalidDataException("LZ4 frame: skippable sin longitud.")
                            Dim len0 = msIn.ReadByte(), len1 = msIn.ReadByte(), len2 = msIn.ReadByte(), len3 = msIn.ReadByte()
                            If len0 < 0 OrElse len1 < 0 OrElse len2 < 0 OrElse len3 < 0 Then Throw New EndOfStreamException()
                            Dim skipLen As Integer = CInt(len0) Or (CInt(len1) << 8) Or (CInt(len2) << 16) Or (CInt(len3) << 24)
                            Dim newPos = msIn.Position + skipLen
                            If newPos > msIn.Length Then Throw New InvalidDataException("LZ4 frame: skippable excede el buffer.")
                            msIn.Position = newPos
                            Continue While
                        ElseIf frame Then
                            ' decodificar UN frame completo; el decoder avanza msIn hasta el fin del frame
                            Using dec As Stream = LZ4Stream.Decode(msIn, leaveOpen:=True)
                                dec.CopyTo(msOut)
                            End Using
                            Continue While
                        Else
                            ' bytes inesperados entre frames → error duro; no “probamos” nada
                            Throw New InvalidDataException("LZ4 frame: bytes inesperados entre frames/skippables.")
                        End If
                    End While

                    Dim outB = msOut.ToArray()
                    If outB.Length <> expected Then
                        Throw New InvalidDataException($"LZ4 frame: tamaño descomprimido {outB.Length} != esperado {expected}.")
                    End If
                    Return outB
                End Using
            Else
                ' --------- RAW BLOCK path (sin knownOutputLength) ---------
                Dim outBytes As Byte() = New Byte(expected - 1) {}
                Dim written As Integer = K4os.Compression.LZ4.LZ4Codec.Decode(packed, 0, packed.Length, outBytes, 0, expected)
                If written < 0 Then
                    Throw New InvalidDataException("LZ4 raw: error de decodificación.")
                End If
                If written <> expected Then
                    Throw New InvalidDataException($"LZ4 raw: tamaño descomprimido {written} != esperado {expected}.")
                End If
                Return outBytes
            End If
        End Function
    End Module
    Friend Module ZlibStrict

        ''' <summary>
        ''' Descomprime un stream ZLIB (RFC1950) de forma estricta:
        ''' - Verifica header CMF/FLG (CM=8 y (CMF<<8+FLG) mod 31 == 0).
        ''' - Descomprime con header y Adler-32.
        ''' - Exige que el output tenga exactamente 'expected' bytes (tamaño del chunk header).
        ''' </summary>
        Friend Function ZlibDecompressStrict(packed As Byte(), expected As Integer) As Byte()
            If expected <= 0 Then Throw New InvalidDataException("Zlib: 'expected' debe ser > 0.")
            If packed Is Nothing OrElse packed.Length = 0 Then Throw New InvalidDataException("Zlib: buffer vacío.")
            If packed.Length < 2 Then Throw New InvalidDataException("Zlib: buffer demasiado corto (faltan CMF/FLG).")

            ' Validación de cabecera ZLIB: CMF/FLG
            Dim cmf As Integer = packed(0)
            Dim flg As Integer = packed(1)
            If (cmf And &HF) <> 8 Then Throw New InvalidDataException("Zlib: CM != 8 (no es deflate).")
            If (((cmf << 8) + flg) Mod 31) <> 0 Then Throw New InvalidDataException("Zlib: header CMF/FLG inválido (mod31).")

            Dim inflater As New Inflater(noHeader:=False)
            inflater.SetInput(packed)

            Dim outBytes As Byte() = New Byte(expected - 1) {}
            Dim total As Integer = 0

            ' Inflar hasta alcanzar exactamente expected, o hasta quedar sin input útil
            While total < expected AndAlso Not inflater.IsNeedingInput
                Dim wrote = inflater.Inflate(outBytes, total, expected - total)
                If wrote = 0 Then Exit While ' no avanza más (input agotado o estado final)
                total += wrote
            End While

            If total <> expected Then
                Throw New InvalidDataException($"Zlib: tamaño descomprimido {total} != esperado {expected}.")
            End If
            ' NO chequear inflater.IsFinished acá. El chunk header del BA2 trae el tamaño descomprimido
            ' EXACTO, así que el truncamiento ya está cubierto arriba (total <> expected). El inflater se
            ' detiene a propósito en 'expected' bytes de OUTPUT, ANTES de consumir el trailer Adler-32 del
            ' zlib, por lo que IsFinished es legítimamente False para chunks FO4 válidos (p.ej. los
            ' behavior .hkx de Fallout4 - Animations.ba2, que con un "If Not IsFinished Then Throw"
            ' rechazan aunque sean válidos). El check de longitud es el guard correcto, no IsFinished.
            Return outBytes
        End Function

        ''' <summary>
        ''' Descomprime un stream DEFLATE RAW (RFC1951, sin header zlib) de forma estricta:
        ''' - No acepta header zlib.
        ''' - Exige output de tamaño exacto y fin de stream.
        ''' </summary>
        Friend Function DeflateRawDecompressStrict(packed As Byte(), expected As Integer) As Byte()
            If expected <= 0 Then Throw New InvalidDataException("DeflateRaw: 'expected' debe ser > 0.")
            If packed Is Nothing OrElse packed.Length = 0 Then Throw New InvalidDataException("DeflateRaw: buffer vacío.")

            ' Si detectás header zlib, consideralo un error aquí (es RAW)
            If packed.Length >= 2 Then
                Dim cmf As Integer = packed(0)
                Dim flg As Integer = packed(1)
                Dim looksZlib As Boolean = ((cmf And &HF) = 8) AndAlso ((((cmf << 8) + flg) Mod 31) = 0)
                If looksZlib Then Throw New InvalidDataException("DeflateRaw: se detectó header zlib (usar ZlibDecompressStrict).")
            End If

            Dim inflater As New Inflater(noHeader:=True)
            inflater.SetInput(packed)

            Dim outBytes As Byte() = New Byte(expected - 1) {}
            Dim total As Integer = 0
            While total < expected AndAlso Not inflater.IsFinished AndAlso Not inflater.IsNeedingInput
                total += inflater.Inflate(outBytes, total, expected - total)
            End While

            If total <> expected Then
                Throw New InvalidDataException($"DeflateRaw: tamaño descomprimido {total} != esperado {expected}.")
            End If
            If Not inflater.IsFinished Then
                Throw New InvalidDataException("DeflateRaw: flujo truncado (no alcanzó estado Finished).")
            End If
            If inflater.RemainingInput <> 0 Then
                Throw New InvalidDataException("DeflateRaw: quedaron bytes extra tras terminar (input no consumido).")
            End If

            Return outBytes
        End Function

    End Module


    ' ======== BSA (Skyrim SE v105) ========


    Friend Class BsaImpl
        Implements IArchiveImpl

        Private ReadOnly _fs As Stream
        Private ReadOnly _br As BinaryReader
        Private ReadOnly _enc As Encoding

        Private _hdr As BsaHeader
        Private _fileDataBase As Long
        Private ReadOnly _records As New List(Of FileRec)

        ' Flags
        Private Const ARCHIVE_COMPRESS As UInteger = &H4UI
        Private Const ARCHIVE_EMBED_NAMES As UInteger = &H100UI
        Private Shared ReadOnly ICOMPRESSION As UInteger = CUInt(1UI << 30)
        Private Shared ReadOnly ICHECKED As UInteger = CUInt(1UI << 31)

        Private Structure BsaHeader
            Public Magic As UInteger          ' "BSA\0"
            Public Version As UInteger        ' 105 (0x69)
            Public HeaderSize As UInteger     ' 0x24
            Public ArchiveFlags As UInteger
            Public FolderCount As UInteger
            Public FileCount As UInteger
            Public FolderNameLength As UInteger
            Public FileNameLength As UInteger
            Public FileFlags As UInteger
        End Structure

        Private Class FileRec
            Public Index As Integer
            Public Directory As String
            Public FileName As String
            Public Offset As UInteger         ' ABSOLUTO
            Public SizeField As UInteger      ' con bits 30/31
            Public Compressed As Boolean
            Public HasEmbeddedName As Boolean
            Public FileHash As ULong          ' hash TES4 de 64-bit del archivo (stored)
            Public DirHash As ULong           ' hash TES4 de 64-bit de la carpeta (stored)
        End Class

        Public Sub New(fs As Stream, enc As Encoding)
            _fs = fs : _enc = enc
            _br = New BinaryReader(_fs, _enc, leaveOpen:=True)
            Open()
        End Sub

        Private Shared Function ReadZString(br As BinaryReader, enc As Encoding) As String
            Dim tmp As New List(Of Byte)(64)
            While True
                Dim b = br.ReadByte()
                If b = 0 Then Exit While
                tmp.Add(b)
            End While
            Return enc.GetString(tmp.ToArray())
        End Function

        Private Shared Function ReadBZ(br As BinaryReader, enc As Encoding) As String
            Dim l = br.ReadByte() ' incluye NULL
            If l = 0 Then Return ""
            Dim raw = br.ReadBytes(l - 1)
            Dim _null = br.ReadByte()
            Return enc.GetString(raw)
        End Function

        Private Sub Open()
            ' --- Header ---
            _hdr.Magic = _br.ReadUInt32()
            If _hdr.Magic <> &H415342UI Then Throw New InvalidDataException("BSA: magic inválido")
            _hdr.Version = _br.ReadUInt32()
            If _hdr.Version <> &H69UI Then Throw New NotSupportedException("BSA: sólo SSE v105")
            _hdr.HeaderSize = _br.ReadUInt32()         ' 0x24
            _hdr.ArchiveFlags = _br.ReadUInt32()
            _hdr.FolderCount = _br.ReadUInt32()
            _hdr.FileCount = _br.ReadUInt32()
            _hdr.FolderNameLength = _br.ReadUInt32()
            _hdr.FileNameLength = _br.ReadUInt32()
            _hdr.FileFlags = _br.ReadUInt32()

            ' --- Offsets v105 ---
            Dim directoriesOffset As Long = _hdr.HeaderSize
            Dim dirEntrySize As Integer = &H18
            Dim filesOffset As Long = directoriesOffset + CLng(dirEntrySize) * CLng(_hdr.FolderCount)

            Dim hasDirStrings As Boolean = ((_hdr.ArchiveFlags And 1UI) <> 0UI)
            Dim hasFileStrings As Boolean = ((_hdr.ArchiveFlags And 2UI) <> 0UI)

            Dim dirStrSz As Long = If(hasDirStrings, CLng(_hdr.FolderNameLength) + CLng(_hdr.FolderCount), 0L)
            Dim fileStringsStart As Long = filesOffset + dirStrSz + CLng(_hdr.FileCount) * &H10
            _fileDataBase = fileStringsStart + CLng(_hdr.FileNameLength) ' (no se usa para calcular offsets de archivo)

            ' --- 1) Leer sólo los counts de carpetas ---
            _fs.Position = directoriesOffset
            Dim folderCounts As New List(Of UInteger)(CInt(_hdr.FolderCount))
            Dim folderHashes As New List(Of ULong)(CInt(_hdr.FolderCount))
            For i = 0UI To _hdr.FolderCount - 1UI
                Dim _hash = _br.ReadUInt64()
                Dim cnt = _br.ReadUInt32()
                folderCounts.Add(cnt)
                folderHashes.Add(_hash)
                _fs.Position += 12 ' resto del folder record v105 (0x18 total)
            Next

            ' --- 2) Recorrido intercalado: [Dir BZString] + [count * FileEntry] ---
            Dim filesCursor As Long = filesOffset
            Dim namesCursor As Long = fileStringsStart

            _records.Clear()
            _records.Capacity = CInt(_hdr.FileCount)
            Dim idx As Integer = 0
            Dim globCompressed As Boolean = ((_hdr.ArchiveFlags And ARCHIVE_COMPRESS) <> 0UI)

            For d = 0 To folderCounts.Count - 1
                ' 2.a) BZString de directorio (si existen nombres de carpeta)
                Dim dirName As String = ""
                If hasDirStrings Then
                    _fs.Position = filesCursor
                    dirName = PathUtil.NormalizeSlash(ReadBZ(_br, _enc))   ' avanza según longitud+1 (incluye null)
                    filesCursor = _fs.Position               ' avanzar cursor tras el BZString
                End If

                ' 2.b) 'count' file entries de esta carpeta
                For j = 0UI To folderCounts(d) - 1UI
                    _fs.Position = filesCursor

                    Dim fhash = _br.ReadUInt64()
                    Dim sizeField = _br.ReadUInt32()
                    Dim offAbs As UInteger = _br.ReadUInt32()  ' offset ABSOLUTO (v105)
                    filesCursor = _fs.Position

                    ' nombre de archivo desde la tabla de nombres (zstring)
                    Dim fileName As String = ""
                    If hasFileStrings Then
                        Dim ret = _fs.Position
                        _fs.Position = namesCursor
                        fileName = ReadZString(_br, _enc)
                        namesCursor = _fs.Position
                        _fs.Position = ret
                    End If

                    ' compresión efectiva = flag global XOR bit30
                    Dim comp As Boolean = globCompressed
                    If (sizeField And ICOMPRESSION) <> 0UI Then comp = Not comp
                    Dim embedNames As Boolean = ((_hdr.ArchiveFlags And ARCHIVE_EMBED_NAMES) <> 0UI)

                    ' al crear cada registro:
                    _records.Add(New FileRec With {
    .Index = idx, .Directory = dirName, .FileName = fileName,
    .Offset = offAbs, .SizeField = sizeField, .Compressed = comp,
    .HasEmbeddedName = embedNames,
    .FileHash = fhash, .DirHash = folderHashes(d)
})
                    idx += 1
                Next
            Next
        End Sub



        Public Function ListEntries() As List(Of ArchiveEntry) Implements IArchiveImpl.ListEntries
            Dim list As New List(Of ArchiveEntry)(_records.Count)
            For Each r In _records
                Dim decomp As Long = 0
                If Not r.Compressed Then
                    ' Non-compressed entry: payload size = sizeField (low 30 bits) minus the
                    ' embedded BSTRING (1 byte length + dir + '\' + filename) when applicable.
                    Dim sizeWork As Long = CLng(r.SizeField And &H3FFFFFFFUI)
                    Dim bstringOverhead As Long = 0
                    If r.HasEmbeddedName Then
                        Dim dirBytes As Long = If(r.Directory Is Nothing, 0L, CLng(_enc.GetByteCount(r.Directory.Trim(Correct_Path_separator, InCorrect_Path_separator))))
                        Dim fileBytes As Long = If(r.FileName Is Nothing, 0L, CLng(_enc.GetByteCount(r.FileName)))
                        bstringOverhead = 1 + dirBytes + 1 + fileBytes
                    End If
                    decomp = Math.Max(0L, sizeWork - bstringOverhead)
                End If
                ' For compressed entries the u32 decompSize is at the start of the payload and is
                ' NOT parsed here (would require per-entry I/O). Reporting 0 = unknown.
                list.Add(New ArchiveEntry With {
                    .Index = r.Index,
                    .Directory = r.Directory,
                    .FileName = r.FileName,
                    .DecompressedSize = decomp,
                    .BsaFileHash = r.FileHash,
                    .BsaDirHash = r.DirHash
                })
            Next
            Return list
        End Function

        Public Function ExtractByIndex(index As Integer) As Byte() Implements IArchiveImpl.ExtractByIndex
            Dim r = _records(index)

            ' 1) Posición física (offset absoluto con bit 31 limpio por posible “secondary archive”)
            ' ⛔ EL BIT 31 DEL OFFSET NO SE ENMASCARA. Acá se hacía `r.Offset And &H7FFFFFFFUI`, con el
            ' comentario "posible secondary archive". No existe tal cosa en el formato BSA: el offset es un
            ' u32 y el CANON lo trata entero — `wbBSArchive.pas:162` `BSA_MAX_OFFSET = High(Integer)`
            ' (2 GiB−1) y su validación (`:1235`) avisa *"N file(s) start above 2 GB max allowed BSA size,
            ' they won't work or crash the game"*, o sea que un offset con el bit 31 puesto describe un
            ' archivo ROTO, no una señal. Enmascararlo convertía ese archivo roto en uno que se lee sin
            ' error y devuelve OTROS BYTES.
            ' MEDIDO sobre un BSA de 2 GiB fabricado para probarlo: 0 de 227 entradas correctas, 166
            ' devueltas silenciosamente equivocadas. En el disco de hoy: 0 BSA por encima del tope.
            Dim offPhys As UInteger = r.Offset
            _fs.Position = CLng(offPhys)

            ' 2) Trabajar con copia del size del entry
            Dim sizeWork As UInteger = (r.SizeField And &H3FFFFFFFUI)

            ' 3) Nombre embebido (BSTRING: u8 len + len bytes, SIN terminador)
            Dim hasEmbeddedName As Boolean = r.HasEmbeddedName
            If hasEmbeddedName Then
                Dim nameLen As Integer = _fs.ReadByte()
                If nameLen < 0 Then Throw New EndOfStreamException("BSA: EOF leyendo longitud de nombre embebido.")
                If _fs.Position + nameLen > _fs.Length Then
                    Throw New EndOfStreamException("BSA: nombre embebido excede el archivo.")
                End If
                If nameLen > 0 Then _fs.Position += nameLen
                ' Descontar: byte de longitud + bytes del nombre. Validar en Long para no
                ' underflowear el UInteger si el header trae un tamaño inválido.
                If CLng(sizeWork) < CLng(nameLen + 1) Then Throw New InvalidDataException("BSA: tamaño de entrada inválido")
                sizeWork -= CUInt(nameLen + 1)
            End If

            ' 4) ¿Comprimido?
            Dim isComp As Boolean = r.Compressed
            Dim uncompLen As UInteger = 0UI
            If isComp Then
                If sizeWork < 4UI Then Throw New InvalidDataException("BSA: size < 4 en archivo comprimido.")
                uncompLen = _br.ReadUInt32() ' u32 LE de tamaño descomprimido
                If uncompLen = 0UI Then Throw New InvalidDataException("BSA: tamaño descomprimido = 0.")
                sizeWork -= 4UI ' restar antes de limpiar flags
            End If

            ' 5) Leer payload
            Dim payloadLen As Integer = CInt(sizeWork)
            If payloadLen < 0 Then Throw New InvalidDataException("BSA: payloadLen negativo.")
            Dim payload As Byte() = If(payloadLen > 0, _br.ReadBytes(payloadLen), Array.Empty(Of Byte)())
            If payload.Length <> payloadLen Then
                Throw New EndOfStreamException($"BSA: lectura corta del payload (read={payload.Length}, need={payloadLen}).")
            End If

            ' 7) No comprimido → devolver
            If Not isComp Then
                Return payload
            End If

            ' 8) LZ4 (FRAME/RAW) estricto con tamaño esperado
            Dim outData As Byte()
            outData = Lz4Strict.Lz4DecompressStrict(payload, CInt(uncompLen))
            Return outData
        End Function

        Public Function ExtractRawByIndex(index As Integer) As RawCompressedEntry Implements IArchiveImpl.ExtractRawByIndex
            Dim r = _records(index)

            ' Same skipping logic as ExtractByIndex up to the point where we'd decompress: we want
            ' the post-BSTRING / post-decompSize payload bytes verbatim.
            ' Ver el bloque de ExtractByIndex: el bit 31 es parte del offset, no una bandera.
            Dim offPhys As UInteger = r.Offset
            _fs.Position = CLng(offPhys)

            Dim sizeWork As UInteger = (r.SizeField And &H3FFFFFFFUI)

            If r.HasEmbeddedName Then
                Dim nameLen As Integer = _fs.ReadByte()
                If nameLen < 0 Then Throw New EndOfStreamException("BSA: EOF leyendo longitud de nombre embebido.")
                If _fs.Position + nameLen > _fs.Length Then
                    Throw New EndOfStreamException("BSA: nombre embebido excede el archivo.")
                End If
                If nameLen > 0 Then _fs.Position += nameLen
                ' Validar en Long antes de restar para no underflowear el UInteger.
                If CLng(sizeWork) < CLng(nameLen + 1) Then Throw New InvalidDataException("BSA: tamaño de entrada inválido")
                sizeWork -= CUInt(nameLen + 1)
            End If

            Dim isComp As Boolean = r.Compressed
            Dim uncompLen As UInteger = 0UI
            If isComp Then
                If sizeWork < 4UI Then Throw New InvalidDataException("BSA: size < 4 en archivo comprimido.")
                uncompLen = _br.ReadUInt32()
                If uncompLen = 0UI Then Throw New InvalidDataException("BSA: tamaño descomprimido = 0.")
                sizeWork -= 4UI
            End If

            Dim payloadLen As Integer = CInt(sizeWork)
            If payloadLen < 0 Then Throw New InvalidDataException("BSA: payloadLen negativo.")
            Dim payload As Byte() = If(payloadLen > 0, _br.ReadBytes(payloadLen), Array.Empty(Of Byte)())
            If payload.Length <> payloadLen Then
                Throw New EndOfStreamException($"BSA: lectura corta del payload (read={payload.Length}, need={payloadLen}).")
            End If

            ' For BSA, "compressed payload" means the LZ4 frame bytes (without the leading u32
            ' decompSize, which the writer re-adds). For uncompressed entries, Bytes == raw data,
            ' DecompSize == payload length, CompSize == 0 (mirroring BA2 conventions).
            Return New RawCompressedEntry With {
                .Bytes = payload,
                .IsCompressed = isComp,
                .CompSize = If(isComp, CUInt(payload.Length), 0UI),
                .DecompSize = If(isComp, uncompLen, CUInt(payload.Length)),
                .HasDx10Metadata = False
            }
        End Function

        Public Function GetPayloadSourceByIndex(index As Integer) As PayloadStreamSource Implements IArchiveImpl.GetPayloadSourceByIndex
            Dim r = _records(index)
            ' Layout per entry on disk (in order):
            '   [BSTRING: 1 byte len + (dir + '\\' + filename) bytes]   (only if HasEmbeddedName)
            '   [u32 LE decompSize]                                     (only if r.Compressed)
            '   [LZ4 frame OR raw bytes]
            ' We hand the writer a source pointing at the third part, so it can re-emit the
            ' BSTRING and decompSize itself (with the entry's current Directory/FileName).

            ' Ver el bloque de ExtractByIndex: el bit 31 es parte del offset, no una bandera.
            Dim offPhys As Long = CLng(r.Offset)
            Dim sizeWork As Long = CLng(r.SizeField And &H3FFFFFFFUI)
            Dim cursor As Long = offPhys

            ' Compute BSTRING size from the parsed strings (no disk read needed).
            If r.HasEmbeddedName Then
                Dim dirTxt = If(r.Directory, "").Trim(Correct_Path_separator, InCorrect_Path_separator)
                Dim dirLen As Integer = If(String.IsNullOrEmpty(dirTxt), 0, _enc.GetByteCount(dirTxt))
                Dim fileLen As Integer = If(r.FileName Is Nothing, 0, _enc.GetByteCount(r.FileName))
                Dim bstrContent As Integer = dirLen + 1 + fileLen   ' dir + '\\' + filename
                Dim bstringSize As Integer = 1 + bstrContent        ' length byte + content
                cursor += bstringSize
                sizeWork -= bstringSize
            End If

            Dim decompSize As UInteger = 0
            If r.Compressed Then
                If sizeWork < 4 Then Throw New InvalidDataException("BSA: bloque comprimido < 4 bytes (sin u32 decompSize).")
                ' Single 4-byte read at the position right after the BSTRING. Cheap.
                _fs.Position = cursor
                decompSize = _br.ReadUInt32()
                cursor += 4
                sizeWork -= 4
            End If

            If sizeWork < 0 Then Throw New InvalidDataException("BSA: tamaño de payload negativo tras descontar overhead.")

            Return New PayloadStreamSource With {
                .SourceStream = _fs,
                .Offset = cursor,
                .Length = sizeWork,
                .DecompSize = If(r.Compressed, CLng(decompSize), sizeWork),
                .IsCompressed = r.Compressed,
                .SourceCodec = If(r.Compressed, PayloadCodec.Lz4Frame, PayloadCodec.None)
            }
        End Function

        Public ReadOnly Property Ba2HeaderVersion As UInteger? Implements IArchiveImpl.Ba2HeaderVersion
            Get
                Return Nothing   ' BSA: not a BA2; v105 is a BSA version, not a BA2 header version.
            End Get
        End Property

        Public Sub Dispose() Implements IArchiveImpl.Dispose
            _br?.Dispose()
        End Sub
    End Class

    ' ======== BA2 (FO4: GNRL / DX10) ========

    Friend Class Ba2Impl
        Implements IArchiveImpl

        Private ReadOnly _fs As Stream
        Private ReadOnly _br As BinaryReader
        Private ReadOnly _enc As Encoding

        Private _hdr As Ba2Header
        Private ReadOnly _entries As New List(Of EntryBase)

        Private Enum Ba2Type
            GNRL
            DX10
            GNMF
        End Enum

        Private Structure Ba2Header
            Public Version As UInteger
            Public Type As Ba2Type
            Public NumFiles As UInteger
            Public NameTableOffset As ULong
            Public V2V3_UnknownAlways1 As ULong ' v2/v3: suele ser 1
            Public V3_Compression As UInteger   ' v3: 3=LZ4, otro=zlib
        End Structure

        Private MustInherit Class EntryBase
            Public Property Index As Integer
            Public Property Directory As String = ""
            Public Property FileName As String = ""
            Public MustOverride Function Extract(fs As Stream, hdr As Ba2Header) As Byte()
            Public MustOverride Function ExtractRaw(fs As Stream, hdr As Ba2Header) As RawCompressedEntry
        End Class

        ' ---------- GNRL ----------
        Private Class EntryGNRL
            Inherits EntryBase

            Public HashFile As UInteger
            Public HashExt As UInteger
            Public HashDir As UInteger
            Public DataFileIndex As Byte
            Public ChunkCount As Byte
            Public ChunkHeaderSize As UShort

            Public Chunks As New List(Of Chunk)

            Public Structure Chunk
                Public Offset As ULong
                Public CompressedSize As UInteger ' 0 => sin compresión
                Public DecompressedSize As UInteger
                Public Sentinel As UInteger       ' 0xBAADF00D
            End Structure

            Public Overrides Function Extract(fs As Stream, hdr As Ba2Header) As Byte()
                If Chunks Is Nothing OrElse Chunks.Count = 0 Then
                    Return Array.Empty(Of Byte)()
                End If

                ' Orden físico por Offset por robustez (no debería alterar si ya viene ordenado)
                Dim ordered = Chunks.OrderBy(Function(c) c.Offset).ToList()

                ' Capacidad EXACTA del MemoryStream desde DecompressedSize de cada chunk (ya leído del
                ' header) — sin esto el stream arranca en 0 y duplica buffers al crecer, generando
                ' tráfico de LOH grande con miles de extracciones por bake.
                Dim capacidad As Long = 0
                ' El acotado es POR CHUNK contra su propio CompressedSize (bytes reales en el archivo) ×
                ' MaxBlockExpansion — NUNCA contra DecompressedSize crudo (nadie lo valida; la app abre
                ' archives ajenos), ni contra una cota global (fs.Length) ni un multiplicador fijo como
                ' ×32: esas dos formas apagan el pre-alloc justo en archives chicos que comprimen muy
                ' bien (textura uniforme, facetint plano). Chunk sin comprimir → cota contra fs.Length
                ' (bytes crudos en el archivo). Techo del formato: MaxPreAllocBytes (256 MB, ninguna
                ' textura de estos juegos se acerca).
                ' ⚠️ Hueco conocido: ZlibStrict/Lz4Strict (abajo) reservan DecompressedSize por chunk SIN
                ' este acotado — decisión pendiente del usuario, cerrarlo cambiaría qué chunks se aceptan hoy.
                For Each c In ordered
                    Dim declarado As Long = CLng(c.DecompressedSize)
                    Dim creible As Long
                    If c.CompressedSize <> 0UI Then
                        creible = CLng(c.CompressedSize) * Ba2ReaderLimits.MaxBlockExpansion
                    Else
                        creible = fs.Length
                    End If
                    capacidad += Math.Min(declarado, creible)
                Next
                If capacidad > Ba2ReaderLimits.MaxPreAllocBytes Then capacidad = 0
                If capacidad <= 0 OrElse capacidad > Integer.MaxValue Then capacidad = 0

                Using ms As New MemoryStream(CInt(capacidad))
                    For Each ch In ordered

                        If CLng(ch.Offset) < 0 OrElse CLng(ch.Offset) >= fs.Length Then
                            Throw New InvalidDataException($"BA2.GNRL: offset fuera de rango (offset={ch.Offset}).")
                        End If
                        fs.Position = CLng(ch.Offset)

                        Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                        Dim needUL As ULong = If(isComp, CULng(ch.CompressedSize), CULng(ch.DecompressedSize))
                        If needUL > CULng(Integer.MaxValue) Then
                            Throw New InvalidDataException($"BA2.GNRL: tamaño de chunk excede Int32 (need={needUL}).")
                        End If
                        Dim need As Integer = CInt(needUL)
                        ' Verificar el rango completo [Offset, Offset+need) (no solo el inicio) en Long
                        ' para no leer fuera del archivo si el header trae un tamaño corrupto.
                        If CLng(ch.Offset) + CLng(need) > fs.Length Then
                            Throw New InvalidDataException($"BA2.GNRL: chunk excede el archivo (offset={ch.Offset}, need={need}).")
                        End If

                        Dim packed As Byte() = If(need > 0, New Byte(need - 1) {}, Array.Empty(Of Byte)())
                        If need > 0 Then ReadExact(fs, packed, 0, need)

                        Dim outChunk As Byte()
                        If isComp Then
                            If hdr.Version >= 3UI AndAlso hdr.V3_Compression = 3UI Then
                                ' LZ4
                                outChunk = Lz4Strict.Lz4DecompressStrict(packed, CInt(ch.DecompressedSize))
                            Else
                                ' zlib/deflate
                                outChunk = ZlibStrict.ZlibDecompressStrict(packed, CInt(ch.DecompressedSize))
                            End If
                        Else
                            outChunk = packed
                        End If
                        If isComp AndAlso ch.DecompressedSize = 0UI Then
                            Throw New InvalidDataException("BA2.GNRL: chunk comprimido con DecompressedSize=0 no es válido.")
                        End If
                        If ch.DecompressedSize > 0UI AndAlso outChunk.Length <> CInt(ch.DecompressedSize) Then
                            ' ajustar o validar
                            If outChunk.Length < CInt(ch.DecompressedSize) Then
                                Throw New InvalidDataException($"BA2.GNRL: tamaño descomprimido menor al esperado (got={outChunk.Length}, exp={ch.DecompressedSize}).")
                            End If
                            Array.Resize(outChunk, CInt(ch.DecompressedSize))
                        End If

                        ms.Write(outChunk, 0, outChunk.Length)

                    Next

                    Return ms.ToArray()
                End Using
            End Function

            Public Overrides Function ExtractRaw(fs As Stream, hdr As Ba2Header) As RawCompressedEntry
                If Chunks Is Nothing OrElse Chunks.Count <> 1 Then
                    Throw New NotSupportedException(
                        $"BA2.GNRL: ExtractCompressedPayload requires single-chunk entries (got {If(Chunks Is Nothing, 0, Chunks.Count)}).")
                End If

                Dim ch = Chunks(0)
                If CLng(ch.Offset) < 0 OrElse CLng(ch.Offset) >= fs.Length Then
                    Throw New InvalidDataException($"BA2.GNRL: offset fuera de rango (offset={ch.Offset}).")
                End If
                fs.Position = CLng(ch.Offset)

                Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                Dim needUL As ULong = If(isComp, CULng(ch.CompressedSize), CULng(ch.DecompressedSize))
                If needUL > CULng(Integer.MaxValue) Then
                    Throw New InvalidDataException($"BA2.GNRL: tamaño de chunk excede Int32 (need={needUL}).")
                End If

                Dim need As Integer = CInt(needUL)
                ' Verificar el rango completo [Offset, Offset+need) en Long antes de leer.
                If CLng(ch.Offset) + CLng(need) > fs.Length Then
                    Throw New InvalidDataException($"BA2.GNRL: chunk excede el archivo (offset={ch.Offset}, need={need}).")
                End If
                Dim packed As Byte() = If(need > 0, New Byte(need - 1) {}, Array.Empty(Of Byte)())
                If need > 0 Then ReadExact(fs, packed, 0, need)

                Return New RawCompressedEntry With {
                    .Bytes = packed,
                    .IsCompressed = isComp,
                    .CompSize = ch.CompressedSize,
                    .DecompSize = ch.DecompressedSize,
                    .HasDx10Metadata = False
                }
            End Function

        End Class

        ' ---------- DX10 ----------
        Private Class EntryDX10
            Inherits EntryBase

            Public HashFile As UInteger
            Public HashExt As UInteger
            Public HashDir As UInteger
            Public DataFileIndex As Byte
            Public ChunkCount As Byte
            Public ChunkHeaderSize As UShort

            Public Height As UShort
            Public Width As UShort
            Public MipCount As Byte
            Public DxgiFormatU8 As Byte
            Public Flags As Byte           ' bit0: cubemap
            Public TileMode As Byte

            Public Chunks As New List(Of Chunk)

            Public Structure Chunk
                Public Offset As ULong
                Public CompressedSize As UInteger
                Public DecompressedSize As UInteger
                Public MipFirst As UShort
                Public MipLast As UShort
                Public Sentinel As UInteger
            End Structure

            Public Overrides Function Extract(fs As Stream, hdr As Ba2Header) As Byte()
                Dim isCube As Boolean = ((Flags And 1) <> 0)
                If Chunks Is Nothing OrElse Chunks.Count = 0 Then
                    Return Array.Empty(Of Byte)()
                End If

                If TileMode <> 8 Then
                    Throw New NotSupportedException($"BA2.DX10: TileMode={TileMode} no soportado (requiere destileo).")
                End If

                ' El encode vive en UN solo lugar (Dx10Importer.EncodeDdsHeader) porque lo necesita
                ' tambien quien extrae al disco una fila ya despojada — ver el docstring de esa funcion.
                ' `arraySize` sale de isCube, igual que antes: la entrada DX10 del BA2 no tiene campo de
                ' tamaño de array.
                Dim ddsHeader As Byte() = Dx10Importer.EncodeDdsHeader(CInt(DxgiFormatU8), CInt(Width), CInt(Height),
                                                                      CInt(MipCount), isCube)

                ' Los chunks DX10 pueden no venir en orden lógico de mips; ordenamos por MipFirst asc
                Dim ordered = Chunks.OrderBy(Function(c) CInt(c.MipFirst)).ThenBy(Function(c) CLng(c.Offset)).ToList()

                ' Capacidad EXACTA del MemoryStream desde DecompressedSize de cada chunk (ya leído del
                ' header) — sin esto el stream arranca en 0 y duplica buffers al crecer, generando
                ' tráfico de LOH grande con miles de extracciones por bake.
                Dim capacidad As Long = ddsHeader.Length
                ' El acotado es POR CHUNK contra su propio CompressedSize (bytes reales en el archivo) ×
                ' MaxBlockExpansion — NUNCA contra DecompressedSize crudo (nadie lo valida; la app abre
                ' archives ajenos), ni contra una cota global (fs.Length) ni un multiplicador fijo como
                ' ×32: esas dos formas apagan el pre-alloc justo en archives chicos que comprimen muy
                ' bien (textura uniforme, facetint plano). Chunk sin comprimir → cota contra fs.Length
                ' (bytes crudos en el archivo). Techo del formato: MaxPreAllocBytes (256 MB, ninguna
                ' textura de estos juegos se acerca).
                ' ⚠️ Hueco conocido: ZlibStrict/Lz4Strict (abajo) reservan DecompressedSize por chunk SIN
                ' este acotado — decisión pendiente del usuario, cerrarlo cambiaría qué chunks se aceptan hoy.
                For Each c In ordered
                    Dim declarado As Long = CLng(c.DecompressedSize)
                    Dim creible As Long
                    If c.CompressedSize <> 0UI Then
                        creible = CLng(c.CompressedSize) * Ba2ReaderLimits.MaxBlockExpansion
                    Else
                        creible = fs.Length
                    End If
                    capacidad += Math.Min(declarado, creible)
                Next
                If capacidad > Ba2ReaderLimits.MaxPreAllocBytes Then capacidad = 0
                If capacidad <= 0 OrElse capacidad > Integer.MaxValue Then capacidad = 0

                Using ms As New MemoryStream(CInt(capacidad))
                    ms.Write(ddsHeader, 0, ddsHeader.Length)

                    For Each ch In ordered
                        ' Validar inicio y rango completo [Offset, Offset+need) en Long: estos
                        ' chunks no traían guarda de offset y un header corrupto podía hacer
                        ' Position/Read fuera del archivo.
                        If CLng(ch.Offset) < 0 OrElse CLng(ch.Offset) >= fs.Length Then
                            Throw New InvalidDataException($"BA2.DX10: offset fuera de rango (offset={ch.Offset}).")
                        End If
                        fs.Position = CLng(ch.Offset)
                        Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                        Dim needUL As ULong = If(isComp, CULng(ch.CompressedSize), CULng(ch.DecompressedSize))
                        If needUL > CULng(Integer.MaxValue) Then
                            Throw New InvalidDataException($"BA2.DX10: tamaño de chunk excede Int32 (need={needUL}).")
                        End If
                        Dim need As Integer = CInt(needUL)
                        If CLng(ch.Offset) + CLng(need) > fs.Length Then
                            Throw New InvalidDataException($"BA2.DX10: chunk excede el archivo (offset={ch.Offset}, need={need}).")
                        End If

                        Dim packed = If(need > 0, New Byte(need - 1) {}, Array.Empty(Of Byte)())
                        If need > 0 Then ReadExact(fs, packed, 0, need)

                        Dim outChunk As Byte()
                        If isComp Then
                            If hdr.Version >= 3UI AndAlso hdr.V3_Compression = 3UI Then
                                outChunk = Lz4Strict.Lz4DecompressStrict(packed, CInt(ch.DecompressedSize))
                            Else
                                outChunk = ZlibStrict.ZlibDecompressStrict(packed, CInt(ch.DecompressedSize))
                            End If
                        Else
                            outChunk = packed
                        End If
                        If isComp AndAlso ch.DecompressedSize = 0UI Then
                            Throw New InvalidDataException("BA2.DX10: chunk comprimido con DecompressedSize=0 no es válido.")
                        End If
                        If ch.DecompressedSize > 0UI AndAlso outChunk.Length <> CInt(ch.DecompressedSize) Then
                            If outChunk.Length < CInt(ch.DecompressedSize) Then
                                Throw New InvalidDataException($"BA2.DX10: tamaño descomprimido menor al esperado (got={outChunk.Length}, exp={ch.DecompressedSize}).")
                            End If
                            Array.Resize(outChunk, CInt(ch.DecompressedSize))
                        End If

                        ms.Write(outChunk, 0, outChunk.Length)
                    Next

                    Return ms.ToArray()
                End Using
            End Function

            Public Overrides Function ExtractRaw(fs As Stream, hdr As Ba2Header) As RawCompressedEntry
                If Chunks Is Nothing OrElse Chunks.Count <> 1 Then
                    Throw New NotSupportedException(
                        $"BA2.DX10: ExtractCompressedPayload requires single-chunk entries (got {If(Chunks Is Nothing, 0, Chunks.Count)}).")
                End If
                If TileMode <> 8 Then
                    Throw New NotSupportedException($"BA2.DX10: ExtractCompressedPayload requires TileMode=8 (got {TileMode}).")
                End If

                Dim ch = Chunks(0)
                If CLng(ch.Offset) < 0 OrElse CLng(ch.Offset) >= fs.Length Then
                    Throw New InvalidDataException($"BA2.DX10: offset fuera de rango (offset={ch.Offset}).")
                End If
                fs.Position = CLng(ch.Offset)

                Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                Dim needUL As ULong = If(isComp, CULng(ch.CompressedSize), CULng(ch.DecompressedSize))
                If needUL > CULng(Integer.MaxValue) Then
                    Throw New InvalidDataException($"BA2.DX10: tamaño de chunk excede Int32 (need={needUL}).")
                End If

                Dim need As Integer = CInt(needUL)
                ' Verificar el rango completo [Offset, Offset+need) en Long antes de leer.
                If CLng(ch.Offset) + CLng(need) > fs.Length Then
                    Throw New InvalidDataException($"BA2.DX10: chunk excede el archivo (offset={ch.Offset}, need={need}).")
                End If
                Dim packed As Byte() = If(need > 0, New Byte(need - 1) {}, Array.Empty(Of Byte)())
                If need > 0 Then ReadExact(fs, packed, 0, need)

                Dim isCube As Boolean = ((Flags And 1) <> 0)
                Return New RawCompressedEntry With {
                    .Bytes = packed,
                    .IsCompressed = isComp,
                    .CompSize = ch.CompressedSize,
                    .DecompSize = ch.DecompressedSize,
                    .HasDx10Metadata = True,
                    .Width = CInt(Width),
                    .Height = CInt(Height),
                    .MipCount = CInt(If(MipCount = 0, 1, MipCount)),
                    .DxgiFormat = CInt(DxgiFormatU8),
                    .IsCubemap = isCube,
                    .Faces = If(isCube, 6, 1)
                }
            End Function

        End Class

        Public Sub New(fs As Stream, enc As Encoding)
            _fs = fs : _enc = enc
            _br = New BinaryReader(_fs, _enc, leaveOpen:=True)
            Open()
        End Sub

        Private Sub Open()
            ' Magic "BTDX"
            Dim magic = Encoding.ASCII.GetString(_br.ReadBytes(4))
            If magic <> "BTDX" Then Throw New InvalidDataException("BA2: magic inválido")

            _hdr.Version = _br.ReadUInt32()

            Dim typeStr = Encoding.ASCII.GetString(_br.ReadBytes(4))
            Select Case typeStr
                Case "GNRL" : _hdr.Type = Ba2Type.GNRL
                Case "DX10" : _hdr.Type = Ba2Type.DX10
                Case "GNMF" : _hdr.Type = Ba2Type.GNMF
                Case Else : Throw New InvalidDataException("BA2: tipo desconocido " & typeStr)
            End Select

            _hdr.NumFiles = _br.ReadUInt32()
            _hdr.NameTableOffset = _br.ReadUInt64()

            If _hdr.Version = 2UI OrElse _hdr.Version = 3UI Then
                _hdr.V2V3_UnknownAlways1 = _br.ReadUInt64()
                If _hdr.Version = 3UI Then _hdr.V3_Compression = _br.ReadUInt32()
            End If

            _entries.Capacity = CInt(_hdr.NumFiles)

            For i = 0UI To _hdr.NumFiles - 1UI
                Dim hashFile = _br.ReadUInt32()
                Dim hashExt = _br.ReadUInt32()
                Dim hashDir = _br.ReadUInt32()
                Dim dataFileIdx = _br.ReadByte()
                Dim chunkCount = _br.ReadByte()
                Dim chunkHdrSize = _br.ReadUInt16()


                Select Case _hdr.Type
                    Case Ba2Type.GNRL
                        If chunkHdrSize <> CUShort(16) AndAlso chunkHdrSize <> CUShort(20) Then
                            Throw New NotSupportedException($"BA2.GNRL: chunk header size inesperado ({chunkHdrSize}).")
                        End If
                        Dim e As New EntryGNRL With {
                            .Index = CInt(i),
                            .HashFile = hashFile, .HashExt = hashExt, .HashDir = hashDir,
                            .DataFileIndex = dataFileIdx, .ChunkCount = chunkCount, .ChunkHeaderSize = chunkHdrSize
                        }
                        For c = 0 To e.ChunkCount - 1
                            Dim ch As New EntryGNRL.Chunk With {
                                .Offset = _br.ReadUInt64(),           ' 8
                                .CompressedSize = _br.ReadUInt32(),   ' +4 = 12
                                .DecompressedSize = _br.ReadUInt32() ' +4 = 16
                                }

                            ' Sentinel SIEMPRE va inmediatamente después del header base.
                            Dim s As UInteger = _br.ReadUInt32()   ' +4 = 20 consumidos
                            If s <> &HBAADF00DUI Then
                                Throw New InvalidDataException($"BA2.GNRL: sentinel inválido (0x{s:X8}).")
                            End If
                            ch.Sentinel = s
                            e.Chunks.Add(ch)
                        Next
                        e.FileName = $"{e.HashFile:X8}.bin"
                        If e.HashDir <> 0UI Then e.Directory = $"{e.HashDir:X8}"
                        _entries.Add(e)

                    Case Ba2Type.DX10
                        If chunkHdrSize <> CUShort(24) AndAlso chunkHdrSize <> CUShort(20) Then
                            Throw New NotSupportedException($"BA2.DX10: chunk header size inesperado ({chunkHdrSize}).")
                        End If

                        Dim e As New EntryDX10 With {
                            .Index = CInt(i),
                            .HashFile = hashFile, .HashExt = hashExt, .HashDir = hashDir,
                            .DataFileIndex = dataFileIdx, .ChunkCount = chunkCount, .ChunkHeaderSize = chunkHdrSize,
                            .Height = _br.ReadUInt16(),
                            .Width = _br.ReadUInt16(),
                            .MipCount = _br.ReadByte(),
                            .DxgiFormatU8 = _br.ReadByte(),
                            .Flags = _br.ReadByte(),
                            .TileMode = _br.ReadByte()
                        }

                        For c = 0 To e.ChunkCount - 1
                            Dim ch As New EntryDX10.Chunk With {
                                .Offset = _br.ReadUInt64(),           ' 8
                                .CompressedSize = _br.ReadUInt32(),   ' +4 = 12
                                .DecompressedSize = _br.ReadUInt32(), ' +4 = 16
                                .MipFirst = _br.ReadUInt16(),         ' +2 = 18
                                .MipLast = _br.ReadUInt16()          ' +2 = 20
                                }

                            Dim s As UInteger = &HBAADF00DUI

                            If chunkHdrSize = CUShort(24) Then
                                ' Sentinel forma parte del header “declarado”
                                s = _br.ReadUInt32()               ' +4 = 24
                                If s <> &HBAADF00DUI Then
                                    Throw New InvalidDataException($"BA2.DX10: sentinel inválido (0x{s:X8}).")
                                End If
                            Else
                                ' chunkHdrSize=20: algunos siguen poniendo el sentinel inmediatamente después.
                                ' Lo detectamos sin romper el alineamiento.
                                Dim pos = _fs.Position
                                Dim maybe = _br.ReadUInt32()
                                If maybe = &HBAADF00DUI Then
                                    s = maybe
                                Else
                                    _fs.Position = pos ' no había sentinel; retroceder
                                End If
                            End If

                            ch.Sentinel = s
                            e.Chunks.Add(ch)
                        Next

                        e.FileName = $"{e.HashFile:X8}.dds"
                        If e.HashDir <> 0UI Then e.Directory = $"{e.HashDir:X8}"
                        _entries.Add(e)

                    Case Else
                        Throw New NotSupportedException("BA2: GNMF fuera de alcance")
                End Select
            Next

            ' Name table: u16 length + bytes (encoding _enc), uno por entry en el mismo orden que _entries
            If _hdr.NameTableOffset > 0UL Then
                _fs.Position = CLng(_hdr.NameTableOffset)
                For i = 0 To _entries.Count - 1
                    Dim nlen As UShort = _br.ReadUInt16()
                    Dim nb = If(nlen > 0US, _br.ReadBytes(nlen), Array.Empty(Of Byte)())
                    Dim full = PathUtil.NormalizeSlash(_enc.GetString(nb))
                    Dim slash = full.LastIndexOf(Correct_Path_separator)
                    If slash >= 0 Then
                        _entries(i).Directory = full.Substring(0, slash)
                        _entries(i).FileName = full.Substring(slash + 1)
                    Else
                        _entries(i).Directory = ""
                        _entries(i).FileName = full
                    End If
                Next
            End If
        End Sub

        Public Function ListEntries() As List(Of ArchiveEntry) Implements IArchiveImpl.ListEntries
            Dim list As New List(Of ArchiveEntry)(_entries.Count)
            For Each e In _entries
                ' Sum chunk decompressed sizes — already parsed from the archive's per-file
                ' chunk headers during Open(). Zero extra I/O.
                Dim decomp As Long = 0
                Dim nameHash As UInteger = 0UI, dirHash As UInteger = 0UI
                Dim eg = TryCast(e, EntryGNRL)
                If eg IsNot Nothing Then
                    For Each ch In eg.Chunks
                        decomp += CLng(ch.DecompressedSize)
                    Next
                    nameHash = eg.HashFile : dirHash = eg.HashDir
                Else
                    Dim ed = TryCast(e, EntryDX10)
                    If ed IsNot Nothing Then
                        For Each ch In ed.Chunks
                            decomp += CLng(ch.DecompressedSize)
                        Next
                        nameHash = ed.HashFile : dirHash = ed.HashDir
                    End If
                End If
                list.Add(New ArchiveEntry With {
                    .Index = e.Index,
                    .Directory = e.Directory,
                    .FileName = e.FileName,
                    .DecompressedSize = decomp,
                    .Ba2NameHash = nameHash,
                    .Ba2DirHash = dirHash
                })
            Next
            Return list
        End Function

        Public Function ExtractByIndex(index As Integer) As Byte() Implements IArchiveImpl.ExtractByIndex
            Return _entries(index).Extract(_fs, _hdr)
        End Function

        Public Function ExtractRawByIndex(index As Integer) As RawCompressedEntry Implements IArchiveImpl.ExtractRawByIndex
            Return _entries(index).ExtractRaw(_fs, _hdr)
        End Function

        Public Function GetPayloadSourceByIndex(index As Integer) As PayloadStreamSource Implements IArchiveImpl.GetPayloadSourceByIndex
            ' BA2 chunk headers are fully parsed at Open(). The payload is a self-contained
            ' zlib stream / LZ4 raw block / uncompressed blob — no BSTRING or decompSize
            ' prefix to skip, so we hand a direct reference to the chunk bytes.
            Dim e = _entries(index)
            Dim eg = TryCast(e, EntryGNRL)
            If eg IsNot Nothing Then
                If eg.Chunks Is Nothing OrElse eg.Chunks.Count <> 1 Then
                    Throw New NotSupportedException(
                        $"BA2.GNRL: GetPayloadSource requires single-chunk entries (got {If(eg.Chunks Is Nothing, 0, eg.Chunks.Count)}).")
                End If
                Dim ch = eg.Chunks(0)
                Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                Return New PayloadStreamSource With {
                    .SourceStream = _fs,
                    .Offset = CLng(ch.Offset),
                    .Length = If(isComp, CLng(ch.CompressedSize), CLng(ch.DecompressedSize)),
                    .DecompSize = CLng(ch.DecompressedSize),
                    .IsCompressed = isComp,
                    .SourceCodec = If(isComp, Ba2ChunkCodec(_hdr), PayloadCodec.None)
                }
            End If

            Dim ed = TryCast(e, EntryDX10)
            If ed IsNot Nothing Then
                If ed.Chunks Is Nothing OrElse ed.Chunks.Count <> 1 Then
                    Throw New NotSupportedException(
                        $"BA2.DX10: GetPayloadSource requires single-chunk entries (got {If(ed.Chunks Is Nothing, 0, ed.Chunks.Count)}).")
                End If
                If ed.TileMode <> 8 Then
                    Throw New NotSupportedException($"BA2.DX10: GetPayloadSource requires TileMode=8 (got {ed.TileMode}).")
                End If
                Dim ch = ed.Chunks(0)
                Dim isComp As Boolean = (ch.CompressedSize <> 0UI)
                Dim isCube As Boolean = ((ed.Flags And 1) <> 0)
                Return New PayloadStreamSource With {
                    .SourceStream = _fs,
                    .Offset = CLng(ch.Offset),
                    .Length = If(isComp, CLng(ch.CompressedSize), CLng(ch.DecompressedSize)),
                    .DecompSize = CLng(ch.DecompressedSize),
                    .IsCompressed = isComp,
                    .SourceCodec = If(isComp, Ba2ChunkCodec(_hdr), PayloadCodec.None),
                    .HasDx10Metadata = True,
                    .Width = CInt(ed.Width),
                    .Height = CInt(ed.Height),
                    .MipCount = CInt(If(ed.MipCount = 0, CByte(1), ed.MipCount)),
                    .DxgiFormat = CInt(ed.DxgiFormatU8),
                    .IsCubemap = isCube,
                    .Faces = If(isCube, 6, 1)
                }
            End If

            Throw New NotSupportedException("BA2: tipo de entry no soportado por GetPayloadSource.")
        End Function

        Public ReadOnly Property Ba2HeaderVersion As UInteger? Implements IArchiveImpl.Ba2HeaderVersion
            Get
                Return _hdr.Version
            End Get
        End Property

        Public Sub Dispose() Implements IArchiveImpl.Dispose
            _br?.Dispose()
        End Sub

        Private Shared Sub ReadExact(s As Stream, buf As Byte(), offset As Integer, count As Integer)
            Dim read As Integer = 0
            While read < count
                Dim n = s.Read(buf, offset + read, count - read)
                If n <= 0 Then Throw New EndOfStreamException($"Faltan {count - read} bytes.")
                read += n
            End While
        End Sub

        ' Codec of a COMPRESSED BA2 chunk, derived from the header (mirrors Extract's branch):
        ' v3 + V3_Compression=3 → LZ4 raw block; cualquier otra versión → zlib.
        Private Shared Function Ba2ChunkCodec(hdr As Ba2Header) As PayloadCodec
            If hdr.Version >= 3UI AndAlso hdr.V3_Compression = 3UI Then
                Return PayloadCodec.Lz4Block
            End If
            Return PayloadCodec.Zlib
        End Function


    End Class
End Namespace

