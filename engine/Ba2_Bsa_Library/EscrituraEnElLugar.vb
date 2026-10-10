Imports System.IO
Imports System.Linq

''' <summary>Escritura de archivos que funciona adentro de los dos gestores de mods.
''' <para>LEY: se sobrescribe el destino EN EL LUGAR. No hay temporal, no hay rename y no se borra el
''' destino. Bajo MO2 el VFS resuelve la apertura de un archivo existente al archivo real del mod que lo
''' aporta; bajo Vortex la escritura viaja por el hardlink. Un rename o un Delete+Create rompen las dos
''' cosas a la vez.</para>
''' <para>⛔ BASE Y ESTADO DE ESA AFIRMACION — es sobre TERCEROS, y esta cabecera es su UNICA casa (los
''' demas sitios apuntan aca, no la repiten): (1) el VFS de MO2 (usvfs) engancha abrir/borrar/renombrar
''' pero NO <c>ReplaceFileW</c>, asi que <c>File.Replace</c> corre contra el disco real, fuera del
''' perfil; (2) Vortex despliega por hardlink, y borrar o renombrar el destino corta el vinculo — el mod
''' se queda con la version vieja, y bajo MO2 lo escrito con nombre nuevo cae en <c>overwrite</c>.
''' Las dos afirmaciones vienen del cambio que introdujo esta clase (2.0.5); su derivacion NO quedo
''' registrada y NO estan re-medidas contra los gestores en este arbol. Si alguien las re-mide, la
''' medicion va ACA. Hasta entonces: NO volver al <c>.tmp</c>+<c>File.Replace</c> sin esa medicion —
''' el sintoma que este diseño evita es el archivo aterrizando en el Data real o en <c>overwrite</c>,
''' fuera de su mod.</para>
''' <para>NO ES ATOMICO, y tampoco tolera un lector que no comparta escritura: no existe primitivo que sea
''' a la vez virtualizado por el VFS, respetuoso del hardlink y atomico. La red la pone GuardarConCopia,
''' que NO escribe si no pudo dejar antes una copia verificada del contenido que va a pisar.</para>
''' <para>ESTA CLASE VA EN EL NAMESPACE RAIZ del proyecto (no en BethesdaArchive.Core): los consumidores
''' la importan como BSA_BA2_Library_DLL. Moverla de namespace rompe a los siete a la vez.</para>
''' <para>⛔⛔ EL CONTRATO PUBLICO DE LAS CUATRO OPERACIONES, Y LO QUE CADA UNA **NO** GARANTIZA. Esta es
''' la casa de esa tabla: los sitios que la necesiten apuntan acá y no la repiten.</para>
''' <list type="table">
''' <item><term>Escribir</term><description>salida REGENERABLE; sin copia; NO garantiza supervivencia a un
''' corte electrico. Si se pierde, se rehace.</description></item>
''' <item><term>GuardarConCopia</term><description>UN archivo del usuario; no toca el destino si no
''' consiguio Y SINCRONIZO un respaldo; restaura ante excepcion manejable; NO es atomico frente a la
''' terminacion del proceso.</description></item>
''' <item><term>LoteConCopias</term><description>varios archivos que son UNA unidad; rollback ante
''' excepcion; diario durable para detectar una terminacion abrupta; la recuperacion la DECIDE el usuario
''' en el arranque siguiente.</description></item>
''' <item><term>VolcarEncima</term><description>instala bytes YA preparados sobre el mismo archivo para
''' conservar la identidad que MO2/Vortex necesitan; el origen tiene que sobrevivir hasta que el destino
''' este verificado.</description></item></list>
''' <para>⛔ LA GARANTIA HONESTA, y la frase que NO se usa sin calificar: no existe escritura en el lugar
''' verdaderamente atomica. Nunca escribir «todo o nada» a secas — se dice <b>«todo o nada ante excepciones
''' manejadas dentro del proceso»</b>. Ante terminacion abrupta lo que hay es el DIARIO y las copias, que
''' permiten DETECTAR y ofrecer la recuperacion; no una transaccion que se resuelva sola.</para></summary>
Public NotInheritable Class EscrituraEnElLugar

    Private Sub New()
    End Sub

    ''' <summary>El cuerpo CERRO el stream, rompiendo el contrato de <see cref="EscribirNucleo"/>.
    ''' <para>⛔ ES UN TIPO PROPIO PORQUE HAY UNA DECISION COLGADA DE EL, no para clasificar mejor. Cuando
    ''' esto se tira, la escritura del cuerpo YA TERMINO: el wrapper cerro y vacio, y los bytes que hay en
    ''' el destino son exactamente los que el cuerpo produjo. Lo unico que la violacion se llevo puesto es
    ''' la GARANTIA DE DURABILIDAD (el <c>Flush(True)</c> de esta clase no llego a correr).</para>
    ''' <para><b>Por eso un destino NUEVO no se borra en este camino, y ese es el cambio.</b> El
    ''' <c>Borrar(destino)</c> de <see cref="Escribir"/> existe para no dejar "un archivo de 0 bytes donde
    ''' antes no habia NADA" — un archivo del que no sabemos nada porque el cuerpo murio a mitad. Acá el
    ''' cuerpo NO murio: volvio normalmente. Y con un cuerpo bien portado esos mismos bytes se aceptan sin
    ''' verificar nada —<see cref="Escribir"/> no mira un solo byte antes de devolver "guardado"—, asi que
    ''' aplicarles un estandar mas duro por el solo hecho de que el cuerpo uso un wrapper es una asimetria
    ''' que no se sostiene: no sabemos MENOS del archivo en un caso que en el otro.</para>
    ''' <para>⛔ Y SE SIGUE TIRANDO IGUAL. Que el archivo se conserve no vuelve buena a la corrida: el
    ''' llamador tiene que arreglar el <c>leaveOpen:=True</c> antes de confiar en la durabilidad de lo que
    ''' acaba de escribir. Conservar el archivo y avisar son cosas distintas.</para>
    ''' <para>⚠️ NO cambia el otro brazo: si el CUERPO TIRO, los bytes del destino si son desconocidos
    ''' (pudo morir a mitad) y el borrado del destino nuevo se queda como estaba. Tampoco cambia la
    ''' restauracion desde la copia de <see cref="GuardarConCopia"/>: alli el destino EXISTIA y devolverle
    ''' al usuario su version anterior es lo correcto pase lo que pase.</para>
    ''' <para>Hereda de <see cref="InvalidOperationException"/> a proposito: es lo que los llamadores y el
    ''' gate ya atrapan, asi que nadie que hoy lo maneje deja de manejarlo.</para></summary>
    Public NotInheritable Class ContratoDelCuerpoException
        Inherits InvalidOperationException

        Public Sub New(mensaje As String)
            MyBase.New(mensaje)
        End Sub
    End Class

    ''' <summary>Sufijo de la copia. NO es `.bak` (lo usa ArchivePackager) ni `.npcm.bak` (ese es el
    ''' respaldo de LoadOrderActivator, que vive hasta que su escritura queda CONFIRMADA por el verify
    ''' de relectura y recien ahi se borra — misma ley que esta, con el punto de confirmacion corrido).
    ''' <para>Los slots son `.npcm.prev`, `.npcm.prev2`, `.npcm.prev3`… La copia de cada corrida va al
    ''' primer slot LIBRE, asi que ninguna copia anterior se pisa nunca.</para></summary>
    Public Const SufijoCopia As String = ".npcm.prev"

    ''' <summary>Nucleo: la unica implementacion de la ley. <paramref name="seToco"/> sale en True desde
    ''' el instante en que el destino pudo abrirse — o sea, desde que puede quedar a medias. Los
    ''' llamadores lo usan para distinguir "no pude ni empezar" (destino intacto) de "fallo escribiendo".
    ''' <para><paramref name="sincronizar"/> agrega un <c>FlushFileBuffers</c> antes de cerrar: los bytes
    ''' llegan al plato y no se quedan en la cache del sistema. Ver <see cref="Escribir"/> para el costo
    ''' MEDIDO y por que el default es False.</para>
    ''' <para>⛔⛔ CONTRATO DEL CUERPO: <b>el cuerpo escribe en el stream y NO LO CIERRA.</b> El dueño del
    ''' stream es esta clase, que lo abre, lo trunca, lo sincroniza y lo cierra. Un cuerpo que envuelve el
    ''' stream en un `BinaryWriter` / `StreamWriter` / `XmlWriter` / `GZipStream` dentro de un `Using`
    ''' <b>tiene que pasar `leaveOpen:=True`</b>, porque si no el `End Using` del wrapper cierra el
    ''' FileStream de abajo.</para>
    ''' <para>Esto no es teorico y el costo fue una app cerrada: `OSD_Class.Save_As` envolvia en
    ''' `New IO.BinaryWriter(stream)` sin `leaveOpen`. Mientras no habia nada despues de `cuerpo(fs)` el
    ''' defecto era invisible —el stream se cerraba dos veces y a nadie le importaba—; en cuanto el
    ''' `Flush(True)` se puso DESPUES del cuerpo, el mismo codigo empezo a tirar
    ''' `ObjectDisposedException: Cannot access a closed file` en produccion. Por eso la guarda de abajo
    ''' existe: el proximo violador tiene que fallar diciendo QUE contrato rompio y sobre QUE archivo, no
    ''' con un ObjectDisposed criptico desde las entrañas del framework.</para></summary>
    Private Shared Sub EscribirNucleo(destino As String, cuerpo As Action(Of Stream), ByRef seToco As Boolean,
                                      Optional sincronizar As Boolean = False)
        seToco = False
        Dim carpeta = IO.Path.GetDirectoryName(destino)
        If Not String.IsNullOrEmpty(carpeta) Then Directory.CreateDirectory(carpeta)

        ' OpenOrCreate SIN truncar. Si el destino esta TOMADO o es de SOLO LECTURA se falla ACA y el
        ' archivo queda intacto.
        ' ⛔ SOBRE UN DESTINO OCULTO **NO** SE FALLA: se ESCRIBE, y el atributo se CONSERVA. Esa es la
        ' razon de ser de esta primitiva y esta medida en el gate (caso M, y el G21 que cita el docstring
        ' de mas abajo: "destino oculto restaurado byte a byte y todavia oculto"). Este comentario decia lo
        ' contrario —que el oculto tambien fallaba aca— y contradecia a su propio gate: es CREATE_ALWAYS el
        ' que da ACCESS_DENIED sobre un archivo oculto, y por eso no se usa CREATE_ALWAYS.
        Using fs As New FileStream(destino, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read)
            seToco = True
            fs.SetLength(0)
            cuerpo(fs)

            ' ⛔ GUARDA DEL CONTRATO. Si el cuerpo cerro el stream, `CanWrite` da False y CUALQUIER cosa
            ' que hagamos despues revienta con un mensaje que no nombra ni la causa ni el archivo. Se
            ' falla ACA, diciendo las dos cosas.
            ' ⛔ Y NO se saltea el Flush en silencio: un cuerpo que cierra el stream ya se llevo puesta la
            ' garantia de durabilidad (el wrapper cerro y sincronizo lo que quiso, cuando quiso), asi que
            ' seguir de largo seria devolver "guardado" sobre una promesa que no podemos sostener.
            ' ⛔ TIPO PROPIO, no un InvalidOperationException pelado: los llamadores tienen que poder
            ' distinguir ESTE fallo de cualquier otro, porque de el cuelga si el destino nuevo se borra o
            ' se conserva. Ver ContratoDelCuerpoException. Deriva de InvalidOperationException, asi que
            ' quien ya lo atrapaba lo sigue atrapando.
            If Not fs.CanWrite Then
                Throw New ContratoDelCuerpoException(
                    $"The write body closed the stream for '{IO.Path.GetFileName(destino)}'. Bodies passed to " &
                    "EscrituraEnElLugar must write and NOT close: this class owns the stream (it opens, " &
                    "truncates, syncs and closes it). If the body wraps the stream in a BinaryWriter, " &
                    "StreamWriter, XmlWriter or similar inside a Using, pass leaveOpen:=True. " &
                    "The file was left with the bytes the body wrote, but WITHOUT the durability flush " &
                    "this class guarantees.")
            End If

            ' Adentro del Using: cerrar NO sincroniza. Sin esto los bytes viven en la cache del sistema y
            ' un corte de luz se los lleva aunque el guardado haya dicho que salio bien.
            If sincronizar Then fs.Flush(True)
        End Using
    End Sub

    ''' <summary>Sobrescribe <paramref name="destino"/>. Sin red: es el camino de la salida regenerable
    ''' (horneado, build, texturas, materiales, cache, .pex).
    ''' <para>⛔ QUE SIGNIFICA "materiales" EN ESA LISTA, porque la ambiguedad ya costo una revision: es el
    ''' camino de CLONE (<c>Clone_Materials_class.WriteMaterialJob</c>, que escribe a <c>ManoloCloned\</c> —
    ''' una carpeta que la app posee y regenera sola en la corrida siguiente). NO es el guardado de un
    ''' material desde el editor de Wardrobe Manager: ese pisa un suelto YA INSTALADO que la app no
    ''' produjo y no puede rehacer, es un archivo por click, y va por <see cref="GuardarConCopia"/> —
    ''' igual que <c>NifContent_Class.Save_As_Manolo_ConCopia</c> frente a <c>Save_As_Manolo</c>.</para>
    ''' <para>⛔ LA LISTA NO SE LEE POR TIPO DE ARCHIVO, SE LEE POR LLAMADOR. El NIF esta en las DOS
    ''' listas y el material tambien: lo que decide es quien escribe y si lo escrito se puede rehacer.</para>
    ''' <para>⛔ <paramref name="sincronizar"/> DEFAULTEA EN FALSE Y ESE DEFAULT ESTA MEDIDO. Con la forma
    ''' exacta de <see cref="EscribirNucleo"/> (OpenOrCreate → SetLength(0) → Write → Dispose), 300
    ''' archivos preexistentes reescritos, dos corridas alternadas por configuracion:</para>
    ''' <list type="bullet">
    ''' <item>disco del sistema, 300 KB: 0,654 ms/archivo sin sincronizar → 5,181 ms con. <b>+4,53 ms, x7,9.</b></item>
    ''' <item>disco del sistema, 2 KB: 0,580 → 3,242 ms. <b>+2,66 ms.</b> El costo es POR LLAMADA, no por byte.</item>
    ''' <item>segundo disco, 300 KB: 0,827 → 1,896 ms. +1,07 ms.</item>
    ''' </list>
    ''' <para>Sobre el horneado eso es +4,5 s cada 1.000 NIFs (+45 s a los 10.000) encima de un paso que
    ''' hoy tarda 0,654 ms. Por eso la salida REGENERABLE no sincroniza: si se pierde, se rehornea. El
    ''' dato del usuario si — <see cref="GuardarConCopia"/> sincroniza siempre y no pregunta.
    ''' ⛔ No dar vuelta este default sin volver a medir.</para></summary>
    Public Shared Sub Escribir(destino As String, cuerpo As Action(Of Stream),
                               Optional sincronizar As Boolean = False)
        If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
        If cuerpo Is Nothing Then Throw New ArgumentNullException(NameOf(cuerpo))

        Dim existia = File.Exists(destino)
        Dim seToco As Boolean = False
        Try
            EscribirNucleo(destino, cuerpo, seToco, sincronizar)
        Catch ex As ContratoDelCuerpoException
            ' ⛔ ACA NO SE BORRA, y el motivo entero esta en ContratoDelCuerpoException: el cuerpo VOLVIO
            ' NORMALMENTE y despues fallo la guarda, asi que los bytes del destino son exactamente los que
            ' el cuerpo produjo — los mismos que, viniendo de un cuerpo bien portado, esta funcion acepta
            ' sin verificar nada. Lo que se perdio es el Flush, no el archivo. Se tira igual.
            Throw
        Catch
            ' No dejar un archivo de 0 bytes donde antes no habia NADA: el juego y xEdit levantan un
            ' .esp vacio como corrupto. Este brazo es el del cuerpo que TIRO: ahi los bytes SI son
            ' desconocidos (pudo morir a mitad) y el borrado se queda como estaba.
            If seToco AndAlso Not existia Then Borrar(destino)
            Throw
        End Try
    End Sub

    ''' <summary>Igual, con red: copia el contenido actual a &lt;destino&gt;.npcm.prev antes de tocarlo y, si
    ''' la escritura falla DESPUES de haber empezado, restaura el destino solo. Es el camino de los datos
    ''' del usuario (plugin, ini de BodyGen, sidecars, proyecto de WM). NO se usa en horneado ni en build:
    ''' ahi son miles de archivos por corrida y se regeneran solos.
    ''' <para>⛔ "RESTAURA EL DESTINO SOLO" ES UNA PROMESA, Y ESTUVO ROTA. La restauracion era un
    ''' <c>File.Copy</c> —CREATE_ALWAYS— mientras la escritura ya usaba <c>OpenOrCreate</c>: sobre un
    ''' destino OCULTO, que es justo el caso para el que existe todo este diseño, el camino de ida
    ''' funcionaba y el de vuelta tiraba, dejando el destino PARCIAL. Hoy la vuelta usa la MISMA primitiva
    ''' que la ida. Testigos: caso <c>M</c> (la medicion de que primitiva aguanta que atributo) y
    ''' <c>G21</c> (destino oculto restaurado byte a byte y todavia oculto) de
    ''' <c>Tools\EscrituraEnElLugarGate</c>; <c>G8</c> es el mismo camino con destino normal, que es por lo
    ''' que el defecto podia vivir sin que nada se pusiera rojo.</para>
    ''' <para>⛔ LA COPIA NO ES OPCIONAL. Si hay contenido que perder y no se pudo dejar una copia
    ''' VERIFICADA (el tamano tiene que coincidir: File.Copy puede dejarla a medias), NO se escribe. Antes
    ''' esto seguia igual "porque cancelar el guardado seria peor", y el resultado era truncar el unico
    ''' ejemplar que el usuario tenia sin nada para volver.</para>
    ''' <para>⛔ LA LEY DE LA COPIA, y por que dice esto y no otra cosa. Las copias HEREDADAS (las que
    ''' dejo una caida anterior) NO se pisan y NO se borran a la ligera: cada una es un ejemplar del dato
    ''' del usuario que no existe en ningun otro lado. La de esta corrida va al primer slot LIBRE
    ''' (`.npcm.prev`, `.npcm.prev2`, `.npcm.prev3`…), asi que tomar red nunca destruye red.</para>
    ''' <para><b>Al salir bien se borra la copia que tomo ESTA corrida. Una heredada se borra SOLO cuando
    ''' esta corrida PROBO que no guarda nada propio: cuando la copia que tomamos —los bytes que habia en
    ''' el destino al empezar— es BYTE POR BYTE la misma.</b> No hay umbral, no hay tolerancia y no se
    ''' mira el CONTENIDO para adivinar si un archivo "parece" valido: es una comparacion exacta.</para>
    ''' <para>Antes decia «un guardado que sale bien limpia las dos» y eso BORRABA EL ULTIMO EJEMPLAR.
    ''' Medido: { destino = 0 B, .prev = 3000 B } → { destino = 5 B, .prev = AUSENTE }. El 0 B es
    ''' exactamente lo que deja un corte a mitad de <see cref="EscribirNucleo"/>, porque `SetLength(0)`
    ''' corre antes del cuerpo; y con el corte cayendo un poco despues el destino queda A MEDIAS con
    ''' longitud > 0 y el daño era identico. Gate: Tools\EscrituraEnElLugarGate (G3, G4, G5, G12).</para>
    ''' <para>⚠️ EL COSTO, dicho y no escondido: una heredada con contenido propio QUEDA EN DISCO al lado
    ''' del archivo del usuario (bajo Mod Organizer, en la carpeta Overwrite) hasta que el usuario la
    ''' borre o hasta que un guardado la pruebe redundante. Eso es deliberado: es su dato, y el unico
    ''' que lo tiene. La alternativa —borrarla igual— es el defecto que este parrafo documenta.</para></summary>
    Public Shared Sub GuardarConCopia(destino As String, cuerpo As Action(Of Stream))
        If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
        If cuerpo Is Nothing Then Throw New ArgumentNullException(NameOf(cuerpo))

        ' Las heredadas ocupan slots desde el 1; la de esta corrida va al primero LIBRE. Que el slot este
        ' libre es lo que hace seguro el `Borrar(copia)` del camino de error: nunca puede caer sobre una
        ' copia que no tomo esta corrida. (Antes el nombre era el fijo `.prev2` y el `Borrar` previo
        ' destruia la copia de una segunda caida — el mismo daño un nivel mas adentro.)
        Dim heredadas = SlotsOcupados(destino, SufijoCopia)
        ' ⛔ Misma ley que los lotes: el primer HUECO, no el conteo.
        Dim copia = PrimerSlotLibre(destino, SufijoCopia)

        ' ⛔⛔ TODO DESTINO QUE EXISTA SE RESPALDA, 0 BYTES INCLUIDO — y esto ARREGLA UN AGUJERO, no es
        ' simetria por prolijidad. Acá habia `AndAlso New FileInfo(destino).Length > 0`: con un destino
        ' EXISTENTE de 0 bytes no se tomaba copia, asi que `copiaHecha` quedaba en False y el `Catch` de mas
        ' abajo NO restauraba nada — un fallo a mitad del cuerpo dejaba el archivo con BYTES PARCIALES
        ' donde habia uno vacio, contra el docstring de este mismo metodo, que promete que "si la escritura
        ' falla DESPUES de haber empezado, restaura el destino solo". No se perdia DATO (no habia), pero la
        ' promesa era falsa y el estado posterior al fallo no era el anterior.
        ' Copiar 0 bytes es trivial, el verify por tamaño da 0 = 0, y `MismoContenido` maneja vacios.
        ' ⚠️ Y NO afloja la ley de las heredadas: con el destino en 0 B la copia sale de 0 B, asi que una
        ' heredada con contenido NO da `MismoContenido` y sobrevive — que es exactamente lo que G3 exige.
        Dim existeDestino As Boolean = File.Exists(destino)
        Dim copiaHecha As Boolean = False
        Dim causaCopia As String = ""
        If existeDestino Then
            Try
                File.Copy(destino, copia, overwrite:=True)
                LimpiarAtributos(copia)
                ' ⛔ EXISTIR NO ES ESTAR COMPLETA. File.Copy puede dejar el destino a medias (disco lleno,
                ' error de I/O) y ahi lo que queda es un archivo que PARECE una copia. Se compara el
                ' tamano contra el original: si no coinciden, no hay copia.
                copiaHecha = (New FileInfo(copia).Length = New FileInfo(destino).Length)
                If Not copiaHecha Then
                    Borrar(copia)
                Else
                    ' ⛔⛔ LA RED SOLO ES RED SI LLEGO AL PLATO, Y AHORA ESO SE EXIGE. `Sincronizar` era
                    ' best-effort —se tragaba el fallo— y el metodo seguia adelante a TRUNCAR el destino
                    ' con un respaldo cuya durabilidad nadie pudo confirmar. Es la misma frase que esta
                    ' clase ya le exige al packer, aplicada a si misma.
                    Try
                        SincronizarRespaldo(copia)
                    Catch exFlush As Exception
                        Borrar(copia)
                        copiaHecha = False
                        causaCopia = "el respaldo no se pudo forzar a disco: " & exFlush.Message
                    End Try
                End If
            Catch ex As Exception
                ' ⛔ LA CAUSA REAL NO SE TIRA A LA BASURA. Acá habia un `Catch` pelado, asi que una
                ' `PathTooLongException`, una `DirectoryNotFoundException` o una `NotSupportedException`
                ' salian todas disfrazadas del mensaje fijo de abajo —"disco lleno o el archivo tomado"— y
                ' el usuario se quedaba buscando espacio en disco por un problema de ruta.
                copiaHecha = False
                causaCopia = ex.Message
                Borrar(copia)
            End Try

            ' ⛔ SIN RED NO SE TRUNCA. Si hay contenido que perder y no se pudo respaldar, no se toca el
            ' original: escribir igual seria destruir la unica copia que existe sin nada para volver. El
            ' usuario ve por que fallo (disco lleno, permisos, el archivo tomado) y su dato sigue entero.
            If Not copiaHecha Then
                ' El `causaCopia` va EMBEBIDO en el texto y no como InnerException: los dialogos de la app
                ' muestran solo `.Message` (FomodExport_Form.vb:355 lo documenta para el caso gemelo), asi
                ' que un inner es una causa que nadie ve. Cuando la copia fallo por tamaño —no hubo
                ' excepcion, solo no coincidio— no hay causa que agregar y el mensaje queda como estaba.
                Throw New IOException(
                    $"'{IO.Path.GetFileName(destino)}' was NOT modified: its backup could not be created or flushed to disk" &
                    $" ('{IO.Path.GetFileName(copia)}'). Free up disk space or close whatever is holding " &
                    "that file, then save again." &
                    If(causaCopia = "", "", Environment.NewLine & causaCopia))
            End If
        End If

        Dim existia = File.Exists(destino)
        Dim seToco As Boolean = False
        Try
            ' sincronizar:=True y sin preguntar: ESTE es, por contrato, el camino del dato del usuario
            ' (no se usa en horneado ni en build). El costo medido esta en el docstring de `Escribir`:
            ' +2,66 a +4,53 ms POR ARCHIVO, y aca es un archivo por accion del usuario.
            EscribirNucleo(destino, cuerpo, seToco, sincronizar:=True)
        Catch ex As Exception
            If Not seToco Then
                ' No se pudo ni abrir: el destino esta INTACTO. No hay nada que restaurar y no hay que
                ' decirle al usuario que quedo a medias.
                If copiaHecha Then Borrar(copia)
                Throw
            End If
            ' ⛔ MISMA EXCEPCION QUE EN `Escribir`, MISMA LEY: un destino NUEVO no se borra cuando lo unico
            ' que fallo fue la guarda del contrato — el cuerpo volvio normalmente y esos bytes son los
            ' suyos. (En la practica este brazo solo se alcanza con un destino que NO existia, porque si
            ' existia con contenido hay `copia` y manda la restauracion de abajo, que NO se toca: alli
            ' devolverle al usuario su version anterior es lo correcto pase lo que pase.)
            If seToco AndAlso Not existia AndAlso Not (TypeOf ex Is ContratoDelCuerpoException) Then Borrar(destino)
            If copiaHecha Then
                ' Solo se restaura desde la copia que tomo ESTA corrida: es la unica de la que sabemos
                ' que contenido tiene. La heredada se deja donde esta.
                '
                ' ⛔⛔ LA RESTAURACION USA LA MISMA PRIMITIVA QUE LA ESCRITURA, Y ESTO ERA UN DEFECTO REAL.
                ' Acá habia `File.Copy(copia, destino, overwrite:=True)`. `File.Copy` pide CREATE_ALWAYS,
                ' y CREATE_ALWAYS sobre un destino OCULTO da ERROR_ACCESS_DENIED — la MISMA trampa que la
                ' cabecera de esta clase documenta para la escritura y que `EscribirNucleo` resuelve con
                ' OpenOrCreate. O sea que el camino de ida estaba blindado y el de vuelta no:
                ' <b>exactamente sobre los archivos ocultos para los que este diseño existe, la
                ' restauracion automatica que promete el docstring NO funcionaba.</b> El destino quedaba
                ' con lo que el cuerpo alcanzo a escribir (PARCIAL) y el usuario recibia el mensaje de
                ' "no se pudo restaurar" — su version anterior sobrevivia en la copia, pero la promesa
                ' era falsa. Afecta a todo GuardarConCopia: ESP, NIF, materiales, OSP/OSD, BodyGen y
                ' sidecars. Lo dejan OneDrive y los desempaquetadores, asi que no es raro.
                '
                ' MEDIDO en net8.0.30 (gate: caso M de EscrituraEnElLugarGate, que lo remide en cada
                ' corrida en vez de dejarlo escrito en un comentario que puede envejecer):
                '   File.Copy(-> destino OCULTO, overwrite:=True) .... UnauthorizedAccessException
                '   OpenOrCreate + SetLength(0) sobre OCULTO ......... OK, y CONSERVA el atributo
                '
                ' Se abre la COPIA primero y recien despues se toca el destino: es la misma forma que usa
                ' VolcarEncima, y evita que un fallo abriendo la copia deje el destino ya truncado.
                ' sincronizar:=True por el mismo motivo que el resto de este metodo: es el dato del
                ' usuario volviendo a su lugar.
                Try
                    VolcarEncima(copia, destino, permitirOrigenVacio:=True)
                Catch
                    Throw New IOException(MensajeCopiaViva(destino, copia), ex)
                End Try
                Borrar(copia)           ' fuera del Try de arriba: que falle el borrado NO es "no pude restaurar"
            End If
            Throw
        End Try

        ' ⛔ SALIO BIEN. Aca vivia el defecto: `Borrar(heredada)` a secas.
        '
        ' Se borra la copia que tomo ESTA corrida — es un duplicado de lo que el usuario acaba de
        ' reemplazar a proposito, y eso es la mitad de la ley vieja que SI era cierta.
        '
        ' Una heredada se borra SOLO si esta corrida la probo redundante. La prueba es una implicacion
        ' cerrada, no un juicio: `copia` son los bytes que habia en el destino al empezar y, por la ley
        ' de arriba, es descartable; si una heredada es BYTE POR BYTE igual a `copia`, entonces no
        ' guarda nada que `copia` no guarde, y es descartable por el mismo motivo. Si difiere, es un
        ' ejemplar unico del dato del usuario y se queda.
        '
        ' Se compara ANTES de borrar `copia`, que es contra quien se compara.
        Dim redundantes As New List(Of String)
        If copiaHecha Then
            For Each h In heredadas
                If MismoContenido(h, copia) Then redundantes.Add(h)
            Next
        End If
        If copiaHecha Then Borrar(copia)
        For Each h In redundantes
            Borrar(h)
        Next
    End Sub

    ''' <summary>Vuelca un archivo ya escrito encima del destino, en el lugar. Lo usan los que no pueden
    ''' escribir directo: el packer (lee el archive viejo mientras produce el nuevo) y el exportador de
    ''' FOMOD (tiene que poder cancelar sin romper el zip anterior).
    ''' <para>⛔ Y ES TAMBIEN LA PRIMITIVA DE RESTAURACION — el tercer uso, y no es un agregado: restaurar
    ''' es exactamente esto (tomar un archivo que ya existe y ponerlo encima de otro, en el lugar). Lo
    ''' consumen <see cref="GuardarConCopia"/> desde su copia y
    ''' <c>LoadOrderActivator.RestaurarDesdeRespaldo</c> desde su `.npcm.bak`. NO se escribio una funcion
    ''' `RestaurarDesde` al lado: seria un segundo nombre para el mismo primitivo, y la unica forma de que
    ''' una restauracion vuelva a quedarse atras de la escritura es que sean dos implementaciones.</para>
    ''' <para>Lo que la restauracion gana por venir de acá, y que un <c>File.Copy</c> no daba:
    ''' <list type="bullet">
    ''' <item>escribe con <c>OpenOrCreate</c>, o sea que <b>funciona sobre un destino OCULTO</b> — la
    ''' medicion esta en el caso <c>M</c> de <c>Tools\EscrituraEnElLugarGate</c>, y era el defecto: el
    ''' camino de ida estaba blindado y el de vuelta no;</item>
    ''' <item>CONSERVA los atributos del destino, asi que restaurar no le cambia el archivo al usuario;</item>
    ''' <item>se niega a volcar un origen VACIO, o sea que un respaldo de 0 bytes nunca puede pisar el
    ''' archivo que dice proteger;</item>
    ''' <item>sincroniza por default, que es lo que corresponde cuando el llamador va a dar por buena la
    ''' restauracion y borrar el respaldo.</item></list></para>
    ''' <para>Se abre el ORIGEN primero: si no esta, o esta tomado, el destino no se toca. El reintento
    ''' cubre SOLO el fallo de apertura —alguien tiene el archivo abierto y lo suelta en un rato—; si ya
    ''' se empezo a escribir no se reintenta, porque repetir tapa el error real.</para>
    ''' <para>⛔ CONTRATO de <paramref name="alTocarElDestino"/>: <b>si este callback NUNCA corrio y
    ''' VolcarEncima tiro, el destino esta byte-identico a como estaba.</b> Se dispara ADENTRO del cuerpo,
    ''' despues de abrir Y truncar y antes de copiar el primer byte del origen: es el instante exacto en
    ''' que el destino ya esta probadamente destruido. Existe porque los llamadores no podian distinguir
    ''' "no pude ni empezar" de "lo rompi", y sin esa distincion o avisan de una perdida que no paso, o
    ''' no intentan una recuperacion que si hacia falta.</para>
    ''' <para>El unico hueco, y se dice: si el <c>SetLength(0)</c> de <see cref="EscribirNucleo"/> tirara,
    ''' el callback no corre y el destino queda en estado desconocido. El <c>seToco</c> interno si cubre
    ''' ese instante; la senal publica no. No hay medicion de que ese fallo ocurra.</para>
    ''' <para>⛔⛔ <paramref name="sincronizar"/> DEFAULTEA EN <b>True</b>, al reves que en
    ''' <see cref="Escribir"/>, y el motivo es el patron de los llamadores: <b>los tres que borran la otra
    ''' copia inmediatamente despues</b> — <c>ArchivePackager</c> (borra el `.new` y su sello),
    ''' <c>FomodExporter</c> (borra el `.tmp`) y el "move project" de Wardrobe Manager (borra el `.osp`
    ''' ORIGEN). En los tres, apenas vuelve esta llamada el destino pasa a ser <b>el unico ejemplar</b>. Sin
    ''' el flush, el borrado de la otra copia puede llegar al plato ANTES que los bytes que la reemplazan y
    ''' un corte de luz se lleva las dos — que es exactamente el estado que toda esta clase existe para
    ''' evitar, y el mismo que documenta el «la red solo es red si llego al plato» de
    ''' <see cref="GuardarConCopia"/>.</para>
    ''' <para>Va por DEFAULT y no por pedido porque lo peligroso tiene que ser lo que exige justificacion
    ''' explicita, no al reves: un llamador nuevo que vuelque y borre hereda la garantia sin saber que
    ''' existe. El opt-out es para el camino MEDIDO que no puede pagarlo — hoy uno solo:
    ''' <c>OSP_Clases.CommitTextureJobs</c>, cientos de archivos por corrida, que ademas NO borra el origen
    ''' (el origen es el suelto del juego) y por lo tanto no necesita la garantia. Costo del flush, medido
    ''' en <see cref="Escribir"/>: +2,66 a +4,53 ms POR LLAMADA — despreciable en un volcado de archive de
    ''' GiB, caro en un lote de cientos de texturas.</para></summary>
    ''' <param name="permitirOrigenVacio">⛔ SOLO PARA EL CAMINO DE RESTAURACION. Por default un origen de
    ''' 0 bytes se RECHAZA, y esa ley se queda: para el packer, el exportador de FOMOD y el "move project",
    ''' un origen vacio significa "lo que iba a instalar esta roto" y volcarlo destruiria el destino bueno.
    ''' <para>Pero restaurar es al reves: si el archivo del usuario ERA de 0 bytes, su copia es de 0 bytes
    ''' —y eso esta PROBADO, no supuesto: la copia se verifica comparando longitudes— y devolverlo a 0
    ''' bytes es exactamente restaurarlo. Con el rechazo puesto, la restauracion TIRABA y el archivo se
    ''' quedaba con los bytes parciales del intento fallido; lo cazaron L8 y G22 de
    ''' <c>Tools\EscrituraEnElLugarGate</c>.</para>
    ''' <para>Va como parametro y no como branch en cada llamador para que la restauracion siga siendo UNA
    ''' sola llamada: dos ramas "si esta vacio truncar, si no volcar" en dos sitios distintos es como una se
    ''' queda atras de la otra.</para></param>
    Public Shared Sub VolcarEncima(origen As String, destino As String,
                                   Optional reintentos As Integer = 10,
                                   Optional esperaMs As Integer = 200,
                                   Optional alTocarElDestino As Action = Nothing,
                                   Optional sincronizar As Boolean = True,
                                   Optional permitirOrigenVacio As Boolean = False)
        If String.IsNullOrEmpty(origen) Then Throw New ArgumentException("Empty path.", NameOf(origen))
        If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
        If reintentos < 1 Then Throw New ArgumentOutOfRangeException(NameOf(reintentos))

        For intento = 1 To reintentos
            Dim seToco As Boolean = False
            Try
                Using src As New FileStream(origen, FileMode.Open, FileAccess.Read,
                                            FileShare.Read Or FileShare.Delete)
                    If src.Length = 0 AndAlso Not permitirOrigenVacio Then
                        Throw New InvalidDataException(
                            $"'{IO.Path.GetFileName(origen)}' is empty: refusing to overwrite " &
                            $"'{IO.Path.GetFileName(destino)}' with nothing.")
                    End If
                    Dim existiaDestino = File.Exists(destino)
                    Try
                        ' El aviso va ADENTRO del cuerpo: EscribirNucleo ya abrio y ya trunco, y todavia
                        ' no se copio un byte. Ese es el instante en que el destino esta destruido y el
                        ' llamador lo tiene que saber. Afuera no sirve: antes de la llamada todavia no
                        ' paso nada, y despues ya no se distingue de "no pude ni empezar".
                        EscribirNucleo(destino,
                                       Sub(fs)
                                           alTocarElDestino?.Invoke()
                                           src.CopyTo(fs)
                                       End Sub,
                                       seToco,
                                       sincronizar)
                    Catch
                        ' Misma ley que Escribir: donde no habia nada, no queda un archivo de 0 bytes.
                        If seToco AndAlso Not existiaDestino Then Borrar(destino)
                        Throw
                    End Try
                End Using
                Return
            Catch ex As Exception When (TypeOf ex Is IOException OrElse
                                        TypeOf ex Is UnauthorizedAccessException) AndAlso
                                       Not seToco AndAlso intento < reintentos
                Threading.Thread.Sleep(esperaMs)
            End Try
        Next
    End Sub

    ''' <summary>Escribe por el LOTE si hay lote, y como <see cref="GuardarConCopia"/> si no.
    ''' <para>⛔ EL `If lote Is Nothing` VIVE ACA Y EN NINGUN OTRO LADO. Los cuatro escritores de la cadena
    ''' del proyecto (`OSD_Class.Save_As`, `NifContent_Class.Save_As_Manolo_ConCopia`, `SaveHighHeel`,
    ''' `EscribirTextoUtf8`) mas el `.osp` reciben el lote como parametro OPCIONAL; si cada uno se escribiera
    ''' su propio despacho serian cinco copias de la misma decision, y la primera que se olvide de
    ''' actualizarse deja su etapa fuera de la transaccion SIN que nada se ponga rojo.</para>
    ''' <para>El parametro es opcional a proposito: los llamadores que NO son la cadena del proyecto
    ''' (el editor de materiales, los sidecars sueltos, el CLI) no cambian de conducta — pasan Nothing por
    ''' omision y siguen yendo por <see cref="GuardarConCopia"/> exactamente como antes.</para></summary>
    Public Shared Sub GuardarConCopia(destino As String, cuerpo As Action(Of Stream),
                                      lote As LoteConCopias)
        If lote Is Nothing Then
            GuardarConCopia(destino, cuerpo)
        Else
            lote.Guardar(destino, cuerpo)
        End If
    End Sub

    ''' <summary>Borra por el LOTE si hay lote, y con un <c>File.Delete</c> best-effort si no.
    ''' <para>⛔ EL `If lote Is Nothing` VIVE ACA, igual que en el <see cref="GuardarConCopia"/> de tres
    ''' argumentos: los sitios que borran un sidecar dentro de una cadena de guardado NO deciden cada uno si
    ''' hay lote, porque la primera copia de esa decision que se olvide de actualizarse deja su borrado
    ''' fuera de la transaccion SIN que nada se ponga rojo.</para>
    ''' <para>Sin lote el borrado es best-effort y NO tira: es lo que hacian los `File.Delete` crudos que
    ''' esto reemplaza, y cambiarlo seria meterle un modo de fallo nuevo a un llamador que no lo pidio. Lo
    ''' que cambia con lote es que el borrado pasa a tener VUELTA.</para></summary>
    Public Shared Sub BorrarDelLote(destino As String, lote As LoteConCopias)
        If String.IsNullOrEmpty(destino) Then Return
        If lote Is Nothing Then
            Borrar(destino)
        Else
            lote.Eliminar(destino)
        End If
    End Sub

    ''' <summary>Los bits de atributo que esta aplicacion puede QUITAR y volver a APLICAR con
    ''' <c>SetAttributes</c>.
    ''' <para>⛔ SE EXCLUYEN LOS BITS QUE MANEJA EL FILESYSTEM: Directory, ReparsePoint, Compressed,
    ''' Encrypted, SparseFile, Device, IntegrityStream, NoScrubData y los RecallOn*. Comparar —o reaplicar—
    ''' el valor CRUDO que devuelve <c>GetAttributes</c> da falsos errores: en un volumen comprimido de
    ''' NTFS el bit <c>Compressed</c> aparece solo y <c>SetAttributes</c> no lo controla, asi que un verify
    ''' contra el valor completo fallaria SIEMPRE — y el rollback declararia ROTO un archivo que volvio
    ''' perfecto. Un error de rollback falso es peor que no verificar: manda al usuario a buscar un daño
    ''' que no existe.</para>
    ''' <para>⛔ ES LA UNICA DEFINICION DE ESA MASCARA. La usan el registro del diario, el rollback en
    ''' proceso y la recuperacion del arranque. Dos mascaras distintas es como una empieza a guardar un bit
    ''' que la otra no puede reponer.</para></summary>
    Public Const MascaraDeAtributosRestaurables As FileAttributes =
        FileAttributes.ReadOnly Or FileAttributes.Hidden Or FileAttributes.System Or
        FileAttributes.Archive Or FileAttributes.NotContentIndexed Or FileAttributes.Temporary

    ''' <summary>La parte restaurable de un valor de atributos. Devuelve <c>Normal</c> cuando no queda
    ''' ninguno: ese es el valor que <c>SetAttributes</c> acepta para "sin atributos" — no se le puede
    ''' pasar 0.</summary>
    Public Shared Function AtributosRestaurables(valor As FileAttributes) As FileAttributes
        Dim aplicables = valor And MascaraDeAtributosRestaurables
        If aplicables = 0 Then Return FileAttributes.Normal
        Return aplicables
    End Function

    ''' <summary>Donde viven los diarios de lote. ⛔ SIN SETEAR ⇒ LOS LOTES NO ESCRIBEN DIARIO, que es
    ''' EXACTAMENTE la conducta anterior. Cada app la setea al arrancar (p. ej.
    ''' <c>LocalApplicationData\&lt;app&gt;\diarios</c>); los arneses que no la setean no cambian de
    ''' comportamiento ni ensucian el disco del usuario con diarios de prueba.
    ''' <para>No va bajo <c>Data</c>: es metadata de la aplicacion, y ponerla al lado de los archivos del
    ''' mod la meteria bajo el VFS de MO2.</para></summary>
    Public Shared Property CarpetaDeDiarios As String = ""

    ''' <summary>Los diarios que quedaron de una corrida que NO termino su lote. Es la API del arranque:
    ''' la app los enumera, MUESTRA los destinos y sus copias, y le ofrece al usuario
    ''' <i>Restaurar estado anterior</i> o <i>Conservar estado actual</i>.
    ''' <para>⛔ NO RESTAURA NADA. Despues de un reinicio no hay forma de saber si el usuario quiere volver
    ''' atras o quedarse con lo nuevo; decidir por el seria pisarle el trabajo.</para>
    ''' <para>⛔ LOS ILEGIBLES TAMBIEN SALEN. Antes se filtraban y un diario corrupto DESAPARECIA del
    ''' arranque: el usuario perdia la unica pista de que hubo un guardado a medias, y las copias al lado de
    ''' sus archivos quedaban sin explicacion. Ahora vuelven con su error adentro
    ''' (<see cref="DiarioDeEscritura.EsLegible"/>) y el dialogo los muestra como tales — describir un
    ''' problema que no se puede resolver solo es mejor que ocultarlo.</para></summary>
    Public Shared Function DiariosPendientes() As List(Of DiarioDeEscritura)
        Dim out As New List(Of DiarioDeEscritura)
        If String.IsNullOrEmpty(CarpetaDeDiarios) OrElse Not Directory.Exists(CarpetaDeDiarios) Then Return out
        For Each f In Directory.EnumerateFiles(CarpetaDeDiarios, "*" & DiarioDeEscritura.SufijoDiario)
            out.Add(DiarioDeEscritura.Leer(f))
        Next
        Return out
    End Function

    ''' <summary>Abre un LOTE: varios archivos que forman UNA sola unidad para el usuario (el proyecto de
    ''' Wardrobe Manager son cinco: `.osd`, `.nif`, `.hht`, el `.xml` de SMP y el `.osp`).
    ''' <para>⛔ EL DEFECTO QUE CIERRA. <see cref="GuardarConCopia"/> es transaccional POR ARCHIVO y borra
    ''' su copia al salir bien. Encadenando cinco, un fallo en la etapa 3 deja las etapas 1 y 2 escritas y
    ''' SIN copia: el proyecto queda mezclando archivos nuevos y viejos —un `.osp` apuntando a un `.osd`
    ''' nuevo con un `.nif` viejo— y no hay nada para volver. No es hipotetico: el docstring de
    ''' <c>OSP_Clases.Save_Shapedatas</c> ya documenta una instancia anterior de este mismo daño
    ''' (<i>"se escribia el .osd, se re-apuntaba el .osp, se escribia el .nif y recien ahi SaveHighHeel
    ''' reventaba — proyecto a medio escribir"</i>). Lo que se arreglo entonces fue preguntar el overwrite
    ''' UNA sola vez; su promesa «o se escribe TODO o no se toca NADA» es cierta para esa pregunta y FALSA
    ''' para un fallo de I/O.</para>
    ''' <para>Lo unico que cambia respecto de encadenar <see cref="GuardarConCopia"/> es CUANDO se borran
    ''' las copias: no al exito individual, sino al de <see cref="LoteConCopias.Confirmar"/>. No hay copias
    ''' de mas, no hay temporales y no hay rename — la ley MO2/Vortex no se toca.</para>
    ''' <para>⚠️ LO QUE NO DA, dicho en vez de escondido: NO es atomico. Cubre la EXCEPCION, no el corte de
    ''' luz. Si el proceso muere entre dos etapas, el lote queda a medias y las copias quedan en disco —que
    ''' ya es mejor que hoy, donde no quedarian— pero nadie las restaura solo. Lo que cierra ese hueco es
    ''' <see cref="CopiasPendientes"/>, que las NOMBRA cuando el usuario vuelve a abrir el proyecto.
    ''' ⛔ Un protocolo tipo `.new`+sello (el del packer) daria recuperacion automatica y queda declarado
    ''' como upgrade POSIBLE y NO implementado: no lo paga un evento raro cuando las copias ya sobreviven y
    ''' quedan nombradas, y los bytes los decide el usuario.</para></summary>
    Public Shared Function NuevoLote() As LoteConCopias
        Return New LoteConCopias()
    End Function

    ''' <summary>Las copias de lote que quedaron en disco para <paramref name="rutas"/> — o sea: los
    ''' archivos que un guardado anterior alcanzo a respaldar y nunca confirmo.
    ''' <para>⛔ ES UN DETECTOR, NO UNA RECUPERACION. Devuelve nombres para que el llamador los MUESTRE
    ''' ("un guardado anterior quedo a medias; estas son las versiones previas"). NO restaura, NO borra y
    ''' NO decide: los bytes del usuario los decide el usuario. Es la contraparte barata del hueco que
    ''' <see cref="NuevoLote"/> declara — un corte de luz entre etapas deja el lote a medias, y sin esto el
    ''' usuario tiene copias al lado de sus archivos sin saber que son ni de cuando.</para></summary>
    Public Shared Function CopiasPendientes(rutas As IEnumerable(Of String)) As List(Of String)
        Dim out As New List(Of String)
        If rutas Is Nothing Then Return out
        For Each r In rutas
            If String.IsNullOrEmpty(r) Then Continue For
            out.AddRange(SlotsOcupados(r, SufijoCopia))
        Next
        Return out
    End Function

    ''' <summary>El scope del lote. Ver <see cref="NuevoLote"/> para el por que.</summary>
    Public NotInheritable Class LoteConCopias
        Implements IDisposable

        ''' <summary>Una etapa del lote. ⛔ SON TRES OPERACIONES, NO UNA — y esa fue la falla de clase de la
        ''' primera version: la ley modelaba solo REEMPLAZAR un archivo con contenido, asi que CREAR y
        ''' BORRAR quedaban fuera del rollback. Deshacer una creacion es BORRAR el archivo, y deshacer un
        ''' borrado es RECREARLO — ninguna de las dos es "volcar una copia encima".
        ''' <list type="bullet">
        ''' <item><b>reemplazo</b>: <c>Copia</c> lleno, <c>EsBorrado</c>=False ⇒ deshacer VUELCA la copia;</item>
        ''' <item><b>creacion</b>: <c>Copia</c> vacio ⇒ el destino NO existia ⇒ deshacer lo BORRA;</item>
        ''' <item><b>borrado</b>: <c>EsBorrado</c>=True ⇒ deshacer vuelca la copia Y reaplica
        ''' <c>Atributos</c>.</item></list>
        ''' <para>⛔ <c>Copia = ""</c> SIGNIFICA "creacion" Y NADA MAS, y eso vale porque el guard de
        ''' <c>Length &gt; 0</c> se saco: hoy se respalda TODO destino que exista, 0 bytes incluido. Con el
        ''' guard puesto, un destino vacio tambien daba <c>Copia = ""</c> y el deshacer lo habria BORRADO
        ''' creyendolo una creacion — el bug que el propio fix habria introducido.</para>
        ''' <para><c>Atributos</c> hace falta porque el respaldo pasa por <see cref="LimpiarAtributos"/> (si
        ''' no, la copia de un archivo OCULTO queda invisible y una de SOLO LECTURA no se puede borrar). Al
        ''' recrear hay que devolverle al archivo los suyos, no los de la copia.</para></summary>
        Private NotInheritable Class Etapa
            Public Property Destino As String
            Public Property Copia As String          ' "" ⟺ el destino NO existia ⟺ la etapa fue una CREACION
            Public Property Heredadas As List(Of String)
            Public Property EsBorrado As Boolean
            Public Property Atributos As FileAttributes

            ''' <summary>¿Este destino llegó a TOCARSE? ⛔ SEPARA "hay WAL" de "hay que deshacer", y
            ''' son dos cosas distintas: la etapa se anota en `_etapas` apenas queda escrita en el diario
            ''' —o sea ANTES de abrir el destino—, asi que existe un tramo en el que el diario ya la
            ''' describe y el archivo todavia esta intacto. Si la apertura falla ahi (destino TOMADO, de
            ''' SOLO LECTURA, carpeta sin permiso), volcarle la copia encima seria REESCRIBIR un archivo
            ''' que esta ejecucion nunca modifico.
            ''' <para>La etapa igual se conserva: participa de la LIMPIEZA (su copia se borra recien
            ''' despues de `MarcarResuelto`, como todas), pero no de la RESTAURACION.</para>
            ''' <para>⛔ El default es True y no es casual: deja intacta la semantica de todas las etapas
            ''' que se anotan cuando el destino YA se toco — las de <see cref="Eliminar"/>, que registran
            ''' despues del borrado.</para></summary>
            Public Property RequiereRollback As Boolean = True
        End Class

        Private ReadOnly _etapas As New List(Of Etapa)
        ' Nothing SOLO cuando `CarpetaDeDiarios` no esta seteada (opt-out explicito). Si esta seteada y el
        ' diario no se puede abrir, `AbrirDiario` TIRA y el lote no llega a existir.
        Private ReadOnly _diario As DiarioDeEscritura = AbrirDiario()

        ''' <summary>⛔ NO DEGRADA EN SILENCIO. Antes, un diario que no se podia crear devolvia Nothing
        ''' y el lote seguia adelante SIN diario: el usuario creia tener recuperacion ante un corte y no la
        ''' tenia — el peor de los dos mundos, porque una recuperacion que se cree presente cambia lo que el
        ''' usuario hace despues. Si la carpeta esta configurada, el diario es parte del contrato: si no se
        ''' puede abrir, no se toca NINGUN destino.
        ''' <para>Carpeta VACIA sigue devolviendo Nothing a proposito: eso es el opt-out EXPLICITO —los
        ''' arneses y los caminos que no configuran carpeta no escriben diario y no cambian de
        ''' conducta—.</para></summary>
        Private Shared Function AbrirDiario() As DiarioDeEscritura
            If String.IsNullOrEmpty(CarpetaDeDiarios) Then Return Nothing
            Try
                Return New DiarioDeEscritura(CarpetaDeDiarios)
            Catch ex As Exception
                Throw New IOException(
                    "No se pudo crear el diario de recuperacion. No se modifico ningun archivo.", ex)
            End Try
        End Function
        Private _confirmado As Boolean = False
        Private _dispuesto As Boolean = False

        ''' <summary>Qué pasó con la restauración del lote. Cadena vacia = todo volvio (o no hizo falta).
        ''' Se llena cuando el lote se deshace; el llamador la puede mostrar.</summary>
        Public ReadOnly Property InformeDeRestauracion As String = ""

        ''' <summary>Escribe un archivo del lote. Identico a <see cref="GuardarConCopia"/> —copia
        ''' verificada al primer slot LIBRE, sincronizada, y sin red no se trunca— MENOS el borrado de la
        ''' copia, que se difiere a <see cref="Confirmar"/>.
        ''' <para>⛔ SI ESTA ESCRITURA FALLA, SE DESHACE EL LOTE ENTERO ACA MISMO y se tira UNA excepcion
        ''' que lleva la causa original Y el estado archivo por archivo. Deshacer desde el `Dispose` no
        ''' alcanzaria: una excepcion tirada durante el desenrollado TAPA la original, y el usuario perderia
        ''' justamente el motivo.</para></summary>
        Public Sub Guardar(destino As String, cuerpo As Action(Of Stream))
            If _confirmado Then Throw New InvalidOperationException("El lote ya fue confirmado.")
            If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
            If cuerpo Is Nothing Then Throw New ArgumentNullException(NameOf(cuerpo))

            Dim heredadas = SlotsOcupados(destino, SufijoCopia)
            ' ⛔ `PrimerSlotLibre` y NO `Slot(..., Count + 1)`: con la enumeracion que ve huecos,
            ' contar devuelve un nombre YA OCUPADO (ver el ⛔ de PrimerSlotLibre). Los dos cambios
            ' son atomicos.
            Dim copia = PrimerSlotLibre(destino, SufijoCopia)
            ' ⛔⛔ SE RESPALDA TODO DESTINO QUE EXISTA, 0 BYTES INCLUIDO. Acá habia
            ' `AndAlso New FileInfo(destino).Length > 0`, y ese guard rompia el invariante del que depende
            ' TODO el rollback: con el, un destino EXISTENTE PERO VACIO tambien salia con `Copia = ""`, o
            ' sea indistinguible de una CREACION — y deshacer lo habria BORRADO. Sacandolo,
            ' `Copia = ""` ⟺ el destino no existia, que es lo que Etapa documenta.
            ' Y ademas cierra un agujero por si mismo: con el guard, un fallo a mitad de escritura sobre un
            ' destino de 0 bytes dejaba BYTES PARCIALES y no habia copia para volver, contra un docstring
            ' que promete "restaura el destino solo". Copiar 0 bytes es trivial y `MismoContenido` maneja
            ' vacios (dos archivos de largo 0 dan iguales en la primera lectura).
            Dim existeDestino As Boolean = File.Exists(destino)
            Dim copiaHecha As Boolean = False
            Dim causaCopia As String = ""

            If existeDestino Then
                Try
                    File.Copy(destino, copia, overwrite:=True)
                    LimpiarAtributos(copia)
                    copiaHecha = (New FileInfo(copia).Length = New FileInfo(destino).Length)
                    ' ⛔ Flush OBLIGATORIO del respaldo; el fallo va por DeshacerYTirar (hay etapas
                    ' previas del lote que deshacer), no por un Throw crudo.
                    If Not copiaHecha Then
                        Borrar(copia)
                    Else
                    Try
                        SincronizarRespaldo(copia)
                    Catch exFlush As Exception
                        Borrar(copia)
                        copiaHecha = False
                        causaCopia = "el respaldo no se pudo forzar a disco: " & exFlush.Message
                    End Try
                    End If
                Catch ex As Exception
                    copiaHecha = False
                    causaCopia = ex.Message
                    Borrar(copia)
                End Try
                ' SIN RED NO SE TRUNCA — y acá ademas no se toco nada todavia de ESTA etapa, asi que el
                ' lote se deshace limpio.
                If Not copiaHecha Then
                    DeshacerYTirar($"'{IO.Path.GetFileName(destino)}' was NOT modified: its backup could not " &
                                   $"be created ('{IO.Path.GetFileName(copia)}')." &
                                   If(causaCopia = "", "", Environment.NewLine & causaCopia), Nothing)
                End If
            End If

            ' ⛔⛔ LA ETAPA SE INCORPORA APENAS SU COPIA EXISTE — ANTES DEL DIARIO Y ANTES DE TOCAR EL
            ' DESTINO. Es la regla, y de ella cuelgan dos defectos cerrados:
            '
            ' (1) Antes la etapa entraba a `_etapas` recien al terminar BIEN, asi que los `Catch` de aca
            '     abajo tenian que recuperarla por un camino PARALELO y mas debil que el comun:
            '     `Borrar(destino)` para una creacion parcial (tragado, sin verificar que el archivo se
            '     fuera), `VolcarEncima` sin comparar los bytes despues, y `Borrar(copia)` — que ademas
            '     VIOLABA el protocolo de dos estados, porque borraba el respaldo con el diario todavia en
            '     `en_curso`. Y despues llamaba a `DeshacerYTirar`, cuyo rollback sólo conocia las etapas
            '     ANTERIORES. Escenario que producia: creacion parcial + `Borrar` que falla en silencio ⇒
            '     `Deshacer` no ve nada que hacer ⇒ informe vacio ⇒ diario `resuelto` y CERRADO con el
            '     archivo a medias vivo en disco. Medido en R1/R2.
            '
            ' (2) Y va ANTES del `Registrar` y no despues: `Registrar` PUEDE TIRAR (si el diario no se
            '     puede persistir, ver `AbrirDiario`). Con el `Add` despues, esa excepcion se llevaba
            '     puesta una copia que YA existia en disco y que no estaba en ninguna lista — ni el
            '     rollback la conocia ni la limpieza la levantaba: respaldo HUERFANO al lado del archivo
            '     del usuario, sin nada que lo explique. Con el `Add` antes, la excepcion propaga, el
            '     `Dispose` del `Using` deshace (esta etapa se saltea: el destino no se toco) y la copia
            '     se va con la limpieza al resolver. Medido en R4.
            '
            ' ⚠️ EL `False` DE ACA ES UN DEFAULT DEFENSIVO Y NO ESTA MEDIDO, dicho en vez de escondido: hoy
            ' todos los caminos que siguen lo pisan (éxito ⇒ True; los dos `Catch` ⇒ `= seToco`), asi que
            ' voltearlo a True no pone rojo ningun caso. Se deja igual porque es el valor SEGURO — y ahora
            ' ademas cubre de verdad el tramo `Registrar`, que esta entre este `Add` y el `Try`. Lo que SI
            ' esta medido es el `= seToco` de los `Catch` (R3, y sus mutantes MR3b/MR4).
            Dim etapaActual As New Etapa With {
                .Destino = destino,
                .Copia = If(copiaHecha, copia, ""),
                .Heredadas = heredadas,
                .RequiereRollback = False
            }
            _etapas.Add(etapaActual)

            ' ⛔ EL DIARIO SE ESCRIBE ACA: la copia ya esta asegurada y sincronizada, y el destino
            ' todavia NO se toco. Al reves el diario describiria algo que no paso, o el destino
            ' cambiaria sin que nadie lo hubiera anotado. Para una CREACION se registra antes de crear.
            ' ⛔⛔ LA OPERACION SE DERIVA DE LA COPIA, NO DE UNA SEGUNDA CONSULTA AL FILESYSTEM. Acá habia
            ' un `Dim existia = File.Exists(destino)`, y eso era una SEGUNDA lectura despues de la que
            ' decidio si se tomaba copia (`existeDestino`, arriba). Entre las dos hay una ventana: si el
            ' archivo aparece o desaparece en el medio —otro proceso, MO2 refrescando su VFS— el diario
            ' sale con una forma que `DiarioDeEscritura.Leer` RECHAZA ("crear" con copia, o "reemplazar"
            ' sin copia), y el arranque siguiente lo muestra ILEGIBLE: copias buenas en disco que la
            ' recuperacion ya no puede usar. `copiaHecha` es la MISMA variable que decidio si hay copia,
            ' asi que «copia presente ⟺ reemplazar» vale POR CONSTRUCCION y la ventana no existe.
            ' ⚠️ La carrera en si NO es inyectable desde un arnes y queda declarada; lo que si esta medido
            ' es la red de contencion —que `Leer` rechaza las dos formas invalidas— en D2.
            If _diario IsNot Nothing Then
                _diario.Registrar(destino, If(copiaHecha, copia, ""),
                                  If(copiaHecha, "reemplazar", "crear"), FileAttributes.Normal)
            End If

            Dim seToco As Boolean = False
            Try
                EscribirNucleo(destino, cuerpo, seToco, sincronizar:=True)
                etapaActual.RequiereRollback = True
            Catch ex As ContratoDelCuerpoException
                ' ⛔⛔ ACA EL LOTE DIVERGE DE LA LEY DE UN SOLO ARCHIVO, A PROPOSITO. La ley no cambio con
                ' este fix: cambio la VIA. Antes esta rama agregaba la etapa a mano; ahora la etapa ya
                ' esta y sólo se marca — pero el resultado es el mismo, porque `EscribirNucleo` sólo
                ' produce esta excepcion DESPUES de abrir y truncar, o sea siempre con `seToco = True`.
                '
                ' La divergencia es la correcta y por eso se declara en vez de disimularla:
                '   · en `Escribir`/`GuardarConCopia` (ver el ⛔ de ContratoDelCuerpoException) el archivo
                '     se CONSERVA, porque es la unidad entera: sus bytes son los que el cuerpo produjo y
                '     no hay nada mas con que quedar consistente;
                '   · en un LOTE la unidad son N archivos y manda todo-o-nada ANTE EXCEPCIONES MANEJADAS
                '     DENTRO DEL PROCESO (ver el contrato en la cabecera). Conservar el del violador del
                '     contrato mientras sus hermanos se deshacen deja EXACTAMENTE el estado mezclado que
                '     el lote existe para impedir — y encima uno cuya durabilidad no podemos sostener.
                ' Lo que NO cambia es el aviso: se sigue tirando con el mensaje del contrato, asi que el
                ' llamador se entera de que lo que rompio fue el `leaveOpen`, no un fallo de disco.
                etapaActual.RequiereRollback = seToco
                DeshacerYTirar(ex.Message, ex)
            Catch ex As Exception
                ' `seToco` sale en True desde el instante en que el destino pudo ABRIRSE. False ⇒ no se
                ' pudo ni empezar (tomado, de solo lectura, sin permiso): el archivo del usuario esta
                ' intacto y el rollback NO lo tiene que tocar. Su copia igual se limpia al resolver.
                etapaActual.RequiereRollback = seToco
                DeshacerYTirar(ex.Message, ex)
            End Try
        End Sub

        ''' <summary>BORRA un archivo COMO ETAPA DEL LOTE. Es la tercera operacion destructiva, y hasta acá
        ''' la ley no la tenia: los sitios que borraban un sidecar en medio de un guardado lo hacian con un
        ''' <c>File.Delete</c> crudo, o sea SIN VUELTA — si una etapa posterior fallaba, el lote devolvia los
        ''' archivos que habia reemplazado y el borrado quedaba hecho igual.
        ''' <para>Inexistente ⇒ no-op (no hay nada que borrar ni que restaurar). Existente ⇒ copia VERIFICADA
        ''' al primer slot LIBRE —la misma ley de slots que <see cref="GuardarConCopia"/>, asi que un
        ''' respaldo que ya estaba no se pisa nunca—, se anotan sus atributos, y RECIEN AHI se borra.</para>
        ''' <para>⛔ SIN RED NO SE BORRA, exactamente como "sin red no se trunca": si no se pudo dejar la
        ''' copia, el archivo NO se toca y el lote se deshace. Un borrado sin respaldo es la unica operacion
        ''' de las tres que no se puede deshacer NUNCA.</para>
        ''' <para><see cref="Confirmar"/> se lleva la copia; deshacer recrea el archivo con sus bytes Y sus
        ''' atributos (ver <c>Deshacer</c>).</para></summary>
        Public Sub Eliminar(destino As String)
            If _confirmado Then Throw New InvalidOperationException("El lote ya fue confirmado.")
            If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
            If Not File.Exists(destino) Then Return          ' no-op: no hay nada que borrar

            Dim heredadas = SlotsOcupados(destino, SufijoCopia)
            ' ⛔ `PrimerSlotLibre` y NO `Slot(..., Count + 1)`: con la enumeracion que ve huecos,
            ' contar devuelve un nombre YA OCUPADO (ver el ⛔ de PrimerSlotLibre). Los dos cambios
            ' son atomicos.
            Dim copia = PrimerSlotLibre(destino, SufijoCopia)
            ' ⛔ SE REGISTRA SI LA LECTURA SALIO BIEN, no solo el valor. Si `GetAttributes` fallara, la
            ' variable se queda en `Normal` — que NO son "los atributos originales" sino un default. Volver
            ' a poner ese default seria ALTERAR el archivo por segunda vez, creyendo que se lo restaura.
            Dim atributos As FileAttributes = FileAttributes.Normal
            Dim atributosLeidos As Boolean = False
            Try
                atributos = File.GetAttributes(destino)
                atributosLeidos = True
            Catch
            End Try
            ' ⛔ SE GUARDA Y SE REAPLICA LA MASCARA, NO EL VALOR CRUDO. `GetAttributes` puede traer bits que
            ' administra el filesystem —`Compressed` en un volumen comprimido, `SparseFile`, `ReparsePoint`—
            ' que `SetAttributes` NO controla. Guardar el crudo hace que el rollback intente reponer un bit
            ' que no puede, y que la verificacion posterior falle SIEMPRE: el usuario recibiria un error de
            ' restauracion sobre un archivo que volvio perfecto. Ver `MascaraDeAtributosRestaurables`.
            Dim atribsParaReponer = AtributosRestaurables(atributos)

            Dim copiaHecha As Boolean = False
            Dim causaCopia As String = ""
            Try
                File.Copy(destino, copia, overwrite:=True)
                LimpiarAtributos(copia)
                copiaHecha = (New FileInfo(copia).Length = New FileInfo(destino).Length)
                ' ⛔ Flush OBLIGATORIO ANTES de tocar atributos o borrar el destino.
                If Not copiaHecha Then
                    Borrar(copia)
                Else
                Try
                    SincronizarRespaldo(copia)
                Catch exFlush As Exception
                    Borrar(copia)
                    copiaHecha = False
                    causaCopia = "el respaldo no se pudo forzar a disco: " & exFlush.Message
                End Try
                End If
            Catch ex As Exception
                copiaHecha = False
                causaCopia = ex.Message
                Borrar(copia)
            End Try
            If Not copiaHecha Then
                DeshacerYTirar($"'{IO.Path.GetFileName(destino)}' was NOT deleted: its backup could not be created or flushed to disk " &
                               $"created ('{IO.Path.GetFileName(copia)}')." &
                               If(causaCopia = "", "", Environment.NewLine & causaCopia), Nothing)
            End If

            ' ⛔⛔ LA ETAPA ENTRA APENAS SU COPIA ESTA ASEGURADA — ANTES DEL DIARIO Y ANTES DEL `Delete`.
            ' Es EL MISMO GEMELO que en `Guardar`, y tenia el mismo agujero: `Registrar` PUEDE TIRAR, y con
            ' el `_etapas.Add` alla abajo esa excepcion dejaba en disco una copia que no estaba en ninguna
            ' lista — ni el rollback la conocia ni la limpieza la levantaba: respaldo HUERFANO al lado del
            ' archivo del usuario, sin nada que lo explique. Medido en R5.
            ' `RequiereRollback` arranca en False y pasa a True RECIEN cuando el `Delete` salio: mientras
            ' sea False el archivo SIGUE en disco, asi que `Deshacer` no tiene nada que recrear y saltearlo
            ' es lo correcto — pero la etapa igual esta, y por eso su copia se limpia al resolver.
            Dim etapaActual As New Etapa With {
                .Destino = destino,
                .Copia = copia,
                .Heredadas = heredadas,
                .EsBorrado = True,
                .Atributos = atribsParaReponer,
                .RequiereRollback = False
            }
            _etapas.Add(etapaActual)

            ' ⛔ El borrado se anota ANTES de hacerlo, con su copia y sus atributos: si el proceso muere
            ' entre el Delete y el registro, el arranque siguiente no sabria que ese archivo existia.
            If _diario IsNot Nothing Then _diario.Registrar(destino, copia, "borrar", atribsParaReponer)
            Try
                ' Un SOLO LECTURA no se deja borrar; los atributos originales ya quedaron anotados arriba.
                LimpiarAtributos(destino)
                File.Delete(destino)
                etapaActual.RequiereRollback = True
            Catch ex As Exception
                ' ⛔⛔ SI EL BORRADO FALLA, EL ARCHIVO SE QUEDA — Y TIENE QUE QUEDARSE COMO ESTABA. El
                ' `LimpiarAtributos` de arriba ya le saco el OCULTO / SOLO LECTURA al archivo del USUARIO
                ' para poder borrarlo; si el `Delete` no pudo (archivo tomado, que es el caso normal), sin
                ' esto el archivo sobrevivia ALTERADO y el mensaje decia solamente "could not be deleted",
                ' sin admitir que ademas le habia cambiado los atributos. Un oculto que reaparece a la vista
                ' del usuario en una carpeta que MO2 le presenta no es un detalle cosmetico.
                ' Va PRIMERO, antes de tocar la copia y antes de deshacer, y con su Try propio que traga: la
                ' restauracion no puede pisar la causa original — es la misma ley que el `Dispose` que nunca
                ' tira. Cubre tambien el caso en que lo que tiro fue el propio `LimpiarAtributos`: reponer
                ' el mismo valor es un no-op.
                Dim atributosDevueltos As Boolean = Not atributosLeidos    ' nada que devolver = nada que fallar
                If atributosLeidos Then
                    Try
                        File.SetAttributes(destino, atribsParaReponer)
                        atributosDevueltos = True
                    Catch
                    End Try
                End If

                ' ⛔⛔ LA COPIA NO SE BORRA ACA. Habia un `Borrar(copia)` con el argumento "el archivo sigue
                ' ahi, su copia no aporta nada": el argumento es cierto sobre el ESTADO FINAL y falso sobre
                ' el CAMINO. El diario ya registro este borrado con ESA copia y sigue en `en_curso`; entre
                ' el `Borrar` y el `MarcarResuelto` hay una ventana en la que el diario promete restaurar
                ' desde un respaldo que ya no existe — y si el proceso muere ahi, el arranque siguiente
                ' encuentra la entrada y falla con "Backup missing". La copia se va donde se van todas:
                ' en la limpieza, DESPUES de `MarcarResuelto`. Como esta etapa tiene RequiereRollback=False,
                ' `Deshacer` no la toca y el informe queda vacio, asi que la limpieza corre igual.
                ' ⛔ EL MENSAJE DICE LA VERDAD DE LAS DOS COSAS. Si los atributos volvieron, el texto de
                ' siempre es cierto y alcanza. Si NO volvieron, el usuario tiene que enterarse de que su
                ' archivo quedo distinto de como estaba, y de cual era el valor.
                DeshacerYTirar($"'{IO.Path.GetFileName(destino)}' could not be deleted." &
                               If(atributosDevueltos, "",
                                  $" It was also left WITHOUT its original attributes (it had: {atributos}).") &
                               Environment.NewLine & ex.Message, ex)
            End Try
        End Sub

        ''' <summary>El lote salio bien. ⛔⛔ EL ORDEN ES EL PROTOCOLO, NO UN DETALLE:
        ''' <c>MarcarResuelto → borrar copias → borrar diario</c>.
        ''' <para>Antes se borraban las copias PRIMERO y el diario despues. Un corte en esa ventana dejaba
        ''' un diario en `en_curso` con la mitad de sus copias ya borradas, y el arranque siguiente ofrecia
        ''' "restaurar el estado anterior" de un guardado que en realidad estaba CONFIRMADO — restaurando
        ''' las pocas copias que sobrevivieron sobre archivos nuevos: cosecha mixta, que es exactamente el
        ''' daño que este lote existe para evitar. Con `resuelto` escrito ANTES, un corte en la limpieza
        ''' solo reinicia la limpieza, y la restauracion no se ofrece nunca sobre algo ya resuelto.</para>
        ''' <para>Se conserva la ley de heredadas de <see cref="GuardarConCopia"/>: una heredada se borra
        ''' SOLO si esta corrida la PROBO byte por byte redundante — y la comparacion se hace ARRIBA,
        ''' mientras las copias todavia existen.</para>
        ''' <para>⛔ SI `MarcarResuelto` FALLA, LA EXCEPCION SE PROPAGA Y EL GUARDADO ENTERO SE DESHACE. Es
        ''' deliberado y es ruidoso: `Confirmar` tira SIN poner `_confirmado`, asi que el `Dispose` del
        ''' `Using` del llamador corre `Deshacer()` y devuelve los archivos. La alternativa —seguir adelante
        ''' con un diario que no pudo pasar a `resuelto`— dejaria los destinos nuevos Y un diario `en_curso`
        ''' con sus copias: el arranque siguiente ofreceria DESHACER un guardado que el usuario ya dio por
        ''' bueno. Entre perder un guardado con aviso ahora y que se revierta solo la semana que viene, se
        ''' elige lo primero.</para></summary>
        Public Sub Confirmar()
            If _confirmado Then Return

            ' La comparacion va ANTES de borrar nada: necesita las copias vivas para poder probar la
            ' redundancia byte por byte.
            Dim heredadasRedundantes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each e In _etapas
                If String.IsNullOrEmpty(e.Copia) Then Continue For
                For Each h In e.Heredadas
                    If MismoContenido(h, e.Copia) Then heredadasRedundantes.Add(h)
                Next
            Next

            ' Primero, durablemente: los destinos YA son el resultado final.
            If _diario IsNot Nothing Then _diario.MarcarResuelto()

            ' Recien ahora las copias dejan de hacer falta.
            Dim sobrantes = LimpiarCopiasPropias()
            For Each h In heredadasRedundantes
                Borrar(h)
            Next

            ' ⛔ EL DIARIO SE BORRA SOLO SI NO QUEDO NINGUNA COPIA. Si alguna sobrevivio, el diario ya
            ' `resuelto` se queda y el arranque siguiente retoma la limpieza — inofensiva, porque `resuelto`
            ' le prohibe ofrecer restauracion.
            If sobrantes.Count = 0 AndAlso _diario IsNot Nothing Then _diario.Cerrar()
            _confirmado = True
        End Sub

        ''' <summary>Borra las copias que tomo ESTE lote y devuelve las que NO se pudieron borrar.
        ''' <para>Se deduplica por ruta: dos etapas sobre el mismo destino pueden compartir slot de copia, y
        ''' borrar dos veces la misma ruta haria que la segunda vuelta la contara como sobrante.</para>
        ''' <para>⛔ NO se le agrega un helper que ademas marque el diario. Sus dos unicos consumidores
        ''' —<see cref="Confirmar"/> y <c>CerrarRollbackTerminadoSinTirar</c>— tienen politicas OPUESTAS de
        ''' propagacion: uno TIRA y el otro NUNCA puede tirar. Unificarlos esconderia esa diferencia o
        ''' generaria un parametro que solo uno usa.</para></summary>
        Private Function LimpiarCopiasPropias() As List(Of String)
            Dim sobrantes As New List(Of String)
            Dim unicas = _etapas.Select(Function(e) e.Copia).
                Where(Function(ruta) Not String.IsNullOrEmpty(ruta)).
                Distinct(StringComparer.OrdinalIgnoreCase)
            For Each copia In unicas
                Borrar(copia)
                If File.Exists(copia) Then sobrantes.Add(copia)
            Next
            Return sobrantes
        End Function

        ''' <summary>⛔ NUNCA TIRA. Si el lote no se confirmo, deshace lo escrito en orden INVERSO y deja
        ''' el informe en <see cref="InformeDeRestauracion"/>. Tirar desde aca taparia la excepcion que
        ''' venia propagando —que es la causa que el usuario necesita—, asi que el camino que SI tira es el
        ''' `Guardar` fallido. Este cubre el otro caso: que el llamador aborte por su cuenta entre
        ''' etapas.</summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            If _dispuesto Then Return
            _dispuesto = True
            If _confirmado Then Return

            _InformeDeRestauracion = Deshacer()
            ' ⛔ SI ALGO NO VOLVIO, NO SE TOCA NADA MAS. El diario sigue `en_curso` y sus copias siguen en
            ' disco: son exactamente lo que el arranque siguiente necesita para ofrecerle al usuario lo que
            ' este rollback no pudo hacer. Marcar resuelto aca seria declarar terminada una restauracion
            ' incompleta.
            If _InformeDeRestauracion <> "" Then Return

            Dim aviso = CerrarRollbackTerminadoSinTirar()
            If aviso <> "" Then _InformeDeRestauracion = aviso
        End Sub

        ''' <summary>Cierra el ciclo del diario DESPUES de que <c>Deshacer</c> confirmo que TODOS los
        ''' destinos volvieron. Mismo protocolo que <see cref="Confirmar"/> —resuelto, copias, diario—
        ''' porque el estado del disco es igual de final: un rollback terminado tambien es una decision
        ''' completa, y ofrecerlo de nuevo en el arranque siguiente pisaria lo que el usuario ya recupero.
        ''' <para>⛔ NUNCA TIRA: sus dos llamadores corren MIENTRAS se propaga la excepcion original (el
        ''' `Dispose` de un `Using` que se esta desenrollando, y `DeshacerYTirar`). Una excepcion desde aca
        ''' TAPARIA esa causa, que es el dato que el usuario necesita. Lo que no se pudo hacer vuelve como
        ''' TEXTO y se anexa al informe.</para>
        ''' <para>Los tres avisos son distintos a proposito: "no se pudo marcar resuelto" (el arranque
        ''' siguiente va a ofrecer restaurar algo YA restaurado), "quedaron respaldos" (hay archivos al lado
        ''' de los suyos) y "quedo el diario" (inofensivo, se reintenta solo). Un solo mensaje generico
        ''' mandaria al usuario a revisar la cosa equivocada.</para></summary>
        Private Function CerrarRollbackTerminadoSinTirar() As String
            Try
                If _diario IsNot Nothing Then _diario.MarcarResuelto()
            Catch ex As Exception
                Return "Los archivos se restauraron, pero el diario de recuperacion no se pudo marcar " &
                       "como resuelto: " & ex.Message
            End Try

            Dim sobrantes = LimpiarCopiasPropias()
            If sobrantes.Count > 0 Then
                Return "Los archivos se restauraron, pero estos respaldos no se pudieron borrar: " &
                       String.Join(", ", sobrantes)
            End If

            If _diario IsNot Nothing AndAlso Not _diario.Cerrar() Then
                Return "Los archivos se restauraron y sus respaldos se borraron, pero el diario de " &
                       "recuperacion ya resuelto no se pudo eliminar. La limpieza se reintenta en el " &
                       "proximo arranque."
            End If
            Return ""
        End Function

        ''' <summary>Restaura en orden INVERSO. ⛔ SE INTENTAN TODAS: no se corta en la primera que falla,
        ''' porque cortar deja restauradas justo las ultimas y sin tocar las primeras — el estado mezclado
        ''' que este lote existe para evitar.
        ''' <para>⛔ EL ORDEN INVERSO NO ES ESTILO. Un lote puede tocar el MISMO destino mas de una vez
        ''' (escribirlo y despues borrar su sidecar, o guardarlo dos veces en la misma cadena). Hacia
        ''' adelante, la ultima copia que se vuelca es la MAS NUEVA y el archivo queda en el estado
        ''' INTERMEDIO —el que tenia a mitad del guardado—, no en el que el usuario tenia antes de empezar.
        ''' Hacia atras, la que manda al final es la primera copia tomada. Medido en `D3`.</para>
        ''' <para>⛔⛔ Y ACA NO SE BORRA NINGUNA COPIA. Ni la del que no volvio —es su unico ejemplar— ni la
        ''' del que si: mientras el diario siga en `en_curso`, sus copias son lo unico que un corte de luz
        ''' deja para restaurar, y borrarlas durante el rollback abre una ventana en la que el diario
        ''' promete recuperacion sobre respaldos que ya no estan. La limpieza es la ULTIMA fase del
        ''' protocolo y vive en <c>CerrarRollbackTerminadoSinTirar</c>, despues de `MarcarResuelto`.</para></summary>
        Private Function Deshacer() As String
            Dim vueltos As New List(Of String)
            Dim caidos As New List(Of String)
            ' ⛔ TERCERA LISTA, y no es cosmetica: una advertencia metida en `vueltos` MORIA en el
            ' epilogo, porque con `caidos.Count = 0` se devolvia "" y el informe entero desaparecia. O sea
            ' que el aviso "volvio SIN sus atributos" solo se veia si ADEMAS algo no habia vuelto — justo
            ' al reves de lo que hace falta. Los tres estados son distintos y se reportan por separado:
            ' volvio / volvio incompleto / no volvio.
            Dim advertencias As New List(Of String)
            For i = _etapas.Count - 1 To 0 Step -1
                Dim e = _etapas(i)

                ' ⛔ EL WAL PUEDE EXISTIR AUNQUE EL DESTINO NUNCA SE HAYA TOCADO. La etapa se anota
                ' apenas queda escrita en el diario, antes de abrir el archivo; si la apertura fallo, el
                ' destino sigue siendo el del usuario y volcarle la copia encima seria REESCRIBIRLO —
                ' cambiandole el mtime y la identidad por un guardado que nunca ocurrio. La etapa se
                ' conserva igual para que su copia se limpie al resolver el lote, pero no se restaura.
                If Not e.RequiereRollback Then Continue For

                ' ⛔ CREACION: el destino NO existia, asi que deshacerla es BORRARLO. Acá se hacia
                ' `Continue For` —"no hay copia, no hay nada que restaurar"—, y eso dejaba EN DISCO todos
                ' los archivos que el lote habia creado: un proyecto NUEVO que falla en la etapa 3 dejaba el
                ' `.osd` y el `.nif` huerfanos, sin `.osp` que los nombre. Restaurar una creacion no es
                ' volcar nada: es que el archivo no este.
                If e.Copia = "" AndAlso Not e.EsBorrado Then
                    If File.Exists(e.Destino) Then
                        Try
                            LimpiarAtributos(e.Destino)   ' un SOLO LECTURA no se deja borrar
                            File.Delete(e.Destino)
                            ' ⛔ SE VERIFICA QUE SE HAYA IDO. `File.Delete` puede volver sin excepcion y
                            ' dejar el archivo (borrado diferido: alguien lo tiene abierto con
                            ' FILE_SHARE_DELETE). Sin este chequeo el rollback informaria "se quita" un
                            ' archivo que sigue ahi, y el usuario cerraria el dialogo creyendo que su
                            ' carpeta volvio a estar limpia.
                            If File.Exists(e.Destino) Then
                                Throw New IOException(
                                    "El archivo creado por la transaccion fallida sigue existiendo.")
                            End If
                            vueltos.Add(IO.Path.GetFileName(e.Destino) & " (creado, se quita)")
                        Catch
                            caidos.Add($"  · '{IO.Path.GetFileName(e.Destino)}' se creo en este guardado y " &
                                       "NO se pudo quitar; borralo a mano si no lo querias")
                        End Try
                    End If
                    Continue For
                End If

                ' ⛔ UNA COPIA REGISTRADA QUE YA NO ESTA NO SE SALTEA EN SILENCIO. Acá habia un
                ' `Continue For` a secas: si el respaldo se esfumaba entre que se tomo y el deshacer (lo
                ' borro un limpiador, un antivirus, el usuario), la etapa DESAPARECIA del informe — el
                ' archivo NO habia vuelto y nadie lo nombraba. Es la misma clase de defecto que el `Catch`
                ' mudo: el peor resultado no es fallar, es fallar sin que se note.
                ' (La etapa de CREACION no llega hasta acá: la resuelve la rama de arriba, que no necesita
                ' copia porque deshacerla es borrar.)
                If Not File.Exists(e.Copia) Then
                    caidos.Add($"  · '{IO.Path.GetFileName(e.Destino)}' NO se pudo devolver: su respaldo " &
                               $"'{IO.Path.GetFileName(e.Copia)}' ya no esta en disco")
                    Continue For
                End If
                Try
                    VolcarEncima(e.Copia, e.Destino, permitirOrigenVacio:=True)
                    ' ⛔ SE COMPRUEBAN LOS BYTES ANTES DE DECLARAR LA ETAPA RECUPERADA. Sin esto, `vueltos`
                    ' afirmaba "volvio" con lo unico que sabia: que `VolcarEncima` no habia tirado. Un
                    ' volcado corto —disco lleno a mitad, escritura parcial— pasa por ahi sin excepcion y
                    ' deja el destino MEZCLADO; el informe lo daba por bueno y la copia quedaba como el
                    ' unico ejemplar sin que nadie lo dijera. Un rollback que miente es peor que uno que
                    ' falla.
                    If Not MismoContenido(e.Copia, e.Destino) Then
                        Throw New IOException("El destino restaurado no coincide con su respaldo.")
                    End If
                    ' ⛔ BORRADO: el archivo se habia ido, asi que recrearlo NO alcanza con los bytes — hay
                    ' que devolverle SUS atributos. El respaldo paso por `LimpiarAtributos` (si no, la copia
                    ' de un OCULTO queda invisible y una de SOLO LECTURA no se puede borrar despues), asi
                    ' que los originales viajan en la Etapa y se reaplican acá.
                    ' ⛔ Y SI LA DEVOLUCION DE ATRIBUTOS FALLA, SE DICE. Este `Catch` estaba vacio y despues
                    ' `vueltos.Add` afirmaba "volvio" a secas: el usuario recuperaba los BYTES y perdia el
                    ' OCULTO / SOLO LECTURA sin enterarse. Es el gemelo exacto del `Catch` de `Eliminar` que
                    ' ya lleva esta ley; tenerla en uno solo de los dos es como se separan.
                    ' Va a `vueltos` CON NOTA y no a `caidos` porque las dos cosas son ciertas y el usuario
                    ' necesita las dos: el archivo SI volvio —sus bytes estan— y ademas hay algo que revisar.
                    ' Mandarlo a `caidos` diria que no volvio, que es falso y lo mandaria a buscar una copia
                    ' que ya no hace falta.
                    If e.EsBorrado Then
                        Try
                            ' Mascara, no valor crudo: ver `MascaraDeAtributosRestaurables`. Y se VERIFICA,
                            ' porque `SetAttributes` sobre un archivo que otro proceso tiene tomado puede
                            ' volver sin efecto.
                            Dim esperados = AtributosRestaurables(e.Atributos)
                            File.SetAttributes(e.Destino, esperados)
                            If AtributosRestaurables(File.GetAttributes(e.Destino)) <> esperados Then
                                Throw New IOException("No se restauraron los atributos originales.")
                            End If
                        Catch ex As Exception
                            advertencias.Add(
                                $"  · '{IO.Path.GetFileName(e.Destino)}' recupero sus bytes, pero NO sus " &
                                $"atributos originales ({e.Atributos}): {ex.Message}")
                        End Try
                    End If
                    ' ⛔⛔ LA COPIA NO SE BORRA ACA. Antes se borraba "probada redundante", y eso rompia el
                    ' protocolo del diario: mientras el diario siga en `en_curso`, sus copias son lo unico
                    ' que un corte de luz deja para restaurar. Borrarlas durante el rollback abria una
                    ' ventana en la que el diario prometia recuperacion sobre copias que ya no estaban.
                    ' Las copias se van RECIEN despues de `MarcarResuelto`, en
                    ' `CerrarRollbackTerminadoSinTirar`.
                    vueltos.Add(IO.Path.GetFileName(e.Destino))
                Catch
                    caidos.Add($"  · '{IO.Path.GetFileName(e.Destino)}' NO se pudo devolver; su version " &
                               $"anterior esta en '{IO.Path.GetFileName(e.Copia)}'")
                End Try
            Next
            ' ⛔ EL INFORME SALE SI HAY ALGO QUE DECIR, y "algo" incluye una advertencia sola.
            If caidos.Count = 0 AndAlso advertencias.Count = 0 Then Return ""
            Dim sb As New Text.StringBuilder()
            If caidos.Count > 0 Then
                sb.Append("El guardado se deshizo, pero ")
                sb.Append(caidos.Count)
                sb.Append(" archivo(s) no volvieron solos:")
                sb.Append(Environment.NewLine)
                sb.Append(String.Join(Environment.NewLine, caidos))
            End If

            If advertencias.Count > 0 Then
                If sb.Length > 0 Then sb.Append(Environment.NewLine)
                sb.Append("Archivos recuperados con atributos incompletos:")
                sb.Append(Environment.NewLine)
                sb.Append(String.Join(Environment.NewLine, advertencias))
            End If

            If vueltos.Count > 0 Then
                sb.Append(Environment.NewLine)
                sb.Append("Si volvieron: ")
                sb.Append(String.Join(", ", vueltos))
            End If
            Return sb.ToString()
        End Function

        ''' <summary>Deshace y RE-TIRA con la causa original. Mismo protocolo que el `Dispose`: si el
        ''' rollback dejo algo afuera, el diario y las copias SE QUEDAN; si todo volvio, se cierra el ciclo
        ''' y lo unico que puede pasar es que se anexe un aviso al mensaje.</summary>
        Private Sub DeshacerYTirar(causa As String, interna As Exception)
            Dim informe = Deshacer()
            If informe = "" Then informe = CerrarRollbackTerminadoSinTirar()
            _InformeDeRestauracion = informe
            _confirmado = True          ' ya se deshizo: el Dispose no lo tiene que volver a hacer
            Throw New IOException(causa & If(informe = "", "", Environment.NewLine & informe), interna)
        End Sub
    End Class

    ''' <summary>Nombre del slot <paramref name="n"/> de respaldo: 1 =&gt; `&lt;destino&gt;&lt;sufijo&gt;`,
    ''' 2 =&gt; `…&lt;sufijo&gt;2`, 3 =&gt; `…&lt;sufijo&gt;3`… El 1 no lleva numero para no romper los
    ''' archivos que ya estan en disco de las versiones anteriores.</summary>
    Private Shared Function Slot(destino As String, sufijo As String, n As Integer) As String
        Return destino & sufijo &
               If(n = 1, "", n.ToString(Globalization.CultureInfo.InvariantCulture))
    End Function

    ''' <summary>Los slots de respaldo OCUPADOS, ordenados por número. Cada uno es una copia HEREDADA: la
    ''' dejó una corrida que nunca confirmó su escritura (el proceso murió, o la restauración automática
    ''' falló y el mensaje le dijo al usuario dónde estaba su versión anterior).
    ''' <para>⛔⛔ ENUMERA AUNQUE HAYA HUECOS, y esto SUPERSEDE al docstring anterior. Antes cortaba en el
    ''' primer número ausente y lo justificaba con «un huérfano que sobrevive de más es el lado seguro del
    ''' error». Esa justificación ya no se sostiene, por dos motivos concretos:</para>
    ''' <list type="bullet">
    ''' <item>lo que hace SEGURO no borrar de más no es la ceguera, es la PRUEBA: una heredada sólo se
    ''' descarta cuando <see cref="MismoContenido"/> demostró byte a byte que no guarda nada propio. Ver
    ''' esa copia no la pone en peligro — la somete a la misma prueba que a todas;</item>
    ''' <item>y no verla sí tiene costo: con `.npcm.prev` y `.npcm.prev3` en disco, la tercera quedaba
    ''' invisible para la detección (<see cref="CopiasPendientes"/> no la nombraba), para la comparación
    ''' (nunca se probaba redundante, así que se acumulaba para siempre) y para la limpieza.</item></list>
    ''' <para>⚠️ EL COSTO, dicho: esto enumera la CARPETA en vez de hacer N `File.Exists`. Por eso lo usan
    ''' sólo <see cref="GuardarConCopia"/> y los lotes, que son acciones interactivas de unos pocos archivos.
    ''' <see cref="Escribir"/> —miles de archivos por horneado— no lo toca.</para></summary>
    Private Shared Function SlotsOcupados(destino As String, sufijo As String) As List(Of String)
        Dim carpeta = IO.Path.GetDirectoryName(destino)
        If String.IsNullOrEmpty(carpeta) Then carpeta = Environment.CurrentDirectory
        If Not Directory.Exists(carpeta) Then Return New List(Of String)()

        Dim nombreBase = IO.Path.GetFileName(destino) & sufijo
        Dim encontrados As New List(Of Tuple(Of Integer, String))

        For Each ruta In Directory.EnumerateFiles(carpeta, nombreBase & "*")
            Dim nombre = IO.Path.GetFileName(ruta)
            If nombre.Equals(nombreBase, StringComparison.OrdinalIgnoreCase) Then
                encontrados.Add(Tuple.Create(1, ruta))
                Continue For
            End If

            If Not nombre.StartsWith(nombreBase, StringComparison.OrdinalIgnoreCase) Then Continue For
            ' La cola tiene que ser un numero >= 2 Y NADA MAS: asi `plugin.esp.npcm.prev2` entra y
            ' `plugin.esp.npcm.prev2.viejo` o `...prevX` quedan afuera. `NumberStyles.None` rechaza signo,
            ' espacios y separadores, que es lo que hace que "2 " o "+2" no cuenten como slot.
            Dim cola = nombre.Substring(nombreBase.Length)
            Dim numero As Integer
            If Integer.TryParse(cola, Globalization.NumberStyles.None,
                                Globalization.CultureInfo.InvariantCulture, numero) AndAlso numero >= 2 Then
                encontrados.Add(Tuple.Create(numero, ruta))
            End If
        Next

        Return encontrados.OrderBy(Function(x) x.Item1).Select(Function(x) x.Item2).ToList()
    End Function

    ''' <summary>El primer nombre de respaldo LIBRE para <paramref name="destino"/> con el sufijo dado.
    ''' <para>⛔ ESTA ES LA LEY DEL NOMBRE, Y VIVE ACA SOLA. La usa <see cref="GuardarConCopia"/> con
    ''' <see cref="SufijoCopia"/>, la usan los lotes, y la usa <c>LoadOrderActivator.WriteEntries</c> con su
    ''' `.npcm.bak`, que no puede usar GuardarConCopia porque su respaldo tiene que sobrevivir a la
    ''' escritura (lo consume el rollback del verify de relectura, que corre después). Artefactos con vidas
    ''' distintas, una sola ley: <b>un respaldo que ya está en disco no se pisa jamás</b>.</para>
    ''' <para>⛔⛔ BUSCA EL PRIMER HUECO; NO CUENTA. `Count + 1` era correcto SÓLO mientras
    ''' <see cref="SlotsOcupados"/> cortaba en el primer hueco —con un prefijo contiguo, `Count + 1` ES el
    ''' primer libre—. Al pasar a enumerar con huecos, ese cálculo devuelve un nombre YA OCUPADO: con
    ''' `.prev` y `.prev3` presentes, `Count = 2` y `Count + 1 = 3` ⇒ `.prev3`, que existe. Por eso los dos
    ''' cambios son ATÓMICOS: enumerar con huecos sin arreglar esto pisaría el respaldo del usuario, que es
    ''' el daño exacto que esta función existe para impedir.</para>
    ''' <para>El `File.Exists` extra del bucle no es redundancia decorativa: cubre la carrera entre el
    ''' enumerado y la creación, y los nombres que la enumeración descarta por forma.</para></summary>
    Public Shared Function PrimerSlotLibre(destino As String, sufijo As String) As String
        If String.IsNullOrEmpty(destino) Then Throw New ArgumentException("Empty path.", NameOf(destino))
        If String.IsNullOrEmpty(sufijo) Then Throw New ArgumentException("Empty suffix.", NameOf(sufijo))

        Dim ocupados = New HashSet(Of String)(SlotsOcupados(destino, sufijo), StringComparer.OrdinalIgnoreCase)
        Dim n As Integer = 1
        Do
            Dim candidato = Slot(destino, sufijo, n)
            If Not ocupados.Contains(candidato) AndAlso Not File.Exists(candidato) Then Return candidato
            n += 1
        Loop
    End Function

    ''' <summary>Comparacion BYTE POR BYTE de dos archivos. Corta por tamano primero.
    ''' <para>⛔ NO alcanza con comparar el tamano: es la misma trampa que documenta el `copiaHecha` de
    ''' <see cref="GuardarConCopia"/> — dos archivos distintos con la misma longitud existen, y aca el
    ''' precio de equivocarse es borrar el unico ejemplar del dato del usuario.</para>
    ''' <para>Costo: cero en el camino sano, porque sin copias heredadas no se llama. Cuando se llama,
    ''' son dos lecturas secuenciales de un archivo que esta misma corrida YA copio entero unas lineas
    ''' antes — es estrictamente menos que el `File.Copy` que el metodo ya paga.</para>
    ''' <para>⛔ ES PUBLICO A PROPOSITO, y por eso el predicado vive ACA y no se reimplementa: la misma
    ''' ley de retencion —"un respaldo se descarta SOLO cuando se PROBO byte por byte que no guarda nada
    ''' propio"— la necesita <c>FomodExporter</c> para sus <c>.recovered</c>. Dos implementaciones del
    ''' mismo predicado es como se empiezan a borrar ejemplares unicos por un lado y no por el otro.</para></summary>
    Public Shared Function MismoContenido(a As String, b As String) As Boolean
        Try
            Dim fa As New FileInfo(a), fb As New FileInfo(b)
            If Not fa.Exists OrElse Not fb.Exists Then Return False
            If fa.Length <> fb.Length Then Return False
            Using sa As Stream = File.OpenRead(a), sb As Stream = File.OpenRead(b)
                Dim ba(65535) As Byte, bb(65535) As Byte
                Do
                    Dim na = LeerBloque(sa, ba)
                    Dim nb = LeerBloque(sb, bb)
                    If na <> nb Then Return False
                    If na = 0 Then Return True
                    For i = 0 To na - 1
                        If ba(i) <> bb(i) Then Return False
                    Next
                Loop
            End Using
        Catch
            ' ⛔ Si no se pudo comparar, la respuesta segura es NO. Una heredada que no pudimos PROBAR
            ' redundante no se borra: el fallo del comparador no puede costarle el dato al usuario.
            Return False
        End Try
        Return False
    End Function

    ''' <summary>Lee hasta llenar el buffer o agotar el stream. `Stream.Read` puede devolver menos de lo
    ''' pedido sin estar en el final, y comparar bloques de largo distinto daria "difieren" sobre dos
    ''' archivos identicos.</summary>
    Private Shared Function LeerBloque(s As Stream, buf As Byte()) As Integer
        Dim total As Integer = 0
        While total < buf.Length
            Dim n = s.Read(buf, total, buf.Length - total)
            If n = 0 Then Exit While
            total += n
        End While
        Return total
    End Function

    ''' <summary>Fuerza a disco un respaldo que va a ser la UNICA red antes de truncar (o borrar) el
    ''' destino. Si no se puede garantizar la sincronizacion, la operacion aborta SIN tocar el destino.
    ''' <para>⛔ ES ESTRICTA, Y REEMPLAZA A UNA `Sincronizar` BEST-EFFORT que se tragaba el fallo. Con
    ''' la vieja, `GuardarConCopia` y el lote seguian de largo y truncaban el destino apoyados en un
    ''' respaldo cuya durabilidad NADIE habia podido confirmar — o sea que la red podia no existir
    ''' justo cuando hacia falta. La clase ya le exige esta misma frase al packer ("la red solo es red
    ''' si llego al plato"); acá se la exige a si misma.</para>
    ''' <para>⚠️ NO se usa en <see cref="Escribir"/>: esa ruta es salida REGENERABLE y su default de
    ''' no-sincronizar esta MEDIDO (+2,66 a +4,53 ms por llamada, miles de archivos por corrida).</para>
    ''' <para>`FileAccess.ReadWrite` y no `Write`: `FlushFileBuffers` necesita un handle con permiso de
    ''' escritura, y abrir ReadWrite no cambia los bytes ni los atributos.</para></summary>
    Private Shared Sub SincronizarRespaldo(ruta As String)
        If String.IsNullOrEmpty(ruta) Then Throw New ArgumentException("Empty path.", NameOf(ruta))
        Using fs As New FileStream(ruta, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)
            fs.Flush(flushToDisk:=True)
        End Using
    End Sub

    Private Shared Sub Borrar(ruta As String)
        Try
            File.Delete(ruta)
        Catch
            ' Best-effort en todos los usos: un archivo huerfano no rompe nada.
        End Try
    End Sub

    ''' <summary>Le saca a un RESPALDO recien copiado los atributos que lo esconden o lo congelan.
    ''' <para>⛔ HACE FALTA PORQUE <c>File.Copy</c> PROPAGA LOS ATRIBUTOS DEL ORIGEN. MEDIDO en net8.0.30:
    ''' copiar un archivo OCULTO deja la copia OCULTA, y uno de SOLO LECTURA la deja de SOLO LECTURA. Sin
    ''' esto, el respaldo del dato del usuario le queda INVISIBLE justo cuando lo necesita, y ademas un
    ''' respaldo de solo lectura no se puede borrar despues.</para>
    ''' <para>⛔ SOLO SE APLICA A COPIAS QUE ESTA CLASE (o el packer) ACABA DE CREAR. NUNCA al archivo del
    ''' usuario: sacarle el oculto a un archivo suyo seria una ley nueva, y la cabecera de
    ''' <see cref="EscribirNucleo"/> es explicita en que la escritura CONSERVA el atributo.</para>
    ''' <para>Es publico para que el `.bak.unpack` de <c>ArchivePackager.Unpack</c> —el otro respaldo del
    ''' arbol que nace de un <c>File.Copy</c>— use ESTA ley y no una segunda copiada al lado.</para></summary>
    Public Shared Sub LimpiarAtributos(ruta As String)
        Try
            Dim attr = File.GetAttributes(ruta)
            Dim malos = FileAttributes.ReadOnly Or FileAttributes.Hidden
            If (attr And malos) <> 0 Then File.SetAttributes(ruta, attr And Not malos)
        Catch
        End Try
    End Sub

    Private Shared Function MensajeCopiaViva(destino As String, copia As String) As String
        Return $"'{IO.Path.GetFileName(destino)}' was left half-written and could not be restored " &
               $"automatically. The previous version is in '{IO.Path.GetFileName(copia)}', next to it " &
               "(with Mod Organizer, in the Overwrite folder)."
    End Function
End Class
