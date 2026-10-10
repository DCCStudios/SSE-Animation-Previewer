Option Strict On
Imports System.IO
Imports System.Security.Cryptography

Namespace BethesdaArchive.Core

    ''' <summary>
    ''' Behavior when the bundle does not fit a single archive under MaxArchiveBytes.
    ''' ThrowOnExceed throws InvalidOperationException. SplitByPlugin creates a new numbered
    ''' companion plugin slot (e.g. "&lt;base&gt;2.esp") and continues distributing there.
    ''' </summary>
    Public Enum ArchiveOverflowPolicy
        ThrowOnExceed
        SplitByPlugin
    End Enum

    Public NotInheritable Class PackagerRequest
        ''' <summary>Tope de archivo por defecto: 3 GiB. Margen de estabilidad de motor usado por
        ''' las apps que empaquetan (WM, NPC Manager) — no es una preferencia de cada app, así que
        ''' vive UNA sola vez acá.</summary>
        ''' <para>⚠️ `Shared ReadOnly` y NO `Const`: un `Const` lo INLINEA el compilador en cada EXE, asi que
''' una DLL nueva con otro tope no llegaria a un ejecutable que no se recompile. Asi viaja de verdad.</para>
        Public Shared ReadOnly MaxArchiveBytesDefault As Long = 3L << 30

        ''' <summary>Offset máximo que un BSA puede expresar: <b>2 GiB−1</b>.
        ''' SYNC: <c>3rd party references\TES5Edit\Core\wbBSArchive.pas:162</c> <c>BSA_MAX_OFFSET = High(Integer)</c>, que es
        ''' además lo que <c>DefaultSplitSize</c> devuelve para <c>baSSE</c> (<c>:1000-1005</c>).
        ''' <para>NO aplica al BA2, que usa offsets de 64 bits. Ver el gate en <c>Pack</c>.</para></summary>
        Public Shared ReadOnly BsaMaxOffset As Long = CLng(Integer.MaxValue)

        Public Property Game As GameKind

        ' BA2 header version written for FO4 archives (GNRL + DX10). FO4-only: IGNORED when
        ' Game = SSE_BSA (BSA is always v105). Valid values are the same the writers accept
        ' (1/2/3/7/8); the writer throws on anything else. Default 8 = Next Gen, which is what
        ' the writers default to — so existing callers are byte-for-byte unaffected.
        '   - 8 (or 7): Next Gen format. Only the 1.10.980+ ("NG") Fallout4.exe can load it.
        '   - 1:        Old Gen / original format. Loads on BOTH OG and NG (universal). The NG
        '               update only bumped this header field; the body is identical.
        Public Property Ba2Version As UInteger = 8UI

        Public Property ModBaseName As String = "WM_ClonePack"
        Public Property OutputDir As String = ""
        Public Property Entries As List(Of VirtualEntry)
        ' Soft cap per archive: 3 GiB by default (see MaxArchiveBytesDefault). ⛔ Para BSA lo pisa
        ' `Pack` a `BsaMaxOffset` (2 GiB−1, `wbBSArchive.pas:162`): NO son 4 GB. El BA2 sí usa offsets
        ' de 64 bits y se queda en los 3 GiB.
        ' When a bundle exceeds this, Pack distributes entries across numbered companion plugins
        ' ("WM_ClonePack2.esp", "WM_ClonePack3.esp", ...) so the engine auto-loads each pair.
        Public Property MaxArchiveBytes As Long = MaxArchiveBytesDefault

        ' Estimate of compressed/raw size ratio used when planning slot assignment for new entries.
        ' Lower = more conservative (more splits). 0.85 means "assume payload shrinks to 85% after
        ' compression" — a margin to keep us under MaxArchiveBytes given typical BC7/Zlib output.
        ' IGNORED when BundleAlreadyCompressed = True (the packager uses VirtualEntry.PreCompressedCompSize
        ' directly, which is exact).
        Public Property CompressionRatioEstimate As Double = 0.85

        ' When True, every VirtualEntry in Entries is expected to have PreCompressed = True with
        ' valid PreCompressedBytes / PreCompressedCompSize / PreCompressedDecompSize set by the
        ' caller (typically via PayloadCompressor.CompressFor*). Distribution then sums exact
        ' compressed sizes — no estimation, no ratio. The writer stream-copies the bytes verbatim.
        Public Property BundleAlreadyCompressed As Boolean = False

        ' When deciding whether to add free entries (paths not yet present in any archive) to an
        ' existing slot, the packager rewrites the slot only when the slot's REAL free space
        ' (MaxArchiveBytes - existing on-disk size) is at least this many bytes. Default 100 MB.
        ' This is an absolute floor in bytes, not a ratio: the rule is "is there enough actual
        ' room to be worth the I/O of rewriting?". Anchored entries (paths already present in
        ' the slot) bypass this check — that rewrite is mandatory to honor the no-duplicate
        ' contract regardless of how little room is left.
        '
        ' Examples (cap = 3 GB):
        '   - slot at 1.2 GB → 1.8 GB free ≥ 100 MB → fill it (caller's free entries land here).
        '   - slot at 2.5 GB → 500 MB free ≥ 100 MB → fill it (accept the 2.5 GB rewrite cost
        '     to avoid creating an extra fragmented slot).
        '   - slot at 2.95 GB → 50 MB free < 100 MB → leave it alone (rewriting 2.95 GB to
        '     squeeze in 50 MB is not worth it; the entries go to a new slot instead).
        Public Property MinFreeSpaceToFill As Long = 100L * 1024L * 1024L

        ' Callback invoked exactly once per NEW plugin slot that Pack creates. Signature is
        ' (pluginFilePath, game). The caller is expected to write the dummy plugin file at that
        ' path (e.g. via FO4_Base_Library.PluginWriter.WriteLightMasterDummy). When Pack reuses
        ' an existing plugin, the callback is NOT invoked.
        ' If null and a new slot is needed, Pack throws InvalidOperationException so the engine
        ' never ends up with archives that have no anchor plugin.
        Public Property PluginWriter As Action(Of String, GameKind)

        Public Property Overflow As ArchiveOverflowPolicy = ArchiveOverflowPolicy.SplitByPlugin

        ' When True, DiscoverSlots returns ONLY the slot whose plugin name equals ModBaseName
        ' exactly (slot 1). Numbered companion plugins (<ModBaseName>2.esp, <ModBaseName>3.esp,
        ' ...) are ignored even if they exist on disk — they are not considered for anchoring
        ' nor for free-pass distribution, and their archives are never read or rewritten.
        ' Combine with Overflow=ThrowOnExceed for "single archive set anchored to this exact
        ' plugin, never spawn or touch numbered companions" semantics.
        '
        ' Default False preserves the WM_ClonePack flow (numbered companion plugins are part
        ' of the design there). NPC_Manager sets True so its Save ESP only ever owns its own
        ' "<plugin> - Main.ba2" + "<plugin> - Textures.ba2" pair and leaves any prior
        ' numbered slots from previous experiments untouched.
        Public Property SingleAnchorOnly As Boolean = False

        ' Optional set of archive entry paths (as stored inside the archive, e.g.
        ' "Meshes\Actors\...\<id>.nif") to DROP from the output even though they exist in the previous
        ' archive — i.e. NOT preserved by the merge. Used by the NPC "mark to delete" flow to strip a
        ' removed NPC's stale FaceGen bake from the app's own target archive while leaving every other
        ' entry intact. When an exclude path is present in the previous archive, that bucket is forced to
        ' rewrite so the drop materializes even with no other changes. Nothing/empty = no exclusions
        ' (existing callers unaffected). Case-insensitive; normalized the same way archive paths are.
        Public Property ExcludePaths As HashSet(Of String) = Nothing
    End Class

    Public NotInheritable Class PackagerResult
        ' Archives that were (re)written this Pack call.
        Public ReadOnly Archives As New List(Of String)
        ' Archives skipped because the bundle was byte-identical to what was already on disk.
        Public ReadOnly Skipped As New List(Of String)
        ' Newly created dummy plugins (one per new slot). Existing plugin paths are NOT listed.
        Public ReadOnly Plugins As New List(Of String)

        ''' <summary>El tope por archive que esta corrida REALMENTE aplicó, en bytes.
        ''' <para>⛔ EXISTE PORQUE EL TOPE PUEDE NO SER EL QUE PIDIÓ EL LLAMADOR. En la rama BSA el formato
        ''' manda (2 GiB−1, ver <see cref="PackagerRequest.BsaMaxOffset"/>) y el pedido se capa. Antes eso se
        ''' hacía MUTANDO <c>req.MaxArchiveBytes</c> —el objeto del llamador— y en silencio: quien pedía
        ''' 3 GiB para Skyrim volvía con su request cambiado abajo y sin manera de enterarse. Ahora el
        ''' request no se toca y el número efectivo sale por acá.</para></summary>
        Public Property TopeAplicado As Long

        ''' <summary>True cuando <see cref="TopeAplicado"/> es MENOR que el <c>MaxArchiveBytes</c> pedido,
        ''' o sea cuando el formato bajó el tope. La UI lo puede decir sin comparar contra una constante
        ''' que no es suya.</summary>
        Public Property TopeFueCapado As Boolean
    End Class

    ''' <summary>Set of archive + plugin files in OutputDir whose names share a ModBaseName prefix.</summary>
    Public NotInheritable Class ArchiveSetInfo
        Public ReadOnly Archives As New List(Of String)   ' .ba2 / .bsa
        Public ReadOnly Plugins As New List(Of String)    ' .esp / .esm / .esl

        ''' <summary>Archivos que ACOMPAÑAN al set y que NINGÚN camino del packer consume ni borra:
        ''' EXCLUSIVAMENTE los <c>&lt;archive&gt;.ba2.bak</c> / <c>.bsa.bak</c> que dejó el rename de
        ''' 2.0.2 (hasta 3 GiB cada uno).
        ''' <para>⚠️ LOS <c>&lt;suelto&gt;.bak.unpack</c> NO ESTÁN ACÁ, y este párrafo decía dos cosas
        ''' falsas: que sí, y que los dejaba "el Unpack viejo". Los deja el Unpack ACTUAL, a propósito y
        ''' cada vez que pisa un suelto del usuario (ver el <c>PrimerSlotLibre</c> del cuerpo de
        ''' <c>Unpack</c>). Y no pueden entrar en esta lista por CONSTRUCCIÓN: viven bajo
        ''' <c>LooseDataDir</c> con el nombre del SUELTO, no bajo <c>OutputDir</c> con el prefijo del mod,
        ''' así que el barrido de abajo —que enumera <c>&lt;modBaseName&gt;*</c> en <c>OutputDir</c>— no
        ''' los ve. Los reporta <see cref="UnpackResult.Huerfanos"/>, que es quien conoce esa carpeta.
        ''' Son dos listas de dos caminos distintos.</para>
        ''' <para>⛔ ESTA LISTA NO BORRA NADA Y NO EXISTE PARA HABILITAR UN BORRADO. Es el REPORTE, y ahora
        ''' es verdad que alguien lo lee: <c>Wardrobe_Manager\WM_PackUnpack.Pack</c> los cuenta, suma su
        ''' tamaño, escribe la lista completa a un ARCHIVO de verdad
        ''' (<c>WM_PackUnpack.EscribirReporte</c> — al lado del exe, con fallback a <c>%TEMP%</c>) y
        ''' publica el aviso con esa RUTA en <c>WM_PackUnpack.UltimoAvisoHuerfanos</c>, que
        ''' <c>Config_Form</c> concatena al resumen persistente del Pack. NO va por <c>Logger</c>: en
        ''' Release está apagado y su setter descarta cualquier True, así que remitir ahí sería pedirle al
        ''' usuario algo imposible. Sin diálogo, porque ese camino no tiene ninguno.
        ''' Borrar archivos del disco del usuario es decisión del usuario, y un <c>.bak</c> de 2.0.2 puede
        ''' ser la única copia de un archive que aquel rename dejó a mitad — borrarlo a ciegas es
        ''' exactamente el daño que <c>EscrituraEnElLugar</c> existe para no cometer.</para>
        ''' <para>⚠️ Hasta esta ronda el párrafo de arriba decía "el llamador los muestra" y NO LOS LEÍA
        ''' NADIE: eran hasta 3 GiB por pieza ocupando disco sin que nada los nombrara. Un docstring que
        ''' describe un consumidor inexistente es una afirmación falsa, no una intención.</para>
        ''' <para>MEDIDO al escribir esto, sobre el disco real del usuario: <b>0 huérfanos</b> en el Data de
        ''' FO4 (61 <c>WM_ClonePack*</c> sanos, 89,87 GiB) y 0 en el de SSE. O sea que el defecto es LATENTE
        ''' acá y lo que esta lista cubre son las instalaciones que vienen de 2.0.2.</para></summary>
        Public ReadOnly Huerfanos As New List(Of String)
    End Class

    Public NotInheritable Class UnpackRequest
        ' Where the archive set lives (and where its plugins are). Typically the game's Data folder.
        Public Property OutputDir As String
        ' Same base name used by Pack: discovery uses the "<base>*" prefix to find slots.
        Public Property ModBaseName As String = "WM_ClonePack"
        ' Where to extract entries to as loose files. Each entry's FullPath is appended.
        Public Property LooseDataDir As String
    End Class

    Public NotInheritable Class UnpackResult
        Public ReadOnly LooseFilesWritten As New List(Of String)
        Public ReadOnly ArchivesRemoved As New List(Of String)
        Public ReadOnly PluginsRemoved As New List(Of String)

        ''' <summary>Los archives del set que SIGUEN EN DISCO al terminar: <c>info.Archives</c> menos
        ''' <see cref="ArchivesRemoved"/>. Se llena SIEMPRE, salga la corrida bien, mal o cancelada.
        ''' <para>⛔⛔ EXISTE PORQUE EL CONTRATO DEL LLAMADOR ES ASIMÉTRICO Y ESO DEJABA HUÉRFANOS.
        ''' <c>WM_PackUnpack.Unpack</c> DESREGISTRA los N archives del set ANTES de llamar —y tiene que
        ''' hacerlo, si no se siguen sirviendo entradas de archives que están por borrarse— y sólo vuelve
        ''' a registrar SUELTOS al final. Cuando la corrida sale temprano (el <c>Exit For</c> de un
        ''' archive fallido, o una CANCELACIÓN, que ni excepción tira) los archives POSTERIORES quedan
        ''' <b>vivos en disco y desmontados del diccionario</b>: su contenido es invisible para la app,
        ''' sin que nada haya fallado con ellos y sin nada en el resultado que permitiera remontarlos.
        ''' Con esta lista el llamador los vuelve a montar con <c>RegisterArchive</c>.</para>
        ''' <para>⛔ NO es lo mismo que "los que fallaron": un archive perfectamente sano que estaba
        ''' DESPUÉS del que cortó la corrida también entra acá, y es justamente el caso que se perdía.</para>
        ''' <para>Gate: <c>Tools\UnpackSueltosGate</c> U8 (la lista existe y los nombra) y
        ''' <c>Tools\WmEscrituraGate</c> D7/D7.1 (el llamador los remonta en los DOS caminos).</para></summary>
        Public ReadOnly ArchivesConservados As New List(Of String)

        ''' <summary>Respaldos que ESTA corrida dejó al pisar un suelto que ya existía y NO era nuestro:
        ''' <c>&lt;suelto&gt;.bak.unpack</c>, <c>….bak.unpack2</c>, <c>…3</c>… (primer slot LIBRE, así que un
        ''' segundo Unpack nunca destruye el respaldo del primero).
        ''' <para>⛔ SON DEL USUARIO Y SE QUEDAN: nadie los borra, a propósito — es la misma postura, y el
        ''' mismo costo declarado, que la copia heredada de <c>GuardarConCopia</c>.</para>
        ''' <para>SE MUESTRAN, y ahora es verdad: <c>Wardrobe_Manager\Config_Form.UnpackButton_Click</c> los
        ''' nombra en el label al salir bien y los lista enteros en el diálogo de un unpack PARCIAL
        ''' (<see cref="UnpackParcialException"/>). Hasta que ese llamador existió, este párrafo decía "para
        ''' que el llamador se los pueda mostrar" y NADIE los leía: eran archivos del usuario ocupando
        ''' disco sin que nada se lo dijera.</para></summary>
        Public ReadOnly CopiasDeSueltos As New List(Of String)

        ''' <summary><c>&lt;suelto&gt;.bak.unpack</c> que YA estaban en disco al empezar: los dejó una corrida
        ''' anterior.
        ''' <para>⛔ SE REPORTAN, NO SE BORRAN. Misma postura que <see cref="ArchiveSetInfo.Huerfanos"/>, y
        ''' el mismo lector: ver <see cref="CopiasDeSueltos"/>.</para></summary>
        Public ReadOnly Huerfanos As New List(Of String)

        ''' <summary>Entradas que NO se pudieron escribir, una línea por entrada, con archivo y causa.
        ''' Cuando esta lista no está vacía <c>Unpack</c> termina TIRANDO — pero recién al final, después de
        ''' haberle dado su chance a todo lo demás, y con este mismo resultado ADENTRO de la excepción
        ''' (<see cref="UnpackParcialException"/>). Ver el ⛔ del cuerpo de <c>Unpack</c>.</summary>
        Public ReadOnly Fallos As New List(Of String)
    End Class

    ''' <summary>El <c>Unpack</c> que termina con entradas fallidas, <b>llevando su resultado adentro</b>.
    ''' <para>⛔⛔ POR QUE NO ES UN <c>IOException</c> PELADO, QUE ES LO QUE HABIA. Este camino no es "no
    ''' pasó nada": todo lo que SÍ se extrajo ya está en disco. El llamador
    ''' (<c>Wardrobe_Manager\WM_PackUnpack.Unpack</c>) DESREGISTRA los archives del <c>FilesDictionary</c>
    ''' ANTES de llamar —tiene que hacerlo, para que no se sirvan entradas de un archive que se va a
    ''' borrar— y registra los sueltos DESPUÉS. Con la excepción pelada nunca llegaba a la segunda mitad:
    ''' el diccionario quedaba sin las entradas del archive Y sin las de los sueltos, o sea contenido en
    ''' disco que la app no ve, hasta un <c>Fill_Dictionary</c> completo. Llevando el resultado, el
    ''' llamador registra lo que se escribió y muestra los fallos: las dos cosas, sin mentir ninguna.</para>
    ''' <para>Deriva de <c>IOException</c> a propósito: quien ya lo atrapaba —el <c>Catch ex As Exception</c>
    ''' de <c>Config_Form.UnpackButton_Click</c> y cualquier otro— lo sigue atrapando igual, y el
    ''' <c>Message</c> es el mismo texto que antes.</para>
    ''' <para>Gate: <c>Tools\UnpackSueltosGate</c> U6.</para></summary>
    Public Class UnpackParcialException
        Inherits IOException

        ''' <summary>Lo que la corrida alcanzó a hacer: <c>LooseFilesWritten</c> (para registrar),
        ''' <c>Fallos</c> (para mostrar), <c>CopiasDeSueltos</c> y <c>Huerfanos</c> (para reportar), y los
        ''' archives/plugins que sí se borraron. Nunca es Nothing.</summary>
        Public ReadOnly Property Resultado As UnpackResult

        Public Sub New(mensaje As String, resultado As UnpackResult)
            MyBase.New(mensaje)
            Me.Resultado = If(resultado, New UnpackResult())
        End Sub
    End Class

    ''' <summary>
    ''' Single-archive incremental packer. Compares a bundle of VirtualEntry against an existing
    ''' BA2/BSA on disk and rewrites only when content changed. Unchanged entries are stream-copied
    ''' (compressed bytes lifted verbatim from the previous archive) to avoid recompression on
    ''' multi-GB rebuilds.
    ''' </summary>
    Public NotInheritable Class ArchivePackager
        ' Bethesda engine convention: a plugin "Foo.esp" auto-loads "Foo - Main.ba2" and
        ' "Foo - Textures.ba2" (FO4) or "Foo.bsa" + "Foo - Textures.bsa" (SSE). These suffixes
        ' are spec-of-engine, not game data; safe to hardcode.
        Public Const SUFFIX_BA2_MAIN As String = " - Main.ba2"
        Public Const SUFFIX_BA2_TEXTURES As String = " - Textures.ba2"
        Public Const EXT_BSA As String = ".bsa"

        Private Enum BucketKind
            BA2_GNRL
            BA2_DX10
            BSA
        End Enum

        Private Enum DiffKind
            Unchanged       ' bundle == archive contents; skip rewrite
            NeedsRewrite    ' adds, removes, or content changes detected
        End Enum

        Private NotInheritable Class DiffResult
            Public Property Kind As DiffKind
            ' Paths where the bundle entry has the same length + CRC32 as the existing archive
            ' entry. These are stream-copied verbatim from the ORIGINAL archive (see below).
            Public ReadOnly UnchangedPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            ' Paths that exist in the archive but are NOT in the bundle. The packager preserves
            ' them automatically (stream-copy from the ORIGINAL) — Pack semantics are "merge with
            ' existing", not "the bundle is the complete desired archive state".
            '
            ' ⛔ "THE ORIGINAL", NO ".bak". Todos estos comentarios decian `.bak` porque el packer
            ' RENOMBRABA el archive a `<archive>.bak` y leia de ahi. Eso se fue en 2.0.5 (el rename sacaba
            ' el archive de su mod bajo MO2 y cortaba el hardlink en Vortex — ver el ⛔ de PackOneArchive):
            ' hoy la fuente pass-through es el archive ORIGINAL, abierto para lectura EN SU LUGAR mientras
            ' se escribe el `.new` al lado. No queda ningun `.bak` en el camino.
            Public ReadOnly PreservePaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        End Class

        ' Per-slot plan built during distribution. Owns one plugin file path and 1..2 archive
        ' paths (FO4: Main + Textures; SSE: a single .bsa).
        Private NotInheritable Class PluginSlot
            Public Property SlotNumber As Integer    ' 1 = unsuffixed base, 2+ = numbered
            Public Property BaseName As String       ' e.g. "WM_ClonePack" or "WM_ClonePack2"
            Public Property PluginPath As String
            Public Property IsNew As Boolean         ' True if this slot did not exist on disk before Pack
            ' Existing-archive content used to anchor locked entries. Path → True if present.
            Public ReadOnly ExistingByBucket As New Dictionary(Of BucketKind, HashSet(Of String))
            ' Existing archive size in bytes (per bucket). Drives the size estimate during distribution.
            Public ReadOnly SizeByBucket As New Dictionary(Of BucketKind, Long)
            ' Entries assigned to this slot, grouped by bucket. Filled during distribution.
            Public ReadOnly AssignedByBucket As New Dictionary(Of BucketKind, List(Of VirtualEntry))
            ' Anchored entries (paths that already exist in this slot's archive) per bucket.
            ' Tracked separately from AssignedByBucket so the reuse threshold can ignore them
            ' when deciding whether to also accept free entries here: anchored forces rewrite
            ' regardless, so any free entries piggyback for free.
            Public ReadOnly AnchoredByBucket As New Dictionary(Of BucketKind, List(Of VirtualEntry))
        End Class

        ''' <summary>Empaqueta <paramref name="req"/> contra el archive set que ya está en
        ''' <c>OutputDir</c>: censa los slots, distribuye las entradas y reescribe SOLO los archives que
        ''' cambiaron. La semántica es de MERGE ("upsert"), no de reemplazo: lo que ya estaba en un archive
        ''' y no viene en el bundle se preserva (salvo que esté en <c>ExcludePaths</c>).
        ''' <para>⛔ UNA REQUEST VACÍA PUEDE TIRAR, Y NO ES UN BUG. Lo PRIMERO que hace este método —antes de
        ''' mirar si hay entradas— es barrer el set y recuperar los volcados cortados de corridas anteriores
        ''' (ver el ⛔ de <c>RecuperarVolcadosCortados</c> en el cuerpo). Si no había ningún par
        ''' <c>.new</c>/sello pendiente, una request vacía sigue siendo EXACTAMENTE el no-op de siempre; si
        ''' lo había, se reparó o se falla. Es deliberado: un archive truncado no se puede quedar roto
        ''' porque justo el pack que lo encontró no tenía nada que escribir. Un llamador que use
        ''' <c>Pack</c> vacío como sonda barata tiene que estar preparado para la excepción.</para>
        ''' <para>⛔ EL TOPE POR ARCHIVE PUEDE NO SER EL QUE PEDISTE. En la rama BSA lo baja el formato a
        ''' <see cref="PackagerRequest.BsaMaxOffset"/> (2 GiB−1). <b><paramref name="req"/> NO se modifica</b>:
        ''' el número que se aplicó sale en <see cref="PackagerResult.TopeAplicado"/> y
        ''' <see cref="PackagerResult.TopeFueCapado"/>.</para>
        ''' <para>Lo que devuelve cada lista de <see cref="PackagerResult"/>: <c>Archives</c> = los que ESTA
        ''' corrida reescribió y verificó; <c>Skipped</c> = los que quedaron byte-idénticos (diff Unchanged)
        ''' o los que quedaron vacíos por exclusión y se borraron; <c>Plugins</c> = SOLO los dummy nuevos
        ''' (un plugin que ya existía no se lista).</para>
        ''' <para>Fallos: cualquier error deja el archive entregable como estaba o conserva el par
        ''' <c>.new</c>/sello, que es la única copia íntegra del archive nuevo; nunca las dos cosas rotas a
        ''' la vez. Ver los ⛔ de <c>PackOneArchive</c> y <c>RecuperarVolcadoCortado</c>.</para></summary>
        Public Shared Function Pack(req As PackagerRequest) As PackagerResult
            ArgumentNullException.ThrowIfNull(req)
            If String.IsNullOrWhiteSpace(req.ModBaseName) Then Throw New ArgumentException("ModBaseName is empty.", NameOf(req))
            If String.IsNullOrWhiteSpace(req.OutputDir) Then Throw New ArgumentException("OutputDir is empty.", NameOf(req))
            If req.Entries Is Nothing Then Throw New ArgumentException("Entries is null.", NameOf(req))
            If req.MaxArchiveBytes <= 0 Then Throw New ArgumentException("MaxArchiveBytes must be positive.", NameOf(req))

            ' ⛔ EL TOPE LO DECIDE EL FORMATO, NO UNA PREFERENCIA. Acá el default eran 3 GiB para LOS DOS
            ' juegos, con un comentario que decía "the BSA hard limit remains 4 GB (u32 offsets)". Es
            ' falso: el canon dice `BSA_MAX_OFFSET = High(Integer)` (`wbBSArchive.pas:162`), o sea
            ' **2 GiB−1**, y `DefaultSplitSize` devuelve ese valor para `baSSE`. Su validación (`:1235`)
            ' lo dice con todas las letras: *"N file(s) start above 2 GB max allowed BSA size, they won't
            ' work or crash the game"*.
            ' El BA2 sí usa offsets de 64 bits y se queda en los 3 GiB de siempre — por eso el tope se
            ' baja SOLO en la rama BSA y FO4 no se mueve un byte.
            ' MEDIDO hoy: 29 `WM_ClonePack*` de 3,00 GiB en el Data de FO4 (BA2, sanos) y 0 archives por
            ' encima de 2 GiB−1 en SSE ⇒ el defecto es LATENTE, y el disparador es este mismo botón
            ' apuntando a Skyrim, donde la app ya demostró 29 veces que llena hasta el tope.
            '
            ' ⛔⛔ Y EL TOPE NO MUTA EL REQUEST DEL LLAMADOR. Acá había `req.MaxArchiveBytes = ...`: el
            ' packer le cambiaba el objeto a quien lo llamó, en silencio y sin devolver nada que lo dijera.
            ' Hoy los tres llamadores construyen una `PackagerRequest` nueva por corrida y ninguno vuelve a
            ' leer el campo después del Pack (medido: 0 lecturas de `req.MaxArchiveBytes` fuera de este
            ' archivo), asi que el defecto era LATENTE — pero el disparador es cualquiera que reutilice la
            ' request entre chunks, o que la muestre/loguee después. El tope efectivo vive en una LOCAL y
            ' viaja por parámetro; lo que se aplicó sale por el resultado.
            Dim topeEfectivo As Long = req.MaxArchiveBytes
            If req.Game = GameKind.SSE_BSA AndAlso topeEfectivo > PackagerRequest.BsaMaxOffset Then
                topeEfectivo = PackagerRequest.BsaMaxOffset
            End If

            EnsureDir(req.OutputDir & Path.DirectorySeparatorChar)

            ' ⛔ LA RECUPERACION VA ACA, ANTES DEL CENSO. Vivia adentro de PackOneArchive — o sea despues de
            ' DiscoverSlots, y detras del `Continue For` de EmitSlot que saltea el bucket sin trabajo. Tres
            ' defectos salian de esa posicion, no del protocolo:
            '   1. EL BUCKET SIN TRABAJO NUNCA REPARABA. Se cortaba el volcado del `- Textures.ba2`, la
            '      corrida siguiente traia solo mallas, ese bucket no recibia entradas y el archive truncado
            '      quedaba asi indefinidamente — con el juego cargandolo roto.
            '   2. EL CENSO LEIA EL TRUNCADO. DiscoverSlots toma SizeByBucket del archivo cortado y deja
            '      ExistingByBucket VACIO (su Catch trata el truncado como archive vacio). Una entrada que
            '      vivia en el `.new` pendiente se censaba como inexistente y se re-distribuia a otro slot;
            '      al recuperarse el `.new`, el MISMO path quedaba en DOS archives del set y se rompia el
            '      contrato de no-duplicados del anclaje.
            '   3. LA DISTRIBUCION DECIDIA CONTRA EL TAMAÑO TRUNCADO, asi que despues de recuperar el
            '      archive podia superar MaxArchiveBytes — en la rama BSA eso es pasarse de 2 GiB−1, que es
            '      crash del juego (ver el tope de aca arriba).
            ' Barriendo el set ENTERO antes del censo, el censo lee el archive ya recuperado y las tres se
            ' cierran juntas, sin maquinaria extra.
            RecuperarVolcadosCortados(req)

            Dim result As New PackagerResult() With {
                .TopeAplicado = topeEfectivo,
                .TopeFueCapado = (topeEfectivo < req.MaxArchiveBytes)
            }
            ' Delete-only Pack: an empty bundle with ExcludePaths still runs (it strips the excluded entries
            ' from the existing target archive). Only a truly empty request (no entries AND no exclusions) is a no-op.
            ' ⛔ EL NO-OP ES DE LA DISTRIBUCION, NO DE LA RECUPERACION: el barrido de arriba ya corrio. Si no
            ' habia ningun par `.new`/sello pendiente, esta request vacia sigue siendo EXACTAMENTE el no-op
            ' de siempre; si lo habia, se reparo o se tiro. Es deliberado: "esta corrida no trae trabajo" es
            ' el caso degenerado del mismo defecto que "este bucket no trae trabajo", y un archive truncado
            ' no se puede quedar roto porque justo el pack que lo encontro no tenia nada que escribir.
            If req.Entries.Count = 0 AndAlso (req.ExcludePaths Is Nothing OrElse req.ExcludePaths.Count = 0) Then Return result

            Dim buckets = BucketsForGame(req.Game)

            ' --- Discover existing plugin slots in the output directory. ---
            Dim slots = DiscoverSlots(req, buckets)

            ' --- Distribute every bundle entry to a slot/bucket pair. ---
            DistributeEntries(req, slots, buckets, topeEfectivo)

            ' --- Emit each affected slot. Order: lower SlotNumber first, so the unsuffixed plugin
            '     gets written before its numbered companions. ---
            slots.Sort(Function(a, b) a.SlotNumber.CompareTo(b.SlotNumber))
            For Each slot In slots
                EmitSlot(req, slot, buckets, result)
            Next

            Return result
        End Function

        Private Shared Function BucketsForGame(g As GameKind) As BucketKind()
            Select Case g
                Case GameKind.FO4_BA2 : Return New BucketKind() {BucketKind.BA2_GNRL, BucketKind.BA2_DX10}
                Case GameKind.SSE_BSA : Return New BucketKind() {BucketKind.BSA}
                Case Else : Throw New ArgumentOutOfRangeException(NameOf(g))
            End Select
        End Function

        Private Shared Function ArchivePathFor(slot As PluginSlot, bucket As BucketKind, outputDir As String) As String
            Select Case bucket
                Case BucketKind.BA2_GNRL : Return Path.Combine(outputDir, slot.BaseName & SUFFIX_BA2_MAIN)
                Case BucketKind.BA2_DX10 : Return Path.Combine(outputDir, slot.BaseName & SUFFIX_BA2_TEXTURES)
                Case BucketKind.BSA : Return Path.Combine(outputDir, slot.BaseName & EXT_BSA)
                Case Else : Throw New ArgumentOutOfRangeException(NameOf(bucket))
            End Select
        End Function

        Private Shared Function BucketForEntry(ve As VirtualEntry, game As GameKind) As BucketKind
            If game = GameKind.SSE_BSA Then Return BucketKind.BSA
            Return If(IsTextureEntry(ve), BucketKind.BA2_DX10, BucketKind.BA2_GNRL)
        End Function

        Private Shared Function SlotName(slotNumber As Integer, baseName As String) As String
            ' Slot 1 uses the bare base ("WM_ClonePack"); 2..N append the number ("WM_ClonePack2").
            ' This matches how nested mods stack on Bethesda's auto-discovery convention.
            If slotNumber <= 1 Then Return baseName
            Return baseName & slotNumber.ToString()
        End Function

        ' --------------------------------------------------------------------------------------
        ' Discovery: enumerate "<base>*.esp" in OutputDir, parse the slot number, list the
        ' companion archive contents (paths only) so we can anchor locked entries to them later.
        ' --------------------------------------------------------------------------------------
        Private Shared Function DiscoverSlots(req As PackagerRequest, buckets As BucketKind()) As List(Of PluginSlot)
            Dim slots As New List(Of PluginSlot)
            If Not Directory.Exists(req.OutputDir) Then Return slots

            For Each pluginPath In Directory.EnumerateFiles(req.OutputDir, req.ModBaseName & "*.esp", SearchOption.TopDirectoryOnly)
                Dim slotNumber = ParseSlotNumber(Path.GetFileNameWithoutExtension(pluginPath), req.ModBaseName)
                If slotNumber <= 0 Then Continue For
                ' SingleAnchorOnly: ignore numbered companion slots (slot >= 2). Caller wants this
                ' Pack to own only the exact-name plugin's archive set and leave the rest alone.
                If req.SingleAnchorOnly AndAlso slotNumber <> 1 Then Continue For

                Dim slot As New PluginSlot With {
                    .SlotNumber = slotNumber,
                    .BaseName = SlotName(slotNumber, req.ModBaseName),
                    .PluginPath = pluginPath,
                    .IsNew = False
                }
                For Each b In buckets
                    slot.AssignedByBucket(b) = New List(Of VirtualEntry)()
                    slot.AnchoredByBucket(b) = New List(Of VirtualEntry)()
                    slot.ExistingByBucket(b) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    slot.SizeByBucket(b) = 0L

                    Dim ap = ArchivePathFor(slot, b, req.OutputDir)
                    If File.Exists(ap) Then
                        slot.SizeByBucket(b) = New FileInfo(ap).Length
                        Try
                            Using fs = File.OpenRead(ap)
                                Using reader As New BethesdaReader(fs)
                                    For Each ae In reader.EntriesFiles
                                        slot.ExistingByBucket(b).Add(NormalizePath(ae.FullPath))
                                    Next
                                End Using
                            End Using
                            ' Narrowed: only genuine format/parse failures mean "corrupt → treat as empty".
                            ' EndOfStreamException (truncated header/table) derives from IOException but is
                            ' a format problem, so it is caught explicitly here. A real IOException (file
                            ' locked / in use) is intentionally NOT caught — it must propagate so the caller
                            ' surfaces a true error instead of silently anchoring against an "empty" archive.
                        Catch ex As InvalidDataException
                            ' Unrecognized magic / bad BA2 or BSA structure → treat as empty for anchoring.
                            ' PackOneArchive will fail more loudly later if it can't diff the file.
                        Catch ex As EndOfStreamException
                            ' Truncated header / file table → same as above.
                        Catch ex As NotSupportedException
                            ' Unsupported variant (GNMF, unexpected chunk header size, BSA != v105) → empty.
                        End Try
                    End If
                Next
                slots.Add(slot)
            Next

            Return slots
        End Function

        Private Shared Function ParseSlotNumber(stem As String, baseName As String) As Integer
            ' "WM_ClonePack" → 1; "WM_ClonePack2" → 2; "WM_ClonePackFoo" → 0 (not a slot).
            If String.Equals(stem, baseName, StringComparison.OrdinalIgnoreCase) Then Return 1
            If Not stem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) Then Return 0
            Dim suffix = stem.Substring(baseName.Length)
            Dim n As Integer
            If Integer.TryParse(suffix, n) AndAlso n >= 2 Then Return n
            Return 0
        End Function

        ' --------------------------------------------------------------------------------------
        ' Distribute each bundle entry to a slot. Two kinds of entries:
        '
        '   - Anchored: entry.path already exists in some slot's archive. To honor the
        '     no-duplicate contract (Pack semantics: at most one copy of each path across the
        '     archive set), the entry must replace the existing one in that slot. Anchor forces
        '     a rewrite of the slot and bypasses the reuse threshold — the rewrite is mandatory
        '     regardless of how big or small the new content is. ComputeDiff later decides whether
        '     the slot actually needs to be rewritten (CRC match → Skipped) or really did change.
        '
        '   - Free: entry.path not yet present in any archive. Distribution chooses where to
        '     park it. Tentative pass: first slot with enough room (compressed size, exact when
        '     BundleAlreadyCompressed = True). Validation pass: for slots that ended up with
        '     ONLY free entries, accept reuse only if the proposed bytes-to-add ≥ slot.SizeByBucket
        '     × ReuseThreshold. Otherwise revert and redistribute, excluding the rejected slot.
        '
        ' Slot 1 is always created (with IsNew=True if it didn't exist on disk) so the anchor
        ' plugin is "<base>.esp" without a number prefix.
        ' --------------------------------------------------------------------------------------
        ''' <summary><paramref name="topeEfectivo"/> es el tope por archive que ESTA corrida aplica: el
        ''' <c>MaxArchiveBytes</c> pedido, o el del formato si el juego lo baja (BSA: 2 GiB−1). ⛔ LOS CINCO
        ''' usos de acá adentro leen el parámetro y NINGUNO vuelve a `req.MaxArchiveBytes`: si uno solo se
        ''' escapa, el tope queda partido en dos y es peor que la mutación que esto vino a sacar.</summary>
        Private Shared Sub DistributeEntries(req As PackagerRequest, slots As List(Of PluginSlot),
                                             buckets As BucketKind(), topeEfectivo As Long)
            ' Ensure slot 1 always exists.
            Dim hasSlot1 = slots.Any(Function(s) s.SlotNumber = 1)
            If Not hasSlot1 Then
                slots.Add(NewSlot(1, req, buckets))
            End If

            ' --- PHASE 1: anchor by path ---------------------------------------------------------
            ' Anchored entries are recorded in slot.AnchoredByBucket and contribute their compressed
            ' size to slot.SizeByBucket immediately, so subsequent free-pass placement sees the
            ' correct projected size for the slot.
            Dim free As New List(Of VirtualEntry)()
            For Each ve In req.Entries
                Dim bucket = BucketForEntry(ve, req.Game)
                Dim p = NormalizePath(ve.FullPath)
                Dim anchorSlot As PluginSlot = Nothing
                For Each slot In slots
                    Dim existingPaths As HashSet(Of String) = Nothing
                    If slot.ExistingByBucket.TryGetValue(bucket, existingPaths) AndAlso existingPaths.Contains(p) Then
                        anchorSlot = slot
                        Exit For
                    End If
                Next
                If anchorSlot IsNot Nothing Then
                    anchorSlot.AnchoredByBucket(bucket).Add(ve)
                    ' Anchored entries replace the existing entry, but the slot's projected
                    ' compressed size doesn't grow by the full entry size (the existing entry
                    ' is going away). For the free-pass placement, leave SizeByBucket as the
                    ' on-disk size — that's the "slot size after anchor rewrite" upper bound
                    ' and a safe over-estimate.
                Else
                    free.Add(ve)
                End If
            Next

            ' --- PHASE 2: tentative free distribution (linear, exact compressed size) ------------
            ' Sort free largest-first so big entries get their pick, smaller ones fill what's left.
            free.Sort(Function(a, b) EntryProjectedSize(b, req).CompareTo(EntryProjectedSize(a, req)))

            ' Per slot we track how many bytes of free entries we've tentatively placed there.
            Dim proposedFreeBytes As New Dictionary(Of PluginSlot, Long)
            Dim proposedFreeBuckets As New Dictionary(Of PluginSlot, Long)
            Dim proposedFreeEntries As New Dictionary(Of PluginSlot, List(Of VirtualEntry))
            For Each s In slots
                proposedFreeBytes(s) = 0L
                proposedFreeEntries(s) = New List(Of VirtualEntry)()
            Next

            For Each ve In free
                Dim bucket = BucketForEntry(ve, req.Game)
                Dim addSize As Long = EntryProjectedSize(ve, req)

                Dim chosen As PluginSlot = Nothing
                For Each slot In slots
                    Dim cur As Long = slot.SizeByBucket(bucket) + proposedFreeBytes(slot)
                    If cur + addSize <= topeEfectivo Then
                        chosen = slot
                        Exit For
                    End If
                Next

                If chosen Is Nothing Then
                    If req.Overflow = ArchiveOverflowPolicy.ThrowOnExceed Then
                        Throw New InvalidOperationException(
                            $"Bundle exceeds MaxArchiveBytes ({topeEfectivo:N0} bytes) and Overflow=ThrowOnExceed.")
                    End If
                    Dim nextNumber As Integer = (slots.Max(Function(s) s.SlotNumber)) + 1
                    chosen = NewSlot(nextNumber, req, buckets)
                    slots.Add(chosen)
                    proposedFreeBytes(chosen) = 0L
                    proposedFreeEntries(chosen) = New List(Of VirtualEntry)()
                End If

                proposedFreeBytes(chosen) = proposedFreeBytes(chosen) + addSize
                proposedFreeEntries(chosen).Add(ve)
            Next

            ' --- PHASE 3: validate reuse against minimum-free-space rule -------------------------
            ' For each slot that has free entries proposed but NO anchored entries, accept the
            ' rewrite only if the slot has enough REAL free space (cap minus existing on-disk
            ' size) to be worth the I/O cost. Anchored slots are always rewritten regardless.
            Dim toRevert As New List(Of VirtualEntry)()
            Dim rejectedSlots As New HashSet(Of PluginSlot)()
            For Each slot In slots.ToList()
                Dim freeList = proposedFreeEntries(slot)
                If freeList.Count = 0 Then Continue For

                Dim hasAnyAnchor As Boolean = False
                For Each b In buckets
                    If slot.AnchoredByBucket(b).Count > 0 Then
                        hasAnyAnchor = True
                        Exit For
                    End If
                Next

                ' Slot already being rewritten by anchor → free entries piggyback for free.
                If hasAnyAnchor Then Continue For

                ' Existing-archive footprint: sum across buckets. 0 = brand new slot created in
                ' this Pack (no rewrite cost) → accept anything.
                Dim totalExistingSize As Long = 0L
                For Each b In buckets
                    totalExistingSize += slot.SizeByBucket(b)
                Next
                If totalExistingSize = 0L Then Continue For

                ' Real free space available in this slot at the cap. If below the configured
                ' minimum, the rewrite cost (moving totalExistingSize bytes) doesn't justify
                ' squeezing a tiny amount of new content in.
                Dim freeSpace As Long = topeEfectivo - totalExistingSize
                If freeSpace >= req.MinFreeSpaceToFill Then Continue For

                ' Not enough room to be worth it — revert this slot's free proposals.
                toRevert.AddRange(freeList)
                proposedFreeBytes(slot) = 0L
                proposedFreeEntries(slot).Clear()
                rejectedSlots.Add(slot)
            Next

            ' --- PHASE 4: redistribute reverted entries, excluding rejected slots ----------------
            If toRevert.Count > 0 Then
                toRevert.Sort(Function(a, b) EntryProjectedSize(b, req).CompareTo(EntryProjectedSize(a, req)))
                For Each ve In toRevert
                    Dim bucket = BucketForEntry(ve, req.Game)
                    Dim addSize As Long = EntryProjectedSize(ve, req)

                    Dim chosen As PluginSlot = Nothing
                    For Each slot In slots
                        If rejectedSlots.Contains(slot) Then Continue For
                        Dim cur As Long = slot.SizeByBucket(bucket) + proposedFreeBytes(slot)
                        If cur + addSize <= topeEfectivo Then
                            chosen = slot
                            Exit For
                        End If
                    Next

                    If chosen Is Nothing Then
                        If req.Overflow = ArchiveOverflowPolicy.ThrowOnExceed Then
                            Throw New InvalidOperationException(
                                $"Bundle exceeds MaxArchiveBytes after threshold reject ({topeEfectivo:N0} bytes).")
                        End If
                        Dim nextNumber As Integer = (slots.Max(Function(s) s.SlotNumber)) + 1
                        chosen = NewSlot(nextNumber, req, buckets)
                        slots.Add(chosen)
                        proposedFreeBytes(chosen) = 0L
                        proposedFreeEntries(chosen) = New List(Of VirtualEntry)()
                    End If

                    proposedFreeBytes(chosen) = proposedFreeBytes(chosen) + addSize
                    proposedFreeEntries(chosen).Add(ve)
                Next
            End If

            ' --- PHASE 5: feed assignments into per-slot bundles ---------------------------------
            For Each slot In slots
                For Each b In buckets
                    For Each ve In slot.AnchoredByBucket(b)
                        slot.AssignedByBucket(b).Add(ve)
                    Next
                Next
                For Each ve In proposedFreeEntries(slot)
                    Dim bucket = BucketForEntry(ve, req.Game)
                    slot.AssignedByBucket(bucket).Add(ve)
                Next
            Next
        End Sub

        ' Projected on-disk size for a single entry, used during distribution and threshold checks.
        ' When BundleAlreadyCompressed = True the caller already populated PreCompressed* fields
        ' with exact byte counts — we use them verbatim. Otherwise fall back to the legacy estimate
        ' (raw size × CompressionRatioEstimate). PayloadSource entries are pre-compressed by
        ' definition (stream-copied from another archive) and report exact byte counts via Length.
        Private Shared Function EntryProjectedSize(ve As VirtualEntry, req As PackagerRequest) As Long
            If ve.PayloadSource IsNot Nothing Then
                Return ve.PayloadSource.Length
            End If
            If req.BundleAlreadyCompressed OrElse ve.PreCompressed Then
                ' PreCompressedCompSize = 0 means stored raw — the on-disk size equals decomp size.
                If ve.PreCompressedCompSize > 0UI Then
                    Return CLng(ve.PreCompressedCompSize)
                End If
                Return CLng(ve.PreCompressedDecompSize)
            End If
            Dim raw As Long = If(ve.Data Is Nothing, 0L, ve.Data.LongLength)
            Return CLng(raw * req.CompressionRatioEstimate)
        End Function

        Private Shared Function NewSlot(slotNumber As Integer, req As PackagerRequest, buckets As BucketKind()) As PluginSlot
            Dim baseName = SlotName(slotNumber, req.ModBaseName)
            Dim slot As New PluginSlot With {
                .SlotNumber = slotNumber,
                .BaseName = baseName,
                .PluginPath = Path.Combine(req.OutputDir, baseName & ".esp"),
                .IsNew = Not File.Exists(Path.Combine(req.OutputDir, baseName & ".esp"))
            }
            For Each b In buckets
                slot.AssignedByBucket(b) = New List(Of VirtualEntry)()
                slot.AnchoredByBucket(b) = New List(Of VirtualEntry)()
                slot.ExistingByBucket(b) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                slot.SizeByBucket(b) = 0L
            Next
            Return slot
        End Function

        ' --------------------------------------------------------------------------------------
        ' Emit one slot: per bucket → PackOneArchive. After all buckets, if the slot is new,
        ' invoke the PluginWriter callback so the dummy .esp ends up next to the archives.
        ' --------------------------------------------------------------------------------------
        Private Shared Sub EmitSlot(req As PackagerRequest, slot As PluginSlot, buckets As BucketKind(), result As PackagerResult)
            Dim emittedAny As Boolean = False
            For Each bucket In buckets
                Dim sub_ As List(Of VirtualEntry) = slot.AssignedByBucket(bucket)
                ' Process the bucket when it has new/changed entries OR when an ExcludePath targets an entry
                ' that already exists in this bucket's archive (delete-only rewrite to strip it). Otherwise skip.
                Dim hasExclusionsHere = BucketHasExclusions(req, slot, bucket)
                If sub_.Count = 0 AndAlso Not hasExclusionsHere Then Continue For

                Dim archivePath = ArchivePathFor(slot, bucket, req.OutputDir)
                PackOneArchive(archivePath, sub_, bucket, result, req.Ba2Version, req.ExcludePaths)
                emittedAny = True
            Next

            If slot.IsNew AndAlso emittedAny Then
                If req.PluginWriter Is Nothing Then
                    Throw New InvalidOperationException(
                        $"Pack would create a new plugin '{slot.PluginPath}' but PackagerRequest.PluginWriter is null.")
                End If
                req.PluginWriter.Invoke(slot.PluginPath, req.Game)
                result.Plugins.Add(slot.PluginPath)
            End If
        End Sub

        ' --------------------------------------------------------------------------------------
        ' Per-archive flow: diff vs existing → skip / rewrite. On rewrite, build the entry list (mixing
        ' pass-through + fresh) reading from the CURRENT archive, write it to a sibling `.new`, verify it,
        ' dump it over the original in place and verify the delivered file. The original is never renamed
        ' nor deleted: that is what keeps it inside its mod under Mod Organizer and linked under Vortex.
        ' On failure the `.new` is kept — it is the only intact copy — and the next run picks it up
        ' (RecuperarVolcadoCortado).
        ' --------------------------------------------------------------------------------------
        ' True when any ExcludePath matches an entry that currently exists in this slot/bucket's archive —
        ' so a delete-only rewrite is needed to strip it. Normalizes the exclude paths the same way archive
        ' paths are stored (case + separator) before comparing against the discovered existing set.
        Private Shared Function BucketHasExclusions(req As PackagerRequest, slot As PluginSlot, bucket As BucketKind) As Boolean
            If req.ExcludePaths Is Nothing OrElse req.ExcludePaths.Count = 0 Then Return False
            Dim existing As HashSet(Of String) = Nothing
            If Not slot.ExistingByBucket.TryGetValue(bucket, existing) OrElse existing Is Nothing OrElse existing.Count = 0 Then Return False
            For Each ex In req.ExcludePaths
                If existing.Contains(NormalizePath(ex)) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Sello que marca que un `.new` PASO su verificacion y por lo tanto se puede volcar en la
        ''' corrida siguiente. Sin el, un `.new` que quedo de un verify FALLIDO se levantaria como si fuera
        ''' bueno.</summary>
        Private Const SUFIJO_SELLO As String = ".ok"

        ''' <summary>Sufijo del respaldo que <c>Unpack</c> deja cuando el suelto que va a escribir YA existía
        ''' y no era nuestro. Es RETENCIÓN, no red de crash: se queda en disco hasta que el usuario lo
        ''' borre, igual que la copia heredada de <c>GuardarConCopia</c> (ver su ⚠️ "EL COSTO"). Por eso NO
        ''' se usa <c>SufijoCopia</c>: ese lo administra <c>GuardarConCopia</c>, que borra su copia al salir
        ''' bien — mezclarlos haría que una escritura exitosa se llevara este respaldo puesto.</summary>
        Private Const SUFIJO_BAK_UNPACK As String = ".bak.unpack"

        ''' <summary>Magia + version del sello. El sello es metadata INTERNA de la app (no es un formato del
        ''' juego), asi que la forma se elige — lo que no se elige es que tenga que ser auto-describible.
        ''' <para>⛔ Y ES LO QUE DISTINGUE UN SELLO VIEJO. Hasta 2.0.7 el sello se escribia con
        ''' <c>File.WriteAllBytes(..., Array.Empty(Of Byte)())</c>: CERO BYTES, o sea que no registraba NADA.
        ''' Un archivo vacio no puede empezar con esta magia, asi que la deteccion es por construccion y no
        ''' hay heuristica ninguna.</para></summary>
        Private Const MAGIA_SELLO As String = "NPCMSEAL1"

        ''' <summary>Lo que el sello REGISTRA del `.new`: su largo y su SHA-256. Es todo lo que hace falta
        ''' para contestar de forma DETERMINISTA las dos preguntas que antes se contestaban por muestreo:
        ''' "¿este `.new` es el que verifico la corrida muerta?" y "¿el entregable YA es ese `.new`?".
        ''' <para>⛔ POR QUE NO ALCANZA EL LARGO SOLO, y no es opinion mia: la ley ya esta escrita en
        ''' <c>EscrituraEnElLugar.MismoContenido</c> — <i>"NO alcanza con comparar el tamano… dos archivos
        ''' distintos con la misma longitud existen, y aca el precio de equivocarse es borrar el unico
        ''' ejemplar del dato del usuario"</i>. Es exactamente esta situacion: del lado equivocado de esa
        ''' comparacion se borra el `.new`, que es la unica copia integra del archive nuevo.</para>
        ''' <para>El largo va igual y va PRIMERO porque es el discriminante barato: descarta el caso comun
        ''' (el volcado cortado) sin leer un byte de payload.</para></summary>
        Private NotInheritable Class RegistroDeSello
            Public ReadOnly Largo As Long
            Public ReadOnly HashHex As String

            Public Sub New(largo As Long, hashHex As String)
                Me.Largo = largo
                Me.HashHex = hashHex
            End Sub

            Public Function Coincide(otro As RegistroDeSello) As Boolean
                If otro Is Nothing Then Return False
                Return Largo = otro.Largo AndAlso
                       String.Equals(HashHex, otro.HashHex, StringComparison.OrdinalIgnoreCase)
            End Function

            Public Overrides Function ToString() As String
                Return $"{Largo:N0} B / {HashHex.Substring(0, Math.Min(16, HashHex.Length))}…"
            End Function
        End Class

        ''' <summary>Largo + SHA-256 de un archivo, en UNA lectura secuencial.
        ''' <para>⚠️ EL COSTO, dicho y no escondido. La idea original era hashear AL VUELO mientras
        ''' <c>WriteArchive</c> escribe, para que el sello saliera gratis. <b>No se puede</b>, y no es una
        ''' opinion: los dos writers SALTAN por el stream de salida para parchear offsets ya escritos
        ''' (<c>Ba2Writer.vb:331,333,543,545,761,763</c> y <c>BSAWriter.vb:371,383,400,423,445</c>), asi que
        ''' los bytes no pasan una sola vez ni en orden y un hash incremental daria cualquier cosa. Por eso
        ''' esto es una relectura completa del `.new`: <b>+1·Σ leidos por archive reescrito</b>, encima de
        ''' los 2·Σ escritos + 1·Σ leidos que el volcado ya paga. El numero real lo mide
        ''' <c>Tools\PackVolcadoCostoProbe</c>.</para>
        ''' <para>SHA-256 y no CRC32: <c>IncrementalHash</c> ya viene incremental de fabrica, y el CRC32 de
        ''' la libreria (<c>Ba2WriterCommon.Crc32Bytes</c>) toma un array entero — usarlo obligaria a
        ''' escribir una version incremental nueva para no materializar 3 GiB en RAM.</para></summary>
        Private Shared Function HuellaDeArchivo(path As String) As RegistroDeSello
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Read,
                                       FileShare.Read Or FileShare.Delete, 1024 * 1024)
                Using hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    Dim buffer(1024 * 1024 - 1) As Byte
                    Dim total As Long = 0
                    Do
                        Dim n = fs.Read(buffer, 0, buffer.Length)
                        If n <= 0 Then Exit Do
                        hash.AppendData(buffer, 0, n)
                        total += n
                    Loop
                    Return New RegistroDeSello(total, Convert.ToHexString(hash.GetHashAndReset()))
                End Using
            End Using
        End Function

        ''' <summary>Escribe el sello y lo SINCRONIZA. Contenido: una linea ASCII
        ''' <c>NPCMSEAL1 &lt;largo&gt; &lt;sha256-hex&gt;</c>.
        ''' <para>⛔ EL ORDEN ES LA LEY Y NO ES DECORATIVO: el `.new` se sincroniza ANTES de que esto corra
        ''' (ver <c>PackOneArchive</c>). Si el sello llegara al plato antes que los bytes que describe, el
        ''' par volveria a mentir exactamente como mentia con el sello vacio — solo que ahora con
        ''' autoridad.</para></summary>
        Private Shared Sub EscribirSelloSincronizado(selloPath As String, registro As RegistroDeSello)
            Dim linea = $"{MAGIA_SELLO} {registro.Largo.ToString(Globalization.CultureInfo.InvariantCulture)} {registro.HashHex}" & vbLf
            Dim bytes = Text.Encoding.ASCII.GetBytes(linea)
            Using fs As New FileStream(selloPath, FileMode.Create, FileAccess.Write, FileShare.Read)
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush(True)
            End Using
        End Sub

        ''' <summary>Lee el sello. Devuelve <c>Nothing</c> cuando lo que hay en disco NO es un sello de este
        ''' formato — el vacio de 2.0.7 incluido.
        ''' <para>⛔ UN SELLO QUE NO ESTA EN ESTE FORMATO NO PRUEBA NADA, Y POR LO TANTO NO ES UN SELLO. No
        ''' hay modo legacy que le crea "un poquito" al sello vacio: creerle era justamente el defecto
        ''' —autorizaba a volcar a ciegas contra un control que mira el 0,2 % de las entradas—. El llamador
        ''' lo trata como el caso YA legislado `.new` SIN sello: se descarta, no se vuelca.</para>
        ''' <para>Costo declarado de esa decision: si alguien actualiza justo con un par pendiente de la
        ''' version anterior, pierde ESA recuperacion y tiene que re-empaquetar. Su archive entregable no se
        ''' toca. MEDIDO al escribir esto: 0 pares pendientes en las dos carpetas Data del usuario.</para>
        ''' <para>No distingue "no existe" de "esta corrupto" a proposito: las dos respuestas son la misma
        ''' —no hay sello— y el llamador hace lo mismo en los dos casos.</para></summary>
        Private Shared Function LeerSello(selloPath As String) As RegistroDeSello
            Try
                Dim fi As New FileInfo(selloPath)
                ' Un sello sano son ~80 bytes. El tope corta un archivo cualquiera que haya caido con ese
                ' nombre antes de leerlo entero; no es una tolerancia sobre el contenido.
                If Not fi.Exists OrElse fi.Length = 0 OrElse fi.Length > 4096 Then Return Nothing

                Dim texto = File.ReadAllText(selloPath, Text.Encoding.ASCII).Trim()
                Dim partes = texto.Split(New Char() {" "c}, StringSplitOptions.RemoveEmptyEntries)
                If partes.Length <> 3 Then Return Nothing
                If Not String.Equals(partes(0), MAGIA_SELLO, StringComparison.Ordinal) Then Return Nothing

                Dim largo As Long
                If Not Long.TryParse(partes(1), Globalization.NumberStyles.None,
                                     Globalization.CultureInfo.InvariantCulture, largo) Then Return Nothing
                If largo <= 0 Then Return Nothing

                ' SHA-256 en hex son 64 caracteres. Cualquier otra cosa no es este formato.
                If partes(2).Length <> 64 Then Return Nothing
                For Each ch In partes(2)
                    If Not Uri.IsHexDigit(ch) Then Return Nothing
                Next

                Return New RegistroDeSello(largo, partes(2))
            Catch
                ' No poder leer el sello es "no hay sello": la respuesta segura es la misma.
                Return Nothing
            End Try
        End Function

        ''' <summary>FlushFileBuffers sobre un archivo ya cerrado. Se abre con <c>OpenOrCreate</c> y NO se
        ''' trunca: es la misma forma que usa <c>EscrituraEnElLugar.Sincronizar</c>.
        ''' <para>⛔ ACA NO ES BEST-EFFORT. En <c>EscrituraEnElLugar</c> el fallo se traga porque la copia ya
        ''' esta escrita y verificada por tamaño; aca el `.new` esta por convertirse en "la unica copia
        ''' integra" y el sello esta por AFIRMARLO. Si no se pudo sincronizar, esa afirmacion no se puede
        ''' sostener y el sello NO se escribe.</para></summary>
        Private Shared Sub SincronizarArchivo(path As String)
            Using fs As New FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
                fs.Flush(True)
            End Using
        End Sub

        ''' <summary>Exige que el ENTREGABLE sea, byte por byte, el archive que el sello registra. Es el
        ''' control que corre DESPUES de cada volcado — el del pack fresco y el de la recuperacion — y
        ''' reemplaza al <c>VerifyArchive</c> posterior al volcado, que muestreaba 3 entradas de N.
        ''' <para>Cuesta una lectura completa del entregable. Es la misma Σ que el volcado acaba de escribir
        ''' y la unica forma de afirmar lo que el mensaje de error afirma.</para>
        ''' <para>⛔ "NO PUDE LEER" NO ES "QUEDO A MEDIAS", y el que las confundia era ESTE metodo. La
        ''' lectura del entregable puede fallar por ACCESO —el antivirus escaneando el .ba2 recien
        ''' escrito, un lector en vuelo— y esa excepcion subia cruda a los dos llamadores, que la
        ''' reportaban como "el volcado se corto y el entregable quedo a medias" sobre un archive que
        ''' probablemente esta perfecto. Es la misma distincion que <see cref="EsFalloTransitorio"/> ya
        ''' aplica en <c>RecuperarVolcadoCortado</c> para el `.new`; faltaba de este lado. Nada se toco:
        ''' el par se conserva y el reintento es gratis.</para></summary>
        Private Shared Sub ExigirEntregableIgualAlSello(archivePath As String, newPath As String,
                                                        registro As RegistroDeSello)
            Dim huella As RegistroDeSello
            Try
                huella = HuellaDeArchivo(archivePath)
            Catch ex As Exception When EsFalloTransitorio(ex)
                Throw New IOException(
                    $"no se pudo LEER '{Path.GetFileName(archivePath)}' para comprobarlo contra su sello " &
                    "despues del volcado. El entregable NO se declara roto: no se pudo mirar. El `.new` y " &
                    "su sello se CONSERVAN; cerra lo que tenga tomado el archive y re-empaqueta." &
                    Environment.NewLine & ex.Message, ex)
            End Try
            If registro.Coincide(huella) Then Return
            Throw New InvalidDataException(
                $"el volcado de '{Path.GetFileName(newPath)}' sobre '{Path.GetFileName(archivePath)}' no " &
                $"dejo el archive completo (sello: {registro}; entregable: {huella}). El `.new` y su sello " &
                "se CONSERVAN (es la unica copia integra); re-empaqueta para que la recuperacion lo vuelva " &
                "a intentar.")
        End Sub

        ''' <summary>True cuando el fallo es de ACCESO (alguien tiene el archivo tomado, el antivirus lo
        ''' esta escaneando, no hubo permiso) y NO de CONTENIDO.
        ''' <para>⛔ LA DISTINCION ES LA QUE FALTABA. Un `Catch ex As Exception` metia las dos familias en el
        ''' mismo veredicto —<i>"el disco cambio abajo, borra los dos a mano"</i>— y un antivirus tocando el
        ''' `.new` un segundo producia ese texto sobre un par perfectamente sano.</para>
        ''' <para><c>FileNotFoundException</c> queda AFUERA aunque herede de <c>IOException</c>: que falte el
        ''' archivo no es transitorio, es que el disco cambio de verdad. <c>EndOfStreamException</c> tambien
        ''' hereda de <c>IOException</c> y tambien queda afuera: es un archivo CORTADO, o sea contenido.</para></summary>
        Private Shared Function EsFalloTransitorio(ex As Exception) As Boolean
            If TypeOf ex Is FileNotFoundException Then Return False
            If TypeOf ex Is DirectoryNotFoundException Then Return False
            If TypeOf ex Is EndOfStreamException Then Return False
            Return TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Function

        ''' <summary>Extension de archive que le toca a cada bucket. El barrido la usa para no cruzar
        ''' juegos: un mismo Data puede tener `G.bsa` y `G - Main.ba2` bajo el mismo base name, y un pack de
        ''' FO4 no tiene por que meter mano en el archive de Skyrim.</summary>
        Private Shared Function ExtensionDeBucket(bucket As BucketKind) As String
            Select Case bucket
                Case BucketKind.BA2_GNRL, BucketKind.BA2_DX10 : Return ".ba2"
                Case BucketKind.BSA : Return EXT_BSA
                Case Else : Throw New ArgumentOutOfRangeException(NameOf(bucket))
            End Select
        End Function

        ''' <summary>Barre TODO el archive set del ModBaseName y recupera cada volcado cortado ANTES de que
        ''' el censo lea un solo byte. Corre una vez por Pack (ver el ⛔ del llamador).
        ''' <para>⛔ SE ENUMERA POR EL `.new`/SELLO, NO POR EL ARCHIVE. Un `.new` cuyo entregable ya no
        ''' existe —lo borro el usuario, o lo borro la rama `emptiedByExclusion` de PackOneArchive— no
        ''' aparece en DiscoverArchiveSet, asi que un barrido que partiera de los archives lo dejaria
        ''' huerfano para siempre. Enumerar por el pendiente es enumerar por lo que hay que reparar.</para>
        ''' <para>Los `.new`/`.ok` NO se cuelan como archives en ningun otro lado: el patron de
        ''' DiscoverArchiveSet y de DiscoverSlots pide que el nombre TERMINE en `.ba2`/`.bsa`/`.esp`, y la
        ''' extension efectiva de estos es `.new` y `.ok`.</para></summary>
        Private Shared Sub RecuperarVolcadosCortados(req As PackagerRequest)
            If Not Directory.Exists(req.OutputDir) Then Return

            Dim buckets = BucketsForGame(req.Game)
            ' Nombres de archive del slot 1, armados con la MISMA regla que usa el resto del packer
            ' (ArchivePathFor sobre SlotName(1, ...)): es lo unico que se acepta con SingleAnchorOnly.
            Dim slot1 As New PluginSlot With {.SlotNumber = 1, .BaseName = SlotName(1, req.ModBaseName)}
            Dim nombresDeSlot1 As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim extensiones As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each b In buckets
                nombresDeSlot1.Add(Path.GetFileName(ArchivePathFor(slot1, b, req.OutputDir)))
                extensiones.Add(ExtensionDeBucket(b))
            Next

            Const SUF_NEW As String = ".new"
            Dim candidatos As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each ruta In Directory.EnumerateFiles(req.OutputDir, req.ModBaseName & "*", SearchOption.TopDirectoryOnly)
                Dim nombre = Path.GetFileName(ruta)
                Dim archivePath As String
                If nombre.EndsWith(SUF_NEW & SUFIJO_SELLO, StringComparison.OrdinalIgnoreCase) Then
                    archivePath = ruta.Substring(0, ruta.Length - (SUF_NEW & SUFIJO_SELLO).Length)
                ElseIf nombre.EndsWith(SUF_NEW, StringComparison.OrdinalIgnoreCase) Then
                    archivePath = ruta.Substring(0, ruta.Length - SUF_NEW.Length)
                Else
                    Continue For
                End If

                ' Que el entregable sea un archive de ESTE juego.
                If Not extensiones.Contains(Path.GetExtension(archivePath)) Then Continue For

                If req.SingleAnchorOnly Then
                    ' ⛔ SingleAnchorOnly declara que los companion numerados "are not considered for
                    ' anchoring nor for free-pass distribution, and their archives are never read or
                    ' rewritten" (ver PackagerRequest.SingleAnchorOnly). Repararlos ES reescribirlos, asi
                    ' que el barrido se queda en el slot 1. Lo que quede afuera lo cubre el llamado de
                    ' respaldo que sigue estando en PackOneArchive.
                    If Not nombresDeSlot1.Contains(Path.GetFileName(archivePath)) Then Continue For
                ElseIf Not BelongsToModBaseName(Path.GetFileNameWithoutExtension(archivePath), req.ModBaseName) Then
                    ' Coincidencia de prefijo sin la forma que escribe Pack ("<base>Legacy.ba2"): no es del set.
                    Continue For
                End If

                candidatos.Add(archivePath)
            Next

            ' ⛔⛔ CADA ARCHIVE EN SU PROPIO Try, Y EL BARRIDO NO SE CORTA EN EL PRIMERO. Acá era
            ' `For Each … : RecuperarVolcadoCortado(…) : Next` a secas: el primer throw abortaba el barrido
            ' ENTERO y salía por arriba de Pack, así que los buckets sanos que venían después NUNCA se
            ' reparaban — un `- Main.ba2` con el `.new` tomado por un antivirus dejaba al `- Textures.ba2`
            ' truncado indefinidamente, que es exactamente el defecto #1 que este barrido vino a cerrar.
            '
            ' ⛔ Y AUN ASI SE ABORTA AL FINAL, con todos los fallos juntos. No se sigue de largo hacia el
            ' censo: la razón entera de que esto corra ANTES de DiscoverSlots (ver el ⛔ del llamador) es que
            ' el censo NO puede leer un archive sin reparar. Dejar pasar uno roto reintroduce el defecto #2
            ' en la misma corrida. O sea: a cada bucket se le da su chance, y después se falla igual.
            Dim fallos As New List(Of String)
            For Each archivePath In candidatos
                Try
                    RecuperarVolcadoCortado(archivePath)
                Catch ex As Exception
                    fallos.Add($"  · {Path.GetFileName(archivePath)}: {ex.Message}")
                End Try
            Next

            If fallos.Count > 0 Then
                Throw New InvalidDataException(
                    $"{fallos.Count} archive(s) del set quedaron con un volcado pendiente que no se pudo " &
                    "resolver. Los demás SÍ se repararon." & Environment.NewLine &
                    String.Join(Environment.NewLine, fallos))
            End If
        End Sub

        ' NOTA: acá vivia `EsperadasDelManifiesto`, que sintetizaba la lista esperada A PARTIR DEL PROPIO
        ' `.new` para dársela a `VerifyArchive`. Se fue con el sello: esa lista era justamente lo que hacia
        ' tautológico el control (el manifiesto de un archivo verifica siempre contra si mismo, y contra
        ' cualquier otro archivo que tenga las mismas rutas). Lo que la reemplaza es la huella del sello.

        ''' <summary>Recupera el volcado cortado de UN archive, o deja todo exactamente como estaba. Los
        ''' cuatro estados del par `.new`/sello:
        ''' <list type="bullet">
        ''' <item><b>sin `.new`, con sello</b> ⇒ sello huerfano, se borra;</item>
        ''' <item><b>`.new` sin sello, o con un sello que NO es de este formato</b> ⇒ puede ser el de un
        ''' verify FALLIDO: se descarta, no se vuelca;</item>
        ''' <item><b>`.new` + sello</b> ⇒ se comprueba que el `.new` ES el que el sello registra, se vuelca,
        ''' se comprueba que el ENTREGABLE quedo igual al sello, y recien ahi se limpia el par;</item>
        ''' <item><b>ninguno</b> ⇒ no hay nada que hacer.</item></list>
        ''' <para>⛔ EL `.new` NO SE BORRA HASTA QUE EL ENTREGABLE ES, BYTE POR BYTE, EL QUE EL SELLO DICE.
        ''' Antes se volcaba a ciegas —la sonda era `EntriesFiles.Count > 0`— y se borraba el par sin volver
        ''' a mirar el destino: un volcado cortado por SEGUNDA vez se llevaba puesta la unica copia integra
        ''' que quedaba.</para>
        ''' <para>⛔⛔ Y DESPUES EL CONTROL SIGUIO SIENDO PROBABILISTICO. La version anterior contestaba las
        ''' dos preguntas con `VerifyArchive`, que compara `Count` + el set de rutas y extrae 3 entradas AL
        ''' AZAR. MEDIDO sobre los archives reales del usuario: <b>3 de 465…1.346 entradas = 0,22 %–0,65 %</b>,
        ''' y fuera de esas 3 no lee un solo byte de ~3 GiB de payload. Dos consecuencias, las dos reales:
        ''' <list type="bullet">
        ''' <item><b>no distingue el archive VIEJO del NUEVO.</b> `esperadas` sale del manifiesto del `.new`
        ''' y `VirtualEntry.FullPath` ≡ `ArchiveEntry.FullPath`, asi que un re-pack de las MISMAS rutas con
        ''' contenido nuevo verifica IGUAL contra el `.ba2` viejo. Con el destino tomado, la corrida 2
        ''' verificaba el viejo, pasaba, y BORRABA el par: el archive nuevo desaparecia sin un error;</item>
        ''' <item><b>no distingue uno sano de uno CORTADO cuando el manifiesto va al frente.</b> MEDIDO: en
        ''' BA2 la name table vive al FINAL (offset = 100,00 % del largo en los 6 archives medidos), asi que
        ''' un `.new` truncado no abre y cae del lado seguro; pero el BSA escribe header → dir entries →
        ''' file entries → nombres → DATA (`BSAWriter.vb:183-213, 442`), o sea manifiesto al FRENTE. Un
        ''' `.new` de BSA cortado por la cola enumera las N rutas bien, `Count` coincide, el set coincide, y
        ''' lo unico que puede cazarlo son las 3 extracciones: con el 90 % escrito PASA el 72,9 % de las
        ''' veces y se volcaba el truncado encima del bueno.</item></list>
        ''' Por eso las dos preguntas se contestan ahora contra el SELLO, que registra {largo, SHA-256}: es
        ''' determinista y no depende de que formato ponga su manifiesto donde.</para>
        ''' <para>⛔ NO VUELVE EN SILENCIO CUANDO NO PUDO DEJAR EL ENTREGABLE VERIFICADO. El que sigue es
        ''' DiscoverSlots, que censaria ese archive a medio volcar como VACIO — o sea el defecto que esta
        ''' funcion existe para cerrar, reintroducido en la misma corrida. Falla LIMPIO y el par se
        ''' conserva, que es la misma postura que el commit de PackOneArchive ya tenia.</para></summary>
        Private Shared Sub RecuperarVolcadoCortado(archivePath As String)
            Dim newPath = archivePath & ".new"
            Dim selloPath = newPath & SUFIJO_SELLO
            ' ⛔ EL SELLO HUERFANO SE LIMPIA PRIMERO. Si sobrevive a la corrida que lo dejo, la siguiente
            ' puede escribir un `.new` cuyo verify FALLA y juntarlo con este sello viejo: la corrida
            ' de despues lo tomaria por verificado y volcaria un archive rechazado encima del bueno.
            If Not File.Exists(newPath) Then
                If File.Exists(selloPath) Then BorrarConReintento(selloPath)
                Return
            End If
            ' Sin sello, ese `.new` puede ser el de un verify FALLIDO: se descarta, no se vuelca.
            If Not File.Exists(selloPath) Then
                BorrarConReintento(newPath)
                Return
            End If

            ' ⛔ UN SELLO QUE NO ES DE ESTE FORMATO NO ES UN SELLO (ver LeerSello). El vacio de 2.0.7 entra
            ' por acá: se lo trata como el caso `.new` SIN sello, que ya esta legislado arriba. NO hay modo
            ' legacy — creerle al sello vacio era autorizar el volcado a ciegas contra un control que mira
            ' el 0,2 % de las entradas, que es el defecto entero.
            Dim registro = LeerSello(selloPath)
            If registro Is Nothing Then
                BorrarConReintento(selloPath)
                BorrarConReintento(newPath)
                Return
            End If

            ' ¿El `.new` que hay en disco ES el que el sello registra? El sello es, por definicion (ver
            ' donde lo escribe PackOneArchive), el registro de que la corrida muerta escribio ESTOS bytes,
            ' los sincronizo y recien ahi sello. Comparar {largo, SHA-256} contesta eso y nada mas — sin
            ' muestreo, sin depender del formato, y sin la tautologia de usar el propio `.new` como lista
            ' esperada de si mismo.
            Dim huellaNew As RegistroDeSello
            Try
                huellaNew = HuellaDeArchivo(newPath)
            Catch ex As Exception When EsFalloTransitorio(ex)
                ' ⛔ TRANSITORIO NO ES SUCIO. No pude LEER el `.new` (¿el antivirus lo esta escaneando?, ¿un
                ' lector en vuelo?). Antes esto compartia veredicto con "no verifica" y le decia al usuario
                ' que el disco habia cambiado abajo y que borrara los dos archivos A MANO — sobre un par
                ' perfectamente sano. Nada se toco y el reintento es gratis.
                Throw New IOException(
                    $"no se pudo LEER '{Path.GetFileName(newPath)}' para comprobarlo contra su sello. " &
                    "NADA se cambio: el entregable y el par quedan como estaban. Cerra lo que tenga tomado " &
                    $"ese archivo y re-empaqueta." & Environment.NewLine & ex.Message, ex)
            End Try

            If Not registro.Coincide(huellaNew) Then
                ' ⛔ UN `.new` SELLADO QUE HOY NO ES EL DEL SELLO NO SE VUELCA NI SE BORRA. El sello dice
                ' que su verify paso en otra corrida; si hoy los bytes no son esos, el disco cambio abajo
                ' nuestro y no sabemos cual de los dos archivos es el sano. Es la misma ley que
                ' EscrituraEnElLugar aplica en GuardarConCopia: sin red no se trunca.
                Throw New InvalidDataException(
                    $"'{Path.GetFileName(newPath)}' no es el que su sello registra (sello: {registro}; " &
                    $"disco: {huellaNew}): el disco cambio abajo. No se vuelca sobre " &
                    $"'{Path.GetFileName(archivePath)}' ni se borra — es la unica copia. Si el problema " &
                    "persiste, borra los dos a mano y re-empaqueta.")
            End If

            ' ⚠️ TRAMPA VB: `Sub() tocado = True` es una ASIGNACION. En un `Function()` el MISMO texto seria
            ' una COMPARACION (devolveria un Boolean y descartaria el resultado), el flag nunca se
            ' prenderia, y todo fallo de volcado se leeria como "no toque el destino". Tiene que ser `Sub()`.
            Dim tocado As Boolean = False
            ' Misma separacion que en PackOneArchive: "se corto escribiendo" y "no pude comprobarlo" son
            ' dos veredictos distintos y hasta acá salian con el mismo texto.
            Dim volcado As Boolean = False
            Try
                ' El callback marca el instante en que el destino ya esta abierto Y truncado: a partir de
                ' ahi, un fallo dejo el entregable destruido. CONTRATO (ver VolcarEncima): si nunca corrio y
                ' VolcarEncima tiro, el destino esta byte-identico.
                EscrituraEnElLugar.VolcarEncima(newPath, archivePath,
                                                alTocarElDestino:=Sub() tocado = True)
                volcado = True
                ' Y el ENTREGABLE despues, contra el MISMO sello: este volcado se puede cortar a la mitad
                ' igual que el de la corrida que murio.
                ExigirEntregableIgualAlSello(archivePath, newPath, registro)
            Catch ex As Exception
                ' El volcado TERMINO y lo que fallo fue releer el entregable: el mensaje de
                ' ExigirEntregableIgualAlSello ya lo dice con el nombre del archivo. Se pasa tal cual.
                If volcado AndAlso EsFalloTransitorio(ex) Then Throw
                If Not tocado Then
                    ' El destino nunca se abrio: lo tenia un lector en vuelo, o no hubo permiso. El
                    ' entregable esta como estaba — y "como estaba" puede ser YA VOLCADO, si la corrida
                    ' muerta murio entre el volcado y la limpieza del par. Se mira el disco para saber cual
                    ' de los dos es.
                    ' (El hueco que documenta VolcarEncima —que el SetLength(0) tire y el callback no
                    ' corra— cae igual acá: esto mira el estado real, no la señal.)
                    '
                    ' ⛔⛔ Y SE MIRA CONTRA EL SELLO, NO CONTRA EL MANIFIESTO. Acá vivia el defecto que
                    ' borraba el archive nuevo: `VerifyArchive(archivePath, esperadas)` con `esperadas`
                    ' sacadas del `.new` es TAUTOLOGICO cuando el set de rutas no cambio, asi que el
                    ' `.ba2` VIEJO pasaba, se daba el volcado por hecho y se borraba el par.
                    Dim yaEstaba As Boolean
                    Try
                        yaEstaba = registro.Coincide(HuellaDeArchivo(archivePath))
                    Catch exLect As Exception When EsFalloTransitorio(exLect)
                        Throw New IOException(
                            $"no se pudo comprometer '{Path.GetFileName(newPath)}' sobre " &
                            $"'{Path.GetFileName(archivePath)}': el destino no se pudo abrir NI leer para " &
                            "saber si el volcado ya estaba hecho. NADA se cambio y el par se CONSERVA; " &
                            "cerra lo que tenga tomado el archive y re-empaqueta." &
                            Environment.NewLine & exLect.Message, ex)
                    End Try
                    If Not yaEstaba Then
                        Throw New InvalidDataException(
                            $"no se pudo comprometer '{Path.GetFileName(newPath)}' sobre " &
                            $"'{Path.GetFileName(archivePath)}': el destino no se pudo abrir (¿un lector en " &
                            "vuelo?) y lo que hay en disco NO es el archive nuevo. El `.new` y su sello se " &
                            "CONSERVAN (es la unica copia integra); cerra lo que tenga tomado el archive y " &
                            "re-empaqueta.", ex)
                    End If
                    ' El entregable ES, byte por byte, el que el sello registra: el volcado YA estaba hecho
                    ' y lo unico que faltaba era limpiar el par. Se limpia abajo y se sigue.
                Else
                    ' ⛔ EL PAR SE CONSERVA. El destino se abrio y se trunco, asi que el entregable esta
                    ' destruido: el `.new` es la unica copia integra del archive nuevo y borrarlo deja al
                    ' usuario sin nada para volver.
                    ' Dos caminos caen acá y los dos dejan el mismo estado: que `VolcarEncima` tirara a
                    ' mitad de la copia, o que el entregable resultante NO coincida con el sello. El
                    ' `ex.Message` va EMBEBIDO (patron de FomodExporter) porque los dialogos muestran solo
                    ' `.Message`: sin eso el usuario no ve cual de los dos fue ni las dos huellas.
                    Throw New InvalidDataException(
                        $"el volcado de '{Path.GetFileName(newPath)}' sobre " &
                        $"'{Path.GetFileName(archivePath)}' no dejo el archive completo: el entregable " &
                        "quedo a medias. El `.new` y su sello se CONSERVAN (es la unica copia integra); " &
                        "re-empaqueta para que la recuperacion lo vuelva a intentar." &
                        Environment.NewLine & ex.Message, ex)
                End If
            End Try

            ' La limpieza no puede tumbar un pack que ya se recupero bien: el `.new` en *delete pending*
            ' por un lector que no cerro hacia tirar a BorrarConReintento y la excepcion salia de aca.
            ' ⛔ EL SELLO PRIMERO, EL `.new` DESPUES. Es la misma ley del ⛔ de mas arriba aplicada al
            ' borrado: si la limpieza se corta a la mitad, lo que quede tiene que ser el estado SEGURO.
            ' Sello→`.new` deja un `.new` huerfano, que la corrida siguiente DESCARTA. Al reves —que es
            ' como estaba— deja un SELLO huerfano, que empareja con el `.new` de otro ciclo y lo hace pasar
            ' por verificado sin haberlo verificado nadie.
            Try
                BorrarConReintento(selloPath)
                BorrarConReintento(newPath)
            Catch
            End Try
        End Sub

        Private Shared Sub PackOneArchive(archivePath As String,
                                          bundle As List(Of VirtualEntry),
                                          kind As BucketKind,
                                          result As PackagerResult,
                                          ba2Version As UInteger,
                                          Optional excludePaths As HashSet(Of String) = Nothing)
            EnsureDir(archivePath)

            ' RED DE ATRAS, no el camino principal: la recuperacion de verdad la hace el barrido
            ' RecuperarVolcadosCortados, que corre en Pack ANTES del censo (ver el ⛔ de alla, que explica
            ' los tres defectos que salian de recuperar desde aca). Este llamado se queda por el unico
            ' archive que el barrido no alcanza: con SingleAnchorOnly el barrido se limita al slot 1 —
            ' porque el flag PROHIBE reescribir los companion numerados— y sin embargo DistributeEntries
            ' puede caer sobre un slot numerado que YA existe en disco de un experimento previo, con su
            ' `.new` pendiente. La rutina es idempotente: despues del barrido no encuentra nada y cuesta un
            ' File.Exists. La LEY sigue viviendo en un solo lugar (la rutina); lo que hay son dos llamadores.
            RecuperarVolcadoCortado(archivePath)

            ' Fast path: nothing on disk yet — write fresh. (No existing entries → nothing to exclude.)
            If Not File.Exists(archivePath) Then
                If bundle.Count = 0 Then Return   ' delete-only against a non-existent archive → no-op
                WriteArchive(archivePath, bundle, kind, ba2Version)
                VerifyArchive(archivePath, bundle)
                result.Archives.Add(archivePath)
                Return
            End If

            ' Diff against existing.
            Dim diff = ComputeDiff(archivePath, bundle, ba2Version, kind, excludePaths)
            If diff.Kind = DiffKind.Unchanged Then
                result.Skipped.Add(archivePath)
                Return
            End If

            ' Rewrite. El archive nuevo se arma en un `.new` AL LADO y despues se vuelca ENCIMA del
            ' original. El original NO se mueve ni se borra en ningun momento.
            '
            ' ⛔ NO volver al `MoverConReintento(archivePath, bakPath)` que habia aca. Ese rename dejaba
            ' el archive definitivo con un nombre NUEVO, y bajo Mod Organizer un nombre nuevo no
            ' pertenece a ningun mod: el .ba2 terminaba en la carpeta `overwrite` en vez de quedar en el
            ' mod que lo aporta. En Vortex, ademas, cortaba el hardlink y el mod se quedaba con el
            ' archive viejo. Volcar encima del original es lo unico que cae adentro del mod en los dos.
            '
            ' Lo que se paga, y es deliberado: volcar necesita ABRIR EL ORIGINAL PARA ESCRITURA, y las
            ' lecturas de archive comparten borrado pero no escritura (AbrirArchiveParaLectura del
            ' diccionario de archivos). O sea que un lector en vuelo puede negar el volcado, cosa que el
            ' rename no sufria. Por eso `VolcarEncima` reintenta: los llamadores desmontan el archive
            ' antes de empaquetar, asi que lo unico que queda vivo es una extraccion en curso, que dura
            ' lo que tarda. Si aun asi no se puede, esto falla LIMPIO: el original queda intacto y el
            ' `.new` tambien, y el usuario reintenta el pack.
            Dim newPath = archivePath & ".new"
            ' ⛔ EL SELLO PRIMERO, EL `.new` DESPUES — el MISMO orden que la limpieza del final y que
            ' RecuperarVolcadoCortado, y por la misma razon: son DOS borrados, no uno atomico, y
            ' BorrarConReintento TIRA. Si el par se corta a la mitad, lo que tiene que sobrevivir es un
            ' `.new` huerfano (que la corrida siguiente DESCARTA), nunca un SELLO huerfano (que emparejaria
            ' con el `.new` de este mismo ciclo y lo haria pasar por verificado sin que nadie lo
            ' verificara). Acá el orden estaba al reves. Hoy es inocuo —el `.new` que se acaba de borrar es
            ' justo el que ese sello describia— pero el orden de estos dos borrados es UNA ley, y tenerla
            ' escrita en un lugar y aplicada al reves en otro es como se pierde.
            BorrarConReintento(newPath & SUFIJO_SELLO)
            BorrarConReintento(newPath)

            Try
                ' El reader del ORIGINAL (y su FileStream) tiene que seguir abierto mientras corre
                ' WriteArchive: las VirtualEntry de pass-through referencian _fs y el writer las
                ' streamea sin buffer intermedio.
                Dim entriesToWrite As List(Of VirtualEntry)
                Dim emptiedByExclusion As Boolean = False
                Using fs = New FileStream(archivePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read Or FileShare.Delete)
                    Using reader As New BethesdaReader(fs)
                        entriesToWrite = BuildEntriesToWrite(bundle, reader, diff, kind)
                        ' Delete-only rewrite that emptied the archive (the excluded entries were its only
                        ' content): drop the file entirely rather than writing a zero-entry archive.
                        If entriesToWrite.Count = 0 Then
                            emptiedByExclusion = True
                        Else
                            WriteArchive(newPath, entriesToWrite, kind, ba2Version)
                        End If
                    End Using
                End Using

                If emptiedByExclusion Then
                    BorrarConReintento(newPath)
                    BorrarConReintento(archivePath)
                    result.Skipped.Add(archivePath)
                    Return
                End If

                ' Se verifica el contenido ANTES de comprometerlo. Verify against the actual write
                ' (bundle ∪ preserved), not just the bundle — otherwise preserved entries would be
                ' counted as "extra" and verification would fail.
                VerifyArchive(newPath, entriesToWrite)

                ' ⛔⛔ EL ORDEN DE ESTAS TRES LINEAS ES LA LEY DE DURABILIDAD, Y ANTES NO EXISTIA NINGUNA.
                ' En todo el camino de pack no habia UN SOLO FlushFileBuffers: `WriteArchive` es
                ' `File.Create` + `Dispose` (cerrar NO sincroniza) y el sello era
                ' `File.WriteAllBytes(..., Array.Empty)` — CERO BYTES, o sea un sello que no registraba
                ' nada y solo podia decir "existo". La ley que eso viola ya estaba escrita y ratificada en
                ' `EscrituraEnElLugar` ("cerrar NO sincroniza… un corte de luz se los lleva aunque el
                ' guardado haya dicho que salio bien" / "la red solo es red si llego al plato"), y este
                ' `.new` es lo unico del arbol que se declara "la unica copia integra" TRES VECES y no se
                ' sincronizaba.
                '   1. el `.new` al PLATO. Si esto falla, no hay sello: no se puede afirmar lo que el sello
                '      afirma, asi que no se afirma.
                '   2. su huella {largo, SHA-256}: lo que convierte al sello en un REGISTRO y no en una
                '      marca. Es lo que despues deja contestar de forma determinista "¿este `.new` es el
                '      que se verifico?" y "¿el entregable YA es ese `.new`?" — las dos preguntas que se
                '      contestaban muestreando 3 entradas de hasta 1.346 (0,22 %).
                '   3. el sello al plato, DESPUES del `.new`. Al reves el par vuelve a mentir.
                SincronizarArchivo(newPath)
                Dim registro = HuellaDeArchivo(newPath)
                EscribirSelloSincronizado(newPath & SUFIJO_SELLO, registro)

                ' ⛔ `alTocarElDestino` TAMBIEN ACA. Esta llamada era `VolcarEncima(newPath, archivePath)`
                ' pelada mientras la de RecuperarVolcadoCortado si pasaba el callback: la misma operacion,
                ' con dos fidelidades distintas. Sin el, el `Catch` de abajo no puede distinguir "no pude ni
                ' abrir el archive" (entregable INTACTO) de "lo rompi a la mitad", y el usuario recibe el
                ' IOException crudo del kernel, que no dice ninguna de las dos cosas.
                ' ⚠️ TRAMPA VB: tiene que ser `Sub()`. En un `Function()` el mismo texto seria una
                ' COMPARACION, el flag no se prenderia nunca y todo fallo se leeria como "no toque nada".
                Dim destinoTocado As Boolean = False
                ' ⛔ Y ESTE SEGUNDO FLAG SEPARA "SE CORTO ESCRIBIENDO" DE "NO PUDE COMPROBARLO". Sin el, un
                ' fallo de LECTURA del entregable (el antivirus escaneando el .ba2 recien volcado) salia
                ' con el texto de "quedo a medias" sobre un archive completo, y eso manda al usuario a
                ' pisar un entregable sano. Mismo `Sub()` obligatorio que arriba: en un `Function()` seria
                ' una comparacion.
                Dim volcadoCompleto As Boolean = False
                Try
                    EscrituraEnElLugar.VolcarEncima(newPath, archivePath,
                                                    alTocarElDestino:=Sub() destinoTocado = True)
                    volcadoCompleto = True
                    ' Y el ENTREGABLE despues, contra el sello: el volcado puede cortarse a la mitad.
                    ExigirEntregableIgualAlSello(archivePath, newPath, registro)
                Catch ex As Exception
                    If volcadoCompleto AndAlso EsFalloTransitorio(ex) Then
                        ' El volcado TERMINO; lo que fallo fue volver a LEER el entregable para
                        ' comprobarlo. ExigirEntregableIgualAlSello ya dice exactamente eso y nombra el
                        ' archivo: se pasa tal cual, sin re-etiquetarlo de "a medias".
                        Throw
                    End If
                    If Not destinoTocado Then
                        ' CONTRATO de VolcarEncima: el callback nunca corrio ⇒ el destino esta
                        ' byte-identico a como estaba. No se le dice al usuario que quedo a medias, que lo
                        ' mandaria a pisar un archive BUENO.
                        Throw New InvalidDataException(
                            $"'{Path.GetFileName(archivePath)}' NO se modifico: el archive no se pudo abrir " &
                            "para escribir (¿un lector en vuelo?, ¿el juego abierto?). Tu archive esta " &
                            "INTACTO y el `.new` con su sello se CONSERVAN; cerra lo que lo tenga tomado y " &
                            "re-empaqueta." & Environment.NewLine & ex.Message, ex)
                    End If
                    Throw New InvalidDataException(
                        $"el volcado sobre '{Path.GetFileName(archivePath)}' se corto DESPUES de empezar a " &
                        "escribir: el entregable quedo a medias. El `.new` y su sello se CONSERVAN (es la " &
                        "unica copia integra); re-empaqueta para que la recuperacion lo vuelva a intentar." &
                        Environment.NewLine & ex.Message, ex)
                End Try

                ' El archive ya esta volcado Y comprobado contra el sello: a partir de aca el pack es un exito.
                result.Archives.Add(archivePath)
                ' La limpieza es cosmetica y va DESPUES, en su propio Try: BorrarConReintento TIRA a los 5
                ' intentos, y un antivirus escaneando el sello de 0 bytes recien creado convertia un pack
                ' perfecto en "pack failed".
                ' ⛔ EL SELLO PRIMERO, EL `.new` DESPUES — el mismo orden que usa RecuperarVolcadoCortado, y
                ' por la misma razon. Estos son DOS borrados, no uno atomico: el primero puede salir y el
                ' segundo tirar. Con este orden lo que sobrevive es un `.new` huerfano, que la corrida
                ' siguiente DESCARTA. Al reves —que es como estaba— lo que sobrevive es un SELLO huerfano,
                ' que empareja con el `.new` de un ciclo posterior y lo hace pasar por verificado sin que
                ' nadie lo haya verificado.
                Try
                    BorrarConReintento(newPath & SUFIJO_SELLO)
                    BorrarConReintento(newPath)
                Catch
                    ' Un `.new` huerfano lo levanta y lo resuelve la corrida siguiente.
                End Try
            Catch
                ' ⛔ El `.new` NO se borra: si el volcado llego a cortarse, es la unica copia integra del
                ' archive nuevo. Lo levanta la corrida siguiente (ver el arranque de PackOneArchive).
                Throw
            End Try
        End Sub

        ' --------------------------------------------------------------------------------------
        ' Diff: walk the existing archive's manifest, compare against bundle by path + decompressed
        ' size + CRC32. CRC32 of existing entries forces a decompression pass — only paid once
        ' per Pack and only for paths that also appear in the bundle.
        ' --------------------------------------------------------------------------------------
        ''' <summary>Borra un archivo tolerando *delete pending*: si un handle abierto con
        ''' <c>FileShare.Delete</c> todavia no cerro, el nombre sigue existiendo hasta que lo haga y un
        ''' borrado nuevo sobre ese nombre falla con ERROR_DELETE_PENDING (que .NET traduce a
        ''' <see cref="UnauthorizedAccessException"/>, NO a <see cref="IOException"/> — atrapar sólo IO no
        ''' alcanzaba). Cinco intentos de 100 ms: la ventana dura lo que tarde el <c>ExtractToMemory</c> en
        ''' curso. Si igual no se puede, se deja tirar para que el llamador lo vea.</summary>
        Private Shared Sub BorrarConReintento(path As String)
            For intento = 1 To 5
                Try
                    ' ⛔ SIN `File.Exists`. En un archivo en *delete pending*, `GetFileAttributesEx` falla
                    ' con ACCESS_DENIED y `File.Exists` devuelve False — un guard así saltearía el
                    ' reintento justo en el estado que existe para cubrir. `File.Delete` sobre un archivo
                    ' inexistente ya es no-op, así que el guard no hace falta.
                    File.Delete(path)
                    Return
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    If intento = 5 Then Throw
                    Threading.Thread.Sleep(100)
                End Try
            Next
        End Sub

        Private Shared Function ComputeDiff(existingPath As String, bundle As List(Of VirtualEntry),
                                            requestedBa2Version As UInteger, kind As BucketKind,
                                            Optional excludePaths As HashSet(Of String) = Nothing) As DiffResult
            Dim result As New DiffResult() With {.Kind = DiffKind.NeedsRewrite}

            Dim newByPath As New Dictionary(Of String, VirtualEntry)(StringComparer.OrdinalIgnoreCase)
            For Each ve In bundle
                newByPath(NormalizePath(ve.FullPath)) = ve
            Next

            ' Normalize the exclude set the same way archive paths are stored, so the drop matches regardless
            ' of how the caller formatted the entry path (case / separators). Empty when no exclusions.
            Dim normExclude As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            If excludePaths IsNot Nothing Then
                For Each ex In excludePaths
                    If Not String.IsNullOrEmpty(ex) Then normExclude.Add(NormalizePath(ex))
                Next
            End If
            Dim removedAny As Boolean = False

            Using fs = File.OpenRead(existingPath)
                Using reader As New BethesdaReader(fs)
                    ' Force a rewrite when the on-disk BA2 header version differs from the requested
                    ' one, even if every entry is byte-identical. Without this, switching the version
                    ' selector (e.g. NG v8 → OG v1) on an already-packed set would be skipped as
                    ' "Unchanged" and the archive would keep its old version. BSA has no version
                    ' choice (Ba2HeaderVersion = Nothing) so it never triggers a mismatch.
                    Dim versionMismatch As Boolean =
                        (kind = BucketKind.BA2_GNRL OrElse kind = BucketKind.BA2_DX10) AndAlso
                        reader.Ba2HeaderVersion.HasValue AndAlso
                        reader.Ba2HeaderVersion.Value <> requestedBa2Version

                    ' Self-heal gate: force a rewrite when any existing record carries a stale name/dir
                    ' hash — i.e. the archive was written with the wrong hash algorithm (e.g. pre-fix BA2
                    ' used zip-CRC32 instead of the engine's init=0/no-xor CRC), so the game can't find
                    ' its files by hash. Payloads are byte-identical, so without this the diff would report
                    ' Unchanged and the broken archive would survive every re-pack. Cheap: the stored hashes
                    ' were already parsed at Open() (no payload I/O); Exit For on the first mismatch. Covers
                    ' both paths — BA2 (32-bit FO4 hash) and BSA (64-bit TES4 hash).
                    Dim staleHash As Boolean = False
                    For Each ae In reader.EntriesFiles
                        If kind = BucketKind.BA2_GNRL OrElse kind = BucketKind.BA2_DX10 Then
                            Dim parent As String = "", stem As String = "", extNoDot As String = ""
                            Ba2WriterCommon.Fo4SplitPath(ae.FullPath, parent, stem, extNoDot)
                            If ae.Ba2NameHash <> Ba2WriterCommon.Fo4PathHash(stem) OrElse
                               ae.Ba2DirHash <> Ba2WriterCommon.Fo4PathHash(parent) Then
                                staleHash = True : Exit For
                            End If
                        ElseIf kind = BucketKind.BSA Then
                            Dim expFile = BitConverter.ToUInt64(BsaWriter.BASTes4Hashing.Tes4HashFileBytes(ae.FileName), 0)
                            Dim expDir = BitConverter.ToUInt64(BsaWriter.BASTes4Hashing.Tes4HashDirectoryBytes(ae.Directory), 0)
                            If ae.BsaFileHash <> expFile OrElse ae.BsaDirHash <> expDir Then
                                staleHash = True : Exit For
                            End If
                        End If
                    Next

                    Dim existingPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                    For Each ae In reader.EntriesFiles
                        existingPaths.Add(NormalizePath(ae.FullPath))
                    Next

                    ' Existing paths NOT in the bundle are preserved (merge semantics) — UNLESS they are in the
                    ' exclude set, in which case they are deliberately dropped (not preserved) and the presence of
                    ' any such path forces a rewrite so the drop materializes even with no other change.
                    For Each ep In existingPaths
                        If normExclude.Contains(ep) Then
                            removedAny = True
                        ElseIf Not newByPath.ContainsKey(ep) Then
                            result.PreservePaths.Add(ep)
                        End If
                    Next

                    Dim addedAny As Boolean = False
                    For Each np In newByPath.Keys
                        If Not existingPaths.Contains(np) Then
                            addedAny = True
                            Exit For
                        End If
                    Next

                    Dim changedAny As Boolean = False
                    For Each ae In reader.EntriesFiles
                        Dim p = NormalizePath(ae.FullPath)
                        Dim ve As VirtualEntry = Nothing
                        If Not newByPath.TryGetValue(p, ve) Then Continue For

                        ' Determine the "logical decompressed size" of the bundle entry — the
                        ' field we can compare against the source archive's stored decomp size
                        ' without ever decompressing. PreCompressed entries (the caller already
                        ' compressed) report PreCompressedDecompSize. Legacy entries with raw
                        ' Data report Data.Length.
                        Dim newDecompSize As Long
                        If ve.PreCompressed Then
                            newDecompSize = CLng(ve.PreCompressedDecompSize)
                        ElseIf ve.Data IsNot Nothing Then
                            newDecompSize = ve.Data.LongLength
                        Else
                            newDecompSize = 0L
                        End If

                        ' Cheap first check: extract the raw stored chunk (no decompression) so we
                        ' can read the existing entry's decompressed size from the chunk header.
                        ' If the sizes don't match, the content has changed for sure — no need
                        ' to actually decompress to find out.
                        Dim raw As RawCompressedEntry
                        Try
                            raw = reader.ExtractCompressedPayload(ae.Index)
                        Catch ex As NotSupportedException
                            ' Layout we can't replay verbatim (multi-chunk BA2, non-default tile mode,
                            ' BSA edge cases). Treat as "changed" — forces a rewrite, the safe direction.
                            changedAny = True
                            Continue For
                        Catch ex As InvalidDataException
                            ' Corrupt chunk header (offset/size out of range) → treat as "changed".
                            changedAny = True
                            Continue For
                        Catch ex As EndOfStreamException
                            ' Truncated payload (short read while extracting the raw chunk) → "changed".
                            changedAny = True
                            Continue For
                        End Try

                        If CLng(raw.DecompSize) <> newDecompSize Then
                            changedAny = True
                            Continue For
                        End If

                        ' Sizes match — verify content via CRC32 of the decompressed payload.
                        ' Do this only when the caller filled ve.Crc32 (otherwise we can't tell
                        ' identity cheaply, fall back to "changed" to be safe).
                        If ve.Crc32 = 0UI Then
                            changedAny = True
                            Continue For
                        End If

                        ' Decompress raw.Bytes manually so we get the EXACT payload that the
                        ' caller compressed and CRC'd, without any header reconstruction the
                        ' archive's high-level Extract path may apply (e.g. BA2 DX10 prepends a
                        ' rebuilt DDS header on ExtractToMemory). The caller's ve.Crc32 is over
                        ' the stripped/raw payload — we need the same shape on this side.
                        Dim payloadForCrc As Byte() = Nothing
                        Try
                            If raw.IsCompressed Then
                                ' Compressed payload: raw.Bytes is the chunk's compressed stream,
                                ' raw.DecompSize is the expected decompressed length. The codec
                                ' is Zlib unless this is a BSA LZ4 entry or BA2 v3+LZ4 archive.
                                ' We pick by trying Zlib first (most common for BA2 GNRL/DX10);
                                ' if that fails, fall back to LZ4. Both decoders are strict.
                                Try
                                    payloadForCrc = ZlibStrict.ZlibDecompressStrict(raw.Bytes, CInt(raw.DecompSize))
                                Catch
                                    payloadForCrc = Lz4Strict.Lz4DecompressStrict(raw.Bytes, CInt(raw.DecompSize))
                                End Try
                            Else
                                ' Stored uncompressed: raw.Bytes IS the payload.
                                payloadForCrc = raw.Bytes
                            End If
                        Catch
                            ' Decompression failed for any reason → can't verify identity, treat
                            ' as changed (safe direction).
                            changedAny = True
                            Continue For
                        End Try

                        If payloadForCrc IsNot Nothing AndAlso Ba2WriterCommon.Crc32Bytes(payloadForCrc) = ve.Crc32 Then
                            result.UnchangedPaths.Add(p)
                        Else
                            changedAny = True
                        End If
                    Next

                    ' Skip rewrite only if the bundle is fully covered by unchanged entries AND
                    ' nothing in the bundle is missing from the archive AND nothing was excluded. Preserved
                    ' paths alone don't need a rewrite — they're already on disk.
                    If Not addedAny AndAlso Not changedAny AndAlso Not versionMismatch AndAlso Not removedAny AndAlso Not staleHash Then
                        result.Kind = DiffKind.Unchanged
                    End If
                End Using
            End Using

            Return result
        End Function

        ' --------------------------------------------------------------------------------------
        ' Build the entry list to feed the writer. La FUENTE pass-through es el archive ORIGINAL, abierto
        ' para lectura en su lugar mientras se escribe el `.new` (ver PackOneArchive) — NO un `.bak`: ese
        ' rename se fue en 2.0.5 y con el el archivo intermedio.
        '   - paths in diff.UnchangedPaths → wrap bundle entry with pass-through bytes from the original.
        '   - paths that are added or changed → forward the bundle entry as-is (writer compresses).
        '   - paths in diff.PreservePaths (existing in the original but NOT in the bundle) → emit
        '     pass-through entry from the original so the rewritten archive keeps everything that was
        '     already there. This is what makes Pack a merge ("upsert") instead of a destructive replace.
        '
        ' BA2-014 — single-chunk-only DX10 pass-through limitation:
        '   Stream-copy pass-through (BuildPassThroughEntry) only works for entries the reader can
        '   replay verbatim, i.e. SINGLE-CHUNK BA2 entries (and TileMode=8 for DX10). The library's
        '   own writers always emit single-chunk entries, so archives this packager produced are
        '   always pass-through-eligible. A MULTI-CHUNK DX10 texture (a large mip set split across
        '   several chunks by an external tool such as Archive2/BSArch) CANNOT be lifted verbatim:
        '   GetPayloadSource / ExtractCompressedPayload throw NotSupportedException for Chunks.Count<>1
        '   (see Ba2Impl.EntryDX10.ExtractRaw / GetPayloadSourceByIndex).
        '   How that plays out per path:
        '     - UNCHANGED bundle entry: ComputeDiff's ExtractCompressedPayload throws → caught as
        '       "changed" → the entry is forwarded as the bundle's own VirtualEntry and the DX10
        '       writer RECOMPRESSES it as a single chunk. Never a verbatim copy.
        '     - PRESERVED entry (in the original, not in the bundle): PassThroughCodecSafe → GetPayloadSource
        '       throws NotSupportedException, and the BuildRecompressEntry fallback ALSO calls
        '       ExtractCompressedPayload, which throws too. So a multi-chunk DX10 preserve-path entry
        '       would surface a hard error (rolled back by PackOneArchive), not a silent recompress.
        '   In practice this packager only ever diffs/rewrites archives IT wrote (single-chunk by
        '   construction), so multi-chunk DX10 never reaches these paths. The limitation is recorded
        '   here so a future change that ingests externally-built multi-chunk DX10 archives knows it
        '   must add a multi-chunk extract+recompress path before relying on pass-through.
        ' --------------------------------------------------------------------------------------
        Private Shared Function BuildEntriesToWrite(bundle As List(Of VirtualEntry),
                                                    origenReader As BethesdaReader,
                                                    diff As DiffResult,
                                                    kind As BucketKind) As List(Of VirtualEntry)
            Dim origenPorRuta As New Dictionary(Of String, ArchiveEntry)(StringComparer.OrdinalIgnoreCase)
            For Each ae In origenReader.EntriesFiles
                origenPorRuta(NormalizePath(ae.FullPath)) = ae
            Next

            Dim targetCodec As PayloadCodec = TargetCodecFor(kind)
            Dim out As New List(Of VirtualEntry)(bundle.Count + diff.PreservePaths.Count)

            ' First: bundle entries (each one keeps its order; unchanged ones get pass-through).
            For Each ve In bundle
                Dim p = NormalizePath(ve.FullPath)
                Dim origenAe As ArchiveEntry = Nothing
                If diff.UnchangedPaths.Contains(p) AndAlso origenPorRuta.TryGetValue(p, origenAe) Then
                    ' Pass-through is byte-correct only when the ORIGINAL's codec matches the target.
                    ' On a codec mismatch (e.g. a v3/LZ4 source rewritten as v8/Zlib) a verbatim
                    ' stream-copy would corrupt the entry, so fall back to the bundle entry as-is
                    ' and let the writer recompress with the target codec.
                    If PassThroughCodecSafe(origenReader, origenAe.Index, targetCodec) Then
                        out.Add(BuildPassThroughEntry(ve.Directory, ve.FileName, ve.PreferCompress, ve.Crc32, origenReader, origenAe.Index))
                    Else
                        out.Add(ve)
                    End If
                Else
                    out.Add(ve)
                End If
            Next

            ' Then: preserved entries (existing in the original, not in the bundle). Pass-through when the
            ' codec matches; otherwise recompress from the ORIGINAL's decompressed payload (the bundle
            ' has no copy of these, so we must re-extract them to re-encode safely).
            For Each pp In diff.PreservePaths
                Dim origenAe As ArchiveEntry = Nothing
                If Not origenPorRuta.TryGetValue(pp, origenAe) Then Continue For
                If PassThroughCodecSafe(origenReader, origenAe.Index, targetCodec) Then
                    out.Add(BuildPassThroughEntry(origenAe.Directory, origenAe.FileName, False, 0UI, origenReader, origenAe.Index))
                Else
                    out.Add(BuildRecompressEntry(origenAe.Directory, origenAe.FileName, origenReader, origenAe.Index))
                End If
            Next

            Return out
        End Function

        ' Codec the writers emit for a bucket given how WriteArchive constructs Options: it sets
        ' only .Version, so CompressionFormat stays the default Zip → BA2 always writes Zlib (even
        ' v3, which is v3+Zip). The BSA writer always frames compressed payloads (Lz4Frame).
        Private Shared Function TargetCodecFor(kind As BucketKind) As PayloadCodec
            Select Case kind
                Case BucketKind.BA2_GNRL, BucketKind.BA2_DX10 : Return PayloadCodec.Zlib
                Case BucketKind.BSA : Return PayloadCodec.Lz4Frame
                Case Else : Throw New ArgumentOutOfRangeException(NameOf(kind))
            End Select
        End Function

        ' A stream-copy is byte-correct only when the source payload is either stored (no codec)
        ' or encoded with the SAME codec the destination archive will declare. Anything else would
        ' silently corrupt the entry, so we refuse and let the caller recompress instead.
        Private Shared Function PassThroughCodecSafe(origenReader As BethesdaReader, origenIndex As Integer, targetCodec As PayloadCodec) As Boolean
            Dim ps = origenReader.GetPayloadSource(origenIndex)
            If Not ps.IsCompressed Then Return True               ' stored raw → always copy-safe
            Return ps.SourceCodec = targetCodec
        End Function

        Private Shared Function BuildPassThroughEntry(dir As String,
                                                       fileName As String,
                                                       preferCompress As Boolean,
                                                       crc32 As UInteger,
                                                       origenReader As BethesdaReader,
                                                       origenIndex As Integer) As VirtualEntry
            ' Stream-copy pass-through: the writer seeks PayloadSource.SourceStream and copies
            ' Length bytes directly into the output without ever materializing the payload in
            ' a managed array. RAM peak per entry is O(64 KB) regardless of file size, which is
            ' the difference between minutes and hours on multi-GB archive rewrites.
            Dim ps = origenReader.GetPayloadSource(origenIndex)
            Dim ve As New VirtualEntry With {
                .Directory = dir,
                .FileName = fileName,
                .PayloadSource = ps,
                .PreferCompress = preferCompress,
                .Crc32 = crc32
            }
            If ps.HasDx10Metadata Then
                ve.Width = ps.Width
                ve.Height = ps.Height
                ve.MipCount = ps.MipCount
                ve.DxgiFormat = ps.DxgiFormat
                ve.IsCubemap = ps.IsCubemap
                ve.Faces = ps.Faces
            End If
            Return ve
        End Function

        ' Codec-mismatch fallback for a PRESERVED entry (one that exists only in the ORIGINAL, with no
        ' bundle copy to forward). We extract the entry's raw stored chunk, decompress it to the
        ' stripped/decompressed payload, and hand that to the writer as plain Data so it recompresses
        ' with the target codec. RawCompressedEntry.Bytes decodes to the exact stripped payload for
        ' both GNRL and DX10 (no DDS header is reconstructed on this path), which is what the writers
        ' expect on the non-pass-through branch.
        Private Shared Function BuildRecompressEntry(dir As String,
                                                      fileName As String,
                                                      origenReader As BethesdaReader,
                                                      origenIndex As Integer) As VirtualEntry
            Dim raw = origenReader.ExtractCompressedPayload(origenIndex)
            Dim payload As Byte()
            If raw.IsCompressed Then
                ' Source codec is non-target (that's why we're here). The strict decoders detect
                ' their own format; try Zlib then LZ4, same as ComputeDiff's CRC path.
                Try
                    payload = ZlibStrict.ZlibDecompressStrict(raw.Bytes, CInt(raw.DecompSize))
                Catch
                    payload = Lz4Strict.Lz4DecompressStrict(raw.Bytes, CInt(raw.DecompSize))
                End Try
            Else
                payload = raw.Bytes
            End If

            Dim ve As New VirtualEntry With {
                .Directory = dir,
                .FileName = fileName,
                .Data = payload
            }
            ' DX10 entries need their metadata populated for the DX10 writer (it never parses a
            ' DDS header on the Data path; the stripped payload + these fields are the contract).
            If raw.HasDx10Metadata Then
                ve.Width = raw.Width
                ve.Height = raw.Height
                ve.MipCount = raw.MipCount
                ve.DxgiFormat = raw.DxgiFormat
                ve.IsCubemap = raw.IsCubemap
                ve.Faces = raw.Faces
            End If
            Return ve
        End Function

        ' --------------------------------------------------------------------------------------
        ' Writer dispatch by bucket. Options use library defaults — Pack does not expose them yet.
        ' --------------------------------------------------------------------------------------
        ''' <summary>⛔ CIERRA CON <c>Flush(True)</c>, y no es decorativo. `File.Create` + `Dispose` deja los
        ''' bytes en la cache del sistema: es la misma trampa que <c>EscrituraEnElLugar</c> documenta en su
        ''' nucleo (<i>"cerrar NO sincroniza"</i>). Acá el archivo que se escribe es, o el `.new` que la
        ''' corrida siguiente va a tratar como la unica copia integra, o el archive fresco que ya es el
        ''' entregable; en los dos casos hay algo que despues AFIRMA que esos bytes estan.
        ''' <para>El costo es POR LLAMADA, no por byte (medido en <c>EscrituraEnElLugar.Escribir</c>:
        ''' +2,66 a +4,53 ms), y acá es UNA llamada por archive reescrito — no por entrada. Frente a los
        ''' ~3 GiB que la misma llamada acaba de mover, es ruido.</para></summary>
        Private Shared Sub WriteArchive(path As String, entries As List(Of VirtualEntry), kind As BucketKind, ba2Version As UInteger)
            Using fs As FileStream = File.Create(path)
                Select Case kind
                    Case BucketKind.BA2_GNRL
                        Ba2WriterGNRL.Write(fs, entries, New Ba2WriterGNRL.Options With {.Version = ba2Version})
                    Case BucketKind.BA2_DX10
                        Ba2WriterDX10.Write(fs, entries, New Ba2WriterDX10.Options With {.Version = ba2Version})
                    Case BucketKind.BSA
                        BsaWriter.Write(fs, entries, New BsaWriter.Options())
                    Case Else
                        Throw New ArgumentOutOfRangeException(NameOf(kind))
                End Select
                fs.Flush(True)
            End Using
        End Sub

        ' --------------------------------------------------------------------------------------
        ' Verify: re-open the freshly written archive, confirm count + path set match the bundle,
        ' and decompress 3 random entries to exercise the chunk headers / compression streams.
        ' --------------------------------------------------------------------------------------
        Private Shared Sub VerifyArchive(path As String, expectedBundle As List(Of VirtualEntry))
            Dim expectedPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each ve In expectedBundle
                expectedPaths.Add(NormalizePath(ve.FullPath))
            Next

            Using fs = File.OpenRead(path)
                Using reader As New BethesdaReader(fs)
                    Dim actual = reader.EntriesFiles
                    If actual.Count <> expectedBundle.Count Then
                        Throw New InvalidDataException($"Verify failed for '{path}': expected {expectedBundle.Count} entries, got {actual.Count}.")
                    End If

                    For Each ae In actual
                        Dim ap = NormalizePath(ae.FullPath)
                        If Not expectedPaths.Contains(ap) Then
                            Throw New InvalidDataException($"Verify failed for '{path}': unexpected entry '{ae.FullPath}'.")
                        End If
                    Next

                    Dim sampleCount As Integer = Math.Min(3, actual.Count)
                    If sampleCount > 0 Then
                        Dim rnd As New Random()
                        Dim picked As New HashSet(Of Integer)
                        While picked.Count < sampleCount
                            picked.Add(rnd.Next(actual.Count))
                        End While
                        For Each idx In picked
                            Dim bytes = reader.ExtractToMemory(idx)
                            If bytes Is Nothing OrElse bytes.Length = 0 Then
                                Throw New InvalidDataException($"Verify failed for '{path}': spot-check entry {idx} extracts empty.")
                            End If
                        Next
                    End If
                End Using
            End Using
        End Sub

        ''' <summary>True si <paramref name="path"/> existe y su contenido es, byte por byte,
        ''' <paramref name="esperado"/>. Corta por tamaño primero.
        ''' <para>No es <c>EscrituraEnElLugar.MismoContenido</c> con otro nombre: aquella compara DOS
        ''' ARCHIVOS y esta compara un archivo contra bytes que ya están en RAM (la entrada recién extraída),
        ''' así que no hay una segunda lectura que ahorrar ni una ley que duplicar.</para>
        ''' <para>Ante cualquier fallo devuelve False: "no pude probar que son iguales" tiene que caer del
        ''' lado de respaldar y escribir, nunca del lado de saltear.</para></summary>
        Private Shared Function MismosBytes(path As String, esperado As Byte()) As Boolean
            Try
                Dim fi As New FileInfo(path)
                If Not fi.Exists OrElse fi.Length <> esperado.LongLength Then Return False
                Using fs = File.OpenRead(path)
                    Dim buf(65535) As Byte
                    Dim pos As Integer = 0
                    While pos < esperado.Length
                        Dim n = fs.Read(buf, 0, Math.Min(buf.Length, esperado.Length - pos))
                        If n <= 0 Then Return False
                        For i = 0 To n - 1
                            If buf(i) <> esperado(pos + i) Then Return False
                        Next
                        pos += n
                    End While
                    Return True
                End Using
            Catch
                Return False
            End Try
        End Function

        Private Shared Function IsTextureEntry(ve As VirtualEntry) As Boolean
            Dim ext = IO.Path.GetExtension(If(ve.FileName, "")).ToLowerInvariant()
            Return ext = ".dds"
        End Function

        Private Shared Function NormalizePath(p As String) As String
            Return PathUtil.NormalizeSlash(If(p, "")).Trim(Correct_Path_separator).ToLowerInvariant()
        End Function

        Private Shared Sub EnsureDir(filePath As String)
            Dim dir = IO.Path.GetDirectoryName(filePath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If
        End Sub

        ' ======================================================================================
        '                                    UNPACK + DISCOVERY
        ' ======================================================================================

        ''' <summary>
        ''' Enumerates archives and plugins under outputDir whose file name starts with modBaseName.
        ''' Used by Unpack to drive cleanup and by callers who want to inspect the current pack
        ''' state before deciding what to do.
        ''' <para>Además llena <see cref="ArchiveSetInfo.Huerfanos"/>: archivos que acompañan al set y que
        ''' ningún camino del packer consume ni borra. NO se borra nada — ver el ⛔ de esa propiedad.</para>
        ''' </summary>
        Public Shared Function DiscoverArchiveSet(outputDir As String, modBaseName As String) As ArchiveSetInfo
            If String.IsNullOrWhiteSpace(outputDir) Then Throw New ArgumentException("outputDir is empty.", NameOf(outputDir))
            If String.IsNullOrWhiteSpace(modBaseName) Then Throw New ArgumentException("modBaseName is empty.", NameOf(modBaseName))

            Dim info As New ArchiveSetInfo()
            If Not Directory.Exists(outputDir) Then Return info

            ' Restrict to files whose stem either equals modBaseName, or starts with "modBaseName"
            ' followed by a digit (numbered slot) or " - " (bucket suffix). Anything else is a
            ' coincidental name match (e.g. "WM_ClonePackLegacy.esp") and stays untouched.
            For Each ext In New String() {".ba2", ".bsa", ".esp", ".esm", ".esl"}
                For Each filePath In Directory.EnumerateFiles(outputDir, modBaseName & "*" & ext, SearchOption.TopDirectoryOnly)
                    Dim stem = Path.GetFileNameWithoutExtension(filePath)
                    If Not BelongsToModBaseName(stem, modBaseName) Then Continue For

                    Select Case ext
                        Case ".ba2", ".bsa" : info.Archives.Add(filePath)
                        Case ".esp", ".esm", ".esl" : info.Plugins.Add(filePath)
                    End Select
                Next
            Next

            ' --- Huérfanos: se REPORTAN, no se tocan --------------------------------------------------
            ' ⛔ ESTOS NO LOS VE NADIE MAS. El filtro de arriba pide que el nombre TERMINE en una de las 5
            ' extensiones, asi que un `WM_ClonePack3 - Textures.ba2.bak` (extension efectiva `.bak`) queda
            ' fuera del censo, fuera del barrido de recuperacion y fuera del Unpack: son hasta 3 GiB por
            ' pieza que no borra nadie. Los dejo el rename de 2.0.2, que ya no existe, junto con el
            ' `BorrarConReintento(bakPath)` que era su unico barrendero.
            ' MEDIDO al escribir esto: 0 en el Data de FO4 del usuario (61 `WM_ClonePack*` sanos, 89,87 GiB)
            ' y 0 en el de SSE ⇒ el defecto es LATENTE acá; a quien le pega es al que viene de 2.0.2.
            ' Los `.new`/`.ok` NO entran: esos SI tienen dueño (RecuperarVolcadosCortados) y borrarlos o
            ' reportarlos como basura seria pisar el protocolo de recuperacion.
            ' Los `.bak.unpack` TAMPOCO entran acá y no es un olvido: viven bajo `LooseDataDir` con el
            ' nombre del SUELTO, no bajo `OutputDir` con el prefijo del mod, asi que este barrido no los
            ' puede ver. Los reporta `Unpack`, que es quien conoce esa carpeta.
            For Each sufijo In New String() {".ba2.bak", ".bsa.bak"}
                For Each filePath In Directory.EnumerateFiles(outputDir, modBaseName & "*" & sufijo, SearchOption.TopDirectoryOnly)
                    ' El stem contra el que se valida es el del ARCHIVE, o sea el nombre sin el sufijo
                    ' huerfano: "WM_ClonePack3 - Textures.ba2.bak" → "WM_ClonePack3 - Textures".
                    Dim nombre = Path.GetFileName(filePath)
                    Dim stemArchive = nombre.Substring(0, nombre.Length - sufijo.Length)
                    If Not BelongsToModBaseName(stemArchive, modBaseName) Then Continue For
                    info.Huerfanos.Add(filePath)
                Next
            Next

            info.Archives.Sort(StringComparer.OrdinalIgnoreCase)
            info.Plugins.Sort(StringComparer.OrdinalIgnoreCase)
            info.Huerfanos.Sort(StringComparer.OrdinalIgnoreCase)
            Return info
        End Function

        ''' <summary>
        ''' Recognises file stems generated by Pack: "<base>", "<base>N", "<base> - Main",
        ''' "<base>N - Main", "<base> - Textures", "<base>N - Textures". Rejects bare prefix
        ''' matches that don't follow one of those shapes.
        ''' </summary>
        Private Shared Function BelongsToModBaseName(stem As String, modBaseName As String) As Boolean
            If String.Equals(stem, modBaseName, StringComparison.OrdinalIgnoreCase) Then Return True
            If Not stem.StartsWith(modBaseName, StringComparison.OrdinalIgnoreCase) Then Return False

            Dim suffix = stem.Substring(modBaseName.Length)
            ' Strip an optional numeric run (slot suffix) before checking the bucket marker.
            Dim i As Integer = 0
            While i < suffix.Length AndAlso Char.IsDigit(suffix(i))
                i += 1
            End While

            ' If a numeric run was found, fall through to bucket-suffix check on the remainder.
            ' If nothing followed the digits, that's the plain numbered slot ("<base>2").
            Dim afterDigits = suffix.Substring(i)
            If afterDigits.Length = 0 Then Return i > 0   ' "<base>2" valid; "<base>" already handled.

            ' Accept " - Main" or " - Textures" (Pack's only non-numeric suffixes today).
            Return afterDigits.Equals(" - Main", StringComparison.OrdinalIgnoreCase) OrElse
                   afterDigits.Equals(" - Textures", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' Reverses Pack: extracts every entry of every archive in the set as a loose file under
        ''' LooseDataDir, then deletes the archives and their dummy plugins. Loose files preserve
        ''' the entry's FullPath relative to the data root (so "Textures\ManoloCloned\foo.dds"
        ''' becomes "&lt;LooseDataDir&gt;\Textures\ManoloCloned\foo.dds").
        '''
        ''' Optional onEntry: invoked once per extracted entry as (doneSoFar, totalAcrossAllArchives,
        ''' currentEntryRelPath). totalAcrossAllArchives is computed up front from the archive
        ''' file tables (no payload reads), so the caller can drive a determinate progress bar.
        '''
        ''' Optional ct: checked between entries during extraction. Cancellation is safe — already-
        ''' written loose files stay on disk, archives are NOT deleted (phase 2 only runs on full
        ''' success). The caller can re-run Unpack to finish the job.
        '''
        ''' ⛔ MODO DE FALLO — CAMBIO DE CONTRATO DECLARADO (antes: "extraction errors abort BEFORE any
        ''' deletion happens"). Un fallo escribiendo UNA entrada ya no aborta la corrida: se anota en
        ''' <see cref="UnpackResult.Fallos"/>, ese archive queda marcado como NO extraído del todo (y por lo
        ''' tanto NO se borra), y el Unpack sigue con las demás entradas y los demás archives. Al final, si
        ''' hubo algún fallo, se tira UNA excepción que los enumera.
        ''' <para>Por qué: la escritura del suelto era <c>File.WriteAllBytes</c>, que sobre un destino
        ''' OCULTO o de SOLO LECTURA tira <c>UnauthorizedAccessException</c> (MEDIDO). Con el abort en el
        ''' primero, un solo suelto oculto —los deja OneDrive y cualquier desempaquetador— rompía el Unpack
        ''' ENTERO, y el re-intento volvía a chocar contra el mismo archivo: quedaba roto para siempre.
        ''' La condición de borrado NO se relaja ni un milímetro: ningún archive con una entrada fallada se
        ''' borra, así que nada se pierde y un re-run sigue donde quedó.</para>
        ''' <para>⛔ LA ESCRITURA VA EN EL LUGAR, por <c>EscrituraEnElLugar</c>. <c>WriteAllBytes</c> pide
        ''' CREATE_ALWAYS y eso muere sobre un archivo oculto; <c>OpenOrCreate</c> + <c>SetLength(0)</c>
        ''' escribe igual, CONSERVA el atributo y —lo que importa bajo los gestores de mods— deja el suelto
        ''' adentro de SU mod en vez de mandarlo a <c>overwrite</c>. La ley y su medición viven en
        ''' <c>EscrituraEnElLugar</c> y en <c>Wardrobe_Manager\OSP_Clases.vb</c>; acá no se escribe una
        ''' segunda. SOLO LECTURA sigue siendo un fallo duro y a propósito: sacarle el atributo a un archivo
        ''' del usuario sería una ley nueva, y esa no la inventa el packer.</para>
        ''' <para>⛔ QUÉ ES EL <c>.bak.unpack</c>, dicho de una vez porque el código y los comentarios decían
        ''' cosas distintas: es el suelto del usuario que este Unpack está por PISAR, guardado aparte, y se
        ''' queda en disco hasta que él lo borre. No lo consume nadie y eso es DELIBERADO — es retención,
        ''' exactamente la misma postura y el mismo costo declarado que la copia heredada de
        ''' <c>GuardarConCopia</c>. Dos comentarios decían "renamed" y el código copiaba desde 2.0.5; ahora
        ''' dicen lo que pasa.</para>
        ''' <para>⛔ Y VA AL PRIMER SLOT LIBRE. El nombre era FIJO y la copia iba con <c>overwrite:=True</c>:
        ''' un segundo Unpack DESTRUÍA el respaldo del primero. Es el mismo defecto que la ley del slot ya
        ''' cierra en <c>GuardarConCopia</c>, así que se usa esa misma ley (<c>PrimerSlotLibre</c>) y no una
        ''' segunda. ⛔ Lo que NO se usa es <c>GuardarConCopia</c> entera: esa BORRA su copia al salir bien
        ''' —es red de crash, no retención— y habría convertido este respaldo en pérdida de datos.</para>
        ''' </summary>
        Public Shared Function Unpack(req As UnpackRequest,
                                       Optional onEntry As Action(Of Integer, Integer, String) = Nothing,
                                       Optional ct As System.Threading.CancellationToken = Nothing,
                                       Optional onArchiveStart As Action(Of String, Integer, Integer) = Nothing) As UnpackResult
            ArgumentNullException.ThrowIfNull(req)
            If String.IsNullOrWhiteSpace(req.OutputDir) Then Throw New ArgumentException("OutputDir is empty.", NameOf(req))
            If String.IsNullOrWhiteSpace(req.ModBaseName) Then Throw New ArgumentException("ModBaseName is empty.", NameOf(req))
            If String.IsNullOrWhiteSpace(req.LooseDataDir) Then Throw New ArgumentException("LooseDataDir is empty.", NameOf(req))

            Dim info = DiscoverArchiveSet(req.OutputDir, req.ModBaseName)
            Dim result As New UnpackResult()

            If info.Archives.Count = 0 AndAlso info.Plugins.Count = 0 Then Return result

            ' Pre-pass: count total entries across all archives so the caller can show determinate
            ' progress. This is cheap — Open() parses the file table only, no payload reads.
            Dim totalEntries As Integer = 0
            For Each archivePath In info.Archives
                Try
                    Using fs = File.OpenRead(archivePath)
                        Using reader As New BethesdaReader(fs)
                            totalEntries += reader.EntriesFiles.Count
                        End Using
                    End Using
                    ' Narrowed to format/parse failures only. A locked-file IOException is NOT swallowed
                    ' here: it propagates, which is correct — if the archive can't even be opened for the
                    ' count pass, the extraction loop below would fail anyway, so surface it now. The
                    ' genuine "corrupt archive" cases (bad magic / truncated table / unsupported variant)
                    ' are skipped in the count; the extraction loop re-opens and surfaces the real error.
                Catch ex As InvalidDataException
                    ' Unrecognized magic / bad structure → skip in count.
                Catch ex As EndOfStreamException
                    ' Truncated header / file table → skip in count.
                Catch ex As NotSupportedException
                    ' Unsupported variant (GNMF, unexpected chunk header size, BSA != v105) → skip.
                End Try
            Next

            Dim entriesDone As Integer = 0
            Dim cancelled As Boolean = False

            ' Phase 1: extract every entry of every archive, and DELETE THE ARCHIVE only after
            ' all of its entries were successfully written to loose. This bounds disk peak to
            ' "one archive worth of duplicated bytes" instead of the entire packed set, while
            ' staying recoverable: if extraction fails or is cancelled mid-archive, that archive
            ' stays on disk and a re-run of Unpack picks up where it left off (un suelto que ya
            ' existía y no era nuestro queda respaldado en `<suelto>.bak.unpack` — no data loss).
            Dim archiveIndex As Integer = 0
            For Each archivePath In info.Archives
                If ct.IsCancellationRequested Then
                    cancelled = True
                    Exit For
                End If

                archiveIndex += 1
                If onArchiveStart IsNot Nothing Then onArchiveStart(archivePath, archiveIndex, info.Archives.Count)

                Dim archiveFullyExtracted As Boolean = False
                Try
                    Using fs = File.OpenRead(archivePath)
                        Using reader As New BethesdaReader(fs)
                            archiveFullyExtracted = True
                            For Each entry In reader.EntriesFiles
                                If ct.IsCancellationRequested Then
                                    cancelled = True
                                    archiveFullyExtracted = False
                                    Exit For
                                End If

                                Dim relPath = entry.FullPath
                                Dim outPath = Path.Combine(req.LooseDataDir, relPath)

                                ' ⛔⛔ EL `Try` EMPIEZA ACA, Y ANTES EMPEZABA TRES SENTENCIAS MAS ABAJO.
                                ' `EnsureDir` y `ExtractToMemory` —las dos que tocan el disco y el
                                ' payload— quedaban AFUERA, asi que su excepcion caia al `Catch` de
                                ' ARCHIVE y se llevaba puesto el SET entero: exactamente el
                                ' "roto para siempre" que el ⛔ del docstring de Unpack declara CERRADO,
                                ' vivo por una puerta que la ley no cubria. Y el mensaje nombraba el
                                ' ARCHIVE, no la entrada, o sea que el usuario no sabia que arreglar.
                                ' Los disparadores son reales y son tres, todos por la misma puerta: el
                                ' directorio de destino ocupado por un ARCHIVO, la ruta de mas de 260
                                ' caracteres, y el payload corrupto que hace tirar a ExtractToMemory.
                                ' El `Catch` de abajo ya pone `archiveFullyExtracted = False`, que es lo
                                ' que impide el borrado del archive: la entrada falla sola, el archive se
                                ' conserva, y el re-run sigue donde quedo. Gate: UnpackSueltosGate U9.
                                Try
                                    EnsureDir(outPath)

                                    Dim bytes = reader.ExtractToMemory(entry.Index)
                                    If bytes Is Nothing Then bytes = Array.Empty(Of Byte)()

                                    ' Respaldos de corridas ANTERIORES: se REPORTAN y no se tocan.
                                    ' ⛔ SE MIRAN TODOS LOS SLOTS, no solo el primero. Desde que el nombre sale
                                    ' de `PrimerSlotLibre` los respaldos son `.bak.unpack`, `…2`, `…3`…, y un
                                    ' reporte que mirara solo el slot 1 SUBCONTARIA justo en el caso que este
                                    ' cambio hizo posible (varias corridas). Se corta en el primer hueco, que es
                                    ' la MISMA regla con la que `PrimerSlotLibre` decide que slot esta ocupado:
                                    ' dos reglas distintas sobre los mismos nombres es como se empieza a
                                    ' reportar una cosa y a pisar otra.
                                    Dim slotBak As Integer = 1
                                    Do
                                        Dim bakViejo = outPath & SUFIJO_BAK_UNPACK &
                                                       If(slotBak = 1, "", slotBak.ToString(Globalization.CultureInfo.InvariantCulture))
                                        If Not File.Exists(bakViejo) Then Exit Do
                                        result.Huerfanos.Add(bakViejo)
                                        slotBak += 1
                                    Loop

                                    ' ⛔ UN EXTRACT VACIO NO HABILITA EL BORRADO DEL ARCHIVE. `ExtractToMemory`
                                    ' de una entrada DX10 devuelve 0 bytes si el wrapper nativo no carga; sin
                                    ' esto se escribia un .dds vacio por textura y despues se borraba el .ba2,
                                    ' que era la unica copia. Se sigue escribiendo lo que haya (el resto del
                                    ' unpack es util) pero el archive NO se borra.
                                    If bytes.Length = 0 Then archiveFullyExtracted = False

                                    ' ⛔ EL FALLO DE UNA ENTRADA NO SE LLEVA LA CORRIDA. Ver el ⛔ del docstring:
                                    ' un suelto de SOLO LECTURA rompía el Unpack entero y el re-intento volvía a
                                    ' chocar contra el mismo archivo. Se anota, este archive NO se borra, y se
                                    ' sigue. Al final se tira con la lista completa.
                                    ' (El `Try` que cubre esto empieza ARRIBA, antes de EnsureDir: ver su ⛔.)
                                    '
                                    ' ⛔ EL RESPALDO DEL SUELTO EN CONFLICTO VA AL PRIMER SLOT LIBRE, y esto
                                    ' NO es cosmético: acá había `File.Copy(outPath, outPath & ".bak.unpack",
                                    ' True)`, o sea overwrite:=True sobre un nombre FIJO. Un segundo Unpack
                                    ' DESTRUÍA el respaldo del primero — exactamente el defecto que la ley
                                    ' del slot ya documenta en GuardarConCopia ("antes el nombre era el fijo
                                    ' `.prev2` y el `Borrar` previo destruia la copia de una segunda caida").
                                    ' Misma ley, una sola casa: `PrimerSlotLibre`, que es pública justamente
                                    ' porque la comparten artefactos con vidas distintas.
                                    '
                                    ' ⛔ Y NO SE USA `GuardarConCopia` ACÁ, aunque sea la red de al lado: esa
                                    ' BORRA su copia al salir bien (es red de CRASH, no retención). El
                                    ' `.bak.unpack` es lo contrario — el suelto del usuario que estamos por
                                    ' pisar, y que se queda hasta que él lo borre. Cambiarlo por
                                    ' GuardarConCopia habría convertido una retención deliberada en pérdida
                                    ' de datos silenciosa.
                                    '
                                    ' ⚠️ Y EL SLOT LIBRE TIENE UN COSTO QUE HAY QUE ACOTAR: sin nada más,
                                    ' cada Unpack repetido sobre el mismo suelto dejaría OTRA copia entera
                                    ' (`.bak.unpack`, `…2`, `…3`…). Lo que lo acota no es un umbral ni una
                                    ' política de retención inventada: es una IMPLICACIÓN EXACTA — si lo que
                                    ' hay en disco ya es byte por byte lo que íbamos a escribir, esta corrida
                                    ' no cambia nada, así que no hay nada que respaldar y tampoco nada que
                                    ' escribir. En el caso que hace crecer la lista (correr Unpack dos veces)
                                    ' el conflicto es contra el archivo que escribió la corrida anterior, o
                                    ' sea exactamente este caso.
                                    Dim yaEstabaEscrito As Boolean = MismosBytes(outPath, bytes)
                                    If Not yaEstabaEscrito AndAlso File.Exists(outPath) AndAlso New FileInfo(outPath).Length > 0 Then
                                        Dim respaldo = EscrituraEnElLugar.PrimerSlotLibre(outPath, SUFIJO_BAK_UNPACK)
                                        File.Copy(outPath, respaldo, overwrite:=False)
                                        ' ⛔ `File.Copy` PROPAGA los atributos del origen (MEDIDO en
                                        ' net8.0.30: un suelto OCULTO deja su copia OCULTA, uno de SOLO
                                        ' LECTURA la deja de solo lectura). Sin esto, el respaldo del dato
                                        ' del usuario le queda INVISIBLE justo cuando lo necesita. Es la
                                        ' misma ley que GuardarConCopia aplica a su `.npcm.prev`, y por eso
                                        ' se llama a la de allá en vez de escribir una segunda acá.
                                        EscrituraEnElLugar.LimpiarAtributos(respaldo)
                                        result.CopiasDeSueltos.Add(respaldo)
                                    End If

                                    ' ⛔ EN EL LUGAR, NO `WriteAllBytes`. CREATE_ALWAYS sobre un destino
                                    ' OCULTO da ACCESS_DENIED (medido acá y en OSP_Clases.vb, net8.0.30);
                                    ' OpenOrCreate + SetLength(0) escribe igual, CONSERVA el atributo y deja
                                    ' el suelto adentro de SU mod bajo MO2 en vez de mandarlo a `overwrite`.
                                    ' Salida regenerable ⇒ no sincroniza: ese default está MEDIDO y acá son
                                    ' miles de archivos por corrida.
                                    ' El parámetro se llama `salida` y no `fs`: `fs` es el FileStream del
                                    ' archive que estamos LEYENDO, y sombrearlo no compila (BC36641).
                                    If Not yaEstabaEscrito Then
                                        EscrituraEnElLugar.Escribir(outPath, Sub(salida) salida.Write(bytes, 0, bytes.Length))
                                    End If
                                    ' Se lista igual: el suelto ESTÁ en disco con el contenido de la entrada,
                                    ' que es lo que el llamador necesita saber para registrarlo. Que esta
                                    ' corrida no haya tenido que escribirlo no lo hace menos extraído.
                                    result.LooseFilesWritten.Add(outPath)
                                Catch exEscritura As Exception
                                    archiveFullyExtracted = False
                                    result.Fallos.Add($"  · {relPath}: {exEscritura.Message}")
                                End Try

                                entriesDone += 1
                                If onEntry IsNot Nothing Then onEntry(entriesDone, totalEntries, relPath)
                            Next
                        End Using
                    End Using
                Catch exArchive As Exception
                    ' ⛔⛔ ACA HABIA UN `Throw` PELADO, Y ERA LA MISMA PERDIDA DE DATOS QUE CIERRA
                    ' UnpackParcialException, POR LA OTRA PUERTA. `Unpack` tiene DOS salidas por error: la
                    ' final (que lleva el resultado) y esta. Con el `Throw` crudo, un archive corrupto en
                    ' mitad del set tiraba una InvalidDataException SIN resultado — y para entonces los
                    ' archives ANTERIORES ya se habian extraido Y BORRADO. El llamador
                    ' (`WM_PackUnpack.Unpack`) solo atrapa `UnpackParcialException`, asi que no registraba
                    ' esos sueltos: contenido cuya UNICA copia acababa de pasar a ser suelta quedaba
                    ' invisible para la app. Gate: Tools\UnpackSueltosGate U7.
                    ' Se anota como un fallo mas y se CORTA el barrido: lo que ya se extrajo esta en disco
                    ' y listado, este archive no se borra (archiveFullyExtracted = False), los plugins se
                    ' conservan porque no todos los archives se fueron, y la salida final tira
                    ' UnpackParcialException CON el resultado. El mensaje del fallo nombra el archive.
                    ' ⛔ EXIT FOR, NO CONTINUE: si un archive del set no se pudo ni abrir, no sabemos en
                    ' que estado esta el resto; seguir seria borrar archives apoyandose en una lectura
                    ' parcial. El usuario arregla lo que el mensaje nombra y vuelve a correr Unpack, que
                    ' sigue donde quedo — que es lo que promete el texto de la excepcion.
                    archiveFullyExtracted = False
                    result.Fallos.Add($"  · {Path.GetFileName(archivePath)}: {exArchive.Message}")
                    Exit For
                End Try

                ' Delete the source archive only after all its entries are safely on disk as loose.
                ' Lo que hace que este Delete funcione con un reader posiblemente vivo en otro hilo es que
                ' las lecturas de archive abren con FileShare.Delete (ver FilesDictionary_class.AbrirArchiveParaLectura),
                ' no el unregister previo del llamador (que sólo vacía el pool) — el unregister sigue
                ' siendo necesario, pero para que no se sirvan entradas del archive viejo, no para
                ' habilitar este borrado.
                If archiveFullyExtracted Then
                    Try
                        File.Delete(archivePath)
                        result.ArchivesRemoved.Add(archivePath)
                    Catch
                        ' Leave it on disk; the loose copy is already written, the user can clean
                        ' up manually or re-run Unpack later.
                    End Try
                End If

                If cancelled Then Exit For
            Next

            ' ⛔ EL CENSO DE LO QUE QUEDÓ, ANTES DE CUALQUIER SALIDA. Va acá —después del bucle y antes
            ' de la fase de plugins— porque desde este punto TODAS las salidas (bien, error, cancelación)
            ' pasan por abajo, y el llamador necesita esta lista en las tres. Ver ArchivesConservados.
            For Each a In info.Archives
                If Not result.ArchivesRemoved.Contains(a, StringComparer.OrdinalIgnoreCase) Then
                    result.ArchivesConservados.Add(a)
                End If
            Next

            ' Phase 2: plugins. Delete only if extraction was not cancelled — leaving plugins behind
            ' on cancel keeps the engine able to find the still-existing archives if any survived.
            '
            ' ⛔⛔ Y SOLO SI NO QUEDO NINGUN ARCHIVE EN DISCO. La convencion del motor es que `Foo.esp`
            ' auto-carga `Foo - Main.ba2`: borrar el plugin dejando su archive vivo es dejar contenido que
            ' NO LO CARGA NADIE — los assets desaparecen in-game y el usuario no tiene forma de saber por
            ' que.
            ' ⛔ ACA PREGUNTABA `result.Fallos.Count = 0`, QUE ES OTRA COSA. "No hubo fallos" NO implica
            ' "no quedo ningun archive": hay por lo menos dos caminos que conservan un archive SIN poblar
            ' `Fallos` —
            '   · el EXTRACT VACIO de mas arriba (`bytes.Length = 0` ⇒ `archiveFullyExtracted = False`),
            '     que es el wrapper de DirectXTex caido sobre una entrada DX10; y
            '   · el `File.Delete(archivePath)` que NO PUDO (su Catch lo deja en disco a proposito).
            ' En los dos el .esp ancla se borraba con su .ba2 todavia ahi.
            ' La invariante que de verdad autoriza a sacar el ancla se MIDE, no se infiere: TODOS los
            ' archives del set que se descubrieron al empezar fueron efectivamente borrados. Es lo que
            ' `ArchivesRemoved` cuenta, y se compara contra `info.Archives`, que es de donde salieron.
            ' Gate: Tools\UnpackSueltosGate U5.
            Dim todosLosArchivesSeFueron = (result.ArchivesRemoved.Count = info.Archives.Count)
            If Not cancelled AndAlso todosLosArchivesSeFueron Then
                For Each pluginPath In info.Plugins
                    Try
                        File.Delete(pluginPath)
                        result.PluginsRemoved.Add(pluginPath)
                    Catch
                    End Try
                Next
            End If

            ' ⛔ SE TIRA AL FINAL, CON LA LISTA COMPLETA — no en el primero. Todo lo que se pudo extraer YA
            ' esta en disco y ningun archive con una entrada fallada se borro, asi que el estado es
            ' recuperable: el usuario arregla lo que el mensaje nombra (un suelto de solo lectura, un
            ' permiso) y vuelve a correr Unpack, que sigue donde quedo.
            ' ⛔ Y SE TIRA CON EL RESULTADO ADENTRO, no pelado: "todo lo demas si se extrajo" es una
            ' afirmacion sobre el DISCO, y el llamador necesita la LISTA para registrarlo. Ver
            ' UnpackParcialException, que es donde esta el por que entero.
            If result.Fallos.Count > 0 Then
                Throw New UnpackParcialException(
                    $"{result.Fallos.Count} archivo(s) no se pudieron escribir. Los archives que los " &
                    "contienen NO se borraron y todo lo demas si se extrajo: arregla lo de abajo y volve a " &
                    "correr Unpack, que sigue donde quedo." & Environment.NewLine &
                    String.Join(Environment.NewLine, result.Fallos), result)
            End If

            Return result
        End Function
    End Class

End Namespace
