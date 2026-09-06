# Decisión: contabilidad mínima colombiana, impuestos e información exógena

**Estado:** vigente y obligatoria  
**Fecha:** 31 de julio de 2026  
**Alcance:** Auraly Commerce

## 1. Prevalencia

Esta decisión reemplaza las exclusiones anteriores de contabilidad general y exógena. No afirma que Auraly ya cumpla estos alcances: define lo que debe implementarse y probarse antes de ofrecerlos.

## 2. Resultado esperado

Auraly incorporará una contabilidad operacional colombiana integrada, de partida doble, con libros, estados básicos, centros de costos, impuestos, retenciones y preparación versionada de información exógena.

No se declarará cumplimiento únicamente porque existan asientos o archivos exportados.

## 3. Marco y fuentes oficiales

Fuentes verificadas el 31 de julio de 2026:

- Código de Comercio, artículos 48 a 60: https://www.funcionpublica.gov.co/eva/gestornormativo/norma.php?1425=&i=41102
- Decreto Único 2420 de 2015 y modificaciones: https://www.suin-juriscol.gov.co/clp/contenidos.dll/Decretos/30030273
- Resolución Única DIAN 227 de 2025: https://normograma.dian.gov.co/dian/compilacion/docs/resolucion_dian_0227_2025.htm
- Normatividad de información exógena por año: https://www.dian.gov.co/impuestos/sociedades/ExogenaTributaria/Normatividad/Paginas/default.aspx
- Presentación y formatos de exógena: https://www.dian.gov.co/impuestos/sociedades/ExogenaTributaria/Presentacion/Paginas/default.aspx
- Conciliación fiscal: https://www.dian.gov.co/fizcalizacioncontrol/herramienconsulta/NIIF/Conciliacion_Fiscal/Paginas/default.aspx
- Anexo técnico de factura electrónica versión 1.9: https://www.dian.gov.co/impuestos/factura-electronica/Documents/Anexo-Tecnico-Factura-Electronica-de-Venta-vr-1-9.pdf

Las reglas tributarias, anexos y formatos son versionados por vigencia. La implementación debe registrar versión, resolución, fecha de consulta y hash de cada artefacto utilizado.

## 4. Propiedad y dimensiones

En el modelo vigente:

- `Tenant` es la entidad legal y dueña de libros, periodos y estados financieros;
- `Business` es una sede o establecimiento que origina movimientos;
- `CostCenter` es una dimensión analítica adicional;
- `Party` es el tercero común a cliente, proveedor, empleado, vendedor o transportador.

El plan y los periodos pertenecen al tenant. Cada línea conserva el negocio, tercero y centro aplicables.

## 5. Módulos

```text
Auraly.Commerce.Accounting.Domain
Auraly.Commerce.Accounting.Application
Auraly.Commerce.Accounting.Infrastructure
Auraly.Commerce.Accounting.Contracts

Auraly.Commerce.Tax.Domain
Auraly.Commerce.Tax.Application
Auraly.Commerce.Tax.Infrastructure

Auraly.Commerce.ComplianceReporting.Application
Auraly.Commerce.ComplianceReporting.Infrastructure
```

`DocumentProcessing` coordina. `Accounting` decide cuentas y asientos. `Tax` decide impuestos y retenciones. `ComplianceReporting` genera reportes regulatorios versionados.

## 6. Núcleo contable mínimo

Debe cubrir:

- plan de cuentas configurable;
- categorías contables semánticas;
- comprobantes y secuencias;
- asientos de partida doble;
- terceros;
- centros de costos;
- periodos, cierres y reaperturas autorizadas;
- saldos iniciales;
- notas manuales con aprobación;
- reversiones y reclasificaciones;
- soportes y trazabilidad;
- moneda funcional y moneda de transacción;
- contabilización automática de documentos;
- libros y estados financieros básicos.

Un asiento contabilizado es inmutable. Los errores se corrigen mediante reversión o nuevo comprobante.

## 7. Plan de cuentas y reglas

Auraly ofrecerá plantillas colombianas, pero no codificará un PUC rígido. Cada empresa mapea categorías como `Cash`, `Bank`, `AccountsReceivable`, `AccountsPayable`, `Inventory`, `CostOfGoodsSold`, `SalesRevenue`, `SalesReturns`, `OutputVat`, `InputVat`, `WithholdingPayable` y `DamagedInventoryExpense` a sus cuentas reales.

Las reglas se versionan por vigencia y pueden depender de tipo documental, categoría de producto, impuesto, medio de pago, tercero y negocio.

## 8. Centros de costos

`CostCenters` usa UUIDv7, pertenece a un `BusinessId`, tiene código único, nombre, jerarquía opcional, vigencia y estado.

La resolución inicial es:

1. centro explícito autorizado;
2. centro de la caja;
3. centro de la regla operacional;
4. centro predeterminado del negocio.

El cajero no lo selecciona normalmente. Cambiar el maestro no reescribe historia. Reclasificar un movimiento contabilizado crea un asiento auditado.

## 9. Contabilización automática

Cada documento produce un evento contable canónico. La transacción operacional crea obligatoriamente el trabajo contable durable con hash y versión de reglas.

Documentos iniciales:

- factura de venta;
- nota crédito;
- nota débito;
- entrada y factura de proveedor;
- devolución de compra;
- recibo de caja y pago;
- movimientos de inventario;
- avería, conteo, traslado y conversión;
- diferencias de arqueo.

El asiento usa snapshots y costos reconocidos; nunca consulta precios, impuestos o costos actuales para reescribir un documento histórico.

## 10. Impuestos y retenciones

El núcleo contempla definiciones y vigencias para:

- IVA generado y descontable;
- IVA mayor valor del costo o gasto;
- devoluciones de IVA;
- impuesto nacional al consumo;
- impuestos saludables cuando apliquen;
- retención en la fuente practicada y sufrida;
- ReteIVA;
- ICA y ReteICA por jurisdicción;
- autorretenciones;
- timbre cuando corresponda;
- certificados de retención.

Responsabilidades, bases, cuantías y tarifas son configurables y versionadas. No se codifican como constantes eternas.

En compras, la responsabilidad tributaria oficial `O-15` identifica al proveedor
autorretenedor de renta. Una regla `IncomeTax` no se aplica a ese proveedor,
independientemente de su base mínima; la exclusión se decide antes de calcular la
cuantía. Esta condición no elimina ReteIVA ni ReteICA y no cambia las retenciones
sufridas en ventas. Los documentos ya contabilizados permanecen inmutables y se
corrigen mediante un comprobante posterior cuando corresponda.

## 11. Informes mínimos

Contables:

- balance de prueba;
- diario;
- mayor y balances;
- auxiliar por cuenta;
- auxiliar por tercero;
- auxiliar por centro de costos;
- comprobantes;
- estado de situación financiera;
- estado de resultados;
- cambios en patrimonio;
- flujo de efectivo mediante mapeo;
- movimientos sin contabilizar y excepciones.

Fiscales:

- IVA por tarifa y tratamiento;
- INC y otros impuestos soportados;
- retenciones practicadas y sufridas;
- ICA/ReteICA por municipio;
- certificados;
- conciliación XML DIAN contra documentos y contabilidad;
- borradores conciliables de formularios 300, 350 y 310;
- bases para conciliación fiscal 2516/2517.

Los borradores no equivalen a presentación automática ni sustituyen la revisión del contador responsable.

## 12. Información exógena

La exógena se genera desde contabilidad contabilizada, terceros, impuestos y cartera. No desde consultas aisladas a facturas.

El motor regulatorio usa definiciones por autoridad, año, formato, versión y resolución. Mantiene mapeos cuenta-concepto, validaciones, cuantías menores, ejecuciones, errores y artefactos exportados.

Primera cobertura prevista:

- 1001;
- 1003;
- 1005;
- 1006;
- 1007;
- 1008;
- 1009.

Luego se incorporan 1010, 1011, 1012, 1647 y formatos aplicables según tipo de contribuyente. El 2276 depende de Nómina y no se simula mientras ese módulo no exista.

La exógena distrital y municipal usa paquetes por jurisdicción. No existe una plantilla universal que pueda afirmarse válida para todas las ciudades.

## 13. Calidad de terceros

`PartyTaxProfile` conserva tipo y número de identificación, DV, naturaleza, nombres o razón social, país, departamento, municipio, dirección y responsabilidades.

Estos datos no se introducen innecesariamente en el snapshot fiscal de cada venta. La contabilización conserva la identidad histórica necesaria y la exógena permite correcciones auditadas de preparación sin alterar documentos originales.

## 14. Alcance incremental

La primera rebanada contable implementa motor durable, plan, periodos, centros, asientos y contabilización de factura, nota crédito y nota débito, con balance de prueba y auxiliares.

Compras, entradas, CxP, impuestos y retenciones deben completarse antes de afirmar que la exógena es confiable. Bancos y conciliación preceden al cierre contable. Los formatos regulatorios se implementan después de que sus fuentes operativas estén conciliadas.

## 15. Fuera del alcance inicial

- renta completa;
- impuesto diferido automático;
- consolidación de grupos empresariales;
- nómina contable completa;
- activos fijos y depreciación automática;
- presentación automática de declaraciones;
- todos los reportes municipales del país;
- regímenes contables de sectores vigilados especializados.

## 16. Puerta de aceptación

No se ofrece Auraly como sistema contable hasta demostrar:

- cuadre débito/crédito;
- idempotencia y reversiones;
- correspondencia documento-comprobante-soporte;
- cierre de periodos;
- conciliación inventario-contabilidad;
- conciliación caja/cartera-contabilidad;
- factura y notas contabilizadas;
- libros y estados reproducibles;
- terceros y centros trazables;
- SQL Server real, concurrencia y recuperación;
- exportaciones regulatorias validadas contra la versión oficial aplicable.
