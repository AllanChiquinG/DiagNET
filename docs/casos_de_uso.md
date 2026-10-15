# Casos de uso de DiagNET (CU-01 a CU-08)

Especificación de los casos de uso preliminares con precondiciones, flujo principal, flujos alternos y resultado observable. Los identificadores y relaciones provienen del catálogo de actores y casos de uso. Este documento no cambia el alcance: la carga del `.csproj` es **manual** (RF-02, GitHub, está fuera de esta versión).

**Actores:** ACT-01 Desarrollador junior (único usuario, sin cuentas). ACT-02 API pública de NuGet (sistema externo consultado por el servidor).

**Convención de errores:** cada flujo alterno de error indica el `codigo` de Problem Details definido en [contrato_api.md](contrato_api.md). Los criterios marcados **(propuesta)** dependen de una decisión pendiente de confirmación; ver el registro de decisiones.

**Datos que nunca se conservan:** el contenido del `.csproj` se procesa en memoria y no se guarda (RNF-11). Solo se guardan nombres, versiones, indicadores y resultados.

---

## CU-01 Cargar archivo .csproj (incluye CU-02)

- **Actor:** ACT-01. **Prioridad:** Alta. **Requisitos:** RF-01, RF-03.
- **Precondiciones:** el usuario está en la pantalla de inicio y dispone de un archivo `.csproj` de un proyecto .NET 8 o .NET 10.
- **Flujo principal**
  1. El usuario selecciona o arrastra un archivo; la pantalla muestra su nombre y habilita *Analizar proyecto*.
  2. El usuario pulsa *Analizar proyecto*; la pantalla muestra una espera indeterminada accesible (sin porcentaje inventado).
  3. El servidor **valida** el archivo: presente, extensión `.csproj`, no vacío, hasta 1 MB, cuerpo HTTP de hasta 2 MB.
  4. El servidor lee el XML de forma segura (DTD prohibido, sin recursos externos) y ejecuta CU-02.
  5. Continúa con CU-03 (diagnóstico). Al terminar, se persiste el resultado y se muestra (CU-06).
- **Flujos alternos**
  - *A1 Archivo no seleccionado:* *Analizar proyecto* permanece deshabilitado.
  - *A2 Extensión distinta de `.csproj`:* 400 `extension_invalida`. No se consulta NuGet ni se guarda nada.
  - *A3 Archivo vacío:* 400 `archivo_vacio`.
  - *A4 Archivo mayor a 1 MB:* 400 `archivo_excede_tamano`. Si el cuerpo supera 2 MB: 413 `cuerpo_excede_tamano`.
  - *A5 XML mal formado o con DTD:* 400 `xml_invalido`.
  - *A6 XML válido pero sin elemento `Project`:* 400 `proyecto_invalido`.
  - *A7 Ningún TargetFramework dentro del alcance (.NET 8/.NET 10):* 422 `framework_fuera_de_alcance`. **(propuesta DEC-08)**
  - *A8 Más dependencias que el límite (100 propuestas):* 422 `limite_dependencias_excedido`. **(propuesta DEC-09)**
- **Resultado observable:** o bien un análisis persistido (201 + `Location`), o bien un mensaje claro que explica qué corregir; en todos los errores de entrada **no se consulta NuGet ni se persiste nada**. El usuario conserva la pantalla y puede elegir otro archivo.

## CU-02 Extraer dependencias declaradas (incluido en CU-01)

- **Actor:** sistema. **Prioridad:** Alta. **Requisito:** RF-04. *CU-02 es extracción; la validación pertenece a CU-01.*
- **Precondición:** el contenido pasó la validación de CU-01 y es XML seguro.
- **Flujo principal**
  1. Lee `TargetFramework`/`TargetFrameworks` (se separan por `;`, se normalizan y se eliminan duplicados).
  2. Recorre los `PackageReference` (con o sin namespace XML; versión como atributo o elemento).
  3. Para cada uno conserva el identificador, la versión **literal** y su tipo (`exacta`, `rango`, `flotante`, `propiedad`, `ausente`) y la condición `Condition` si existe.
  4. Unifica duplicados equivalentes (mismo identificador sin distinguir mayúsculas, misma versión y condición).
- **Flujos alternos**
  - *B1 Sin `PackageReference`:* lectura correcta con lista vacía y advertencia `sin_paquetes`; el análisis se completa sin dependencias (ver CU-03).
  - *B2 Versión no exacta o con propiedad:* se conserva el valor declarado con advertencia `version_no_resuelta`; **nunca** se sustituye por la última versión.
  - *B3 Duplicados con versiones o condiciones distintas:* se conservan ambas entradas con advertencia `dependencia_duplicada_conflictiva`; no hay selección silenciosa.
  - *B4 `Condition` presente:* se conserva el texto y se avisa `condicion_no_evaluada`.
  - *B5 Sin framework resoluble (ausente o solo por propiedad):* se acepta con advertencia `framework_no_resuelto`; la compatibilidad queda no determinada y el análisis será parcial. **(propuesta DEC-08)**
  - *B6 `TargetFrameworks` mixto (p. ej. `net8.0;net48`):* se evalúan solo los TFM en alcance y se avisa `framework_fuera_de_alcance_omitido`. **(propuesta DEC-08)**
  - *B7 Sufijo de plataforma (`net8.0-windows`):* se acepta y se avisa `sufijo_plataforma`; el sufijo no se evalúa. **(propuesta DEC-08)**
- **Resultado observable:** `ProyectoLeidoDto` (endpoint temporal de E2) o la sección de dependencias del resultado completo. Nada se ejecuta, importa ni restaura; MSBuild no interviene.

## CU-03 Diagnosticar librerías (incluye CU-04; CU-05 lo extiende)

- **Actor:** ACT-01 (lo inicia implícitamente con CU-01). **Prioridad:** Alta. **Requisitos:** RF-08, RF-09, RF-10.
- **Precondición:** CU-01/CU-02 completados con al menos un TFM en alcance.
- **Flujo principal**
  1. Para cada dependencia única obtiene sus metadatos (CU-04).
  2. El motor aplica las reglas (actualización, soporte, framework, licencia, AOT) por cada TFM en alcance y registra hallazgos con motivo y evidencia.
  3. Consolida un estado principal por dependencia y por TFM, y el estado global de la dependencia según la precedencia (ver [criterios_diagnostico.md](criterios_diagnostico.md)).
  4. Para las dependencias deficientes solicita recomendaciones (CU-05).
  5. Calcula el resumen (total + cinco estados) y el estado del análisis (`completo` o `parcial`).
  6. Guarda el resultado completo **de una vez** (sin estados «en proceso»).
- **Flujos alternos**
  - *C1 Sin dependencias:* análisis `completo` con 0 dependencias y advertencia `sin_paquetes` (se persiste).
  - *C2 Datos de NuGet incompletos o NuGet no disponible:* la dependencia queda `no_determinada` conservando los hallazgos conocidos; el análisis es `parcial`.
  - *C3 Presupuesto global de 120 s agotado:* las dependencias pendientes quedan `no_determinada` con advertencia `presupuesto_agotado`; se persiste el resultado parcial si el almacenamiento funciona.
  - *C4 Fallo de almacenamiento:* 503 `almacenamiento_no_disponible`; no se persiste nada.
  - *C5 AOT:* siempre «no evaluado» en esta versión; es informativo y **no** vuelve parcial un análisis. **(propuesta DEC-04)**
- **Resultado observable:** `ResultadoAnalisisDto` con un diagnóstico explicado por dependencia; la ausencia de dato se muestra como «no determinado» y nunca como cero o falso.

## CU-04 Consultar metadatos en NuGet (incluido en CU-03)

- **Actor:** sistema; ACT-02 NuGet. **Prioridad:** Alta. **Requisitos:** RF-05, RF-06, RF-17.
- **Precondición:** existe una dependencia con identificador (la versión puede ser nula).
- **Flujo principal:** el servidor consulta primero la caché vigente (24 h, configurable); si no existe o venció, consulta solo hosts HTTPS de nuget.org con reintentos y límites, guarda en caché y devuelve el dato con origen `nuget`.
- **Flujos alternos**
  - *D1 Caché vigente:* origen `cache_vigente`, sin red.
  - *D2 NuGet falla y hay copia vencida:* se usa con origen `cache_expirada`, fecha original y advertencia.
  - *D3 NuGet falla sin copia:* origen `no_determinado`; no se inventa ningún valor.
  - *D4 Paquete inexistente (404):* sin reintentos; la dependencia queda `no_determinada` con motivo «el paquete no existe en nuget.org»; las demás dependencias no se ven afectadas.
  - *D5 Descargas:* se muestran solo si NuGet las informó; nunca se rellenan con cero. **(propuesta DEC-05: cobertura para todas las dependencias o solo candidatos)**
- **Resultado observable:** metadatos con origen, fecha de consulta e inferencias marcadas como tales (p. ej. compatibilidad inferida por grupos de dependencias).

## CU-05 Generar recomendaciones alternativas (extiende CU-03)

- **Actor:** sistema. **Prioridad:** Alta. **Requisitos:** RF-11, RF-12. **Módulo previsto:** E7.
- **Precondición:** la dependencia es `desactualizada`, `incompatible` o `sin_soporte_activo`.
- **Flujo principal**
  1. Si existe una versión estable posterior compatible del mismo paquete, recomienda `actualizar_version` y advierte si es un cambio mayor.
  2. Si NuGet declara un paquete alterno (con rango de versión), lo usa como candidato prioritario respetando rango y compatibilidad.
  3. De la búsqueda toma hasta 5 candidatos y valida hasta 3. Un hallazgo de búsqueda sin relación funcional demostrada se muestra como `candidato_para_revision`, **no** como alternativa. **(propuesta DEC-12)**
  4. Cada recomendación incluye justificación, evidencia y limitaciones.
- **Flujos alternos:** *E1* sin candidato válido: ausencia explícita de recomendación (no se inventa un reemplazo). *E2* fallo de búsqueda: se conserva el diagnóstico y se añade una nota. Sin IA ni fuentes distintas de NuGet.
- **Resultado observable:** cero o más recomendaciones trazables; ninguna se justifica solo por popularidad.

## CU-06 Visualizar resultados del diagnóstico

- **Actor:** ACT-01. **Prioridad:** Alta. **Requisitos:** RF-13, RF-14.
- **Precondición:** existe un análisis persistido (recién creado o recuperado con CU-08).
- **Flujo principal:** la interfaz muestra nombre del archivo, frameworks, fecha, seis contadores calculados desde los datos, tabla de dependencias ordenada por gravedad, filtros por estado y panel de detalle con indicadores, motivos, origen, fecha de consulta, inferencias, advertencias y recomendaciones.
- **Flujos alternos:** *F1* análisis parcial: aviso visible que explica qué faltó y conserva los diagnósticos conocidos. *F2* análisis sin paquetes: mensaje explicativo. *F3* análisis no encontrado (404 `analisis_no_encontrado`): pantalla «análisis no disponible».
- **Resultado observable:** ningún dato fijo ni simulado; todo proviene del resultado.

## CU-07 Exportar reporte PDF (extiende CU-06)

- **Actor:** ACT-01. **Prioridad:** Media. **Requisito:** RF-15. **Módulo previsto:** E10.
- **Precondición:** el usuario está viendo un análisis existente.
- **Flujo principal:** `GET /api/analisis/{id}/pdf` genera el PDF bajo demanda **desde el resultado guardado** (sin consultar NuGet ni persistir el PDF) y el navegador lo descarga.
- **Flujos alternos:** *G1* análisis inexistente: 404 `analisis_no_encontrado`. *G2* fallo del generador: 500 `pdf_no_disponible`; el resultado en pantalla y el almacenado se conservan.
- **Resultado observable:** un archivo `application/pdf` cuyo contenido coincide con el resultado guardado.

## CU-08 Consultar resultados previos

- **Actor:** ACT-01. **Prioridad:** Media. **Requisito:** RNF-08. **Módulo previsto:** E8/E9.
- **Precondición:** existen análisis dentro del período de conservación (90 días, configurable, pendiente de validación).
- **Flujo principal:** el usuario abre el historial, ve una lista de archivo, fecha, framework, dependencias y estado, y abre un análisis; se muestra el resultado **sin consultar NuGet ni recalcular**.
- **Flujos alternos:** *H1* sin análisis: estado vacío. *H2* identificador inválido: 400 `identificador_invalido`. *H3* análisis purgado: 404 `analisis_no_encontrado`. *H4* parámetros de paginación fuera de rango: 400 `paginacion_invalida`.
- **Visibilidad del listado: pendiente DEC-06.** Hasta confirmarla no se publica un listado global con nombres de archivos de otras personas; un UUID no es control de acceso. La propuesta es que el historial se limite al navegador (el cliente guarda los identificadores de sus propios análisis).
- **Resultado observable:** el mismo resultado que se vio al crearlo, con sus fechas originales.

---

## Relaciones (sin cambios respecto del catálogo)

| Relación | Tipo |
|---|---|
| CU-01 → CU-02 | include |
| CU-03 → CU-04 | include |
| CU-05 → CU-03 | extend |
| CU-07 → CU-06 | extend |

RF-17 (manejo de errores en servicios externos) es transversal: se trata como flujos alternos de CU-01 y CU-04.
