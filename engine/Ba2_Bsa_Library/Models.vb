Option Strict On
Imports System.Text

Namespace BethesdaArchive.Core

    Public NotInheritable Class VirtualEntry
        Public Property Directory As String = ""
        Public Property FileName As String = ""
        Public ReadOnly Property FullPath As String
            Get
                If String.IsNullOrEmpty(Directory) Then Return FileName
                Return Directory.TrimEnd(Correct_Path_separator, InCorrect_Path_separator) & Correct_Path_separator & FileName
            End Get
        End Property
        ' Contenido lógico completo del archivo. Para BA2 DX10, Data debe ser el payload DDS
        ' SIN cabecera (ver Dx10Importer.FromDdsBytes; el writer lanza excepción si recibe el
        ' DDS completo). BSA guarda el DDS completo verbatim como bytes opacos.
        Public Property Data As Byte()
        Public Property PreferCompress As Boolean  ' Sólo lo lee BSA (override del Compressed global); BA2 GNRL/DX10 lo ignoran.

        ' DX10 metadata
        Public Property DxgiFormat As Integer
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property MipCount As Integer
        Public Property IsCubemap As Boolean
        Public Property Faces As Integer

        ' CRC32 of the logical (decompressed) Data. Used by callers (e.g. Wardrobe Pack) for cheap
        ' diff against an existing archive; writers do NOT consume this field.
        Public Property Crc32 As UInteger

        ' Pass-through of pre-compressed bytes (in-memory variant). When PreCompressed=True the
        ' writer skips its own compression step and writes PreCompressedBytes verbatim, using
        ' PreCompressedCompSize as the chunk's "compressed size" field (0 means stored uncompressed).
        ' Compatibility constraint: same Version + CompressionFormat between source and destination.
        ' Mixing v3/LZ4 with v8/Zlib produces corrupted output.
        Public Property PreCompressed As Boolean
        Public Property PreCompressedBytes As Byte()
        Public Property PreCompressedCompSize As UInteger   ' 0 = stored raw, >0 = compressed length
        Public Property PreCompressedDecompSize As UInteger ' decompressed length (== Data.Length when known)

        ' Pass-through of pre-compressed bytes (streaming variant). Preferred over the in-memory
        ' PreCompressed* fields for multi-GB rewrites: the writer seeks PayloadSource.SourceStream
        ' to PayloadSource.Offset and copies PayloadSource.Length bytes directly into the output
        ' with a small reusable buffer. RAM peak per entry stays at the buffer size (~64 KB)
        ' regardless of payload size.
        '
        ' Same Version/CompressionFormat compatibility constraint as PreCompressed*. The caller
        ' must keep SourceStream alive for the entire writer call. When set, the writer ignores
        ' Data, PreCompressed*, and uses PayloadSource.IsCompressed as the authoritative
        ' compression state for this entry.
        Public Property PayloadSource As PayloadStreamSource
    End Class

    ''' <summary>
    ''' Streamed pass-through reference — the writer copies Length bytes from SourceStream at
    ''' Offset directly into the archive output, without buffering the payload in a managed array.
    ''' For BA2 GNRL/DX10: bytes refer to the chunk payload as-is (zlib stream / LZ4 raw block /
    ''' uncompressed). The writer pairs them with a freshly emitted chunk header.
    ''' For BSA: bytes refer to the LZ4 frame (compressed) or raw payload (non-compressed) ONLY,
    ''' WITHOUT the embedded BSTRING name prefix and WITHOUT the u32 decompSize prefix — the
    ''' writer emits those itself based on the entry's current Directory/FileName and DecompSize.
    ''' </summary>
    Public NotInheritable Class PayloadStreamSource
        Public Property SourceStream As IO.Stream
        Public Property Offset As Long
        Public Property Length As Long
        Public Property DecompSize As Long
        Public Property IsCompressed As Boolean

        ' Codec the source bytes are encoded with. A stream-copy pass-through is only byte-correct
        ' when the destination archive uses the SAME codec — otherwise the copied bytes (e.g. an
        ' LZ4 raw block) would land under a header that declares a different codec (e.g. Zlib) and
        ' decode to garbage. The packager compares this against the target codec and falls back to
        ' a full recompress when they differ. Stored (IsCompressed=False) entries carry no codec
        ' and are always copy-safe → reported as None.
        Public Property SourceCodec As PayloadCodec = PayloadCodec.None

        ' DX10 metadata, populated only when the source archive entry is BA2 DX10. Lets the
        ' writer rebuild the chunk header without reparsing the payload.
        Public Property HasDx10Metadata As Boolean
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property MipCount As Integer
        Public Property DxgiFormat As Integer
        Public Property IsCubemap As Boolean
        Public Property Faces As Integer
    End Class

    ''' <summary>
    ''' Raw payload extracted from an archive entry without decompression. Returned by
    ''' BethesdaReader.ExtractCompressedPayload for stream-copy scenarios.
    ''' For BA2 GNRL/DX10: Bytes is the chunk's raw payload exactly as stored on disk
    ''' (compressed if CompSize>0, raw if CompSize=0). Multi-chunk entries are not supported here
    ''' (writers we control emit single-chunk entries by convention).
    ''' For BSA: Bytes is the post-BSTRING / post-decompSize payload (i.e. the LZ4 frame when
    ''' compressed, or the raw bytes when not), without the embedded name prefix.
    ''' </summary>
    Public NotInheritable Class RawCompressedEntry
        Public Property Bytes As Byte()
        Public Property IsCompressed As Boolean
        Public Property CompSize As UInteger    ' 0 if stored raw; otherwise == Bytes.Length
        Public Property DecompSize As UInteger
        ' DX10 metadata, populated only when source is a BA2 DX10 entry.
        Public Property HasDx10Metadata As Boolean
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property MipCount As Integer
        Public Property DxgiFormat As Integer
        Public Property IsCubemap As Boolean
        Public Property Faces As Integer
    End Class

    ' ⛔ DEMOTADO A Friend: `ArchiveChangeSet`/`ArchiveHandle`/`MultiArchiveEditor`/`OpenMany` no tienen
    ' NI UNA referencia fuera de este archivo — sólo las tres declaraciones y sus usos mutuos. Es una API
    ' de edición multi-archivo que nunca ejecutó una instrucción, congelada como superficie pública en la
    ' más chica de las dos DLL que se distribuyen. Se conserva el código (puede ser la base de la edición
    ' por lotes) pero deja de ser un compromiso binario.
    Friend NotInheritable Class ArchiveChangeSet
        Friend ReadOnly Adds As New Dictionary(Of String, VirtualEntry)(StringComparer.OrdinalIgnoreCase)
        Friend ReadOnly Deletes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Friend ReadOnly Renames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Friend ReadOnly Replacements As New Dictionary(Of String, VirtualEntry)(StringComparer.OrdinalIgnoreCase)
    End Class

    Public Enum GameKind
        SSE_BSA
        FO4_BA2
    End Enum

    ''' <summary>
    ''' Compression codec of a pre-encoded payload, used to decide whether a stream-copy
    ''' pass-through is byte-correct between a source and destination archive.
    '''   - None: the bytes are stored uncompressed (no codec) → always copy-safe.
    '''   - Zlib: BA2 GNRL/DX10 zlib (RFC1950) chunk stream.
    '''   - Lz4Block: BA2 v3 LZ4 raw block (no frame header).
    '''   - Lz4Frame: BSA SSE LZ4 frame stream.
    ''' Lz4Block and Lz4Frame are deliberately distinct: a BA2 v3 block cannot be copied into a
    ''' BSA frame slot (or vice versa) without re-encoding.
    ''' </summary>
    Public Enum PayloadCodec
        None
        Zlib
        Lz4Block
        Lz4Frame
    End Enum

    Friend NotInheritable Class ArchiveHandle
        Friend ReadOnly Property Kind As GameKind
        Friend ReadOnly Property SourcePath As String
        Friend ReadOnly Property Encoding As Encoding
        Friend ReadOnly ChangeSet As New ArchiveChangeSet()
        Friend ReadOnly Entries As List(Of VirtualEntry)

        Friend Sub New(kind As GameKind, sourcePath As String, encoding As Encoding, entries As List(Of VirtualEntry))
            Me.Kind = kind
            Me.SourcePath = sourcePath
            Me.Encoding = If(encoding, Encoding.UTF8)
            Me.Entries = entries
        End Sub
    End Class

    Friend NotInheritable Class MultiArchiveEditor
        Private ReadOnly _handles As New List(Of ArchiveHandle)
        Friend ReadOnly Property [Handles] As IReadOnlyList(Of ArchiveHandle)
            Get
                Return _handles
            End Get
        End Property

        Friend Sub OpenMany(kind As GameKind, archives As IEnumerable(Of (path As String, entries As List(Of VirtualEntry), enc As Encoding)))
            For Each a In archives
                _handles.Add(New ArchiveHandle(kind, a.path, a.enc, a.entries))
            Next
        End Sub
    End Class

    Public Module PathUtil
        Public Const Correct_Path_separator As Char = "\"c
        Public Const InCorrect_Path_separator As Char = "/"c
        Public Function NormalizeSlash(p As String) As String
            If String.IsNullOrEmpty(p) Then Return ""
            Dim result = p.Replace(InCorrect_Path_separator, Correct_Path_separator)
            Return result
        End Function
        Public Function JoinDirFile(dir As String, file As String) As String
            If String.IsNullOrEmpty(dir) Then Return file
            Return NormalizeSlash(dir).TrimEnd(Correct_Path_separator) & Correct_Path_separator & file
        End Function
        Public Function MakeRelativeUnderDataRoot(absPath As String, dataRoot As String) As String
            Dim root = If(dataRoot, "")
            Try
                If root.Length > 0 Then
                    Dim fullRoot = IO.Path.GetFullPath(root).TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                    Dim fullAbs = IO.Path.GetFullPath(absPath)
                    If fullAbs.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) Then
                        Dim rel = fullAbs.Substring(fullRoot.Length).TrimStart(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                        Return NormalizeSlash(rel)
                    Else
                        Return fullAbs
                    End If
                Else
                    Return absPath
                End If
            Catch
            End Try
            Return NormalizeSlash(IO.Path.GetFileName(absPath))
        End Function

        Public Sub SplitDirFile(relPath As String, ByRef dir As String, ByRef file As String)
            Dim relNorm As String = NormalizeSlash(relPath)
            dir = ""
            file = relNorm
            Dim slash As Integer = relNorm.LastIndexOf(Correct_Path_separator)
            If slash >= 0 Then
                dir = If(slash = 0, "", relNorm.Substring(0, slash))
                file = relNorm.Substring(slash + 1)
            End If
        End Sub
    End Module

End Namespace

