# Criterios de diagnóstico y recomendación

> **Estado: PROPUESTA.** Los criterios marcados con `DEC-xx` no están aprobados por el propietario ni por el asesor. Son la base para implementar E6/E7 y se ajustarán según las respuestas registradas. Versión de reglas del contrato: `0.1-propuesta`.

## 1. Principios

1. Todo diagnóstico se basa en información pública de NuGet (RN-01, RN-03) y refleja el momento de la consulta (RN-06). No sustituye el criterio del desarrollador (RN-07).
2. **Nunca se inventa un dato.** Se distinguen tres situaciones por indicador (`disponibilidad`): `conocido` (incluye `false` y `0`), `no_determinado` (no se pudo obtener) y `no_evaluado` (esta versión no lo evalúa: AOT).
3. La evidencia positiva parcial vale, la negativa no: si hay **una** versión posterior compatible conocida, la dependencia está desactualizada aunque la selección sea incompleta; pero «está al día» exige selección completa.
4. Compatibilidad es **inferida** de `dependencyGroups`; no certifica assets ni ejecución.
5. Cada clasificación es reproducible: estado + hallazgos con motivo + evidencia + versión de reglas guardada en el snapshot.

## 2. Estados (RF-09)

| Estado | Significado | Texto para el usuario (propuesta) |
|---|---|---|
| `actualizada` | Hay evidencia completa y no se halló ningún hallazgo negativo | «Al día» |
| `desactualizada` | Existe una versión estable posterior **compatible** con todos los TFM evaluados | «Hay una versión más reciente» |
| `incompatible` | La versión declarada no declara compatibilidad con algún TFM evaluado | «No declara compatibilidad con tu framework» |
| `sin_soporte_activo` | NuGet marca el paquete como obsoleto (deprecado) | «Marcado como obsoleto en NuGet» |
| `no_determinada` | Falta evidencia decisiva y no hay hallazgo negativo conocido | «No se pudo determinar» |

**Limitación obligatoria del estado `sin_soporte_activo`:** la deprecación es evidencia *publicada* por el autor. Su ausencia no demuestra soporte activo y su presencia no demuestra, por sí sola, ausencia de correcciones de seguridad. La interfaz y el PDF deben mostrarlo así. (DEC-02)

## 3. Precedencia del estado principal (DEC-03, propuesta)

Cuando coexisten hallazgos gana el más grave:

`incompatible` > `sin_soporte_activo` > `desactualizada` > `actualizada`

- `no_determinada` **solo** se asigna cuando no existe ningún hallazgo negativo conocido y falta evidencia decisiva. Si hay un hallazgo negativo conocido, ese estado gana y los datos faltantes se listan como limitaciones en los motivos.
- **Todos** los hallazgos se conservan y se muestran, no solo el ganador.
- **Evidencia decisiva** (si falta alguna y no hay hallazgo negativo → `no_determinada`): versión declarada resuelta, fecha/versiones del paquete, última versión estable, selección de versiones completa, estado de deprecación y compatibilidad de cada TFM evaluado.
- **No decisivos** (su ausencia no cambia el estado; generan aviso informativo): licencia, descargas, antigüedad, AOT.

## 4. Reglas

| Regla | Entrada | Hallazgo |
|---|---|---|
| **Actualización** (DEC-01) | versión declarada, versiones estables listadas y su compatibilidad | `desactualizada` si existe una versión estable **listada** posterior a la declarada y compatible con los TFM evaluados. Si la más reciente exige un framework posterior, **no** se recomienda ni se cuenta; se informa «hay una versión más reciente, pero no es compatible con tu framework» |
| **Antigüedad** (separada) | meses desde la última publicación | Solo informativo: «sin publicaciones desde hace N meses». Umbral propuesto 24 meses. **No** cambia el estado: una biblioteca estable y antigua no está abandonada por su edad |
| **Soporte activo** | `deprecado`, razones, paquete alterno | `sin_soporte_activo` (grave) si NuGet lo marca deprecado; la ausencia produce solo evidencia «sin deprecación publicada» |
| **Framework** (DEC-10) | grupos de dependencias del paquete vs TFM | `incompatible` si el paquete no declara compatibilidad con un TFM evaluado; `no_determinada` si no hay grupos o el TFM no se interpreta |
| **Licencia** (DEC-13) | licencia | Se muestra tal cual. Si falta o es una URL, hallazgo informativo «licencia por revisar». Sin lista permitida ni rechazos inventados |
| **AOT** (DEC-04) | — | Siempre `no_evaluado`; hallazgo informativo fijo; no afecta estado ni parcialidad |

### Selección de versiones (DEC-11)

- Se separan: declarada, última estable, última estable compatible, prerelease y no listada. Las prerelease y las no listadas **no** cuentan como «versión posterior».
- La comparación de versiones usa reglas SemVer de NuGet, nunca orden de texto.
- La paginación del registro se amplía dentro de límites configurables; si no alcanza, `seleccionVersionesCompleta = false` y se avisa. Un máximo parcial **nunca** se presenta como global.
- Una versión declarada que no es exacta (`rango`, `flotante`, `propiedad`, `ausente`) **no se resuelve**: la regla de actualización queda `no_determinada` y los hallazgos de paquete (deprecación del paquete, existencia) se evalúan sobre la última versión estable con la nota correspondiente.

## 5. Varios TFM (DEC-08 y DEC-10, propuesta)

- Solo se evalúan TFM en alcance: `net8.0` y `net10.0` (con o sin sufijo de plataforma, que no se interpreta y se advierte). Otros se omiten con advertencia; si ninguno está en alcance se rechaza (422).
- `TargetFrameworks` se separa por `;`; un texto `net8.0;net10.0` nunca se evalúa como un solo TFM.
- Se guarda un estado **por TFM** (`evaluacionPorTfm`) y los indicadores de compatibilidad por TFM.
- **Agregación propuesta (pesimista):** el estado global de la dependencia es el más grave entre sus TFM, porque el proyecto debe compilar en todos. El detalle por TFM es siempre visible.
- Referencias con `Condition` no se evalúan: se advierten y se conservan. Versiones duplicadas conflictivas generan advertencia, no selección silenciosa.

## 6. Análisis parcial (DEC-03, propuesta)

Un análisis es `parcial` si, al menos, una de estas condiciones es cierta:

1. Alguna dependencia quedó `no_determinada` por falta de evidencia decisiva (NuGet no disponible sin copia, paquete inexistente, selección incompleta sin hallazgo positivo, versión no resuelta).
2. Se agotó el presupuesto global de 120 s y quedaron dependencias pendientes.
3. No se resolvió ningún TFM (`framework_no_resuelto`).

**No** vuelven parcial un análisis: AOT no evaluado, licencia o descargas ausentes, datos servidos desde caché vencida si el diagnóstico pudo completarse (se muestra advertencia y la fecha original), ni advertencias de versión/condición que no impiden diagnosticar.

Casos particulares:

| Caso | Resultado |
|---|---|
| Sin `PackageReference` | `completo`, 0 dependencias, advertencia `sin_paquetes` |
| Más de 100 dependencias únicas (DEC-09) | Se rechaza con 422; no se persiste |
| Un paquete inexistente | Solo esa dependencia queda `no_determinada`; análisis `parcial` |

## 7. Tabla de decisiones (entrada → estado → motivo)

| # | Situación | Estado | Motivo principal |
|---|---|---|---|
| 1 | Versión declarada = última estable compatible; sin deprecación; selección completa | `actualizada` | Sin hallazgos negativos |
| 2 | Existe estable posterior compatible | `desactualizada` | `version_posterior_compatible` |
| 3 | La estable posterior solo es para un framework mayor; la declarada es compatible | `actualizada` | Aviso: hay versión más reciente no compatible |
| 4 | La declarada no declara el TFM del proyecto | `incompatible` | `sin_grupo_para_tfm` |
| 5 | Deprecada y además hay versión posterior | `sin_soporte_activo` | Prevalece sobre desactualizada; ambos motivos visibles |
| 6 | Deprecada e incompatible | `incompatible` | Gana la más grave; la deprecación se lista |
| 7 | Antigua (60 meses) pero estable, al día y sin deprecación | `actualizada` | Aviso informativo de antigüedad |
| 8 | NuGet caído, sin copia | `no_determinada` | `sin_evidencia`; análisis parcial |
| 9 | NuGet caído, copia vencida con datos completos | según datos | Origen `cache_expirada` + fecha original + advertencia |
| 10 | Datos parciales con hallazgo negativo conocido (p. ej. deprecada, sin fecha) | `sin_soporte_activo` | El hallazgo conocido gana; el faltante se cita como limitación |
| 11 | Selección incompleta y hay una posterior compatible conocida | `desactualizada` | Evidencia positiva parcial |
| 12 | Selección incompleta y no hay posterior conocida | `no_determinada` | «Está al día» requiere selección completa |
| 13 | Versión declarada es un rango o una propiedad | `no_determinada` (salvo hallazgo negativo de paquete) | `version_no_resuelta` |
| 14 | Licencia ausente | sin cambio | Aviso informativo «licencia por revisar» |
| 15 | AOT | sin cambio | `no_evaluado` |
| 16 | Multi-TFM: incompatible en `net8.0`, actualizada en `net10.0` | `incompatible` | Agregación pesimista; detalle por TFM |
| 17 | Descargas = 0 conocido vs ausente | sin cambio | Se muestra «0» o «no determinado» según corresponda, nunca intercambiados |

Estos casos se convertirán en pruebas de tabla en E6.

## 8. Recomendaciones (para E7; propuesta)

1. Prioridad: actualización del mismo paquete cuando exista versión admisible compatible; marcar `posiblesCambiosDeCodigo` en cambios de versión mayor. No se garantiza una migración sin cambios.
2. Si hay deprecación con paquete alterno, se usa como candidato prioritario respetando rango de versión, compatibilidad y (cuando exista) política de licencias.
3. Hasta 5 candidatos de búsqueda, validación de hasta 3. Un candidato sin relación funcional demostrada se presenta como `candidato_para_revision`; si ninguno es justificable, la recomendación queda vacía con explicación.
4. Toda recomendación incluye tipo, paquete, versión, justificación, evidencia y limitaciones (RN-05). Ninguna se justifica solo por popularidad o descargas.

## 9. Parámetros configurables y versionado

| Parámetro | Valor inicial | Estado |
|---|---|---|
| Umbral informativo de antigüedad | 24 meses | propuesta |
| Límite de dependencias | 100 | propuesta |
| Presupuesto global de análisis | 120 s | documental |
| Vigencia de caché de metadatos | 24 h | documental |
| Retención de análisis / caché | 90 / 30 días | propuesta |

`versionReglas` se guarda con cada análisis para que una regla futura no reinterprete resultados antiguos.
