Imports System.IO
Imports System.Text.Json

''' <summary>El DIARIO de un lote: lo único que sobrevive a que el proceso muera a mitad del guardado.
'''
''' <para>⛔⛔ DOS ESTADOS, Y LA TRANSICION ES IRREVERSIBLE:</para>
''' <code>en_curso → resuelto → borrar copias → borrar diario</code>
''' <list type="bullet">
''' <item><c>en_curso</c>: todavía puede hacer falta restaurar;</item>
''' <item><c>resuelto</c>: los destinos YA contienen una decisión completa —guardado confirmado o rollback
''' terminado— y lo único que queda es limpiar copias.</item></list>
''' <para><b>Nunca se borra una copia mientras el diario siga en `en_curso`. Nunca se ofrece restauración si
''' está `resuelto`.</b></para>
'''
''' <para>⛔ QUE AGUJERO CIERRA, y era REAL en lo que quedó como 2.0.9 preliminar: `Confirmar` borraba las
''' copias ANTES de cerrar el diario, y `Deshacer` borraba cada copia al restaurarla. Un corte en esa
''' ventana dejaba un diario `en_curso` con la mitad de sus copias ya borradas — y el arranque siguiente
''' ofrecía "restaurar" un guardado que en realidad estaba CONFIRMADO, con copias a medias: cosecha mixta.
''' Con el estado durable, el corte en la limpieza sólo reinicia la limpieza, y la restauración no se
''' ofrece nunca sobre algo ya resuelto.</para>
'''
''' <para>⛔ NO VIVE EN `Data`. Es metadata de la aplicación, no del mod: va en la carpeta local de la app.
''' Ponerlo al lado de los archivos del usuario lo metería bajo el VFS de MO2 y ensuciaría el mod. Por eso
''' además el reemplazo atómico de abajo es legítimo: <c>File.Replace</c> corre fuera del VFS.</para>
'''
''' <para>⛔ Y NO RESTAURA SOLO. Después de un reinicio no hay forma de saber si el usuario quiere volver a
''' la versión anterior o conservar lo nuevo. El diario DETECTA y DESCRIBE; el diálogo del arranque
''' decide. Ver <c>FO4_Base_Library.RecuperacionDeLotes</c>.</para></summary>
Public NotInheritable Class DiarioDeEscritura

    Public Const SufijoDiario As String = ".npcm-txn.json"
    Private Const SufijoTemporal As String = ".writing"
    Private Const VersionActual As Integer = 1

    ''' <summary>Una operación del lote. <c>Operacion</c> es "crear", "reemplazar" o "borrar": son las tres
    ''' que el rollback distingue, y sin ese dato una copia suelta no dice qué había que hacer con ella.</summary>
    Public Class Entrada
        Public Property Destino As String
        Public Property Copia As String
        Public Property Operacion As String       ' crear, reemplazar, borrar
        Public Property Atributos As Integer
    End Class

    Public Class Contenido
        Public Property Version As Integer = VersionActual
        Public Property Estado As String = "en_curso"
        Public Property Entradas As New List(Of Entrada)
    End Class

    Private ReadOnly _ruta As String
    Private ReadOnly _contenido As Contenido
    Private ReadOnly _error As String

    Public ReadOnly Property Ruta As String
        Get
            Return _ruta
        End Get
    End Property

    Public ReadOnly Property Entradas As IReadOnlyList(Of Entrada)
        Get
            Return _contenido.Entradas
        End Get
    End Property

    Public ReadOnly Property Estado As String
        Get
            Return _contenido.Estado
        End Get
    End Property

    ''' <summary>False cuando el archivo no se pudo interpretar. ⛔ Un diario ilegible NO se borra solo y NO
    ''' se trata como transacción recuperable: se le MUESTRA al usuario. Borrarlo en silencio destruiría la
    ''' única pista de que hubo un guardado a medias.</summary>
    Public ReadOnly Property EsLegible As Boolean
        Get
            Return String.IsNullOrEmpty(_error)
        End Get
    End Property

    Public ReadOnly Property ErrorDeLectura As String
        Get
            Return If(_error, "")
        End Get
    End Property

    Public Sub New(carpeta As String)
        If String.IsNullOrWhiteSpace(carpeta) Then
            Throw New ArgumentException("Empty journal directory.", NameOf(carpeta))
        End If
        Directory.CreateDirectory(carpeta)
        _ruta = Path.Combine(carpeta, Guid.NewGuid().ToString("N") & SufijoDiario)
        _contenido = New Contenido()
        _error = ""
        PersistirAtomico()
    End Sub

    Private Sub New(ruta As String, contenido As Contenido, errorLectura As String)
        _ruta = ruta
        _contenido = If(contenido, New Contenido())
        _error = If(errorLectura, "")
    End Sub

    ''' <summary>Lee un diario de una corrida anterior. ⛔ NUNCA devuelve Nothing: un archivo que no se pudo
    ''' interpretar vuelve como diario ILEGIBLE con su error adentro. Devolver Nothing lo hacía
    ''' desaparecer del arranque, que es justo lo contrario de lo que hace falta.
    ''' <para>La validación es estructural y cerrada: versión conocida, estado conocido, y cada entrada con
    ''' la forma que su operación exige —una creación NO puede traer copia (no había nada que respaldar) y
    ''' un reemplazo o un borrado SÍ tienen que traerla (sin copia no hay nada que restaurar)—. Un diario
    ''' que no cumple eso no describe una transacción recuperable, y fingir que sí sería peor.</para></summary>
    Public Shared Function Leer(ruta As String) As DiarioDeEscritura
        Try
            Dim contenido = JsonSerializer.Deserialize(Of Contenido)(File.ReadAllBytes(ruta))
            If contenido Is Nothing Then Throw New InvalidDataException("Empty journal.")
            If contenido.Version <> VersionActual Then
                Throw New InvalidDataException("Unsupported journal version: " & contenido.Version)
            End If
            If contenido.Estado <> "en_curso" AndAlso contenido.Estado <> "resuelto" Then
                Throw New InvalidDataException("Unknown journal state: " & contenido.Estado)
            End If
            If contenido.Entradas Is Nothing Then Throw New InvalidDataException("Missing entry list.")

            For i = 0 To contenido.Entradas.Count - 1
                Dim e = contenido.Entradas(i)
                If e Is Nothing OrElse String.IsNullOrWhiteSpace(e.Destino) Then
                    Throw New InvalidDataException($"Invalid entry {i}.")
                End If
                Select Case e.Operacion
                    Case "crear"
                        If Not String.IsNullOrEmpty(e.Copia) Then
                            Throw New InvalidDataException($"Creation entry {i} has a backup.")
                        End If
                    Case "reemplazar", "borrar"
                        If String.IsNullOrWhiteSpace(e.Copia) Then
                            Throw New InvalidDataException($"Entry {i} requires a backup.")
                        End If
                    Case Else
                        Throw New InvalidDataException($"Unknown operation in entry {i}.")
                End Select
            Next
            Return New DiarioDeEscritura(ruta, contenido, "")
        Catch ex As Exception
            Return New DiarioDeEscritura(ruta, Nothing, ex.Message)
        End Try
    End Function

    ''' <summary>⛔ SE LLAMA DESPUES DE ASEGURAR LA COPIA Y ANTES DE TOCAR EL DESTINO. Al revés el diario
    ''' describiría un estado que todavía no ocurrió, o —peor— el destino cambiaría sin que nadie lo haya
    ''' anotado.
    ''' <para>⛔ Y SI NO SE PUDO PERSISTIR, LA ENTRADA SE SACA DE LA LISTA EN MEMORIA. Sin ese rollback, el
    ''' objeto quedaría afirmando una operación que el archivo durable no tiene: la próxima persistencia la
    ''' escribiría como si siempre hubiera estado, y el llamador seguiría adelante creyendo que quedó
    ''' anotada.</para></summary>
    Public Sub Registrar(destino As String, copia As String, operacion As String,
                         atributos As FileAttributes)
        ExigirEditable()
        Dim e = New Entrada With {
            .Destino = destino,
            .Copia = If(copia, ""),
            .Operacion = operacion,
            .Atributos = CInt(atributos)
        }
        _contenido.Entradas.Add(e)
        Try
            PersistirAtomico()
        Catch
            _contenido.Entradas.RemoveAt(_contenido.Entradas.Count - 1)
            Throw
        End Try
    End Sub

    ''' <summary>Debe ejecutarse después de completar y verificar el guardado o rollback, pero ANTES de
    ''' borrar una sola copia. Es la línea que separa "todavía se puede restaurar" de "sólo queda limpiar".
    ''' <para>Si la persistencia falla, el estado en memoria vuelve a `en_curso`: el objeto no puede afirmar
    ''' una transición que el disco no tiene.</para></summary>
    Public Sub MarcarResuelto()
        If Not EsLegible Then Throw New InvalidOperationException(ErrorDeLectura)
        If _contenido.Estado = "resuelto" Then Return
        _contenido.Estado = "resuelto"
        Try
            PersistirAtomico()
        Catch
            _contenido.Estado = "en_curso"
            Throw
        End Try
    End Sub

    ''' <summary>Borra el diario y su temporal. Devuelve True sólo si el diario ya no está.
    ''' <para>Devuelve Boolean y no Sub a propósito: el llamador tiene que poder DECIR que la limpieza quedó
    ''' pendiente. Un diario resuelto que no se pudo borrar hace que el arranque siguiente reintente la
    ''' limpieza —inofensivo—, pero tragarse ese hecho dejaría al usuario sin saber por qué reaparece.</para></summary>
    Public Function Cerrar() As Boolean
        Try
            If File.Exists(_ruta) Then File.Delete(_ruta)
            Dim tmp = _ruta & SufijoTemporal
            If File.Exists(tmp) Then File.Delete(tmp)
            Return Not File.Exists(_ruta)
        Catch
            Return False
        End Try
    End Function

    Private Sub ExigirEditable()
        If Not EsLegible Then Throw New InvalidOperationException(ErrorDeLectura)
        If _contenido.Estado <> "en_curso" Then
            Throw New InvalidOperationException("The journal is already resolved.")
        End If
    End Sub

    ''' <summary>Escribe a un temporal, lo sincroniza y RECIEN AHI reemplaza el diario.
    ''' <para>⛔ ACA SI VA `File.Replace`, y no contradice la ley de MO2: esa ley es sobre los archivos del
    ''' MOD, que tienen que conservar su identidad para el VFS y el hardlink. El diario vive en
    ''' `LocalApplicationData`, fuera de `Data` y fuera del VFS — y acá el reemplazo atómico es exactamente
    ''' lo que hace falta, porque un diario a medio escribir es un diario ilegible.</para>
    ''' <para>El temporal está en el MISMO volumen que el diario (es el mismo path + sufijo), que es la
    ''' condición que `File.Replace` exige.</para></summary>
    Private Sub PersistirAtomico()
        Dim bytes = JsonSerializer.SerializeToUtf8Bytes(_contenido)
        Dim tmp = _ruta & SufijoTemporal
        Try
            Using fs As New FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None)
                fs.Write(bytes, 0, bytes.Length)
                fs.Flush(flushToDisk:=True)
            End Using
            If File.Exists(_ruta) Then
                File.Replace(tmp, _ruta, Nothing, ignoreMetadataErrors:=True)
            Else
                File.Move(tmp, _ruta)
            End If
        Catch
            Try
                If File.Exists(tmp) Then File.Delete(tmp)
            Catch
            End Try
            Throw
        End Try
    End Sub
End Class
