# Decisión: sincronización POS dirigida por eventos, sin polling

**Fecha:** 31 de julio de 2026
**Estado:** vigente y obligatoria

## 1. Prevalencia

Esta decisión reemplaza cualquier documento anterior que proponga sondeo HTTP
periódico, manifiestos consultados por intervalo o polling incremental desde las
cajas. El cursor durable se conserva, pero solo se consulta por un evento:

1. apertura o primer enrolamiento;
2. conexión o reconexión del canal push;
3. regreso de la aplicación desde suspensión;
4. recepción de una notificación del servidor;
5. recuperación manual ante un error diagnosticado.

No habrá un temporizador en cada caja preguntando si existen cambios.

## 2. Regla transaccional

Todo cambio confirmado que afecte la operación local debe producir, en la misma
transacción de negocio:

- la entidad o su nueva versión;
- una entrada ordenada e idempotente en el stream correspondiente;
- un mensaje de outbox para despertar a las cajas afectadas.

Después del commit, un worker .NET publica la invalidación. El socket solo avisa;
la API entrega el delta y el stream durable garantiza recuperación.

La señal contiene como mínimo `BusinessId`, `Stream`,
`AvailableThroughCursor`, `NotificationId` y `OccurredAt`. No transporta el
catálogo completo. La caja descarga el rango pendiente, lo aplica
transaccionalmente en SQLite y confirma el cursor aplicado.

## 3. Streams que necesita la caja

### Catalog

- producto creado;
- nombre, referencia, código interno o descripción de venta modificados;
- activación, bloqueo o retiro;
- códigos de barras y alternos;
- unidad, impuesto y datos mínimos de presentación;
- configuración pesable, PLU y balanza;
- precio base del negocio;
- listas, canales y sus precios efectivos;
- tombstones.

Un costo, margen o propuesta interna no se envía. Solo publicar el precio
efectivo crea un cambio vendible.

### Customers

- cliente creado desde POS o administración;
- nombre, identificación, teléfonos y datos fiscales mínimos;
- datos mínimos necesarios para búsqueda y facturación;
- asignación excluyente de lista o canal;
- activación, corrección, fusión o tombstone;
- vínculo informativo con el negocio.

Un vínculo previo con otro negocio nunca impide venderle al cliente.

La creación rápida desde una caja es local-first. POS Edge reserva el
`CustomerId`, guarda en la misma transacción SQLite la proyección mínima del
cliente y un mensaje `customer.created` en la outbox unificada. El servidor
acepta ese identificador únicamente de un dispositivo enrolado y usa
`OperationId` para hacer idempotente cualquier reintento. Al reconectar, la
outbox sube primero el cliente, el servidor publica `Customers` y el snapshot
autoritativo reemplaza la proyección provisional sin cambiar el identificador.
El catálogo geográfico necesario para el formulario también se conserva
localmente y se renueva con la sincronización de catálogo; por eso país,
división y ciudad no dependen de red durante la captura offline.

### Security

- usuario habilitado para login offline;
- roles y permisos efectivos;
- bloqueo, revocación o cambio de credencial offline;
- versión de autorización.

Solo viajan verificadores seguros y datos mínimos, nunca contraseñas en claro.

### RegisterConfiguration

- asociación caja-negocio y caja-bodega;
- política de negativos de la bodega;
- medios de pago;
- serie operativa y provisión fiscal;
- configuración funcional y estado de enrolamiento.

La impresora y los periféricos físicos permanecen locales salvo una preferencia
servidor expresamente definida.

### Datos excluidos

No se descargan inventario completo, costos, costo promedio, márgenes,
propuestas de precio, secretos fiscales, reportes ni consolidaciones. Los
pedidos continúan online conforme al diseño vigente.

La validación de existencias conserva esta frontera: cuando Auraly Server está
disponible, POS Edge consulta en línea la proyección canónica
`InventoryBalances` de la bodega enrolada. Si el servidor no responde, la
captura, el cambio de cantidad y la recuperación de un borrador no se bloquean
por inventario, porque no existe una copia local autoritativa que permita
validarlo. La venta queda durable en el outbox y, al sincronizar, entra por el
motor documental canónico; POS Edge nunca reconstruye ni descarga saldos.

## 4. Transporte y escala

Cada caja abre una conexión TLS saliente:

- SaaS: Azure Web PubSub;
- on-premise: SignalR autohospedado o adaptador equivalente;
- pruebas: servidor push determinístico con el mismo protocolo.

La API HTTP o Function no mantiene los sockets. Web PubSub realiza el fan-out.
Las conexiones se agrupan por `BusinessId` y stream. Una importación de mil
productos produce una invalidación agregada por cursor, no mil llamadas a cada
caja.

Solo un POS instalado y enrolado abre esta conexión. El navegador y el escritorio
instalado que decidió continuar sin enrolarse trabajan contra la API online y no
registran el suscriptor push, no descargan deltas ni consumen una conexión de Azure
Web PubSub.

## 5. Garantía durable sin polling

1. El cambio y el outbox se confirman atómicamente.
2. El publicador reintenta hasta que el canal acepta el mensaje.
3. SQLite conserva checkpoints por dispositivo, ámbito y stream.
4. El servidor conserva el último cursor confirmado por dispositivo y stream.
5. La caja confirma únicamente después de aplicar el delta.
6. Si una caja conectada no confirma, el servidor reenvía una invalidación con
   backoff; la caja no inicia consultas periódicas.
7. En la reconexión, la caja presenta sus checkpoints y el servidor responde
   inmediatamente qué streams tienen cursores pendientes.
8. Un cursor alto cubre todos los cambios anteriores no aplicados.
9. Los heartbeats del WebSocket mantienen la conexión, pero no consultan
   catálogos ni manifiestos.

Una caja apagada no genera carga: recupera el rango pendiente al reconectar.

## 6. Bootstrap y facturas abiertas

El primer bootstrap automático bloquea facturación solo mientras no exista un
catálogo local válido. Después, la caja abre con su última versión íntegra y la
puesta al día ocurre en segundo plano, activada por el handshake.

Un precio nuevo no repricia silenciosamente una línea ya capturada. Se usa en
líneas posteriores o mediante una acción explícita, autorizada y confirmada.
Ningún cambio muta un snapshot fiscal emitido.

## 7. Persistencia mínima

Deben existir responsabilidades equivalentes a:

- `PosChangeStreams`: cambios ordenados y tombstones;
- `ServerOutboxMessages`: invalidaciones por publicar;
- `PosDeviceStreamCheckpoints`: cursor confirmado por dispositivo;
- entregas/reintentos acotados para dispositivos conectados sin confirmación.

Puede usarse un stream genérico tipado en vez de una tabla por entidad, siempre
que preserve orden, idempotencia, ámbito, retención y payload de delta.

## 8. Seguridad

El servidor obtiene el negocio desde el dispositivo enrolado, no desde el body.
Una caja solo se une a grupos y descarga streams autorizados. Cada descarga
revalida dispositivo, caja y negocio. Cambiar de negocio en POS Edge requiere
nuevo enrolamiento y bootstrap. Revocar el dispositivo impide reconexión.

## 9. Pruebas obligatorias

- alta, cambio, bloqueo y tombstone de producto y cliente;
- códigos, impuestos, precio base, listas, canales y configuración de caja;
- cambios de usuarios y permisos;
- cambio más outbox en una sola transacción;
- ninguna señal antes del commit;
- fan-out agrupado para cambios masivos;
- caja conectada descarga solo el delta;
- caja apagada recupera al reconectar;
- evento perdido se recupera por confirmación y reenvío del servidor;
- duplicados y reintentos idempotentes;
- caída durante aplicación conserva el checkpoint anterior;
- aislamiento entre negocios;
- factura abierta no se repricia;
- POS no almacena inventario, costos ni márgenes;
- carga con muchas conexiones;
- prueba arquitectónica que prohíba timers de polling de sincronización POS.

## 10. Puerta de implementación

La capacidad solo está completa al demostrar:

```text
cambio -> entidad + stream + outbox -> worker -> Web PubSub/SignalR
       -> POS -> delta autenticado -> SQLite -> ack -> checkpoint servidor
```

El mecanismo de recuperación es el stream durable activado al conectar o
reconectar, nunca polling.
