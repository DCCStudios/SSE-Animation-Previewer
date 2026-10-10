Option Strict On
Imports System.IO
Imports K4os.Compression.LZ4
Imports K4os.Compression.LZ4.Streams

Namespace BethesdaArchive.Core

    ''' <summary>
    ''' Result of a pre-write compression. Hands the writer everything it needs to emit the
    ''' entry without recompressing or re-deciding: the bytes to write, whether they are
    ''' compressed or stored raw, and the decompressed length for the chunk header.
    '''
    ''' Convention (matches what the writers consume via VirtualEntry.PreCompressed*):
    '''   - CompSize = 0 means "stored raw"; Bytes is the raw payload.
    '''   - CompSize > 0 means "compressed"; Bytes is the compressed stream and CompSize == Bytes.Length.
    '''   - DecompSize is always the original payload length.
    ''' </summary>
    Public NotInheritable Class CompressedPayload
        Public Property Bytes As Byte()
        Public Property CompSize As UInteger      ' 0 ⇒ stored raw; otherwise == Bytes.Length
        Public Property DecompSize As UInteger
        Public ReadOnly Property IsCompressed As Boolean
            Get
                Return CompSize > 0UI
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Public helpers that produce CompressedPayload values ready to be fed back into the writers
    ''' as VirtualEntry.PreCompressed* fields. Encapsulates the same per-format rules the writers
    ''' apply internally (Zlib vs LZ4, "compress only if it reduces size", BSA frame vs raw)
    ''' so callers can run compression up front, measure the exact archive footprint, and then
    ''' hand the writer pre-compressed bytes that get stream-copied to disk.
    '''
    ''' Compression always happens at most once per entry across the system: the caller invokes
    ''' one of these helpers, populates VirtualEntry.PreCompressed*, and the writer detects
    ''' PreCompressed and emits the bytes verbatim.
    ''' </summary>
    Public NotInheritable Class PayloadCompressor

        Private Sub New()
        End Sub

        ''' <summary>
        ''' Compress a BA2 GNRL entry. Mirrors Ba2WriterGNRL.Write's per-entry logic: try the
        ''' codec implied by Version/CompressionFormat (Zlib for v1/v2/v7/v8, LZ4 raw for v3+LZ4)
        ''' and fall back to "stored raw" when compression doesn't actually reduce the size.
        ''' </summary>
        Public Shared Function CompressForBa2Gnrl(rawData As Byte(),
                                                   version As UInteger,
                                                   compressionFormat As Ba2WriterCommon.CompressionFormat,
                                                   preset As Ba2WriterCommon.ZlibPreset) As CompressedPayload
            If rawData Is Nothing Then rawData = Array.Empty(Of Byte)()
            If rawData.LongLength > Integer.MaxValue Then
                Throw New InvalidDataException("BA2 GNRL: archivo demasiado grande (>2GiB).")
            End If

            Dim decomp As UInteger = CUInt(rawData.Length)
            If decomp = 0UI Then
                Return New CompressedPayload With {
                    .Bytes = Array.Empty(Of Byte)(),
                    .CompSize = 0UI,
                    .DecompSize = 0UI
                }
            End If

            Dim comp As Byte()
            If version = 3UI AndAlso compressionFormat = Ba2WriterCommon.CompressionFormat.Lz4 Then
                comp = Ba2WriterCommon.CompressLz4(rawData)
            Else
                comp = Ba2WriterCommon.CompressZlib(rawData, preset)
            End If

            Dim useComp As Boolean = comp IsNot Nothing AndAlso comp.Length > 0 AndAlso comp.Length < rawData.Length
            If useComp Then
                Return New CompressedPayload With {
                    .Bytes = comp,
                    .CompSize = CUInt(comp.Length),
                    .DecompSize = decomp
                }
            Else
                Return New CompressedPayload With {
                    .Bytes = rawData,
                    .CompSize = 0UI,
                    .DecompSize = decomp
                }
            End If
        End Function

        ''' <summary>
        ''' Compress a BA2 DX10 entry. Input must be the stripped DDS payload (mip data only —
        ''' use Dx10Importer.SplitDdsBytes() to derive it from a .dds file). Same codec rules as
        ''' GNRL: Zlib by default, LZ4 raw on v3+LZ4. "Stored raw" fallback when compression
        ''' doesn't reduce.
        ''' </summary>
        Public Shared Function CompressForBa2Dx10(strippedPayload As Byte(),
                                                    version As UInteger,
                                                    compressionFormat As Ba2WriterCommon.CompressionFormat,
                                                    preset As Ba2WriterCommon.ZlibPreset) As CompressedPayload
            ' DX10 compression rules are identical to GNRL — single-chunk, codec depends on
            ' Version/CompressionFormat, fallback to raw if comp >= raw.
            Return CompressForBa2Gnrl(strippedPayload, version, compressionFormat, preset)
        End Function

        ''' <summary>
        ''' Compress a BSA entry. Always emits an LZ4 frame when wantCompressed is True
        ''' (BSA SSE v105 expects frame format, not raw blocks), or stored raw otherwise.
        ''' Unlike BA2, BSA does NOT do "compress only if it reduces" — the archive's global
        ''' Compressed flag plus the per-entry override decide; the caller is responsible for
        ''' choosing wantCompressed. Pass True to match the archive's GlobalCompressed flag,
        ''' or invert it on a per-entry basis (the writer will set sizeField bit 30 to mark
        ''' the override, same as today).
        ''' </summary>
        Public Shared Function CompressForBsa(rawData As Byte(), wantCompressed As Boolean) As CompressedPayload
            If rawData Is Nothing Then rawData = Array.Empty(Of Byte)()
            If rawData.LongLength > Integer.MaxValue Then
                Throw New InvalidDataException("BSA: archivo demasiado grande (>2GiB).")
            End If

            Dim decomp As UInteger = CUInt(rawData.Length)
            If Not wantCompressed OrElse decomp = 0UI Then
                Return New CompressedPayload With {
                    .Bytes = rawData,
                    .CompSize = 0UI,
                    .DecompSize = decomp
                }
            End If

            Dim frame As Byte()
            Using ms As New MemoryStream()
                Using lz As Stream = LZ4Stream.Encode(ms,
                    New LZ4EncoderSettings() With {
                        .CompressionLevel = LZ4Level.L04_HC,
                        .ChainBlocks = True,
                        .ContentChecksum = False,
                        .BlockSize = 4 * 1024 * 1024,
                        .BlockChecksum = False,
                        .ContentLength = Nothing
                    },
                    leaveOpen:=True)
                    lz.Write(rawData, 0, rawData.Length)
                End Using
                frame = ms.ToArray()
            End Using

            Return New CompressedPayload With {
                .Bytes = frame,
                .CompSize = CUInt(frame.Length),
                .DecompSize = decomp
            }
        End Function

    End Class

End Namespace
