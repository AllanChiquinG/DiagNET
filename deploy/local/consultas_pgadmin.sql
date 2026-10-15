-- Consultas de demostración para pgAdmin (Query Tool) sobre la base diagnet_nueva.
-- Ábrelas en orden y ejecuta cada bloque con F5 (selecciona el bloque antes para ejecutar solo ese).
-- Son solo de LECTURA, salvo el último bloque (limpieza), que está comentado.

-- 1) Historial de análisis (CU-08): el más reciente primero --------------------------------------
SELECT id, nombre_archivo, framework_objetivo, estado, total_dependencias, fecha_analisis
FROM analisis
ORDER BY fecha_analisis DESC;

-- 2) Resultado completo de un análisis: dependencia → diagnóstico → recomendación ----------------
--    Usa el análisis más reciente. Para otro, cambia la condición por: WHERE a.id = 'PEGA-AQUI-EL-UUID'
SELECT a.nombre_archivo,
       d.nombre_paquete,
       d.version_declarada,
       g.estado,
       g.origen,
       g.fecha_consulta,
       g.motivos,
       r.tipo            AS recomendacion_tipo,
       r.paquete_alternativo,
       r.version_sugerida,
       r.justificacion
FROM analisis a
JOIN dependencia d       ON d.analisis_id = a.id
JOIN diagnostico g       ON g.dependencia_id = d.id
LEFT JOIN recomendacion r ON r.diagnostico_id = g.id
WHERE a.id = (SELECT id FROM analisis ORDER BY fecha_analisis DESC LIMIT 1)
ORDER BY d.id, r.id;

-- 3) Contadores por estado de un análisis (las tarjetas de resumen de la pantalla) ----------------
SELECT g.estado, count(*) AS dependencias
FROM analisis a
JOIN dependencia d ON d.analisis_id = a.id
JOIN diagnostico g ON g.dependencia_id = d.id
WHERE a.id = (SELECT id FROM analisis ORDER BY fecha_analisis DESC LIMIT 1)
GROUP BY g.estado
ORDER BY g.estado;

-- 4) Los indicadores guardados dentro del diagnóstico (copia inmutable, columna JSONB) -----------
--    «desconocido» (no_determinado) y «no evaluado» (AOT) se distinguen de false y de 0.
SELECT d.nombre_paquete,
       g.indicadores -> 'descargas'  ->> 'disponibilidad' AS descargas_disponibilidad,
       g.indicadores -> 'descargas'  ->> 'valor'          AS descargas,
       g.indicadores -> 'deprecado'  ->> 'valor'          AS deprecado,
       g.indicadores -> 'licencia'   -> 'valor' ->> 'valor' AS licencia,
       g.indicadores -> 'aot'        ->> 'disponibilidad' AS aot
FROM analisis a
JOIN dependencia d ON d.analisis_id = a.id
JOIN diagnostico g ON g.dependencia_id = d.id
WHERE a.id = (SELECT id FROM analisis ORDER BY fecha_analisis DESC LIMIT 1);

-- 5) Hallazgos con su evidencia (una fila por hallazgo) --------------------------------------------
SELECT d.nombre_paquete,
       h ->> 'regla'    AS regla,
       h ->> 'gravedad' AS gravedad,
       h ->> 'mensaje'  AS mensaje,
       h -> 'evidencia' AS evidencia
FROM dependencia d
JOIN diagnostico g ON g.dependencia_id = d.id
CROSS JOIN LATERAL jsonb_array_elements(g.hallazgos) AS h
WHERE d.analisis_id = (SELECT id FROM analisis ORDER BY fecha_analisis DESC LIMIT 1);

-- 6) Un análisis parcial: lo que quedó «no determinado» (sin fecha ni indicadores inventados) ------
SELECT a.nombre_archivo, d.nombre_paquete, g.estado, g.origen, g.fecha_consulta IS NULL AS sin_fecha, g.indicadores
FROM analisis a
JOIN dependencia d ON d.analisis_id = a.id
JOIN diagnostico g ON g.dependencia_id = d.id
WHERE a.estado = 'parcial' AND g.estado = 'no_determinada';

-- 7) La caché de metadatos y la selección de versiones por paquete (se purgan sin afectar resultados)
SELECT nombre_paquete, version, descargas, deprecado, compatible_aot, fecha_consulta FROM metadato_libreria ORDER BY 1, 2;
SELECT nombre_paquete, ultima_version_estable, ultima_compatible_por_tfm, seleccion_completa, fecha_consulta FROM seleccion_paquete;

-- 8) Relaciones entre tablas (llaves foráneas y qué pasa al borrar) -------------------------------
SELECT conrelid::regclass AS tabla,
       conname            AS restriccion,
       confrelid::regclass AS referencia,
       CASE confdeltype WHEN 'c' THEN 'CASCADE (se borra en cascada)' WHEN 'n' THEN 'SET NULL (queda en NULL)' ELSE confdeltype::text END AS al_borrar
FROM pg_constraint
WHERE contype = 'f' AND connamespace = 'public'::regnamespace
ORDER BY 1;

-- 9) Reglas que protegen los datos (CHECK) --------------------------------------------------------
SELECT conrelid::regclass AS tabla, conname AS restriccion, pg_get_constraintdef(oid) AS regla
FROM pg_constraint
WHERE contype = 'c' AND connamespace = 'public'::regnamespace
ORDER BY 1, 2;

-- 10) Historial de migraciones aplicadas por EF Core ---------------------------------------------
SELECT "MigrationId", "ProductVersion" FROM "__EFMigrationsHistory" ORDER BY 1;

-- 11) LIMPIEZA de los análisis de demostración (descomenta para ejecutar) -------------------------
-- DELETE FROM analisis WHERE nombre_archivo LIKE 'DEMO-%';   -- borra también dependencias, diagnósticos y recomendaciones
