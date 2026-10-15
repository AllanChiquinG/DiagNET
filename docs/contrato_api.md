# Contrato de la API de DiagNET

Los tipos viven en `src/Compartido.Contratos` (proyecto compartido por servidor y cliente). JSON en `camelCase`; las enumeraciones viajan como texto `snake_case`; las fechas en ISO 8601 con zona. Los ejemplos de [docs/ejemplos](ejemplos) son **fixtures ilustrativos con paquetes inventados** (`Ejemplo.*`), validados por pruebas contra los tipos reales; no son datos de paquetes verdaderos.

## 1. Convenciones

- **Errores:** siempre Problem Details (RFC 9457, `application/problem+json`) con las extensiones `codigo` (estable; el cliente decide por él) y `referencia` (identificador para soporte). No existe un `ErrorDto` propio. Tipo: `ProblemaDto`.
- **Distinciones obligatorias:** `desconocido` ≠ `falso` y `nulo` ≠ `cero`. Cada indicador es un `IndicadorDto<T>` con `disponibilidad` = `conocido` | `no_determinado` | `no_evaluado`. `valor` solo existe si es `conocido`.
- **Estados:** `actualizada`, `desactualizada`, `incompatible`, `sin_soporte_activo`, `no_determinada`. Estado de análisis: `completo` | `parcial`. Origen: `nuget`, `cache_vigente`, `cache_expirada`, `no_determinado`.
- **Sin espera con progreso:** el análisis es un único `POST` síncrono; no hay contrato de seguimiento ni porcentaje (DEC-07, propuesta). La interfaz muestra una espera indeterminada.
- **`GET` nunca consulta NuGet ni recalcula.** Cada `POST` crea un análisis nuevo.
- **Rutas `/api/**` desconocidas** devuelven 404 Problem Details, nunca el `index.html` del cliente.
- **Límites:** archivo ≤ 1 MB; cuerpo HTTP ≤ 2 MB; máx. dependencias únicas 100 (propuesta); presupuesto de análisis 120 s.

## 2. Endpoints

| Método y ruta | Entrega | Éxito | Errores |
|---|---|---|---|
| `GET /api/salud` | E0 (hecho) | 200 `SaludDto` | — |
| `POST /api/proyectos/lectura` | E2 (temporal) | 200 `ProyectoLeidoDto` | 400, 413, 422 |
| `POST /api/analisis` | E8 | 201 `ResultadoAnalisisDto` + `Location: /api/analisis/{id}` | 400, 413, 422, 503, 500 |
| `GET /api/analisis/{id}` | E8 | 200 `ResultadoAnalisisDto` | 400, 404 |
| `GET /api/analisis` | E8 | 200 `PaginaDto<ResumenAnalisisDto>` | 400 |
| `GET /api/analisis/{id}/pdf` | E10 | 200 `application/pdf` | 400, 404, 500 |

### 2.1 `GET /api/salud`
`{ "estado": "ok", "servicio": "DiagNET", "version": "…", "fechaServidor": "…" }`. Sin detalles sensibles.

### 2.2 `POST /api/proyectos/lectura` (temporal, E2)
`multipart/form-data` con el campo `archivo`. No consulta NuGet ni persiste. Respuesta: [proyecto_leido.json](ejemplos/proyecto_leido.json). Contrato de transición: se mantiene hasta integrar E8; al integrar se decide si se conserva (decisión de integración, no tomada).

### 2.3 `POST /api/analisis` (E8)
`multipart/form-data`, campo `archivo`. Devuelve **201** con el resultado completo o parcial y `Location`.
Ejemplos: [completo](ejemplos/analisis_completo.json), [parcial](ejemplos/analisis_parcial.json), [sin paquetes](ejemplos/analisis_sin_paquetes.json), [varios TFM](ejemplos/analisis_multi_tfm.json).
Un resultado **parcial no es un error**: es 201 con `estado: "parcial"` y advertencias. Solo falla globalmente (503/500) si es imposible procesar o guardar.

### 2.4 `GET /api/analisis/{id}`
`id` es un UUID. Devuelve el snapshot guardado. Ausente o purgado: 404 `analisis_no_encontrado` ([ejemplo](ejemplos/problema_analisis_no_encontrado.json)).

### 2.5 `GET /api/analisis`
Parámetros: `pagina` (≥ 1, por defecto 1) y `tamano` (1–50, por defecto 20). Respuesta: [historial_pagina.json](ejemplos/historial_pagina.json).
**Visibilidad pendiente (DEC-06):** no se implementa un listado global con nombres de archivos de otras personas hasta que se decida. Propuesta recomendada: historial limitado al navegador mediante el parámetro `ids` (lista de UUID, máx. 50, devuelve solo esos); el contrato de respuesta es el mismo.

### 2.6 `GET /api/analisis/{id}/pdf`
Genera el PDF bajo demanda desde el snapshot. `Content-Type: application/pdf`. No persiste el PDF.

## 3. Catálogo de errores

| `codigo` | HTTP | Cuándo |
|---|---|---|
| `archivo_ausente` | 400 | No llegó el campo `archivo` |
| `extension_invalida` | 400 | La extensión no es `.csproj` |
| `archivo_vacio` | 400 | Archivo sin contenido |
| `archivo_excede_tamano` | 400 | Archivo > 1 MB |
| `cuerpo_excede_tamano` | 413 | Cuerpo HTTP > 2 MB |
| `xml_invalido` | 400 | XML mal formado o con DTD ([ejemplo](ejemplos/problema_xml_invalido.json)) |
| `proyecto_invalido` | 400 | XML válido sin elemento `Project` |
| `framework_fuera_de_alcance` | 422 | Ningún TFM es .NET 8 o .NET 10 ([ejemplo](ejemplos/problema_framework_fuera_de_alcance.json)) |
| `limite_dependencias_excedido` | 422 | Más dependencias únicas que el límite |
| `identificador_invalido` | 400 | El `id` no es un UUID |
| `paginacion_invalida` | 400 | `pagina`/`tamano` fuera de rango |
| `analisis_no_encontrado` | 404 | Análisis inexistente o purgado |
| `recurso_no_encontrado` | 404 | Ruta `/api` desconocida |
| `pdf_no_disponible` | 500 | Falló la generación del PDF; el resultado se conserva |
| `almacenamiento_no_disponible` | 503 | No se pudo guardar o leer |
| `error_interno` | 500 | Error no controlado (sin trazas ni detalles internos) |

Los errores de **entrada** (400/413/422) ocurren antes de cualquier consulta a NuGet y antes de persistir.

## 4. Advertencias (`AdvertenciaDto.codigo`)

`sin_paquetes`, `version_no_resuelta`, `condicion_no_evaluada`, `dependencia_duplicada_conflictiva`, `framework_no_resuelto`, `framework_fuera_de_alcance_omitido`, `sufijo_plataforma`, `cache_expirada`, `sin_datos`, `analisis_parcial`, `presupuesto_agotado`. El texto puede mejorar; el código es estable.

## 5. Casos de borde (respuesta inequívoca)

| Caso | Respuesta |
|---|---|
| Archivo válido con dependencias y datos completos | 201, `completo` |
| Datos faltantes o presupuesto agotado | 201, `parcial`, dependencias `no_determinada`, advertencias |
| Sin `PackageReference` | 201, `completo`, 0 dependencias, `sin_paquetes` |
| TFM ausente o por propiedad | 201, `parcial`, `framework_no_resuelto` (DEC-08) |
| TFM solo fuera de alcance | 422 `framework_fuera_de_alcance` |
| `net8.0;net10.0` | 201, evaluación por TFM y estado global agregado |
| Mezcla `net8.0;net48` | 201, `net48` omitido con advertencia |
| Archivo vacío / grande / no XML | 400 con su código; nada se consulta ni guarda |
| Paquete inexistente en NuGet | Solo esa dependencia `no_determinada`; análisis `parcial` |
| AOT | Siempre `no_evaluado`; no afecta `estado` |
