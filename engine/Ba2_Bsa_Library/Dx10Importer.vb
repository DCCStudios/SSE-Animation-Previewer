Option Strict On
Imports System.IO
Imports DirectXTexWrapperCLI   ' Loader.GetDdsMetadata, DdsMetadata

Namespace BethesdaArchive.Core

    ''' <summary>
    ''' Imports DDS files into VirtualEntry instances suitable for BA2 DX10 archives.
    '''
    ''' Contract for BA2 DX10 (FO4): VirtualEntry.Data carries the STRIPPED payload (mip data
    ''' concatenated, no DDS header), and the explicit DX10 metadata (Width / Height / MipCount /
    ''' DxgiFormat / IsCubemap / Faces) is populated on the entry. The writer NEVER parses the
    ''' DDS header; the reader reconstructs it on extraction via Loader.EncodeDDSHeader.
    '''
    ''' BSA (SSE) does NOT use this importer — it stores DDS files verbatim (header included)
    ''' as opaque bytes; callers should pass the full DDS as VirtualEntry.Data without going
    ''' through SplitDdsBytes / FromDdsBytes.
    ''' </summary>
    Public NotInheritable Class Dx10Importer

        ''' <summary>Returns True if the buffer starts with the DDS magic ("DDS ").</summary>
        Public Shared Function HasDdsMagic(bytes As Byte()) As Boolean
            Return bytes IsNot Nothing AndAlso
                   bytes.Length >= 4 AndAlso
                   bytes(0) = AscW("D"c) AndAlso
                   bytes(1) = AscW("D"c) AndAlso
                   bytes(2) = AscW("S"c) AndAlso
                   bytes(3) = AscW(" "c)
        End Function

        ''' <summary>
        ''' Header-only DDS parse via DirectXTex's GetMetadataFromDDSMemory (μs-cost, no pixel
        ''' decode, no allocs beyond the returned managed objects). Returns the DX10 metadata
        ''' and the payload (DDS file bytes minus the header) ready for BA2 DX10 packaging.
        ''' </summary>
        Public Shared Function SplitDdsBytes(ddsBytes As Byte()) As (Metadata As DdsMetadata, Payload As Byte())
            If ddsBytes Is Nothing OrElse ddsBytes.Length = 0 Then
                Throw New ArgumentException("DDS vacío.", NameOf(ddsBytes))
            End If
            If Not HasDdsMagic(ddsBytes) Then
                Throw New InvalidDataException("DDS inválido: falta magic 'DDS '.")
            End If

            Dim md = Loader.GetDdsMetadata(ddsBytes)
            If md Is Nothing OrElse Not md.Loaded Then
                Throw New InvalidDataException("DDS inválido o formato no soportado.")
            End If
            If md.HeaderSize <= 0 OrElse md.HeaderSize > ddsBytes.Length Then
                Throw New InvalidDataException("DDS truncado o header tamaño inválido.")
            End If

            Dim payloadLen As Integer = ddsBytes.Length - md.HeaderSize
            Dim payload As Byte()
            If payloadLen > 0 Then
                payload = New Byte(payloadLen - 1) {}
                Buffer.BlockCopy(ddsBytes, md.HeaderSize, payload, 0, payloadLen)
            Else
                payload = Array.Empty(Of Byte)()
            End If
            Return (md, payload)
        End Function

        ''' <summary>
        ''' Builds a VirtualEntry for BA2 DX10 from a DDS buffer + relative path. The returned
        ''' entry has Data = stripped payload and the DX10 metadata fields populated.
        ''' </summary>
        Public Shared Function FromDdsBytes(ddsBytes As Byte(), relativePath As String) As VirtualEntry
            If String.IsNullOrWhiteSpace(relativePath) Then
                Throw New ArgumentException("relativePath requerido.", NameOf(relativePath))
            End If

            Dim split = SplitDdsBytes(ddsBytes)
            Dim md = split.Metadata
            Dim payload = split.Payload

            Dim dir As String = "", fileName As String = ""
            PathUtil.SplitDirFile(relativePath, dir, fileName)
            If String.IsNullOrWhiteSpace(fileName) Then
                Throw New InvalidDataException("Nombre de archivo relativo inválido.")
            End If

            Return New VirtualEntry() With {
                .Directory = dir,
                .FileName = fileName,
                .Data = payload,                ' contract: stripped payload, no DDS header
                .PreferCompress = True,         ' sin efecto en DX10, mantenido por compatibilidad
                .DxgiFormat = md.DxgiFormat,
                .Width = md.Width,
                .Height = md.Height,
                .MipCount = md.MipCount,
                .IsCubemap = md.IsCubemap,
                .Faces = md.Faces
            }
        End Function

        ''' <summary>
        ''' The DDS header that corresponds to a BA2 DX10 entry's metadata — the INVERSE of the strip
        ''' that <see cref="SplitDdsBytes"/> performs.
        '''
        ''' ⛔ ESTA ES LA UNICA CASA DE ESE ENCODE, y por eso existe la funcion en vez de llamar a
        ''' `Loader.EncodeDDSHeader` en cada lado. La llaman DOS: el reader, cuando reconstruye el .dds
        ''' al extraer de un BA2 (Bsa_Ba2Reader, EntryDX10.Extract), y el gestor de archives, cuando
        ''' extrae al disco una fila que tiene el payload despojado (Ba2_Bsa_Manager.GuardadoDeArchive).
        ''' Con dos implementaciones, la del gestor producia un .dds distinto del que da el reader — y
        ''' eso no es una diferencia cosmetica: `arraySize` sale de `IsCubemap`, no de `Faces`, y el
        ''' `mipLevels` de 0 se normaliza a 1. Un segundo encode con otra convencion es como se escriben
        ''' dos .dds distintos para la misma textura.
        '''
        ''' El round-trip lo mide `Tools\Ba2ManagerSaveGate` (H3a): abrir un BA2 DX10 y extraerlo tiene
        ''' que dar byte a byte lo mismo que `BethesdaReader.ExtractToMemory`.
        ''' </summary>
        Public Shared Function EncodeDdsHeader(dxgiFormat As Integer, width As Integer, height As Integer,
                                               mipCount As Integer, isCubemap As Boolean) As Byte()
            Return Loader.EncodeDDSHeader(dxgiFormat, width, height,
                                          If(isCubemap, 6, 1),
                                          If(mipCount <= 0, 1, mipCount),
                                          isCubemap)
        End Function

        ''' <summary>
        ''' Rebuilds the complete .dds file from a stripped BA2 DX10 payload plus its metadata — the
        ''' INVERSE of <see cref="FromDdsBytes"/>.
        '''
        ''' ⛔ CUANDO SE USA: solo sobre bytes que son un payload DESPOJADO. Quien tiene el archivo DDS
        ''' completo (BSA, BA2 GNRL, un .dds suelto) NO pasa por aca — anteponer una segunda cabecera lo
        ''' destruye. Lo que distingue los dos estados es la metadata DX10 (Width/MipCount &gt; 0), no la
        ''' extension del archivo. La guarda de abajo es la MISMA que el writer aplica a la ida
        ''' (Ba2WriterDX10.Write rechaza un Data con magic 'DDS '), por el mismo motivo: si estos bytes
        ''' ya son un DDS, alguien se equivoco de camino y el error tiene que decirlo, no producir un
        ''' archivo con dos cabeceras.
        ''' </summary>
        Public Shared Function ToDdsBytes(payload As Byte(), dxgiFormat As Integer, width As Integer,
                                          height As Integer, mipCount As Integer, isCubemap As Boolean) As Byte()
            Dim datos As Byte() = If(payload, Array.Empty(Of Byte)())
            If HasDdsMagic(datos) Then
                Throw New InvalidDataException(
                    "Dx10Importer.ToDdsBytes: the payload already starts with the DDS magic — it is a " &
                    "complete DDS file, not a stripped BA2 DX10 payload. Write it verbatim instead.")
            End If

            Dim header As Byte() = EncodeDdsHeader(dxgiFormat, width, height, mipCount, isCubemap)
            Dim salida(header.Length + datos.Length - 1) As Byte
            Buffer.BlockCopy(header, 0, salida, 0, header.Length)
            If datos.Length > 0 Then Buffer.BlockCopy(datos, 0, salida, header.Length, datos.Length)
            Return salida
        End Function

        ''' <summary>Convenience wrapper that reads a DDS file from disk and forwards to FromDdsBytes.</summary>
        Public Shared Function FromDdsFile(ddsPath As String, dataRoot As String) As VirtualEntry
            If String.IsNullOrWhiteSpace(ddsPath) Then Throw New ArgumentException("ddsPath requerido.")
            If Not File.Exists(ddsPath) Then Throw New FileNotFoundException("DDS no encontrado.", ddsPath)

            Dim bytes = File.ReadAllBytes(ddsPath)
            ' Signature is MakeRelativeUnderDataRoot(absPath, dataRoot): the absolute DDS path
            ' first, the data root second.
            Dim rel As String = PathUtil.MakeRelativeUnderDataRoot(ddsPath, dataRoot)
            Return FromDdsBytes(bytes, rel)
        End Function

    End Class

End Namespace
