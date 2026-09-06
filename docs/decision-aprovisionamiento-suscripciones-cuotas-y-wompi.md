# Decisión: aprovisionamiento, suscripciones, cupos y Wompi

Estado: aprobado para implementación incremental
Fecha: 2026-08-30

## Resultado

Una empresa se crea una sola vez y únicamente después de verificar el pago o de registrar una exención autorizada. El mismo contrato gobierna el alta inicial, las ampliaciones, los cupos visibles para el tenant y su aplicación concurrente.

Se extienden los propietarios existentes: `ITenantProvisioningStore` sigue creando físicamente el tenant; el ciclo de pagos existente sigue siendo el único motor de pagos; usuarios, enrolamiento POS, nómina y fiscal conservan sus casos de uso. No se crean writers, catálogos ni landing paralelos.

## Catálogos comerciales separados

La oferta de gestión empresarial y la de agentes de IA son familias distintas dentro de una sola landing y una sola experiencia comercial. Los planes, créditos y precios actuales de agentes de IA se conservan.

Planes de gestión empresarial, en COP por mes:

| Código | Nombre | Precio | Usuarios completos | Cajas | Documentos DIAN/mes | Empleados de nómina |
|---|---|---:|---:|---:|---:|---:|
| `starter` | Inicio | $60.000 | 1 | 1 | 100 | 0 |
| `essential` | Esencial | $119.900 | 3 | 1 | 500 | 10 |
| `business` | Negocio | $299.900 | 8 | 3 | 1.500 | 30 |
| `company` | Empresa | $449.900 | 12 | 5 | 3.000 | 100 |
| `corporate` | Corporativo a medida | Cotizado | Configurable | Configurable | Configurable | Configurable |

`Negocio` aparece como recomendado. Adicionales:

| Código | Unidad | Precio |
|---|---|---:|
| `full_user` | usuario completo/mes | $30.000 |
| `seller_user` | usuario vendedor/mes | $10.000 |
| `pos_device` | caja/mes | $20.000 |
| `dian_document_pack` | paquete de 1.000 documentos/mes | $20.000 |
| `same_nit_site` | sede del mismo NIT | $0 |
| `payroll_employee_pack` | paquete de 10 empleados de nómina | $25.000/mes antes de IVA |

Todos los precios publicados son valores antes de IVA. El catálogo persistido es la única fuente de verdad. Landing y wizard lo consultan por API; el frontend nunca calcula un precio autoritativo. El catálogo existente se migra y versiona, sin duplicarlo. Cada cotización guarda snapshot de nombres, cantidades, precios, impuestos, moneda y versión. Los documentos adicionales solo se venden en bloques enteros de 1.000; el comprador puede elegir cualquier cantidad entera no negativa de paquetes en cualquier plan. El API rechaza cantidades fraccionarias y recalcula el total como `bloques * 1.000 * $20`. Por ejemplo, `Inicio + 3 paquetes` contrata 3.100 documentos mensuales y agrega $60.000 antes de IVA al precio base. Los empleados de nómina adicionales se venden en paquetes de 10 por $25.000 mensuales antes de IVA.

La periodicidad comercial admite `Annual` y `Monthly`. `Annual` es la selección predeterminada tanto en el aprovisionamiento como en una renovación iniciada por el cliente y aplica 15 % de descuento sobre doce meses del subtotal recurrente elegible: `total anual antes de impuestos = subtotal mensual * 12 * 0,85`. La interfaz presenta juntos el precio mensual equivalente, el total anual, el valor sin descuento y el ahorro exacto en COP (`subtotal mensual * 12 * 0,15`) con una etiqueta visible “Ahorras 15 %”. `Monthly` cobra un mes sin ese descuento. Impuestos, redondeos, conceptos elegibles y cualquier promoción adicional son calculados y devueltos por el servidor; el navegador solo proyecta la cotización y nunca combina descuentos por su cuenta.

## Wizard y pago

1. `/register` crea o recupera un borrador; todavía no crea tenant.
2. El wizard captura empresa, sede, administrador y plan.
3. El comprador puede sumar usuarios completos, vendedores, cajas, paquetes de 1.000 documentos DIAN y empleados. La UI recalcula visualmente y muestra la capacidad total resultante.
4. El paso de plan ofrece `Anual` y `Mensual`, con `Anual` preseleccionado. El resumen anual destaca el 15 % y el ahorro exacto en pesos, sin ocultar el total que se debitará hoy ni el valor mensual equivalente.
5. El servidor recibe periodicidad y cantidades, vuelve a cotizar desde el catálogo vigente y crea una cotización inmutable con expiración. La periodicidad, porcentaje y ahorro quedan en el snapshot.
6. Pago es el último paso. Se crea un intento Wompi de uso único.
7. El retorno del checkout solo consulta y muestra estado; nunca aprovisiona.
8. El callback del Widget envía inmediatamente el `transaction.id` al backend. El backend consulta Wompi y, si confirma `APPROVED`, ejecuta el mismo comando idempotente de confirmación y aprovisionamiento. Webhook y conciliador temporizado son rutas de recuperación que convergen en ese comando; ninguno mantiene una implementación propia de creación.
9. El primer consumidor que reclama el borrador pagado llama a `ITenantProvisioningStore`; los demás reciben el resultado ya creado.
10. El fulfillment crea o vincula idempotentemente la empresa aprovisionada como cliente de la empresa facturadora de Auraly, usando la identidad y los datos fiscales verificados del wizard. Esa relación es la destinataria de los cobros y facturas de suscripción.
11. Suscripción y entitlements se crean durablemente y luego el outbox envía la invitación al administrador.

La invitación del administrador permanece vigente durante tres días completos desde
su creación. Si vence, la activación responde de forma explícita `InvitationExpired`
y no oculta la causa como una invitación genéricamente inválida. Mientras el tenant
no tenga administrador aceptado, plataforma puede reenviarla desde el detalle del
tenant con `tenants.read` y `users.create`: se reutilizan la misma invitación opaca y el mismo
outbox de correo, se extiende su vigencia otros tres días y se audita
`TenantInvitationResent`. Un reenvío nunca crea otro usuario ni otra ruta de alta.

La aceptación resuelve la identidad del administrador por
`TenantId + país + tipo + identificación`. Si una migración ya creó esa `Party`
como cliente u otro rol comercial y todavía no tiene `AppUser`, la misma
transacción la reutiliza, completa sus datos de contacto y sede, conserva sus
roles y crea el acceso Administrador. No se duplica el tercero. Si la `Party` ya
tiene usuario, o correo/nombre de usuario pertenecen a otra cuenta del tenant,
la activación responde conflicto explícito.

Hay dos entradas al mismo motor. La entrada pública `/register` es anónima porque todavía no existe identidad ni tenant: crea un borrador protegido por token opaco, exige pago verificado y nunca expone exención. La entrada interna `/dashboard/tenants/new` es autenticada; únicamente un usuario de plataforma con `tenants.create` y `tenants.provisioning.payment.waive` puede aprovisionar sin Wompi. La decisión se valida en servidor y el navegador no envía una bandera de confianza para omitir el cobro.

La experiencia elegida es el Widget oficial de Wompi abierto como modal desde el último paso, no un iframe propio ni una pestaña nueva. Así el comprador permanece visualmente en el wizard y Wompi presenta sus medios de pago. La public key, referencia, COP, valor en centavos, expiración y firma de integridad provienen de la cotización del servidor; la private key y los secretos nunca llegan al navegador.

El callback del Widget entrega el identificador de transacción y cambia la pantalla a “Verificando pago”. La primera acción es `POST /provisioning/{draftId}/payments/{transactionId}/verify`: el servidor consulta Wompi y puede completar pago y aprovisionamiento en esa misma interacción. El navegador nunca afirma que pagó por sí solo. Si Wompi todavía devuelve `PENDING`, el frontend consulta con backoff el estado propio del borrador; webhook y conciliador continúan en segundo plano. Cuando queda `Provisioned`, la pantalla confirma que la empresa fue creada y que el correo fue programado. Para PSE, Nequi u otros medios asíncronos puede existir espera; cerrar la página no pierde el proceso y el comprador puede retomarlo. SSE puede añadirse para progreso, pero no es requisito ni autoridad del pago.

Estados: `Draft`, `Quoted`, `PaymentPending`, `Paid`, `Provisioning`, `Provisioned`, `PaymentFailed`, `Expired`, `Waived`, `Failed`. Las transiciones son monotónicas y auditadas. Doble clic, retorno, webhook y poller concurrentes producen un tenant y una invitación.

## Wompi

Existen dos alcances:

- Wompi de negocio vive en `IntegrationConnections` de una sede ya creada y cobra a sus clientes.
- Wompi de facturación Auraly es configuración de plataforma y cobra antes de que exista el tenant.

La cuenta de plataforma se resuelve mediante el proveedor canónico de integraciones de la sede facturadora de Auraly. `IntegrationConnections` conserva modo, estado y versiones: cada secreto queda cifrado individualmente mediante `IIntegrationSecretProtector` (AES-GCM) y la llave maestra de 256 bits vive exclusivamente en `Auraly__Integrations__SecretProtectionKey` del entorno. La base nunca guarda llaves Wompi en texto plano. Cambiar cuenta o ambiente crea una versión inmutable dentro de la misma conexión; cada `PaymentTransactions.MerchantConfigurationVersion` guarda la versión original para que Widget, webhook y conciliador verifiquen intentos históricos con la cuenta correcta. No se crea una integración, tabla de pagos ni proveedor paralelo.

Se reutilizan transporte, firma, consulta de transacción, polling e idempotencia de la integración actual de Mimos, no sus secretos ni su fila de negocio. Para habilitar Wompi se exigen private key, public key, events secret e integrity secret del mismo ambiente. Solo se aceptan endpoints oficiales HTTPS. Un secreto ausente siempre falla cerrado.

Un pago se acepta únicamente si pasan firma dinámica, ambiente/cuenta, consulta Wompi, estado aprobado, referencia, COP, valor exacto y unicidad de transacción. La rotación de cuenta no invalida intentos anteriores.

El endpoint de eventos es un router, no un aprovisionador genérico. La referencia resuelve el tipo de pago (`TenantProvisioning`, `EntitlementExpansion`, reserva u otro handler registrado). Antes de consultar Wompi inserta/consulta un recibo con clave única `Provider + MerchantConfigurationVersion + TransactionId`. Si el recibo ya terminó o el borrador está `Provisioned`, responde `200` sin volver a consultar, aprovisionar ni publicar correo. Si otro proceso tiene la transacción reclamada, devuelve éxito reintentable y deja que el propietario termine. Solo un intento conocido, de la cuenta correcta y todavía pendiente puede entrar al comando de confirmación.

## Exención

“Omitir pago” exige `tenants.provisioning.payment.waive`, sembrado únicamente para el rol administrador general de Auraly en el tenant de plataforma y marcado como no delegable. El backend comprueba tenant de plataforma, rol y permiso aunque la UI oculte el botón. Requiere motivo y auditoría, y crea settlement `Waived`; no falsifica una transacción Wompi. La exención aprovisiona inmediatamente y publica una sola invitación al correo de administrador capturado en el wizard. Sin ese permiso, la única transición válida es pago confirmado; después se aprovisiona y se publica la misma invitación. Nunca se invita antes de `Provisioned`.

## Entitlements y permisos

El rol `ADMINISTRATOR` aprovisionado para una empresa cliente no recibe por
defecto las capacidades opt-in de agentes y agenda: `agents.*`,
`conversations.*`, `leads.*`, `campaigns.*` ni `reservations.*`. Esto cubre
Agente IA, canales y contactos de atención, conversaciones, leads, campañas,
reservas y calendario. Las vistas aparecen únicamente después de asignar sus
permisos desde el propietario canónico de roles. El administrador del tenant de
plataforma `@auraly` conserva el catálogo completo. Las plantillas
`ADMINISTRATIVE` tampoco reciben estas capacidades automáticamente.

Los cupos pertenecen al tenant y cubren todas sus sedes. El tenant solo puede ver su uso e iniciar una compra. No escribe límites ni precios. Plataforma con `tenants.capacity.update` puede ajustar cualquier tenant con motivo y auditoría.

La capacidad aplicada siempre sale de las líneas de la cotización efectivamente pagada (o exenta), nunca del formulario del navegador. Al aprovisionar se comparan referencia, versión de catálogo, moneda, importe y cada cantidad comprada antes de materializar los límites. La vista del tenant muestra, por recurso, `usado / contratado / disponible`, fecha de corte y compras pendientes. Las ampliaciones solo aumentan capacidad cuando su propio pago queda confirmado.

Capacidad efectiva = incluida en plan + ampliaciones pagadas + ajustes vigentes. Una compra agrega un ajuste; nunca sobrescribe historia. La consulta muestra límite, usado, disponible, porcentaje y renovación.

- `FullUser`: cuenta activa completa. Se reserva en el caso de uso de usuarios.
- `SellerUser`: cuenta activa creada/vinculada desde Terceros con rol `ORDER_SELLER`.
- `PosDevice`: se reserva en `PosEnrollmentService`.
- `PayrollEmployee`: se reserva al activar un empleo, no al crear una Party.
- `DianDocument`: bolsa mensual compartida por facturas electrónicas, documentos soporte y documentos electrónicos de nómina emitidos.

`DianDocument` se renueva en cada fecha de corte de la suscripción: el consumo del nuevo periodo empieza en cero y la capacidad contratada se conserva; el saldo no usado no se acumula. Cada emisión reserva una unidad antes de numerar o enviar a DIAN y confirma el consumo con el resultado autoritativo. Borradores, consultas, reintentos técnicos y reenvíos del mismo documento conservan la misma reserva y no consumen otra unidad. Cuando no hay saldo, factura electrónica, documento soporte y nómina electrónica quedan bloqueados antes de emitir.

Durante el corte incremental, el tenant facturador de plataforma Auraly y los tenants
creados antes del modelo comercial pueden no tener todavía una fila en
`TenantSubscriptions`. La ausencia total de suscripción significa temporalmente
**cupo no administrado** y no equivale a límite cero; el preflight y la reserva permiten
emitir sin crear consumos ficticios. Esta compatibilidad solo aplica cuando la fila no
existe. Si existe una suscripción, sus estados, periodo mensual y límite se aplican de
forma estricta: `Suspended`, `Cancelled`, periodo ausente o saldo agotado bloquean. La
condición de retiro es completar la migración de límites actuales indicada en Rollout;
entonces todos los tenants comerciales tendrán suscripción y la excepción quedará sin
usuarios, sin cambiar el contrato de reserva.

La validación tiene dos niveles y ambos son obligatorios. El preflight de experiencia consulta el saldo al abrir cada flujo y evita que el usuario invierta trabajo en un documento que no podrá emitir: facturación electrónica muestra un modal bloqueante y deja únicamente comprobantes no electrónicos; recepción de mercancía deshabilita la selección de documento soporte y explica cómo ampliar; nómina marca desde el periodo que el envío DIAN de fin de mes no se programará por falta de saldo. El segundo nivel reserva atómicamente al confirmar la emisión para cubrir consumo concurrente entre pestañas, sedes y cajas. Un preflight exitoso nunca sustituye esa reserva final.

Para cajas desconectadas, el servidor entrega una concesión de cupo (`offline lease`) por dispositivo, con identificador y saldo monotónico, tomada de la misma bolsa mensual. La caja puede emitir offline solamente contra ese saldo reservado. Al sincronizar reporta los documentos e intercambia la concesión por un saldo actualizado. Si el servidor confirma agotamiento, se persiste localmente el bloqueo y ninguna pérdida posterior de conexión vuelve a habilitar la emisión. Una caja que aún no recibió esa confirmación puede continuar únicamente con su concesión ya reservada; nunca inventa saldo ni duplica el concedido a otra caja.

Si una caja alcanzó a cerrar una factura fiscal estando desconectada sin concesión suficiente, la venta y su snapshot fiscal no se pierden. El `Outbox` SQLite y `PosEdgeOutboxUploader` existentes la suben con la misma clave idempotente; el servidor acepta y procesa los efectos comerciales, pero deja su único `FiscalDocumentProcesses` en `PendingCapacity`, sin invocar DIAN. La respuesta y la sincronización fiscal muestran “Pendiente por documentos DIAN”. Ese estado no usa backoff de red ni genera intentos repetidos.

Una ampliación pagada o la apertura de un nuevo subperiodo mensual de capacidad publica una invalidación y reclama, en orden de emisión, los procesos `PendingCapacity` para los que ahora exista saldo. La transición reserva la unidad y devuelve el mismo proceso a `PendingGeneration`; desde allí continúan `FiscalGenerationWorker`, `IFiscalSubmissionWorkStore` y `FiscalSubmissionWorker`. No se crea otra factura, numeración, movimiento, outbox, worker ni cola. El `DocumentId`, snapshot, consecutivo y clave de idempotencia originales permanecen inmutables. Si varias cajas esperan, el reclamo serializado por tenant evita sobreconsumo y deja el excedente todavía pendiente.

### Reutilización canónica para la recuperación offline

| Necesidad | Propietario existente que se extiende |
|---|---|
| conservar la venta local | tabla SQLite `Outbox` y `PosEdgeSaleStore` |
| reconectar y subirla | `PosSynchronizationWork` → `PosUnifiedOutboxDispatcher` → `PosEdgeOutboxUploader` |
| recepción idempotente | `ReceivePosSaleService` y `SqlPosSaleServerStore` |
| estado fiscal durable | única fila de `FiscalDocumentProcesses` |
| generación y envío DIAN | `FiscalGenerationWorker`, `IFiscalSubmissionWorkStore` y `FiscalSubmissionWorker` |
| despertar tras compra/renovación | outbox de entitlements publica señal al motor fiscal e invalidación `FiscalProvisioning` al POS |

`PendingCapacity` es una espera de negocio recuperable, no un error permanente ni una falla de transporte. La compra no recorre cajas ni envía directamente a DIAN: solo aumenta el ledger, publica el evento y el propietario fiscal reclama trabajo. Esto conserva el orden, la idempotencia y una única ruta de emisión.

Todas las reservas bloquean por tenant para impedir que dos solicitudes consuman el último cupo. Creación, activación o cambio de rol valida por separado `FullUser` y `SellerUser`; activar empleados valida `PayrollEmployee`. El backend devuelve contador actual y límite en el error de capacidad para que la interfaz muestre cuánto lleva y ofrezca ampliar.

`ORDER_SELLER` abre únicamente Pedidos y sus acciones mínimas de captura. No incluye rutas, terceros, reportes, configuración, facturación, caja ni plataforma. Una Party sin acceso no consume usuario. Reintentos y reenvíos DIAN no vuelven a consumir documento.

## Alertas y compra de ampliaciones

Al cruzar 70 %, 85 %, 95 % y 100 %, una política idempotente publica aviso in-app y correo al administrador; Web Push se usa si hay suscripción válida. La deduplicación incluye tenant, periodo, entitlement y umbral. El aviso muestra uso/límite, renovación y “Ampliar capacidad”. La ampliación solo se aplica después del pago verificado.

## Ciclo de suscripción, órdenes de renovación, facturas y suspensión

El pago inicial cubre el periodo elegido desde el instante de aprovisionamiento: un mes para `Monthly` o doce meses para `Annual`. Esa fecha queda como `BillingAnchor`; no se deriva del día en que después se pague tarde. Para anclas 29, 30 o 31, los meses cortos usan su último día y el siguiente ciclo vuelve al día original. La zona horaria de facturación es configurable por plataforma y por defecto `America/Bogota`.

La facturación recurrente se agrega al ciclo de pagos existente; no se reutiliza el auto-renovado silencioso de `UsageBillingService`. La única `TenantSubscription` consolida las líneas recurrentes del tenant —plan empresarial, usuarios, vendedores, cajas, paquetes DIAN, empleados y planes de agentes activos—. Para `Monthly` crea una orden de renovación cada mes; para `Annual`, una vez pagado el año, no crea órdenes mensuales y genera la siguiente únicamente al acercarse el aniversario. Cada revisión conserva snapshot del catálogo, periodicidad, cantidades, impuestos, descuentos, ahorro, moneda y periodo; cambios posteriores no la reescriben.

Pagar anual modifica el calendario de cobro, no la semántica operativa de los cupos mensuales. Los documentos DIAN y cualquier capacidad expresada “por mes” abren doce subperiodos mensuales dentro del término anual prepagado, reinician su consumo en cada subancla y no acumulan saldo. Esa apertura mensual no genera una cuenta por cobrar ni otra factura fiscal. Usuarios, vendedores, cajas y empleados permanecen contratados durante todo el término, sujetos a las ampliaciones o disminuciones programadas.

La orden se genera idempotentemente algunos días antes de la siguiente ancla (configurable; recomendado 5), vence en la ancla e identifica el siguiente periodo mensual o anual. Es una preliquidación comercial: no es factura, no se transmite a la DIAN, no consume numeración, no crea cartera y no produce contabilidad. La página Suscripción muestra periodicidad, estado, periodo, detalle, total, descuento, ahorro, factura electrónica posterior al pago y botón “Pagar con Wompi”. El Widget usa una referencia única de la revisión vigente. La verificación inmediata, webhook y conciliador aplican el pago a esa revisión una sola vez. Pagos parciales no renuevan ni reactivan; un excedente no se aplica silenciosamente a otra orden.

Después de confirmar el importe exacto, Wompi o el recaudo manual convergen en un único settlement idempotente. Solo entonces Auraly emite la factura electrónica de contado por el servicio, genera los efectos contables, activa el periodo y sus cupos y entrega los artefactos DIAN. El tenant accede únicamente a su orden, pago y factura. Un fallo fiscal posterior al pago deja el envío DIAN en el proceso recuperable habitual; no revierte ni pierde el pago y no crea otra factura.

Si el navegador se cierra después de abrir Wompi, una orden `PendingPayment` se retoma con la misma transacción lógica: referencia, importe, comercio y expiración originales. Nunca se inserta un segundo intento para “continuar”. Cuando el settlement termina, la vista presenta el mismo `SalesDocuments` emitido como factura completa y como ticket compacto imprimible; no existe una tabla o documento de recibo paralelo.

La orden permite aumentar o disminuir la capacidad del siguiente periodo. Una disminución nunca desactiva ni elimina datos: la capacidad elegida debe ser igual o superior al uso activo de usuarios completos, vendedores, cajas y empleados, y en planes estándar tampoco puede bajar de lo incluido. `Personalizado` usa `Empresa` como piso de precio y capacidad; ninguna dimensión queda por debajo de Empresa y al menos una debe superarla. Si todas coinciden, corresponde seleccionar Empresa. Cada edición crea una nueva revisión inmutable, cancela la anterior e invalida su intención de pago. Una ampliación inmediata dentro del periodo vigente usa un cobro proporcional separado.

El administrador autorizado de plataforma también puede registrar desde Auraly un recaudo manual de una cuenta abierta cuando el dinero llegó por transferencia, consignación u otro medio aprobado. Esta acción reutiliza el caso de uso canónico de pagos manuales y exige el permiso no delegable `tenants.billing.payment.confirm_manual`; el usuario del tenant nunca lo recibe. Antes de confirmar muestra tenant, cobro, periodo, saldo y capacidad que se activará; exige medio, fecha efectiva, importe total exacto, referencia bancaria o comprobante único y observación. No acepta fechas futuras, monedas distintas, cobros pagados/anulados, importes parciales ni una referencia ya utilizada. El soporte se guarda en el almacén documental autorizado, no como datos libres o secretos en el asiento.

Wompi y recaudo manual convergen después de validar el medio en un único comando idempotente de liquidación de la orden. Ese comando marca una sola revisión como pagada, registra/reutiliza el pago, actualiza la proyección de Suscripción, activa o renueva el periodo y sus entitlements, y publica la creación de la única factura electrónica correspondiente. La clave de idempotencia incluye `RenewalOrderId`, revisión y transacción/referencia externa. No existe un botón que cambie directamente el estado de la suscripción; revertir un recaudo exige el flujo contable de anulación/devolución y su impacto fiscal, con motivo y auditoría.

El periodo de gracia es configuración de plataforma, 10 días por defecto. En la ancla, una orden vigente sin pago pasa a vencida y la suscripción a `PastDue`. El tenant conserva operación durante los días 1 a 9 y la capacidad mensual se abre como crédito de gracia trazable. Los avisos previos son configurables (por defecto 5 días antes). La frecuencia vencida también es configurable (por defecto cada 3 días): con gracia 10 se avisa los días 3, 6 y 9 de mora; el día 9 anuncia la fecha de suspensión. Al inicio local del día 10 la suscripción pasa a `Suspended`, se envía aviso final y se bloquea la operación.

Cada evento genera notificación in-app visible en la campanita únicamente para cuentas activas con el rol administrador del tenant; vendedores, cajeros, empleados y demás roles no son destinatarios. El correo es un canal global opcional: habilitado significa que todos los tenants reciben los recordatorios; deshabilitado significa que ninguno los recibe, mientras la campanita permanece obligatoria. No existen excepciones ni interruptores por tenant. Cada entrega tiene clave única `RenewalOrderId + Revision + RecipientUserId + Channel + NotificationKind + DelinquencyDay`, de modo que múltiples administradores reciban su propio aviso y los reintentos no creen duplicados. El outbox registra programado, entregado y error seguro sin guardar contenido sensible innecesario.

En el módulo transversal `Tenants` se agrega una pestaña global `Política de cobranza`, protegida por el permiso de plataforma no delegable `tenants.billing.policy.manage`. Solo el administrador general de Auraly lo recibe. Configura `EmailRemindersEnabled` (`true` por defecto), días previos, días de gracia, frecuencia de mora, zona horaria y versiones de plantilla para todos los tenants. `InAppEnabled` no se expone porque la campanita siempre se entrega. El administrador de un tenant ve en Suscripción el calendario efectivo y sus facturas, pero nunca esta pestaña ni sus endpoints. Cada cambio exige motivo y registra actor, valor anterior/nuevo y vigencia; apagar correo no borra avisos ya entregados ni altera campanita o suspensión.

La campanita y el botón “Pagar ahora” del correo abren una ruta opaca de Auraly que, después de autenticar, lleva a `/dashboard/subscription?order={RenewalOrderId}` y enfoca la revisión autorizada. No se incrusta en el correo una URL estática de checkout Wompi: podría ser reenviada, expirar, pertenecer a una revisión anterior o estar ya pagada. Al abrir Auraly se comprueban tenant, rol, total y estado actuales y recién entonces el backend crea un intento de pago de uso único.

Suscripción contiene una tabla paginada en servidor, ordenada por creación descendente, con filtros por `Todas`, `Pendientes`, `Vencidas`, `Pagadas`, estado, rango y periodo cobrado. Cada fila muestra número de cobro, periodo, creación, vencimiento, estado, total, saldo, fecha de pago y, cuando exista, número de factura electrónica; el detalle ofrece factura/contenedor descargable, líneas y trazabilidad de pagos. “Pagar” solo aparece para saldo abierto. Tanto el CTA del correo como el botón de la tabla abren el mismo componente de pago: el Widget oficial de Wompi como modal dentro de Auraly, con verificación inmediata y recuperación por webhook/conciliador. No se crea una segunda integración, iframe propio ni pestaña obligatoria.

La suspensión se aplica por una política transversal de estado de tenant, no desactivando filas de empresa o usuario. Solo permanecen habilitados autenticación mínima, recuperación de contraseña, Suscripción, consulta/pago de cobros y facturas, soporte y administración de plataforma. Se rechazan nuevas operaciones API y enrolamientos, se revocan sesiones operativas y se publican invalidaciones `Security`/`FiscalProvisioning` a equipos conectados. Un equipo físicamente desconectado no puede recibir una suspensión instantánea y su preparación local no vence por el paso del tiempo; al primer contacto la sincronización persiste el bloqueo y deja de admitir nuevas operaciones. Esta ventana offline deliberada debe mostrarse en plataforma y medirse.

Un pago total confirmado, por Wompi o por recaudo manual autorizado, mueve la orden a `PaymentConfirmed`, crea/reutiliza su factura fiscal de contado, cambia la suscripción a `Active`, abre o confirma el periodo correspondiente y publica las mismas invalidaciones. La reactivación es idempotente. Si había documentos `PendingCapacity`, el motor fiscal los reclama contra la nueva capacidad en orden. Pagar tarde no desplaza `BillingAnchor` ni genera dos periodos. La vista del tenant refleja el mismo resultado y medio de pago sin exponer comprobantes internos ni datos contables de otros tenants.

Ampliaciones durante un periodo se cobran de inmediato prorrateadas por los días restantes y solo se activan tras el pago; el siguiente cobro de renovación incluye su precio recurrente completo según la periodicidad vigente. En anual, el prorrateo usa los días restantes del término anual y conserva el descuento anual solo cuando la política comercial versionada lo declare elegible. Disminuciones y cancelaciones se programan para la siguiente ancla y no borran capacidad ya pagada. Cambios de plan o periodicidad entran en la siguiente renovación, salvo una ampliación inmediata pagada; conservan snapshots y trazabilidad. Créditos o devoluciones requieren documento y aplicación contable, no edición de la factura.

## Factura electrónica de Auraly por servicios pagados

Cada pago total confirmado de aprovisionamiento, renovación o ampliación crea exactamente una factura electrónica de venta de Auraly al cliente por el servicio adquirido. El pago anual emite al confirmarse una factura por el término anual efectivamente comprado y no doce facturas mensuales; la próxima factura de renovación nace cuando se pague el cobro del siguiente año. El pago mensual emite una factura por cada mes efectivamente pagado. Se reutiliza `SalesDocuments` como encabezado canónico; `SalesDocumentLines` permanece exclusivo de producto y el detalle transaccional de servicio vive en `SalesDocumentServiceLines`, sin bodega, producto, caja ni semántica física. No se duplican motores: numeración, snapshots UBL, `FiscalDocumentProcesses`, generación, firma, envío DIAN, contabilidad, cartera y reporting se extienden con el contrato fuente `ServiceInvoice`.

No se crea `TenantBillingCharge`, `Receivable` ni cartera paralela antes del pago. `TenantSubscriptionRenewalOrder` es el snapshot comercial sin efectos contables y tampoco crea `DocumentProcessingJob` ni toca el cursor operacional. Al aprobar Wompi o el recaudo manual, el evento reclama `RenewalOrderId + Revision + PaymentId` y genera una sola `ServiceInvoice` de contado, ya cubierta por el pago. Webhook, verificación inmediata, conciliador y registro manual convergen en el mismo resultado y reciben el mismo `ServiceInvoiceId`. La interfaz distingue `Orden pendiente`, `Pago confirmado` y `Factura electrónica emitida`; una orden no pagada no inventa factura ni cartera y un pago nunca genera dos documentos.

La exención administrativa no equivale a un pago. Por defecto provisiona y registra el beneficio autorizado sin factura de venta ni recaudo; si contabilidad define que debe existir una operación gratuita, se modelará mediante el tratamiento fiscal aprobado y no mediante una transacción Wompi falsa. Pagos parciales continúan fuera de alcance: no disparan factura hasta completar el cobro.

### Facturación de servicios aislada de inventario

La decisión detallada está en `decision-facturacion-servicios-online.md`. `Products` y `SalesDocumentLines`, el POS Edge, cajas, bodegas, kardex, costos, despachos y devoluciones físicas no cambian para admitir servicios. `SalesDocuments` solo se amplía como tronco tipado de encabezado, con invariantes que impiden mezclar producto y servicio. La tabla `Services` existente conserva su responsabilidad de agenda y tampoco se reutiliza como catálogo fiscal.

Los servicios facturables viven en `BillableServices`; el encabezado de factura en `SalesDocuments` y sus líneas en `SalesDocumentServiceLines`. El flujo es exclusivamente online y usa el tipo fuente interno `ServiceInvoice`. Ante la DIAN continúa siendo `Invoice`/código `01`. Solo puede usar un prefijo y resolución realmente autorizados por DIAN; la aplicación no inventa el prefijo `S` ni otro tipo fiscal.

La confirmación persiste atómicamente la fuente y las solicitudes idempotentes a contabilidad, fiscal y reporting. No publica en `auraly-document-processing`, no crea `DocumentProcessingJobs`, no escribe `InventoryMovements`, no usa sesión de caja, no se sincroniza a POS y no entra a despachos. Los tres motores derivados existentes se amplían mediante contratos tipados; no se crean colas, workers, generadores UBL, carteras ni libros paralelos.

El cliente facturado vive como `Party/Customer` dentro de la empresa emisora de Auraly, vinculado de manera única al tenant y a su identificación legal. Todo tenant que alcance `Provisioned`, por pago confirmado o exención, debe tener esa relación de cliente aunque la exención no genere factura. No se crea automáticamente como cliente dentro de su propia empresa: es un tercero de la empresa facturadora de plataforma, visible y administrable solo bajo los permisos contables/comerciales de Auraly.

El borrador de aprovisionamiento debe capturar antes del pago razón social/nombre, tipo y número de identificación, dígito de verificación cuando corresponda, responsabilidades tributarias, dirección, municipio/país y correo de facturación; el servidor vuelve a validarlos. El comando interno `EnsurePlatformBillingCustomer` normaliza la identificación y reclama una clave única `PlatformBillingBusinessId + IdentificationType + IdentificationNumber`; si el mismo cliente ya existe lo vincula al nuevo `TenantId` permitido por la política, y si el tenant ya está vinculado devuelve la misma Party. Reintentos, webhook, conciliador y exención nunca crean duplicados. Una coincidencia ambigua o datos fiscales incompatibles detiene la emisión y queda en revisión, sin inventar ni sobrescribir silenciosamente al tercero.

El identificador de la Party de plataforma queda en la cuenta de facturación del tenant y es obligatorio antes de abrir un cobro facturable. El outbox puede completar la escritura entre propietarios, pero el flujo permanece en `BillingCustomerPending` y no alcanza `Provisioned`, no envía la invitación ni habilita operación hasta obtener la relación. Así, “tenant aprovisionado” implica siempre “cliente de Auraly creado o vinculado”, sin convertir una falla de facturación en datos falsos. La factura toma un snapshot inmutable de esos datos. Cambiar el tercero después no reescribe documentos emitidos. La cantidad, precio, impuestos y descripción salen exclusivamente de las líneas del cobro pagado, por lo que lo facturado siempre concuerda con lo comprado.

### Entrega profesional por correo

La factura no se envía al confirmar el pago, sino cuando el proceso fiscal queda `DianAccepted` y existe la respuesta de validación. `SqlFiscalSubmissionWorkStore` ya publica `FiscalDocument.DianAccepted` en `ServerOutboxMessages` y conserva `SignedXml` y `DianApplicationResponse` en `FiscalArtifacts`; el consumidor de entrega documental extiende ese evento, sin consultar estados por un timer ni volver a enviar a DIAN. Reclama una entrega única por `DocumentId + RecipientEmailSnapshot + DeliveryKind + ArtifactVersion`. El correo usa exclusivamente la dirección válida capturada en el snapshot fiscal del cliente. Si el snapshot no contiene correo válido, no se crea entrega, no se intenta SMTP, no se programa reintento y no queda tarea pendiente para un envío posterior; la factura solamente permanece consultable y descargable en Suscripción.

Lo solicitado como “CIF” se implementa con el nombre técnico vigente: un único archivo `.zip` cuyo contenido principal es el XML UBL `AttachedDocument`, que incorpora la factura electrónica firmada y el `ApplicationResponse` de aprobación de la DIAN. El PDF profesional con QR se incluye opcionalmente dentro de ese mismo ZIP; no se adjuntan varios archivos sueltos. El ZIP respeta el límite de 2 MB y el asunto reglado por el anexo: NIT del facturador; nombre del facturador; número del documento; código del tipo; nombre comercial; línea de negocio opcional. El cuerpo, aunque no está reglado, presenta emisor, cliente, número, periodo/servicio, total, fecha, CUFE y un botón autenticado de consulta, sin convertir el portal en requisito para recibir los documentos.

El `AttachedDocument` y el ZIP se guardan como nuevos tipos versionados en `FiscalArtifacts`, derivados exclusivamente del `SignedXml`, `DianApplicationResponse` y PDF del mismo `DocumentId` y CUFE. El artefacto es inmutable; el correo nunca reconstruye XML desde una vista. Reintentos de SMTP reutilizan el mismo ZIP y no reenvían DIAN ni emiten otra factura. Se registran intentos, entrega, rebote y error seguro en el propietario canónico de entrega documental; los mensajes sensibles y adjuntos no se copian a logs.

Esta entrega sigue la [Resolución DIAN 000165 de 2023](https://www.dian.gov.co/normatividad/Normatividad/Resoluci%C3%B3n%20000165%20de%2001-11-2023.pdf), que contempla entrega por correo del XML, representación gráfica y documento de validación dentro del contenedor electrónico, y el [Anexo técnico de factura electrónica vigente publicado por la DIAN](https://micrositios.dian.gov.co/sistema-de-facturacion-electronica/documentacion-tecnica/). Los nombres, estructura, tamaño y reglas del contenedor se resuelven por la versión activa del generador fiscal, no se hardcodean en la plantilla de correo.

Pruebas adicionales obligatorias: cada tenant aprovisionado por pago o exención queda vinculado como cliente de Auraly; reintentos concurrentes reutilizan una sola Party; NIT existente compatible se vincula y una coincidencia incompatible queda en revisión; el cliente no aparece como tercero propio del tenant; una orden pendiente no crea `Receivable`, asiento ni documento DIAN; pago, webhook y conciliador concurrentes producen un pago y una factura; una `ServiceInvoice` no crea job operacional, kardex, costo, sesión, despacho ni sincronización POS; la matriz completa de facturación de productos conserva hashes, movimientos y resultados anteriores; importes de factura coinciden con la revisión pagada; aislamiento del cliente por tenant; DIAN rechazada no envía correo; aceptación crea una entrega; dos eventos/reintentos SMTP reutilizan un solo ZIP y no duplican factura; correo ausente no crea entrega, intento ni tarea pendiente y conserva descarga; ZIP único menor o igual a 2 MB contiene `AttachedDocument`, Invoice, ApplicationResponse y PDF opcional del mismo CUFE y `DocumentId`; asunto cumple el anexo; nota crédito de servicio usa la misma línea origen.

Estados de cartera: se conservan exactamente los existentes `Open`, `PartiallyPaid`, `Paid`, `Cancelled`; “Vencida” es `Open` o `PartiallyPaid` con saldo y `DueDate` anterior a la fecha efectiva. Los pagos parciales no habilitan renovación. La factura fiscal usa los estados del motor documental/DIAN existente. Estados de suscripción: `Active`, `PastDue`, `Suspended`, `Cancelled`. `Tenant.IsActive` no se usa para mora porque impediría el portal de pago y mezclaría baja administrativa con cobranza.

El programador canónico es `TimedProcessScheduler`: agrega tipos de proceso para generar renovación, emitir recordatorios y evaluar suspensión. Cada próximo hito vive en la tabla durable existente `ScheduledAutomationJobs`, generalizada con el propietario opcional `TenantSubscriptionId` y el tipo `TenantSubscriptionLifecycle`; la orden de renovación no guarda una segunda fecha de scheduler. La clave única por suscripción, el reclamo con lease, el reintento y la reprogramación se conservan en ese único calendario. No se crea otro timer, tabla de jobs ni worker propietario. El outbox publica correo, fiscal, contabilidad, capacidad y sincronización después de cada commit.

Antes de activar cobros reales en cada ambiente debe estar creada y fiscalmente configurada la empresa facturadora Auraly, incluido su NIT, resolución, impuestos y cuenta Wompi. También debe aprobarse el texto contractual de prorrateo, gracia, suspensión y no acumulación de documentos. Estas decisiones son datos versionados de catálogo/configuración; no se hardcodean.

## Experiencia

- Empresa: identidad legal.
- Sede: operación inicial.
- Plan: cards Esencial, Negocio, Empresa y Corporativo; “Ver detalles” abre modal accesible con tags de POS, facturación, contabilidad, nómina, soporte y capacidades.
- Adicionales: controles de cantidad y subtotal por concepto.
- Administrador: correo de invitación.
- Pago: selector anual/mensual con anual predeterminado; ahorro anual del 15 % destacado en porcentaje y COP, comparación transparente, resumen, impuestos, Wompi y estado; exención solo con permiso.

El borrador sobrevive recargas. El tenant ve capacidad propia y compra más; plataforma puede ver todas las sedes y editar contratos.

## Persistencia mínima

- borradores versionados de aprovisionamiento;
- precios versionados de plan y adicionales;
- cotizaciones y líneas inmutables;
- intentos de pago con referencia/transacción únicas y versión Wompi;
- suscripción de tenant;
- ajustes de entitlement con origen y vigencia;
- ledger de consumo idempotente.

Todos los índices y consultas posteriores al alta incluyen `TenantId`; acceso transversal exige permiso de plataforma.

## Rollout y evidencia

El despliegue es incremental con feature flag: esquema/catálogos, cotización, sandbox de plataforma, wizard pagado en dev, producción, migración de límites actuales, luego cupos vendedor/nómina/DIAN y alertas. El rollback desactiva el flag y conserva pagos, cotizaciones y ledger.

Pruebas obligatorias: cálculo servidor y manipulación rechazada; anual predeterminado, fórmula del 15 %, ahorro mostrado y total Wompi exacto; cambio mensual/anual y redondeos; renovación mensual frente a aniversario anual sin cobros intermedios; cupos mensuales reiniciados dentro de un anual sin factura adicional; factura anual única y factura mensual por pago; recaudo manual y Wompi convergen en el mismo settlement; permiso de plataforma, referencia manual duplicada, importe parcial y cobro ya pagado rechazados; concurrencia entre webhook y confirmación manual produce un pago aplicado, una suscripción y una factura; anulación no edita estados directamente; paquetes DIAN distintos de múltiplos de 1.000 rechazados; webhook/poller concurrentes; retorno sin aprovisionar; importe/moneda/referencia/cuenta incorrectos; exención; último cupo concurrente en cada recurso; factura, soporte y nómina bloqueados sin saldo; concesiones offline sin sobreasignación y bloqueo persistente al reconectar; reintento fiscal idempotente; aislamiento; avisos deduplicados; ampliación posterior al pago; rol vendedor limitado; recuperación de contraseña de un solo uso; landing/wizard en escritorio, teléfono y movimiento reducido.
