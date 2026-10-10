Option Strict On
Imports System.IO
Imports System.Linq
Imports System.Text
Imports K4os.Compression.LZ4.Streams

Namespace BethesdaArchive.Core
    Public NotInheritable Class BsaWriter
        Public Shared Event Writed()
        Friend Class BASTes4Hashing
            Private Shared Function GetBytesLatin1(text As String) As Byte()
                If text Is Nothing OrElse text.Length = 0 Then Return Array.Empty(Of Byte)()
                Return Encoding.Latin1.GetBytes(text)
            End Function

            ' CRC estilo 0x1003F con aritmética en 64 bits y máscara a 32
            Private Shared Function Crc1003F(bytes As Byte()) As UInteger
                Dim crc As ULong = 0UL
                If bytes IsNot Nothing Then
                    For i As Integer = 0 To bytes.Length - 1
                        crc = (crc * &H1003FUL + CULng(bytes(i))) And &HFFFFFFFFUL
                    Next
                End If
                Return CUInt(crc)
            End Function

            Friend Shared Function Tes4HashDirectoryBytes(path As String) As Byte()
                path = PathUtil.NormalizeSlash(path).Trim(Correct_Path_separator).ToLowerInvariant()
                ' SSE: si tras normalizar queda vacío, el hash se calcula sobre "."
                If String.IsNullOrEmpty(path) Then path = "."
                Dim b = GetBytesLatin1(path)
                Dim last As Byte = If(b.Length >= 1, b(b.Length - 1), CByte(0))
                ' ⛔ `> 2`, NO `>= 2`. SYNC: `3rd party references\TES5Edit\Core\wbHash.pas:222-225`
                '     if l > 0 then hash1[0] := s[l];
                '     if l > 2 then hash1[1] := s[l-1];      <-- ESTE
                '                   hash1[2] := AnsiChar(l);
                '     if l > 0 then hash1[3] := s[1];
                ' Para un tronco de EXACTAMENTE 2 caracteres el motor deja este byte en 0 y la app ponía
                ' la primera letra ⇒ hash distinto ⇒ el motor NO ENCUENTRA EL ARCHIVO.
                ' Y entra por las dos puertas: `Tes4HashFileBytes` reutiliza esta función sobre el stem.
                '
                ' ⭐ PRUEBA INDEPENDIENTE DE LAS DOS IMPLEMENTACIONES, sobre hashes que Bethesda YA
                ' escribió: los 100 BSA vanilla de Skyrim traen 264.632 hashes; el canónico reproduce
                ' 264.632/264.632 y la app reproducía 264.605/264.632. Los 27 que fallaban son
                ' exactamente los troncos de 2 letras: `lb/lt/rb/rt.png`, `up.hkx` (×19), `on.hkx` (×2),
                ' `go.nif` (×2). Ver Tools\BsaHashGate.
                Dim last2 As Byte = If(b.Length > 2, b(b.Length - 2), CByte(0))
                Dim first As Byte = If(b.Length >= 1, b(0), CByte(0))
                Dim length As Byte = CByte(Math.Min(b.Length, 255))

                Dim crc As UInteger = 0UI
                ' Excluir primer byte y los dos últimos → índices 1..(Length-3)
                If b.Length > 3 Then
                    Dim sliceLen As Integer = b.Length - 3
                    Dim tmp(sliceLen - 1) As Byte
                    Array.Copy(b, 1, tmp, 0, sliceLen)
                    crc = Crc1003F(tmp)
                End If

                Dim out(7) As Byte
                out(0) = last : out(1) = last2 : out(2) = length : out(3) = first
                Dim c = BitConverter.GetBytes(crc) ' LE
                Buffer.BlockCopy(c, 0, out, 4, 4)
                Return out
            End Function

            Friend Shared Function Tes4HashFileBytes(path As String) As Byte()
                path = PathUtil.NormalizeSlash(path).ToLowerInvariant()
                ' nombre sin carpeta
                Dim posSlash = path.LastIndexOf(Correct_Path_separator)
                If posSlash >= 0 Then path = path.Substring(posSlash + 1)
                Dim stem As String = path
                Dim ext As String = ""
                Dim p = path.LastIndexOf("."c)
                If p >= 0 Then
                    stem = path.Substring(0, p)
                    ext = path.Substring(p)
                End If

                If stem.Length = 0 OrElse stem.Length >= 260 OrElse ext.Length >= 16 Then
                    Return New Byte(7) {} ' cero
                End If

                Dim h = Tes4HashDirectoryBytes(stem) ' reutiliza el hash de directorio sobre el stem (sin extensión)

                Dim crcBase As UInteger = BitConverter.ToUInt32(h, 4)
                Dim add As UInteger = Crc1003F(GetBytesLatin1(ext))
                Dim sum As ULong = CULng(crcBase) + CULng(add)
                crcBase = CUInt(sum And &HFFFFFFFFUL)
                Dim crcBytes = BitConverter.GetBytes(crcBase)
                Buffer.BlockCopy(crcBytes, 0, h, 4, 4)

                ' LUT de extensiones (tabla exacta que usa el motor)
                Dim lut = New String() {"", ".nif", ".kf", ".dds", ".wav", ".adp"}
                Dim i As Integer = Array.IndexOf(lut, ext)
                If i >= 0 Then
                    Dim first = CInt(h(3))
                    Dim last = CInt(h(0))
                    Dim last2 = CInt(h(1))
                    first = (first + 32 * (i And &HFC)) And &HFF
                    last = (last + ((i And &HFE) << 6)) And &HFF
                    last2 = (last2 + (i << 7)) And &HFF
                    h(3) = CByte(first) : h(0) = CByte(last) : h(1) = CByte(last2)
                End If

                Return h
            End Function


        End Class
        <Flags>
        Private Enum ArchiveFlags As UInteger
            IncludeDirectoryNames = 1UI   ' bit 0
            IncludeFileNames = 2UI        ' bit 1
            Compressed = 4UI              ' bit 2 (global)
            EmbedFileNames = &H100UI      ' bit 8
        End Enum

        Private Const VERSION As UInteger = &H69UI                  ' SSE v105
        Private Const HEADER_SIZE As UInteger = &H24UI              ' 36
        Private Const DIR_ENTRY_SIZE_V105 As Integer = &H18         ' 24
        Private Const FILE_ENTRY_SIZE As Integer = &H10             ' 16
        Private Const ICOMPRESSION As UInteger = 1UI << 30          ' bit30 (invert per-file)

        Public NotInheritable Class Options
            Public Property Encoding As Encoding = Encoding.UTF8
            Public Property UseDirectoryStrings As Boolean = True
            Public Property UseFileStrings As Boolean = True
            Public Property EmbedNames As Boolean = True
            Public Property GlobalCompressed As Boolean = True
        End Class

        ''' <summary>Bit de "file type" del header BSA (offset 32) para una extensión, replicando la tabla
        ''' del motor (SkyrimSE @0x1420298F0). El motor SALTEA todo archive cuyo fileFlags no tenga el bit
        ''' del tipo pedido (RE: lookup 0x140CF18F0 / registro 0x140CF09F0: <c>test [archive+0x1b0], bit ; je skip</c>).
        ''' bit0 MESH · bit1 TEX · bit2 MENU · bit3 SND · bit4 VOICE · bit5 SHADER · bit6 TREE · bit7 FONT · 0x100 MISC (default).</summary>
        Private Shared Function FileTypeBit(fileName As String) As UInteger
            Dim ext = Path.GetExtension(If(fileName, "")).ToLowerInvariant()
            ' Tabla EXACTA del motor (SkyrimSE_unpacked.exe @0x1420298F0, decodificada byte a byte):
            Select Case ext
                Case ".kf", ".nif", ".egm", ".egt", ".tri", ".cmp", ".lst", ".dtl", ".btr", ".bto" : Return &H1UI  ' MESH
                Case ".dds", ".tai", ".tga", ".bmp" : Return &H2UI                                                   ' TEX
                Case ".xml", ".txt" : Return &H4UI                                                                   ' MENU
                Case ".wav" : Return &H8UI                                                                           ' SND
                Case ".ogg", ".lip", ".xwm" : Return &H10UI                                                          ' VOICE
                Case ".vso", ".pso", ".vsh" : Return &H20UI                                                          ' SHADER
                Case ".spt" : Return &H40UI                                                                          ' TREE
                Case ".fnt", ".tex" : Return &H80UI                                                                  ' FONT
                Case Else : Return &H100UI                                                                           ' MISC (default del motor para ext desconocidas)
            End Select
        End Function

        '===================== PARTE 1: HEADER FIELDS + PAYLOAD BUILDER =====================
        Public Shared Sub Write(output As Stream, entriesIn As IEnumerable(Of VirtualEntry), opts As Options)
            If output Is Nothing OrElse Not output.CanWrite Then Throw New ArgumentException("Stream inválido.")
            If opts Is Nothing Then opts = New Options()
            Dim enc = If(opts.Encoding, Encoding.UTF8)

            ' ---- Normalización de entradas ----
            Dim entries = entriesIn?.ToList()
            If entries Is Nothing OrElse entries.Count = 0 Then Throw New InvalidOperationException("No hay entradas.")
            For Each e In entries
                e.Directory = PathUtil.NormalizeSlash(e.Directory)
                If String.IsNullOrEmpty(e.FileName) Then Throw New InvalidDataException("Entry sin FileName.")
                If e.Data Is Nothing Then e.Data = Array.Empty(Of Byte)()
            Next

            ' ---- Agrupar por carpeta (orden alfabético) ----
            Dim groups = entries.GroupBy(Function(e) e.Directory, StringComparer.OrdinalIgnoreCase).OrderBy(Function(g)
                                                                                                                Dim h = BASTes4Hashing.Tes4HashDirectoryBytes(g.Key)
                                                                                                                Return BitConverter.ToUInt64(h, 0) ' LE → 64-bit
                                                                                                            End Function).ToList()

            Dim folderCount As Integer = groups.Count
            Dim fileCount As Integer = entries.Count

            ' ---- Sumas "raw" para header ----
            ' FolderNameLength (campo del header) debe ser: sum(rawLenDir) + folderCount
            ' (el lector, BsaImpl.Open, calcula dirStrSz = FolderNameLength + FolderCount → da raw + 2*folders = bytes reales de BZString)
            Dim folderNamesRawSum As Integer = groups.Sum(Function(g) enc.GetByteCount(If(g.Key, "")))
            Dim fileNamesRawSum As Integer = entries.Sum(Function(e) enc.GetByteCount(e.FileName))

            ' ---- Layout de secciones (sin escribir todavía) ----
            Dim posHeader As Long = 0
            Dim posDirEntries As Long = posHeader + HEADER_SIZE
            Dim posAfterDirEntries As Long = posDirEntries + CLng(DIR_ENTRY_SIZE_V105) * folderCount

            Dim hasDirStrings As Boolean = opts.UseDirectoryStrings
            Dim hasFileStrings As Boolean = opts.UseFileStrings

            ' BZStrings de carpetas: bytes reales = rawLen + 2 (len+1 + NUL) por carpeta
            Dim dirBzTotal As Long = 0
            Dim dirBzLenByDir As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            If hasDirStrings Then
                For Each g In groups
                    Dim raw = enc.GetBytes(If(g.Key, ""))
                    If raw.Length > 255 Then Throw New InvalidDataException("Directory BZString > 255.")
                    Dim totalThis = raw.Length + 2
                    dirBzLenByDir(g.Key) = totalThis
                    dirBzTotal += totalThis
                Next
            End If

            ' Región "filesOffset" (como la usa BsaImpl.Open): [por carpeta: BZString?] + [N * 16]
            Dim filesOffset As Long = posAfterDirEntries
            Dim fileEntriesBytes As Long = CLng(FILE_ENTRY_SIZE) * fileCount
            Dim posFileNameStrings As Long = filesOffset + dirBzTotal + fileEntriesBytes

            ' Tabla ZSTRING real = sum(rawLen) + fileCount (por los NULs)
            Dim fileStringsBytesActual As Long = If(hasFileStrings, CLng(fileNamesRawSum) + fileCount, 0L)
            Dim posFileData As Long = posFileNameStrings + fileStringsBytesActual

            ' ---- Preparar HEADER fields (no se escriben aún; los usamos en Parte 2) ----
            Dim header_Flags As ArchiveFlags = CType(0UI, ArchiveFlags)
            If hasDirStrings Then header_Flags = header_Flags Or ArchiveFlags.IncludeDirectoryNames
            If hasFileStrings Then header_Flags = header_Flags Or ArchiveFlags.IncludeFileNames
            If opts.GlobalCompressed Then header_Flags = header_Flags Or ArchiveFlags.Compressed
            If opts.EmbedNames Then header_Flags = header_Flags Or ArchiveFlags.EmbedFileNames

            Dim header_FolderCount As UInteger = CUInt(folderCount)
            Dim header_FileCount As UInteger = CUInt(fileCount)
            Dim header_FolderNameLength As UInteger = If(hasDirStrings, CUInt(folderNamesRawSum + folderCount), 0UI)
            Dim header_FileNameLength As UInteger = If(hasFileStrings, CUInt(fileNamesRawSum + fileCount), 0UI)

            ' FileFlags (header offset 32) = OR de los bits de tipo de TODO el contenido. NO es "no usados":
            ' el motor gatea la búsqueda de archives por fileFlags & bitDeTipo, así que con 0 no servía las
            ' texturas/mesh del BSA (la facetint no aparecía). Es lo que computa Archive.exe/CK escaneando el contenido.
            Dim header_FileFlags As UInteger = 0UI
            For Each e In entries
                header_FileFlags = header_FileFlags Or FileTypeBit(e.FileName)
            Next

            ' ---- Build per-entry write records (sizeField + BSTRING bytes + payload source) ----
            ' Three payload paths:
            '   1) PayloadSource: stream-copy from a source archive at write time.
            '   2) PreCompressed: caller-supplied LZ4 frame (or raw bytes) in PreCompressedBytes.
            '   3) Default: compress ve.Data with LZ4 frame (when wantCompressed) or store raw.
            ' Compression is decided per-entry via wantCompressed; bit30 of sizeField marks any
            ' override against the archive's global Compressed flag.
            Dim recByKey As New Dictionary(Of String, BsaWriteRecord)(StringComparer.OrdinalIgnoreCase)

            For Each e In entries
                Dim rec As New BsaWriteRecord()

                ' === Decide compresión efectiva (override vs global) ===
                Dim wantCompressed As Boolean
                If e.PayloadSource IsNot Nothing Then
                    wantCompressed = e.PayloadSource.IsCompressed
                ElseIf e.PreCompressed Then
                    wantCompressed = (e.PreCompressedCompSize > 0UI)
                Else
                    wantCompressed = opts.GlobalCompressed
                    If e.PreferCompress AndAlso Not opts.GlobalCompressed Then wantCompressed = True
                    If (Not e.PreferCompress) AndAlso opts.GlobalCompressed Then wantCompressed = False
                End If
                rec.WantCompressed = wantCompressed

                ' === Precompute BSTRING bytes (length-prefix + dir + '\' + filename, no NUL) ===
                Dim namesBytes As Integer = 0
                If opts.EmbedNames Then
                    Dim dirTxt = If(e.Directory, "").Trim(Correct_Path_separator)
                    Dim dirBytes = enc.GetBytes(dirTxt)
                    Dim fileBytes = enc.GetBytes(e.FileName)
                    Dim bstrlen As Integer = dirBytes.Length + 1 + fileBytes.Length
                    If bstrlen > 255 Then
                        Throw New InvalidDataException($"BSTRING > 255: '{dirTxt}\{e.FileName}'")
                    End If
                    Dim bstring(1 + bstrlen - 1) As Byte
                    bstring(0) = CByte(bstrlen)
                    Buffer.BlockCopy(dirBytes, 0, bstring, 1, dirBytes.Length)
                    bstring(1 + dirBytes.Length) = &H5C ' '\'
                    Buffer.BlockCopy(fileBytes, 0, bstring, 1 + dirBytes.Length + 1, fileBytes.Length)
                    rec.BstringBytes = bstring
                    namesBytes = 1 + bstrlen
                End If

                ' === Resolve payload source and total dataBytes (including u32 decompSize prefix) ===
                Dim dataBytes As Long = 0
                If e.PayloadSource IsNot Nothing Then
                    Dim ps = e.PayloadSource
                    rec.PayloadSrc = ps
                    rec.DecompSize = CUInt(ps.DecompSize)
                    dataBytes = If(wantCompressed, 4L, 0L) + ps.Length
                ElseIf e.PreCompressed Then
                    Dim pc As Byte() = If(e.PreCompressedBytes, Array.Empty(Of Byte)())
                    rec.PayloadBytes = pc
                    rec.DecompSize = e.PreCompressedDecompSize
                    dataBytes = If(wantCompressed, 4L, 0L) + pc.Length
                Else
                    Dim raw As Byte() = If(e.Data, Array.Empty(Of Byte)())
                    If wantCompressed Then
                        ' Compress now. The encoded LZ4 frame's exact length is needed for sizeField,
                        ' so we have to materialize it. Memory peak per entry = comp frame size.
                        Using ms As New MemoryStream()
                            Using lz As Stream = LZ4Stream.Encode(ms,
                                New LZ4EncoderSettings() With {
                                    .CompressionLevel = K4os.Compression.LZ4.LZ4Level.L04_HC,
                                    .ChainBlocks = True,
                                    .ContentChecksum = False,
                                    .BlockSize = 4 * 1024 * 1024,
                                    .BlockChecksum = False,
                                    .ContentLength = Nothing
                                },
                                leaveOpen:=True)
                                If raw.Length > 0 Then lz.Write(raw, 0, raw.Length)
                            End Using
                            rec.PayloadBytes = ms.ToArray()
                        End Using
                        rec.DecompSize = CUInt(raw.Length)
                        dataBytes = 4L + rec.PayloadBytes.Length
                    Else
                        rec.PayloadBytes = raw
                        rec.DecompSize = CUInt(raw.Length)
                        dataBytes = raw.Length
                    End If
                End If

                ' === sizeField (low 30 bits = bytes-on-disk of the entry block) ===
                Dim sizeNoFlags As Long = CLng(namesBytes) + dataBytes
                If sizeNoFlags > &H3FFFFFFFUI Then Throw New OverflowException($"BSA: sizeField demasiado grande ({sizeNoFlags}).")
                Dim sizeField As UInteger = CUInt(sizeNoFlags)
                If (wantCompressed Xor opts.GlobalCompressed) Then sizeField = sizeField Or ICOMPRESSION
                rec.SizeField = sizeField
                rec.BlockBytesOnDisk = sizeNoFlags

                recByKey(PathUtil.JoinDirFile(e.Directory, e.FileName)) = rec
                RaiseEvent Writed()
            Next

            ' ===================== PARTE 2: OFFSET ASSIGN + ESCRITURA HEADER/ENTRIES =====================

            ' Orden estable: primero por carpeta, luego por nombre de archivo
            Dim orderedPairs As New List(Of (Dir As String, Name As String))(fileCount)
            For Each g In groups
                For Each e In g.OrderBy(Function(x) BitConverter.ToUInt64(BASTes4Hashing.Tes4HashFileBytes(x.FileName), 0))
                    orderedPairs.Add((g.Key, e.FileName))
                Next
            Next

            ' Asignar OffsetAbs a cada payload (zona DATA empieza en posFileData)
            Dim runData As Long = posFileData
            For Each p In orderedPairs
                Dim key = PathUtil.JoinDirFile(p.Dir, p.Name)
                Dim rec = recByKey(key)
                If runData < 0 OrElse runData > UInteger.MaxValue Then Throw New OverflowException("Offset > 4GB.")
                rec.OffsetAbs = CUInt(runData)
                recByKey(key) = rec
                ' Avance: BSTRING + (u32 si comp) + payload bytes — el total ya lo precomputamos.
                runData += rec.BlockBytesOnDisk
            Next

            ' Calcular inicio del bloque de carpeta (BZString) y el primer file-entry
            Dim offsetFolderBlockByDir As New Dictionary(Of String, UInteger)(StringComparer.OrdinalIgnoreCase)
            Dim offsetFileEntryByDir As New Dictionary(Of String, UInteger)(StringComparer.OrdinalIgnoreCase)

            Dim cursor As Long = filesOffset
            For Each g In groups
                Dim bzLen As Integer = If(hasDirStrings, dirBzLenByDir(g.Key), 0)

                If cursor < 0 OrElse cursor > UInteger.MaxValue Then Throw New OverflowException("Offset carpeta > 4GB.")
                offsetFolderBlockByDir(g.Key) = CUInt(cursor)          ' inicio del BZString de la carpeta

                Dim firstEntry = cursor + bzLen
                If firstEntry < 0 OrElse firstEntry > UInteger.MaxValue Then Throw New OverflowException("Offset file-entry > 4GB.")
                offsetFileEntryByDir(g.Key) = CUInt(firstEntry)        ' (por si lo necesitás en otras partes)

                cursor += bzLen + CLng(FILE_ENTRY_SIZE) * g.Count()
            Next

            ' ========= HEADER =========
            output.Position = posHeader
            output.Write(BitConverter.GetBytes(&H415342UI), 0, 4)          ' "BSA\0"
            output.Write(BitConverter.GetBytes(VERSION), 0, 4)             ' 0x69
            output.Write(BitConverter.GetBytes(HEADER_SIZE), 0, 4)         ' 0x24
            output.Write(BitConverter.GetBytes(CUInt(header_Flags)), 0, 4) ' ArchiveFlags
            output.Write(BitConverter.GetBytes(header_FolderCount), 0, 4)
            output.Write(BitConverter.GetBytes(header_FileCount), 0, 4)
            output.Write(BitConverter.GetBytes(header_FolderNameLength), 0, 4)
            output.Write(BitConverter.GetBytes(header_FileNameLength), 0, 4)
            output.Write(BitConverter.GetBytes(header_FileFlags), 0, 4)    ' FileFlags = OR de bits de tipo del contenido (el motor gatea lookups por esto)

            ' ========= DIRECTORY ENTRIES =========
            output.Position = posDirEntries
            For Each g In groups
                Dim dirHashBytes = BASTes4Hashing.Tes4HashDirectoryBytes(g.Key)
                output.Write(dirHashBytes, 0, dirHashBytes.Length)                  ' 8 bytes
                output.Write(BitConverter.GetBytes(CUInt(g.Count())), 0, 4)         ' u32 count
                output.Write(BitConverter.GetBytes(0UI), 0, 4)                      ' unused

                ' El offset de DIRECTORY ENTRIES es el inicio real del bloque + FileNameLength total
                ' del header: así layoutea este campo el formato BSA v105 (no es un error si "sobra").
                Dim firstEntryAbs As UInteger = offsetFileEntryByDir(g.Key)
                Dim offsetField As UInteger = offsetFolderBlockByDir(g.Key) + header_FileNameLength
                output.Write(BitConverter.GetBytes(offsetField), 0, 4)              ' u32 offset (con +FileNameLength)

                output.Write(BitConverter.GetBytes(0UI), 0, 4)                      ' unused
            Next

            ' ========= (INTERCALADO) BZStrings de carpeta + FILE ENTRIES =========
            output.Position = filesOffset
            For Each g In groups
                ' BZString de directorio
                If hasDirStrings Then
                    Dim raw = enc.GetBytes(If(g.Key, ""))
                    output.WriteByte(CByte(raw.Length + 1))     ' incluye +1 para NUL
                    If raw.Length > 0 Then output.Write(raw, 0, raw.Length)
                    output.WriteByte(0)                         ' NUL
                End If

                ' File entries de esta carpeta
                For Each e In g.OrderBy(Function(x) BitConverter.ToUInt64(BASTes4Hashing.Tes4HashFileBytes(x.FileName), 0))
                    Dim key = PathUtil.JoinDirFile(g.Key, e.FileName)
                    Dim rec = recByKey(key)

                    Dim fileHashBytes = BASTes4Hashing.Tes4HashFileBytes(e.FileName) ' hash del nombre
                    output.Write(fileHashBytes, 0, fileHashBytes.Length)          ' 8 bytes
                    output.Write(BitConverter.GetBytes(rec.SizeField), 0, 4)      ' u32 sizeField (fsize)
                    output.Write(BitConverter.GetBytes(rec.OffsetAbs), 0, 4)      ' u32 offset ABS al bloque en DATA
                Next
            Next

            ' ========= File name strings (zstring) =========
            output.Position = posFileNameStrings
            If hasFileStrings Then
                For Each g In groups
                    For Each e In g.OrderBy(Function(x) BitConverter.ToUInt64(BASTes4Hashing.Tes4HashFileBytes(x.FileName), 0))
                        Dim raw = enc.GetBytes(e.FileName)
                        If raw.Length > 0 Then output.Write(raw, 0, raw.Length)
                        output.WriteByte(0) ' NUL
                    Next
                Next
            End If

            ' ===================== PARTE 3: ESCRITURA DE DATA (PAYLOADS) =====================

            ' Al terminar los zstrings, debemos estar exactamente donde empieza la DATA
            Dim expectedPos As Long = posFileData
            If output.Position <> expectedPos Then
                Throw New InvalidDataException($"Desalineado: fin de tabla de nombres={output.Position}, esperado posFileData={expectedPos}.")
            End If

            ' DATA: escribir cada bloque en el mismo orden usado para asignar OffsetAbs.
            ' Each block is: [BSTRING bytes] [u32 decompSize, only if WantCompressed] [payload bytes].
            ' Payload comes from PayloadSrc (stream-copy) or PayloadBytes (in-memory).
            output.Position = posFileData
            For Each p In orderedPairs
                Dim key = PathUtil.JoinDirFile(p.Dir, p.Name)
                Dim rec = recByKey(key)

                If rec.BstringBytes IsNot Nothing AndAlso rec.BstringBytes.Length > 0 Then
                    output.Write(rec.BstringBytes, 0, rec.BstringBytes.Length)
                End If
                If rec.WantCompressed Then
                    Dim u = BitConverter.GetBytes(rec.DecompSize)
                    output.Write(u, 0, 4)
                End If
                If rec.PayloadSrc IsNot Nothing Then
                    Ba2WriterCommon.StreamCopyExact(rec.PayloadSrc.SourceStream, rec.PayloadSrc.Offset, rec.PayloadSrc.Length, output)
                ElseIf rec.PayloadBytes IsNot Nothing AndAlso rec.PayloadBytes.Length > 0 Then
                    output.Write(rec.PayloadBytes, 0, rec.PayloadBytes.Length)
                End If
            Next
        End Sub

        ''' <summary>
        ''' Per-entry write plan for the BSA two-pass layout. The first pass per entry computes
        ''' SizeField + BstringBytes + payload identity. The third pass emits BSTRING + (u32
        ''' decompSize if WantCompressed) + payload bytes, picking PayloadSrc (stream-copy) or
        ''' PayloadBytes (in-memory) at write time.
        ''' </summary>
        Private NotInheritable Class BsaWriteRecord
            Public Property SizeField As UInteger
            Public Property OffsetAbs As UInteger
            Public Property BlockBytesOnDisk As Long       ' total bytes of [BSTRING][u32?][payload] for offset advancement
            Public Property BstringBytes As Byte()         ' precomputed length-prefix + dir + '\\' + filename (no NUL); empty if EmbedNames=false
            Public Property WantCompressed As Boolean
            Public Property DecompSize As UInteger         ' written as the u32 prefix when WantCompressed
            Public Property PayloadBytes As Byte()         ' in-memory bytes (LZ4 frame or raw); used when PayloadSrc is null
            Public Property PayloadSrc As PayloadStreamSource ' stream-copy source (mutually exclusive with PayloadBytes)
        End Class

    End Class

End Namespace



