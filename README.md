# DiagNET

Aplicación web en español que diagnostica las dependencias NuGet declaradas en un `.csproj` de .NET 8 o .NET 10.

> **Estado:** E0–E10: solución base, contratos, lectura segura del `.csproj`, datos con PostgreSQL, integración con NuGet, orquestación, diagnóstico, recomendaciones, API de análisis, interfaz web (Blazor WebAssembly) y reporte PDF. Pendiente: instalación y defensa (E11–E12).

## Requisitos

- .NET SDK 10 (se fija en `global.json`; `rollForward: latestFeature`).
- Git.
- PostgreSQL 16 o superior instalado localmente (ver «Base de datos»). Docker no se usa para desarrollo.
- Herramienta de EF Core: `dotnet tool install --global dotnet-ef`.

## Estructura

```text
DiagNET.slnx
src/
  Cliente.Web/          Blazor WebAssembly (solo referencia Compartido.Contratos)
  Compartido.Contratos/ DTO compartidos
  Servidor.Api/         Minimal API; compone servicios y sirve el cliente
  Servidor.Negocio/     Dominio y reglas; sin dependencias externas
  Servidor.Datos/       Persistencia (EF Core/Npgsql desde E3)
  Servidor.Integracion/ NuGet (E4) y generador PDF (E10)
tests/
  Cliente.Web.Tests/ Compartido.Contratos.Tests/ Servidor.Api.Tests/ Servidor.Datos.Tests/ Servidor.Integracion.Tests/ Servidor.Negocio.Tests/
deploy/                 Docker/Compose (desde E11)
```

Dependencias: `Negocio` no referencia nada; `Datos` e `Integracion` → `Negocio`; `Api` → todos los anteriores y `Cliente.Web`; `Cliente.Web` → `Contratos`.

## Arranque local

```bash
dotnet restore DiagNET.slnx
dotnet build DiagNET.slnx
dotnet run --project src/Servidor.Api
```

Abre <http://localhost:5228>. El perfil de lanzamiento usa `ASPNETCORE_ENVIRONMENT=Development`; esto es necesario para que la API sirva los archivos del cliente al ejecutar desde código fuente. Si usas `--no-launch-profile`, define la variable a mano.

Comprobación de salud:

```bash
curl http://localhost:5228/api/salud
```

## Interfaz web (E9)

Con la API en marcha (`dotnet run --project src/Servidor.Api`, ver arriba) y la base de datos migrada, abre <http://localhost:5228>:

1. **Nuevo análisis** (`/`): elige o arrastra un `.csproj` (hasta 1 MB), lee el aviso de acceso por enlace y pulsa **Analizar proyecto**. La espera es indeterminada (el servidor no informa progreso) y se puede cancelar.
2. **Resultado** (`/resultado/{id}`): seis contadores, tabla con filtros y panel de detalle por librería (motivos, evidencia, indicadores, recomendaciones y limitaciones). Abrir un resultado guardado solo lee: no consulta NuGet ni recalcula.
3. **Historial** (`/historial`): lista los análisis que **este navegador** creó (identificadores en `localStorage`; se consultan en lotes de hasta 50). No es una cuenta, no se comparte entre dispositivos y se pierde si se borran los datos del sitio. Cualquier persona con el enlace de un análisis puede verlo.

El botón **Exportar PDF** descarga el reporte real (ver «Reporte PDF (E10)»). Las pruebas de la interfaz (`tests/Cliente.Web.Tests`, bUnit) no necesitan navegador ni base de datos.

## Reporte PDF (E10)

`GET /api/analisis/{id}/pdf` devuelve el reporte del análisis como `application/pdf` (descarga con nombre `DiagNET_<archivo>_<aaaammdd>.pdf`). Desde la interfaz, el botón **Exportar PDF** del resultado hace esa misma petición.

```bash
curl -OJ http://localhost:5228/api/analisis/{id}/pdf
```

- **Solo lee el resultado guardado:** no consulta NuGet, no recalcula reglas ni recomendaciones y no guarda el PDF (se genera de nuevo en cada petición). Las fechas del reporte están en UTC.
- **Contenido y diseño:** resumen inicial (identificación, frameworks, fechas, estado, contadores y avisos) + **tabla compacta de todas las dependencias** + **detalle de cada una en bloques continuos** (motivos con su evidencia completa, indicadores, origen de los datos, recomendaciones con sus limitaciones) + limitaciones generales. Sin una página por dependencia: los saltos ocurren solo cuando se acaba el espacio y el número de páginas depende del contenido (p. ej. 40 dependencias de muestra: 16–17 páginas; 40 reales con detalle completo: ~33). El diagnóstico parcial, los avisos del análisis y las recomendaciones incompletas se muestran por separado.
- **Fechas:** todas en **UTC** y rotuladas «UTC»; el PDF explica que la aplicación web usa la hora local del navegador.
- **Caracteres que la fuente no dibuja** (escrituras no latinas, emoji, controles): no se pierden ni se sustituyen en silencio; se escriben como `‹U+XXXX›` (su punto de código) y el reporte lo avisa al inicio y en las limitaciones.
- **Errores:** `404 analisis_no_encontrado`, `400 identificador_invalido`, `500 pdf_no_disponible` (el resultado sigue guardado), `429 demasiadas_solicitudes` (máximo `Analisis:MaxReportesSimultaneos`, 4 por defecto) y `503 almacenamiento_no_disponible`. Lleva las mismas cabeceras y política de registros que el resto de `/api` (`no-store`, `nosniff`, `no-referrer`; sin identificadores ni URL en los registros).
- **Terceros (licencias):** PDFsharp y MigraDoc **6.2.4, licencia MIT** (versión fijada con `[6.2.4]`: desde la 6.5.0 el proyecto pasó a AGPL-3.0, no actualizar sin una decisión expresa) y la fuente **Liberation Sans 2.1.5, SIL Open Font License 1.1**, incrustada sin modificar en `src/Servidor.Integracion/Pdf/Fuentes/` junto con su `LICENSE`. La biblioteca vive detrás de `IGeneradorPdf` y es sustituible. Los textos completos de las licencias están en [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) (verificado el 21/10/2026); antes de actualizar cualquiera de estos componentes hay que revisar de nuevo su licencia.

## Leer un .csproj (E2)

`POST /api/proyectos/lectura` recibe un `multipart/form-data` con el campo `archivo` y devuelve las dependencias y frameworks declarados. No consulta NuGet, no guarda nada y no ejecuta el archivo.

```bash
curl -F "archivo=@MiProyecto.csproj" http://localhost:5228/api/proyectos/lectura
```

Límites: archivo de hasta 1 MB, cuerpo HTTP de hasta 2 MB y 100 dependencias directas distintas (configurables en la sección `Lectura` de `appsettings.json`). Los errores se devuelven como Problem Details con un campo `codigo` estable (`archivo_ausente`, `extension_invalida`, `archivo_vacio`, `archivo_excede_tamano`, `cuerpo_excede_tamano`, `xml_invalido`, `proyecto_invalido`, `framework_fuera_de_alcance`, `limite_dependencias_excedido`).

## Base de datos (PostgreSQL local, sin Docker)

Requisitos: PostgreSQL 16 o superior instalado en Windows (probado con 18.6) y pgAdmin 4 (opcional, para administrarla y mostrarla).

1. Copia `src/Servidor.Api/appsettings.Local.example.json` como `appsettings.Local.json` y `tests/Servidor.Datos.Tests/appsettings.Local.example.json` como `appsettings.Local.json`; reemplaza `REEMPLAZA_ESTA_CONTRASENA` por contraseñas nuevas y fuertes. Estos archivos **no se versionan**.
2. Crea los roles y la base **nueva** `diagnet_nueva` (psql te pedirá la contraseña del usuario `postgres`; el script no la ve):

```bash
powershell -ExecutionPolicy Bypass -File deploy\local\crear_base_local.ps1
```

3. Aplica las migraciones de EF Core (crea las tablas):

```bash
dotnet ef database update -p src/Servidor.Datos
```

Las migraciones **no** se aplican al arrancar la API: son un paso controlado. Para generar una nueva: `dotnet ef migrations add Nombre -p src/Servidor.Datos -o Migraciones`.

Datos de demostración (opcional; son ejemplos inventados `DEMO-*`, no resultados reales) y consultas para pgAdmin:

```bash
dotnet run deploy/local/CargarDemostracion.cs
```

`deploy/local/consultas_pgadmin.sql` contiene las consultas para mostrar la base. Para borrar la demostración: `dotnet run deploy/local/CargarDemostracion.cs -- --limpiar`.

Las pruebas de `Servidor.Datos.Tests` usan **PostgreSQL real**: crean una base temporal `diagnet_test_…` con el rol `diagnet_pruebas`, aplican las migraciones y la eliminan al terminar. No tocan `diagnet_nueva`. Si faltan las credenciales, **fallan** (no se omiten).

## Integración con NuGet (E4)

`Servidor.Integracion` consulta **solo nuget.org** (HTTPS) con `NuGetGateway`: descubre los servicios desde el índice `https://api.nuget.org/v3/index.json`, lee el registro de cada paquete (con paginación acotada), selecciona la última versión estable y la última compatible por TFM, y consulta las descargas. La compatibilidad es **inferida** de los grupos de dependencias (no certifica assets ni AOT, que no se evalúa). No descarga paquetes.

Valores configurables en la sección `NuGet` de `appsettings.json` (clase `OpcionesNuGet`): tiempo por intento (10 s), reintentos (3, con esperas de 1, 2 y 4 s), concurrencia (4), disyuntor (se abre tras 5 **operaciones** fallidas consecutivas y descansa 30 s), respuesta máxima (5 MB), redirecciones (3), páginas del registro por paquete (8), identificador máximo (100 caracteres, según la referencia oficial del `.nuspec`).

Las pruebas habituales usan **respuestas simuladas** (no tocan internet). Las comprobaciones **reales** contra nuget.org están en un proyecto aparte, fuera de `DiagNET.slnx`, y se ejecutan a mano:

```bash
dotnet test tests/Servidor.Integracion.ContratoNuGet.Tests
```

## Pruebas

```bash
dotnet test DiagNET.slnx
```

## Configuración

- Las cadenas de conexión reales van en `appsettings.Local.json` (ignorado por Git; hay un `.example.json` sin contraseñas) o en variables de entorno (`ConnectionStrings__DiagNet`). Nunca en el repositorio.
- Las migraciones usan la misma cadena (o la variable `DIAGNET_CONNECTION`).
- Usa una base nueva (`diagnet_nueva`); no reutilices bases de implementaciones anteriores.
