# DiagNET

Aplicación web en español que diagnostica las dependencias NuGet declaradas en un `.csproj` de .NET 8 o .NET 10.

> **Estado:** entrega E0 (solución base). Todavía no hay análisis, base de datos ni integración con NuGet.

## Requisitos

- .NET SDK 10 (se fija en `global.json`; `rollForward: latestFeature`).
- Git.
- PostgreSQL 18 o Docker: aún no se usan (se necesitan desde E3).

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

## Pruebas

```bash
dotnet test DiagNET.slnx
```

## Configuración

- `src/Servidor.Api/appsettings.Development.json` contiene una cadena de conexión de **ejemplo** sin contraseña real (se usará en E3).
- Los secretos reales van en `appsettings.Local.json` o en `.env` (ambos ignorados por Git). Ver `.env.example`.
- Usa una base nueva (`diagnet_nueva`); no reutilices bases de implementaciones anteriores.
