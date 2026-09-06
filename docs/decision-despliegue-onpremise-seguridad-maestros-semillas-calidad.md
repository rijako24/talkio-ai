# Decisión: despliegue on-premise, seguridad, maestros, semillas y calidad

**Estado:** decisión vigente y parte obligatoria del MVP de Auraly Commerce  
**Fecha:** 24 de julio de 2026  
**Fuentes verificadas:** Auraly, Xion WinForms, Motor de Xion, Pedidos OK y Xion Web  
**Prevalencia:** este documento complementa el diseño consolidado y reemplaza cualquier supuesto de que Auraly Commerce solo se desplegará en Azure o que los permisos se resolverán únicamente en la interfaz.

---

## 1. Decisión ejecutiva

Auraly Commerce debe soportar dos modalidades con el mismo código funcional:

1. **Auraly Cloud**, operado por Auraly sobre Azure.
2. **Auraly On-Premise**, instalado en la infraestructura del cliente.

No se construirán dos productos ni dos ramas permanentes. El dominio, los casos de uso, la API, el portal web, el motor de documentos y el proyecto SQL serán los mismos. Las diferencias de infraestructura se resolverán mediante adaptadores y perfiles de despliegue.

El perfil on-premise certificado inicial será:

- Windows Server;
- IIS para la API y el portal web;
- `Auraly.Worker` como Windows Service para el motor y los procesos en segundo plano;
- SQL Server;
- almacenamiento local protegido para XML, PDF y demás artefactos;
- SignalR autohospedado para notificaciones en tiempo real;
- conexión saliente a Internet para DIAN, correo y servicios externos autorizados.

Docker Compose puede ofrecerse posteriormente como segundo perfil certificado. Soportar simultáneamente muchas combinaciones desde el MVP elevaría innecesariamente el costo de instalación, pruebas y soporte.

También entran al MVP:

- usuarios;
- perfiles como plantillas de permisos;
- permisos efectivos por usuario y por alcance;
- empleados;
- vendedores;
- transportadores;
- proveedores;
- maestro común de personas y empresas;
- catálogos geográficos, fiscales y operativos;
- pruebas automatizadas y evidencia de aceptación por cada módulo.

Un módulo no se considerará terminado por compilar o mostrar una pantalla. Solo se habilitará cuando sus flujos, efectos contables y operativos, permisos, errores, concurrencia y persistencia hayan pasado las pruebas definidas en este documento.

---

## 2. Hallazgos heredados que se conservan

La revisión de Xion confirmó capacidades importantes:

- `Persona` concentra identificación, nombres o razón social, contacto, dirección, ubicación y clasificación fiscal.
- Una misma persona puede ser cliente, proveedor, empleado, usuario, vendedor o conductor.
- `Usuario` tiene perfil, estado, vigencia y capacidad de autorizar.
- Los permisos se asignan a menú, submenú y acción.
- Existen permisos adicionales por sucursal y por bodega.
- El cambio de permisos deja auditoría.
- Los perfiles sirven como configuración inicial que luego puede especializarse por usuario.
- Proveedor incluye días de crédito, código del proveedor, tipos de suministro y reglas de recepción.
- Vendedor, empleado y transportador tienen ciclos y estados distintos.

Estas capacidades se absorben como reglas, pero no se copia el modelo literalmente:

- no se usarán enteros pequeños consecutivos como identificadores globales;
- no se almacenarán contraseñas del modo heredado;
- no se mezclarán autenticación, persona, empleado y vendedor;
- no se usarán booleanos `EsCliente`, `EsProveedor`, etc. como fuente de verdad;
- no se confiará en ocultar botones como mecanismo de seguridad;
- no se replicarán tablas locales y de servidor por cada entidad.

---

## 3. Arquitectura portable

### 3.1 Principio

Ningún módulo de dominio o aplicación puede depender directamente de Azure, IIS, el sistema de archivos, SignalR, Web PubSub o SQL Server.

```text
Dominio -> ninguna infraestructura
Application -> interfaces/puertos
Infrastructure -> implementaciones Cloud u On-Premise
Hosts -> composición según DeploymentMode
```

Configuración base:

```text
DeploymentMode = Cloud | OnPremise
```

El host selecciona implementaciones al arrancar. Este valor no debe llenar el negocio de condicionales.

### 3.2 Equivalencias

| Capacidad | Auraly Cloud | Auraly On-Premise |
|---|---|---|
| API y portal | Azure App Service o Container Apps | IIS |
| Base de datos | Azure SQL | SQL Server |
| Motor de documentos | Azure Functions/Worker | `Auraly.Worker` como Windows Service |
| Trabajos durables | Service Bus/cola administrada | outbox SQL + Worker |
| Tiempo real POS | Azure Web PubSub | SignalR en la API |
| Archivos fiscales | Azure Blob Storage | almacén local cifrado y respaldado |
| Secretos | Azure Key Vault | almacén de certificados de Windows y configuración cifrada |
| Telemetría | Application Insights/OpenTelemetry | OpenTelemetry + logs estructurados |
| Salud | Azure Monitor + endpoints | endpoints, visor local y paquete de diagnóstico |

Para el MVP on-premise no se introducen RabbitMQ, Redis, Kubernetes ni MinIO salvo necesidad demostrada. Una outbox SQL durable, un Worker y almacenamiento abstraído reducen piezas operativas.

### 3.3 Topología inicial

```text
Navegadores/POS
      |
   HTTPS/WSS
      |
IIS: Auraly.Api + Auraly.Web
      |
      +---- SQL Server
      +---- almacenamiento fiscal protegido
      |
Auraly.Worker (Windows Service)
      |
      +---- DIAN / correo / integraciones por salida HTTPS
```

La caja usa la misma estrategia offline:

- catálogo, precios efectivos, canales, códigos y configuración en almacenamiento local;
- sin inventario local;
- pedidos consultados en línea;
- outbox local para ventas permitidas offline;
- sincronización inicial automática y deltas posteriores.

La política de negativos pertenece a la bodega:

```text
CashRegister.DefaultWarehouseId -> Warehouse.Id
Warehouse.AllowNegativeStockSales
```

Todas las cajas de una misma bodega heredan la política. No pueden sobrescribirla. Si la bodega bloquea negativos, el POS consulta disponibilidad en línea al agregar el producto o cambiar la cantidad, y el motor vuelve a validar dentro de la transacción final. Sin red no puede completarse esa captura porque la caja no almacena inventario.

En on-premise la caja se conecta a la URL local. Si cae Internet pero sigue disponible la LAN, ventas, inventario, caja y documentos internos continúan; la transmisión DIAN queda en cola conforme a las reglas fiscales y de contingencia.

### 3.4 Dependencias externas

Estas capacidades requieren conectividad saliente:

- transmisión y consulta ante DIAN;
- correo;
- pasarelas o integraciones externas;
- descarga de actualizaciones;
- activación o verificación de licencia, si se adopta.

Una falla temporal no detiene el núcleo operativo. Cada envío tendrá:

- outbox durable;
- idempotencia;
- reintentos con espera creciente;
- estado visible;
- último error;
- reanudación manual autorizada;
- conciliación;
- trazabilidad de solicitud y respuesta.

No se abrirán puertos entrantes desde Auraly Cloud hacia el servidor del cliente como requisito normal.

### 3.5 Instalación y actualización

El instalador on-premise debe:

1. verificar Windows Server, IIS, .NET y SQL Server;
2. validar DNS, certificado TLS y conectividad;
3. crear identidades de servicio con mínimo privilegio;
4. instalar API, portal y Worker;
5. publicar el DACPAC de `Auraly.Database.sqlproj`;
6. cargar semillas globales y valores iniciales autorizados;
7. configurar almacenamiento, respaldos y secretos;
8. ejecutar pruebas de salud;
9. generar un informe sin exponer secretos.

Una actualización debe:

1. comprobar compatibilidad;
2. respaldar base de datos y configuración;
3. detener ordenadamente el Worker;
4. aplicar el DACPAC con informe previo;
5. desplegar binarios versionados;
6. ejecutar smoke tests;
7. reanudar procesos pendientes.

Para cambios destructivos se aplicará expansión/contracción: agregar, migrar y retirar estructuras únicamente en una versión posterior.

### 3.6 Operación y soporte

Cada instalación tendrá:

- identificador y versión;
- versión de base de datos;
- panel de salud;
- estado de DIAN, Worker, almacenamiento y SQL;
- métricas de colas y documentos pendientes;
- logs por `CorrelationId`, documento, negocio y caja;
- paquete de soporte sanitizado;
- política de respaldo y prueba de restauración;
- alertas de vencimiento de TLS y certificados DIAN;
- reloj sincronizado;
- guía de puertos, antivirus y firewall;
- ventana de versiones soportadas.

---

## 4. Separación modular

Se agregan:

```text
Auraly.Domain.Identity
Auraly.Application.Identity
Auraly.Infrastructure.Identity
Auraly.Contracts.Identity

Auraly.Domain.Authorization
Auraly.Application.Authorization
Auraly.Infrastructure.Authorization
Auraly.Contracts.Authorization

Auraly.Domain.Parties
Auraly.Application.Parties
Auraly.Infrastructure.Parties
Auraly.Contracts.Parties

Auraly.Domain.ReferenceData
Auraly.Application.ReferenceData
Auraly.Infrastructure.ReferenceData
Auraly.Contracts.ReferenceData
```

Adaptadores:

```text
Auraly.Infrastructure.Hosting.Cloud
Auraly.Infrastructure.Hosting.OnPremise
Auraly.Api
Auraly.Worker
```

Todo se ejecuta inicialmente en la misma API y base SQL, esquema `dbo`. La separación es por ensamblados, contratos, ownership y dependencias, no por bases ni esquemas.

Las modificaciones de base continúan exclusivamente mediante:

```text
database/Auraly.Database/Auraly.Database.sqlproj
```

No se agregan migraciones de Entity Framework.

---

## 5. Identidad y usuarios

### 5.1 Conceptos separados

Un usuario es una identidad que inicia sesión. No es sinónimo de empleado.

```text
UserAccount --0..1--> Party
Employee    ----1--> Party
Seller      ----1--> Party
Carrier     ----1--> Party
Supplier    ----1--> Party
```

Ejemplos:

- un contador externo puede tener usuario sin ser empleado;
- un empleado de bodega puede no necesitar usuario;
- un vendedor puede ser empleado, contratista o usuario de Pedidos;
- un transportador puede ser empresa o persona y no necesita acceso;
- un proveedor no recibe acceso por ser proveedor.

### 5.2 Capacidades mínimas

- crear e invitar usuario;
- activar, desactivar y bloquear;
- restablecer credenciales;
- forzar cambio de contraseña;
- gestionar vencimiento temporal;
- cerrar y revocar sesiones;
- asociar negocios, sucursales, bodegas y cajas permitidas;
- asignar perfiles;
- aplicar excepciones por usuario;
- identificar quién puede autorizar;
- consultar historial;
- impedir que el último administrador elimine su propio acceso crítico;
- refrescar permisos al cambiar su configuración.

Las credenciales usarán un proveedor de identidad o hash moderno. Nunca se migrarán contraseñas reversibles de Xion; se crearán cuentas y se exigirá una contraseña nueva.

Las contraseñas de acceso nunca son consultables ni recuperables, incluso para un
administrador: solo se conserva un verificador criptográfico. Cualquier usuario
autenticado puede cambiar su propia contraseña al confirmar la actual. Quien tenga
`users.update` puede restablecer la contraseña de otro usuario. En un tenant cliente
ese permiso queda limitado a la propia organización. Para un usuario del tenant
canónico `@auraly`, el contexto de ejecución aplica los permisos de la vista Usuarios
al tenant seleccionado cuando tiene acceso a la vista Tenants mediante `tenants.read`;
los endpoints operan únicamente sobre ese contexto ya validado y no existe una segunda
familia `tenants.users.*`. La operación administrativa revoca los tokens de renovación
y publica la invalidación de seguridad del usuario afectado.

---

## 6. Autorización y permisos

### 6.1 Modelo

```text
permisos de perfiles
+ concesiones directas al usuario
- denegaciones directas al usuario
limitados por alcance y estado
```

Una denegación explícita prevalece. Los perfiles son plantillas, no la única fuente.

Entidades conceptuales:

```text
Users
Profiles
UserProfiles
Permissions
ProfilePermissions
UserPermissionOverrides
UserBusinessScopes
UserBranchScopes
UserWarehouseScopes
UserCashRegisterScopes
AuthorizationAudit
```

### 6.2 Claves estables

Las acciones se identifican por claves, no por texto del menú:

```text
sales.invoices.view
sales.invoices.create
sales.invoices.confirm
sales.invoices.discount
sales.invoices.change-price
sales.invoices.remove-line
sales.invoices.cancel-draft
sales.invoices.void
sales.invoices.reprint
sales.orders.view
sales.orders.recover
sales.orders.invoice
inventory.stock.view
inventory.entries.create
inventory.entries.confirm
inventory.transfers.create
inventory.transfers.receive
inventory.damages.create
cash.sessions.open
cash.sessions.count
cash.sessions.approve-difference
security.users.manage
security.permissions.manage
```

Cada módulo es dueño de sus claves. El menú las consume, pero no las define.

### 6.3 Alcances

Un permiso puede aplicar a:

- empresa o negocio;
- sucursal;
- bodega;
- caja.

Los filtros se aplican en el servidor. Nunca se trae toda la información para ocultar filas en el navegador.

### 6.4 Experiencia y seguridad

- sin permiso de vista, el módulo no aparece en el menú;
- con acceso a la pantalla pero sin acción, el control aparece deshabilitado con explicación;
- una acción sensible puede ocultarse si revelar su existencia expone información;
- una URL directa sin permiso responde `403`;
- una consulta fuera del alcance no devuelve datos;
- comandos y consultas validan permiso y alcance;
- el motor revalida antes de ejecutar un documento definitivo;
- toda autorización excepcional identifica solicitante y aprobador.

Deshabilitar un botón mejora la experiencia, pero la API siempre es la autoridad.

### 6.5 Cambios y offline

Los cambios incrementan una versión de autorización y se notifican a las sesiones afectadas.

Una caja offline conserva una instantánea firmada con:

- usuario;
- caja;
- negocio;
- permisos offline;
- versión;
- emisión;
- vencimiento corto.

Requieren conexión:

- administrar usuarios o permisos;
- anular documentos definitivos;
- aprobar diferencias de arqueo;
- cambiar precios fuera del límite;
- autorizar excepciones;
- modificar parámetros fiscales.

### 6.6 Vista de administración

Permitirá:

- crear, clonar y desactivar perfiles;
- seleccionar usuario y aplicar perfiles;
- ver el permiso efectivo y su origen;
- otorgar o negar excepciones;
- asignar alcances;
- comparar usuarios;
- buscar permisos;
- guardar con resumen;
- auditar antes y después;
- previsualizar menú y acciones.

La matriz se presenta por vista: acceso y acciones aparecen bajo la pantalla donde
se ejecutan. No existe una categoría funcional llamada “permisos transaccionales”;
“transaccional” describe garantías técnicas de una operación, no un lugar al cual
asignar autorizaciones.

No se copia la grilla WinForms, pero se conserva su granularidad útil.

---

## 7. Personas, empresas y roles

### 7.1 Party común

`Party` representa persona natural u organización:

- `PartyId` nuevo;
- tipo de persona;
- tipo y número de identificación;
- dígito de verificación;
- nombres y apellidos o razón social;
- nombre comercial y representante;
- correo y teléfonos;
- direcciones;
- país, departamento, municipio/ciudad y barrio;
- responsabilidades y datos fiscales DIAN;
- datos personales solo cuando el rol los necesite;
- estado y auditoría.

Direcciones y contactos adicionales serán colecciones, no columnas `Telefono1` a `Telefono4`.

### 7.2 Proveedores

- código interno y código/EAN;
- días y condiciones de crédito;
- contacto comercial;
- recepción con o sin documento previo si aplica;
- tipos de suministro relevantes;
- cuentas bancarias protegidas si se necesitan;
- estado;
- productos y costos;
- CxP por referencia, separada del maestro.

Concesión, activos fijos y liquidaciones especializadas quedan fuera si el piloto no los usa.

### 7.3 Empleados

- código;
- cargo;
- fecha de ingreso;
- estado;
- empresa/sucursal;
- usuario opcional.

Nómina, salario y seguridad social quedan fuera del MVP salvo regla comercial comprobada.

### 7.4 Vendedores

- código;
- fecha de ingreso;
- estado;
- empresas y sucursales;
- usuario opcional;
- vendedor predeterminado por caja si aplica;
- atribución en pedidos y ventas.

Rutas, metas, portafolios y comisiones avanzadas permanecen fuera.

### 7.5 Transportadores

El nombre web será **Transportadores**, aunque Xion use `Conductor`.

- persona natural o empresa;
- contacto e identificación;
- estado;
- vehículo y placa opcionales;
- relación con entradas, entregas o traslados;
- usuario opcional.

No se construye un TMS ni gestión avanzada de flota.

### 7.6 Migración

```text
LegacyEntityMap
LegacyPartyId -> PartyId
LegacyUserId  -> UserId
```

Se normaliza identificación y se detectan duplicados. Una persona que era proveedor y transportador se migra una vez con dos roles.

No se migran automáticamente:

- contraseñas;
- campos sin uso;
- dependencias de nómina;
- rutas, metas o comisiones excluidas;
- duplicados o inválidos sin informe de excepción.

---

## 8. Datos semilla

### 8.1 Globales

- países;
- departamentos;
- municipios o ciudades;
- tipos de identificación;
- tipos de persona;
- códigos y responsabilidades fiscales DIAN;
- unidades de medida;
- monedas;
- impuestos y códigos fiscales base;
- medios y formas de pago;
- permisos;
- menús y módulos;
- tipos de documento comercial.

Se usarán códigos oficiales cuando existan.

### 8.2 Barrios

Como no existe un catálogo completo y estable para todos:

- se entrega base inicial en ciudades soportadas;
- el negocio puede crear y corregir;
- una actualización no sobrescribe datos del cliente;
- es opcional salvo flujo concreto.

### 8.3 Operativos por negocio

- motivos de ajuste;
- motivos de avería;
- motivos de devolución;
- motivos de entrada/salida de caja;
- tipos de bodega;
- perfiles iniciales;
- parámetros iniciales de caja.

Los estados que pertenecen al código no se vuelven tablas editables sin necesidad.

### 8.4 Reglas

Toda semilla será:

- idempotente;
- versionada;
- trazable;
- segura al repetirse;
- separada entre global y tenant;
- compatible con base limpia y actualización;
- respetuosa de personalizaciones.

Se aplica mediante `Post-Deployment` de `Auraly.Database.sqlproj`, con códigos oficiales o IDs determinísticos.

---

## 9. Pruebas obligatorias

### 9.1 Garantía realista

No es serio prometer ausencia absoluta de defectos. Sí se garantiza un proceso verificable: ninguna capacidad se marca terminada ni se habilita sin pruebas automatizadas, integradas y aceptación.

La migración se mide por comportamientos comprobados, no por archivos trasladados.

### 9.2 Trazabilidad

| ID | Módulo | Fuente | Regla | Caso nuevo | Prueba | Estado |
|---|---|---|---|---|---|---|
| POS-001 | Facturación | Xion Factura | lector deja lista la siguiente captura | `CaptureProduct` | E2E | Pendiente/Aprobada |

Una fila sin prueba aprobada no cuenta como migrada.

### 9.3 Capas

1. dominio: invariantes, estados y cálculos;
2. aplicación: comandos, consultas, permisos e idempotencia;
3. SQL real: transacciones, concurrencia, índices y DACPAC;
4. contrato API;
5. componentes web: grillas, teclado, deshabilitados y recálculo;
6. E2E: navegador, API, SQL y Worker;
7. seguridad: permisos, alcance y elevación;
8. despliegue Cloud y On-Premise;
9. rendimiento;
10. migración y conciliación con Xion.

Facturación electrónica agrega XML dorados, firma, CUFE/QR, notas crédito, sandbox DIAN, respuestas, errores, reintentos e idempotencia.

Offline agrega caída de red, duplicados, reinicio, deltas, conflictos, outbox, permisos expirados y reconciliación.

### 9.4 Flujos críticos

#### Facturación POS

- lectura continua;
- búsquedas múltiples;
- balanza;
- cantidad y recálculo;
- descuentos;
- eliminar línea y cancelar venta;
- guardar/recuperar temporal;
- pagos simples y mixtos;
- crédito/CxC;
- canal de precio y promociones;
- recuperar pedido;
- offline e idempotencia;
- política de negativos por bodega;
- validación online al capturar cuando la bodega bloquea negativos;
- revalidación transaccional en el motor;
- impresión y factura electrónica.

#### Compras e inventario

- entrada y CxP;
- traslado y recepción;
- conteo, reconteo y ajuste;
- avería;
- devoluciones;
- kardex y costo;
- concurrencia.

#### Caja

- apertura;
- entradas y salidas;
- ventas por medio;
- recuperación;
- arqueo;
- diferencia con/sin autorización;
- cierre.

#### Usuarios y permisos

- menú oculto;
- acción deshabilitada;
- API rechaza acceso directo;
- datos fuera de alcance invisibles;
- alcances por negocio, sucursal, bodega y caja;
- perfiles, excepciones y denegación prevalente;
- revocación;
- snapshot offline expirado;
- auditoría.

### 9.5 Base de datos

CI prueba:

- DACPAC sobre base vacía;
- semillas repetidas;
- actualización desde versión soportada;
- ausencia de pérdida inesperada;
- constraints e índices;
- migración de datos;
- respaldo/restauración on-premise.

El motor se prueba contra SQL Server real. Los mocks no bastan para inventario, caja, cartera, consecutivos, outbox ni documentos.

### 9.6 Puertas de calidad

No se integra si falla:

- compilación y análisis estático;
- pruebas unitarias, integración y contrato;
- E2E críticos;
- matriz de permisos;
- publicación DACPAC;
- smoke Cloud;
- smoke On-Premise cuando corresponda;
- conciliación.

Se puede exigir 80 % de líneas y ramas por módulo nuevo, pero el porcentaje no reemplaza casos. Las invariantes críticas deben cubrir todos los escenarios conocidos.

Una función incompleta permanece detrás de una bandera y fuera del menú.

### 9.7 Definición de terminado

Un módulo está terminado cuando:

- reglas y exclusiones están documentadas;
- cumple aceptación y suite completa;
- aplica permisos en UI y API;
- produce auditoría;
- funciona en instalación limpia y actualización;
- funciona en Cloud y On-Premise o declara dependencia permitida;
- tiene monitoreo y errores operables;
- cuenta con migración/conciliación si reemplaza Xion;
- fue aprobado en UAT con datos y roles reales.

---

## 10. Alcance

### Incluido

- identidad y usuarios;
- perfiles y permisos efectivos por usuario;
- alcances por empresa, sucursal, bodega y caja;
- personas y empresas;
- empleados, vendedores y transportadores básicos;
- proveedores;
- datos geográficos, fiscales y operativos;
- instalador on-premise certificado;
- Worker local, SQL Server y SignalR local;
- actualización, observabilidad y respaldo;
- suites de prueba y evidencia.

### Fuera

- nómina y seguridad social;
- rutas, GPS, metas y comisiones avanzadas;
- flota avanzada;
- muchas distribuciones Linux/Kubernetes;
- alta disponibilidad on-premise automatizada;
- sincronización activa-activa Cloud/On-Premise;
- administración remota obligatoria desde Cloud.

---

## 11. Orden de implementación

1. `ReferenceData`, `Parties`, `Identity` y `Authorization`.
2. perfil on-premise mínimo: SQL, almacenamiento, Worker, SignalR y salud.
3. productos, canales de precio, bodega y caja.
4. inventario base, kardex y motor.
5. facturación POS, caja y arqueo.
6. pedidos.
7. compras, entradas y CxP.
8. CxC.
9. devoluciones y averías.
10. facturación electrónica propia.
11. reportes.
12. instalador, actualización, migración y piloto on-premise.

La autorización y las pruebas se incorporan desde el primer incremento.

---

## 12. Decisión final

Sí es viable instalar Auraly Commerce en servidores de clientes sin separar el producto ni duplicar código. La arquitectura correcta es un monolito modular portable con composición Cloud y On-Premise.

El perfil on-premise debe ser certificado, reproducible, actualizable, observable y probado. De lo contrario cada cliente se convertiría en una instalación artesanal.

Usuarios, permisos, empleados, vendedores, transportadores, proveedores y semillas son fundacionales. La condición de salida es innegociable: no se declara migrado un módulo hasta que su comportamiento esté trazado, probado y conciliado.
