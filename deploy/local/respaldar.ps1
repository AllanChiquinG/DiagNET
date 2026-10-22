<#
  Respaldo de la base de DiagNET con pg_dump (formato personalizado comprimido) + suma SHA-256. SOLO LEE la base: no modifica nada.
  Uso:
    powershell -ExecutionPolicy Bypass -File deploy\local\respaldar.ps1 -Destino D:\Respaldos\DiagNET [-Conservar 14]
  La conexión se toma de ConnectionStrings__DiagNet o de src\Servidor.Api\appsettings.Local.json; la contraseña viaja solo como PGPASSWORD de este proceso y no se imprime.
  El destino NO puede estar dentro del repositorio (los respaldos contienen datos y no se versionan).
  -Conservar N  (opcional) borra los respaldos más antiguos de ESTA herramienta en el destino, dejando los N más recientes, solo después de que el nuevo respaldo se verificó.
                Por defecto 0 = no borra ninguno.
  Programación diaria (ejemplo, Programador de tareas de Windows): ver docs\operacion.md.
#>
param(
    [Parameter(Mandatory = $true)][string]$Destino,
    [int]$Conservar = 0,
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pgDump = Join-Path $PgBin 'pg_dump.exe'
$pgRestore = Join-Path $PgBin 'pg_restore.exe'
foreach ($e in @($pgDump, $pgRestore)) { if (-not (Test-Path $e)) { throw "No se encontró $e. Ajusta -PgBin." } }

New-Item -ItemType Directory -Force -Path $Destino | Out-Null
$destinoAbs = (Resolve-Path $Destino).Path
if ($destinoAbs.StartsWith($raiz, [StringComparison]::OrdinalIgnoreCase)) { throw 'El destino no puede estar dentro del repositorio.' }

$cadena = $env:ConnectionStrings__DiagNet
if (-not $cadena) {
    $archivo = Join-Path $raiz 'src\Servidor.Api\appsettings.Local.json'
    if (-not (Test-Path $archivo)) { throw 'Falta la cadena de conexión (ConnectionStrings__DiagNet o appsettings.Local.json).' }
    $cadena = (Get-Content $archivo -Raw | ConvertFrom-Json).ConnectionStrings.DiagNet
}
$kv = @{}
foreach ($par in ($cadena -split ';')) { if ($par -match '^\s*([^=]+?)\s*=\s*(.*)$') { $kv[$Matches[1].ToLower()] = $Matches[2] } }
$servidor = if ($kv['host']) { $kv['host'] } else { 'localhost' }
$puerto = if ($kv['port']) { $kv['port'] } else { '5432' }
$base = $kv['database']; $usuario = $kv['username']
if (-not $base -or -not $usuario -or -not $kv['password']) { throw 'La cadena de conexión no tiene Database, Username o Password.' }

$sello = Get-Date -Format 'yyyyMMdd_HHmmss'
$archivoDump = Join-Path $destinoAbs "diagnet_${base}_$sello.dump"
$env:PGPASSWORD = $kv['password']
try {
    & $pgDump -h $servidor -p $puerto -U $usuario -d $base -Fc -Z 6 -f $archivoDump
    if ($LASTEXITCODE -ne 0) { throw "pg_dump terminó con código $LASTEXITCODE." }
    # Verificación: el archivo debe poder listarse (estructura íntegra) y no estar vacío.
    & $pgRestore --list $archivoDump | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'El respaldo generado no se puede leer con pg_restore --list.' }
} catch {
    if (Test-Path $archivoDump) { Remove-Item $archivoDump -Force }   # un respaldo defectuoso no se deja pasar por bueno
    throw
} finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash $archivoDump -Algorithm SHA256).Hash.ToLower()
Set-Content -Path "$archivoDump.sha256" -Value "$hash  $(Split-Path $archivoDump -Leaf)" -Encoding ascii
$tam = '{0:N0}' -f (Get-Item $archivoDump).Length
Write-Host "Respaldo creado: $archivoDump ($tam bytes)"
Write-Host "SHA-256: $hash"

if ($Conservar -gt 0) {
    $antiguos = Get-ChildItem $destinoAbs -Filter "diagnet_${base}_*.dump" | Sort-Object Name -Descending | Select-Object -Skip $Conservar
    foreach ($a in $antiguos) { Remove-Item $a.FullName -Force; Remove-Item "$($a.FullName).sha256" -Force -ErrorAction SilentlyContinue; Write-Host "Respaldo antiguo eliminado: $($a.Name)" }
}
