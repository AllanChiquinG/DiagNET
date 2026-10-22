<#
  Inicia la aplicación PUBLICADA (no «dotnet run») con la cadena de conexión tomada de src\Servidor.Api\appsettings.Local.json (ignorado por Git) o de la variable
  de entorno ConnectionStrings__DiagNet si ya está definida. La cadena viaja solo como variable de entorno de este proceso: no se copia a la carpeta publicada ni se imprime.
  Uso:
    powershell -ExecutionPolicy Bypass -File deploy\local\iniciar_publicado.ps1 [-Carpeta C:\...\app] [-Url http://localhost:5228] [-Migrar]
  -Migrar  aplica antes las migraciones pendientes (paso controlado, con bloqueo; haz un respaldo antes). Sin esta bandera NUNCA se migra al arrancar.
  Modo demostración local: Production con HTTPS desactivado de forma explícita (Seguridad__ExigirHttps=false) porque se sirve en http://localhost, sin proxy.
#>
param(
    [string]$Carpeta = 'C:\Proyectos\DiagNET-publicado\app',
    [string]$Url = 'http://localhost:5228',
    [switch]$Migrar
)
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not (Test-Path (Join-Path $Carpeta 'Servidor.Api.dll'))) { throw "No hay una publicación en $Carpeta. Ejecuta deploy\local\publicar.ps1 primero." }

if (-not $env:ConnectionStrings__DiagNet) {
    $archivo = Join-Path $raiz 'src\Servidor.Api\appsettings.Local.json'
    if (-not (Test-Path $archivo)) { throw 'Falta la cadena de conexión: define ConnectionStrings__DiagNet o crea src\Servidor.Api\appsettings.Local.json (ver README).' }
    $env:ConnectionStrings__DiagNet = (Get-Content $archivo -Raw | ConvertFrom-Json).ConnectionStrings.DiagNet
}

$env:ASPNETCORE_ENVIRONMENT = 'Production'
if (-not $env:Seguridad__ExigirHttps) { $env:Seguridad__ExigirHttps = 'false' }

Push-Location $Carpeta
try {
    if ($Migrar) {
        dotnet Servidor.Api.dll operacion migrar
        if ($LASTEXITCODE -ne 0) { throw 'La migración falló; la aplicación no se inició.' }
    } else {
        dotnet Servidor.Api.dll operacion estado
        if ($LASTEXITCODE -eq 2) { throw 'Hay migraciones pendientes: ejecuta de nuevo con -Migrar (haz un respaldo antes).' }
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo comprobar la base de datos.' }
    }

    Write-Host "Iniciando DiagNET (publicada) en $Url ..."
    dotnet Servidor.Api.dll --urls $Url
} finally {
    Pop-Location
}
