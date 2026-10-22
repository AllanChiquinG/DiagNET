<#
  Crea la base NUEVA de DiagNET en el PostgreSQL local de Windows (sin Docker).
  - Lee las contraseñas de los archivos appsettings.Local.json (ignorados por Git); no las imprime.
  - psql te pedirá la contraseña del usuario "postgres" (superusuario) en este terminal; este script nunca la ve.
  Uso (desde la raíz del repositorio):  powershell -ExecutionPolicy Bypass -File deploy\local\crear_base_local.ps1
#>
$ErrorActionPreference = 'Stop'
$raiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
if (-not (Test-Path $psql)) { throw "No se encontró psql en $psql. Ajusta la ruta a tu instalación de PostgreSQL." }

function Leer-Clave([string]$archivo, [string]$nombre) {
    $json = Get-Content (Join-Path $raiz $archivo) -Raw | ConvertFrom-Json
    $cadena = $json.ConnectionStrings.$nombre
    if ($cadena -notmatch 'Password=([^;]+)') { throw "No hay contraseña en $archivo ($nombre)." }
    $Matches[1]
}

$pwdApp = Leer-Clave 'src\Servidor.Api\appsettings.Local.json' 'DiagNet'
$pwdPruebas = Leer-Clave 'tests\Servidor.Datos.Tests\appsettings.Local.json' 'Pruebas'

& $psql -h localhost -p 5432 -U postgres -d postgres -v "pwd_app=$pwdApp" -v "pwd_pruebas=$pwdPruebas" -f (Join-Path $PSScriptRoot 'crear_base_local.sql')
if ($LASTEXITCODE -ne 0) { throw "psql terminó con código $LASTEXITCODE." }
Write-Host 'Listo. Siguiente paso: dotnet ef database update (ver README).'
