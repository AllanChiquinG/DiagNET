# Operación de DiagNET (E11)

Instalación, arranque, configuración, migraciones, respaldo/restauración y recuperación. Entorno de desarrollo y demostración: **PostgreSQL local en Windows**
(sin Docker). Al final se describe la alternativa con Docker + Caddy, que está **preparada pero no probada** (ver «Qué está comprobado»).

## 1. Requisitos
- .NET SDK 10 (`global.json`), Git, PostgreSQL 16+ (probado con 18.6) y las herramientas de cliente de PostgreSQL (`psql`, `pg_dump`, `pg_restore`; por defecto en `C:\Program Files\PostgreSQL\18\bin`).
- Acceso a nuget.org (única fuente de datos externa).

## 2. Instalación limpia (desarrollo y demostración, desde los archivos publicados)
1. Clona el repositorio y crea la base y los roles: copia `src\Servidor.Api\appsettings.Local.example.json` a `appsettings.Local.json` (y el de `tests\Servidor.Datos.Tests`), reemplaza las contraseñas
   y ejecuta `powershell -ExecutionPolicy Bypass -File deploy\local\crear_base_local.ps1` (ver README, «Base de datos»). `appsettings.Local.json` **no se versiona**.
2. Publica en Release (API + cliente Blazor): `powershell -ExecutionPolicy Bypass -File deploy\local\publicar.ps1` → `C:\Proyectos\DiagNET-publicado\app` (fuera del repositorio; **no** incluye `appsettings.Local.json`).
3. Primera vez (o tras una actualización con migraciones): `powershell -ExecutionPolicy Bypass -File deploy\local\iniciar_publicado.ps1 -Migrar`. Veces siguientes, sin `-Migrar`
   (el script se niega a arrancar si hay migraciones pendientes y te lo dice).
4. Abre <http://localhost:5228>.

`iniciar_publicado.ps1` toma la cadena de conexión de `appsettings.Local.json` **solo como variable de entorno del proceso** (no la copia a la carpeta publicada ni la imprime) y usa `ASPNETCORE_ENVIRONMENT=Production` con HTTPS desactivado de forma
explícita (`Seguridad__ExigirHttps=false`) porque en demostración local se sirve por `http://localhost`, sin proxy.

## 3. Configuración externa (sin secretos en Git)
Cualquier valor se puede dar por variable de entorno (`Seccion__Clave`) o por `appsettings.Local.json` junto a la aplicación (nunca versionado). Las variables prevalecen.

| Variable | Valor por defecto | Para qué |
|---|---|---|
| `ConnectionStrings__DiagNet` | — (obligatoria) | Conexión a PostgreSQL. **Secreto**: solo por entorno o `appsettings.Local.json`. Sin ella la aplicación arranca, pero los análisis responden 503 y `/api/salud/disponibilidad` lo dice. |
| `ASPNETCORE_ENVIRONMENT` | `Production` publicada / `Development` con `dotnet run` | Producción sirve el cliente publicado y exige HTTPS salvo decisión explícita. |
| `Seguridad__ExigirHttps` | `true` fuera de Desarrollo | Redirección a HTTPS + HSTS. `false` solo en demostración local sin proxy. |
| `Seguridad__ReenviarCabeceras` / `Seguridad__RedesConfiables__0…` | `false` / vacío | Detrás de un proxy: confía en `X-Forwarded-Proto/For/Host` **solo** de esas redes (CIDR; se rechaza `/0`). |
| `AllowedHosts` | `*` | En producción, el dominio exacto. |
| `Historial__RetencionDias` | `90` | Plazo de conservación previsto de un análisis (lo que la interfaz muestra; mientras la purga esté desactivada la interfaz NO promete eliminación automática). |
| `Purga__Habilitada` / `Purga__RetencionCacheDias` / `Purga__IntervaloHoras` | `false` / `30` / `24` | Purga **desactivada** por defecto (ver §7). |
| `Analisis__MaxAnalisisSimultaneos` / `Analisis__MaxReportesSimultaneos` | `8` / `4` | Límites de concurrencia (429 al exceder). |
| `Analisis__PresupuestoAnalisis` | `00:02:00` | Presupuesto global de un análisis. |
| `Lectura__…` | 1 MB archivo, 2 MB cuerpo, 100 dependencias | Límites de entrada. |

## 4. Comprobaciones de salud y disponibilidad
- `GET /api/salud` — **vitalidad**: el proceso responde (no toca la base).
- `GET /api/salud/disponibilidad` — **disponibilidad**: consulta la base con un tiempo máximo de 3 s y las migraciones pendientes. `200 {"estado":"ok",…}` si puede atender análisis; `503 {"estado":"no_disponible",…}` si la base no responde o hay migraciones pendientes.
  No consulta NuGet, no expone cadenas de conexión ni nombres de migraciones.
- `dotnet Servidor.Api.dll operacion estado` — lo mismo desde la línea de comandos (código de salida 0 al día, 2 pendientes, 1 error).

## 5. Migraciones (sin borrar datos)
Las migraciones **nunca** se aplican al arrancar. Procedimiento:
1. Respaldo previo (§6).
2. `dotnet Servidor.Api.dll operacion estado` (en la carpeta publicada, con la misma configuración) → lista las pendientes.
3. `dotnet Servidor.Api.dll operacion migrar` (o `iniciar_publicado.ps1 -Migrar`). Toma un bloqueo en la base (`pg_advisory_lock`), así que dos migradores simultáneos no se pisan, y aplica únicamente las migraciones pendientes y **no recrea el esquema**; las cuatro migraciones actuales solo agregan o amplían columnas (en su sentido «subir» no borran filas). Una migración futura que borrara datos debe revisarse y respaldarse antes.
   No crea la base: si no existe o las credenciales fallan, termina con error.
4. Comprueba `/api/salud/disponibilidad`.
Si una migración falla, la aplicación publicada anterior sigue sirviendo (aún no hay cambios incompatibles) y la base queda en el estado previo de esa migración (cada una es transaccional en PostgreSQL); restaura el respaldo en una base nueva si hiciera falta (§6).

## 6. Respaldo y restauración
- **Respaldo** (solo lee): `powershell -ExecutionPolicy Bypass -File deploy\local\respaldar.ps1 -Destino D:\Respaldos\DiagNET [-Conservar 14]` → `diagnet_<base>_<fecha>.dump` (formato personalizado, comprimido) + `.sha256`. El destino no puede estar dentro del repositorio.
  Verifica que el archivo se pueda leer y borra un respaldo defectuoso. `-Conservar N` (opcional, por defecto 0 = no borra ninguno) elimina los respaldos antiguos de la herramienta, dejando N.
- **Respaldo diario** (Programador de tareas de Windows; ejemplo, 03:00):
  `schtasks /Create /SC DAILY /ST 03:00 /TN "DiagNET respaldo" /TR "powershell -NoProfile -ExecutionPolicy Bypass -File C:\Proyectos\DiagNET\deploy\local\respaldar.ps1 -Destino D:\Respaldos\DiagNET -Conservar 14"`.
  El **destino externo real** (otro disco, red o nube) lo define el propietario; copiar los `.dump` fuera del equipo es lo que protege ante la pérdida del disco.
- **Restauración en una base nueva y aislada**: `powershell -ExecutionPolicy Bypass -File deploy\local\restaurar_en_base_nueva.ps1 -Respaldo <archivo.dump> [-BaseNueva diagnet_restaurada_prueba] [-Comparar]`
  (necesita un usuario con `CREATEDB`; la contraseña por `PGPASSWORD` o se pide). **Nunca** restaura sobre una base existente ni sobre `diagnet_nueva`: rechaza nombres protegidos, la base de origen del respaldo y cualquier base que ya exista; verifica la suma SHA-256;
  y al terminar cuenta las filas por tabla (con `-Comparar`, las contrasta con la base de origen, solo lectura).

## 7. Purga (desactivada: pendiente de tu aprobación)
Plazos **propuestos** (supuestos configurables, sin validar): análisis **90 días** (`Historial__RetencionDias`, la misma cifra que ve el usuario) y caché de metadatos/selecciones **30 días**; pasada **cada 24 h**. Un análisis eliminado arrastra sus dependencias, diagnósticos y recomendaciones por cascada;
la purga de caché **no** destruye diagnósticos (conservan su copia de indicadores). Los límites son estrictos: se elimina lo anterior al límite, nunca lo que está exactamente en él. Plazos absurdos se rechazan (análisis < 7 días, caché < 1 día).
- Simular (no borra): `dotnet Servidor.Api.dll operacion purgar` → informa plazos y cuántas filas tocaría.
- Eliminar manualmente: `dotnet Servidor.Api.dll operacion purgar --ejecutar` (haz un respaldo antes).
- Automática: `Purga__Habilitada=true` (solo tras aprobar los plazos). Registra solo cantidades, nunca identificadores.

## 8. Recuperación
| Situación | Qué hacer |
|---|---|
| La aplicación no arranca / error de configuración | Revisa la salida; sin `ConnectionStrings__DiagNet` arranca pero responde 503 en disponibilidad. |
| PostgreSQL detenido | Inicia el servicio `postgresql-x64-18`; `/api/salud/disponibilidad` vuelve a 200 sin reiniciar la aplicación. |
| Migraciones pendientes tras actualizar | `operacion estado` → respaldo → `operacion migrar`. |
| Datos dañados o perdidos | Restaura el último respaldo en **una base nueva** (§6), revisa los recuentos y, con la aplicación detenida, apunta `ConnectionStrings__DiagNet` a esa base. La base original queda intacta para el análisis posterior. |
| Rollback de la aplicación | Vuelve a publicar la versión anterior (`git checkout <etiqueta>` + `publicar.ps1`) en la misma carpeta. Las migraciones actuales solo agregan o amplían columnas, por lo que la versión anterior suele seguir funcionando con la estructura nueva; si no, restaura el respaldo previo a la migración en una base nueva. |

## 9. HTTPS, registros y tiempos de espera (garantías de E8)
- **HTTPS**: fuera de Desarrollo la API redirige a HTTPS y envía HSTS (`Seguridad__ExigirHttps`). Detrás de un proxy que termina TLS se activan `Seguridad__ReenviarCabeceras` y la red del proxy en `Seguridad__RedesConfiables__0`; una dirección fuera de esa red no puede afirmar que la conexión fue HTTPS (comprobado por pruebas).
- **Cabeceras de `/api`** (`no-store`, `nosniff`, `no-referrer`) las pone la propia aplicación: se mantienen detrás de cualquier proxy.
- **Registros**: la aplicación no escribe identificadores, URL ni nombres de archivo (comprobado con las pruebas de E8/E10 y con la aplicación publicada). Un proxy sí registraría la URL completa: el `Caddyfile` preparado elimina `uri`, cabeceras y dirección del cliente del registro de acceso.
- **Tiempos de espera**: un análisis tiene presupuesto global de 120 s; el proxy debe esperar **más** (el `Caddyfile` usa 180 s) y la interfaz espera hasta 5 min. Un proxy con un tiempo menor cortaría análisis legítimos.
- **Tamaño**: cuerpo máximo 2 MB (Kestrel y proxy coherentes).

## 10. Alternativa con Docker + Caddy (preparada, NO probada)
`deploy/docker/` contiene `Dockerfile` (multietapa, versiones fijadas: SDK 10.0.401, ASP.NET 10.0.13), `docker-compose.yml` (aplicación + PostgreSQL 18.6 con volumen + Caddy 2.11.7; base **sin puertos publicados**; proxy escuchando solo en 127.0.0.1 por defecto; servicio `migrar` de un solo uso),
`Caddyfile` y `.env.example`. **No sustituye** el PostgreSQL local de desarrollo. Está validada solo en sintaxis (`docker compose config`); no se construyó la imagen ni se ejecutó (el motor de Docker no estaba en marcha) ni se validó el `Caddyfile` (no hay binario de Caddy). Para publicar en Internet hace falta un servidor, un dominio y tu autorización (ver el cierre de E11).
