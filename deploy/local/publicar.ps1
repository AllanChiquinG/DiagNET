<#
  Publica la aplicación (API + cliente Blazor) en modo Release en una carpeta.
  Uso (desde la raíz del repositorio):
    powershell -ExecutionPolicy Bypass -File deploy\local\publicar.ps1 [-Destino C:\ruta\carpeta]
  El destino por defecto es C:\Proyectos\DiagNET-publicado\app (fuera del repositorio). La publicación NO incluye appsettings.Local.json (los secretos no viajan).
  Si el destino ya existe se publica ENCIMA (se reemplazan archivos de la aplicación; no se toca nada más).
#>
param([string]$Destino = 'C:\Proyectos\DiagNET-publicado\app')
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')

dotnet publish (Join-Path $raiz 'src\Servidor.Api') -c Release -o $Destino --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish terminó con código $LASTEXITCODE." }

if (Test-Path (Join-Path $Destino 'appsettings.Local.json')) { throw 'La publicación contiene appsettings.Local.json: no debe distribuirse.' }
if (-not (Test-Path (Join-Path $Destino 'wwwroot\_framework\blazor.webassembly.js'))) { throw 'Falta el cliente Blazor en la publicación.' }
Write-Host "Publicado en $Destino"
Write-Host 'Siguiente paso: powershell -ExecutionPolicy Bypass -File deploy\local\iniciar_publicado.ps1 -Migrar   (la primera vez o tras una actualización con migraciones)'
