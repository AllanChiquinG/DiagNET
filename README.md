# DiagNET

Aplicación web en español que diagnostica las dependencias NuGet declaradas en un `.csproj` de .NET 8 o .NET 10.

> **Estado:** E0 (solución base), E1 (contratos), E2 (lectura segura del `.csproj`) y E3 (datos con PostgreSQL y migraciones EF Core). Todavía no hay análisis ni integración con NuGet.

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
  Servidor.Integracion/ NuGet y PDF (desde E4/E10)
tests/
  Servidor.Negocio.Tests/ Servidor.Api.Tests/
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

## Pruebas

```bash
dotnet test DiagNET.slnx
```

## Configuración

- Las cadenas de conexión reales van en `appsettings.Local.json` (ignorado por Git; hay un `.example.json` sin contraseñas) o en variables de entorno (`ConnectionStrings__DiagNet`). Nunca en el repositorio.
- Las migraciones usan la misma cadena (o la variable `DIAGNET_CONNECTION`).
- Usa una base nueva (`diagnet_nueva`); no reutilices bases de implementaciones anteriores.
