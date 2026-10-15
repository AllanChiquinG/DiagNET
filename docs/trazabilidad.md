# Matriz de trazabilidad (requisito → caso de uso → entrega → evidencia)

Estado de verificación al cierre de E1: **solo E0 y E1 tienen evidencia**. Las demás filas indican la prueba mínima prevista; no significan que esté hecho.

| Requisito | Caso de uso | Entrega | Evidencia mínima | Estado |
|---|---|---|---|---|
| RF-01 Carga manual del .csproj | CU-01 | E0, E1, E2 | Contrato y errores de entrada (E1); regresión de lectura y subida (E2) | Contrato definido |
| RF-02 Conexión con GitHub | — | Excluido de esta versión | Decisión de alcance documentada | Excluido |
| RF-03 Validación del archivo | CU-01 | E1, E2 | Códigos `extension_invalida`, `archivo_vacio`, `archivo_excede_tamano`, `xml_invalido`, `proyecto_invalido` | Contrato definido |
| RF-04 Extracción de dependencias | CU-02 | E1, E2 | `ProyectoLeidoDto`, tipos de versión y advertencias | Contrato definido |
| RF-05 Metadatos por librería | CU-04 | E4, E5 | Fixtures de mapeo, paginación y caché | Pendiente |
| RF-06 Indicadores de vigencia | CU-04 | E4, E5 | `IndicadoresDto`; decisión DEC-05 (descargas) | Contrato definido; decisión pendiente |
| RF-08 Evaluación del estado | CU-03 | E6 | Tabla de decisiones ([criterios](criterios_diagnostico.md) §7) | Criterios propuestos |
| RF-09 Clasificación | CU-03 | E6 | `EstadoDiagnostico`, precedencia DEC-03 | Criterios propuestos |
| RF-10 Compatibilidad AOT | CU-03 | E1, E4, E6 | AOT `no_evaluado` visible; conciliación DEC-04 | Decisión pendiente |
| RF-11 Recomendaciones alternativas | CU-05 | E7 | Recomendaciones trazables; DEC-12 | Criterios propuestos |
| RF-12 Justificación | CU-05 | E7 | `RecomendacionDto.Justificacion/Evidencia/Limitaciones` | Contrato definido |
| RF-13 Consolidación | CU-06 | E5, E8 | `ResumenEstadosDto` calculado | Contrato definido |
| RF-14 Visualización | CU-06 | E8, E9 | Resultado completo/parcial en pantalla | Pendiente |
| RF-15 PDF | CU-07 | E10 | PDF real comparado con snapshot | Pendiente |
| RF-17 Errores de servicios externos | CU-01, CU-04 | E1, E4, E5, E8, E9 | Problem Details y `no_determinado`; fixtures de fallo | Contrato definido |
| RNF-01/02/09 Tiempo, consultas, red | CU-03, CU-04 | E4, E5, E12 | Presupuesto 120 s, concurrencia, llamadas con caché | Pendiente |
| RNF-03/04 .NET 8/10, entorno | CU-01 | E0, E2, E6, E11 | Alcance de TFM (DEC-08); arranque documentado | E0 verificado |
| RNF-05/06 Usabilidad, explicaciones | CU-06 | E6, E7, E9, E12 | Textos de motivos; pruebas con usuarios | Pendiente |
| RNF-07/08 Snapshot inmutable, resultados previos | CU-08 | E3, E8, E9, E11 | Guardar, reiniciar y recuperar | Pendiente |
| RNF-11 Protección de la información | CU-01 | E2, E3, E4, E8, E11 | XML seguro, no guardar el .csproj, límites | Contrato definido |
| RNF-12 Arquitectura modular | todos | todas | Prueba de dependencias entre capas | E0 verificado |

## Correcciones de trazabilidad registradas (sin renumerar requisitos)

- **CU-02 es «Extraer dependencias declaradas».** Las referencias secundarias que lo llaman «Validar el archivo» (UML §8.1, prototipos) se corrigen: la validación pertenece a CU-01.
- **RF-02 excluido** de esta versión (actores y arquitectura excluyen GitHub).
- **RF-07, RF-16, CF-07, RNF-10** aparecen en la numeración sin definición propia o con definición ausente; ver el registro de inconsistencias en el seguimiento. No se crean requisitos nuevos.
