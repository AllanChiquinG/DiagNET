<#
  Restaura un respaldo de DiagNET en una base NUEVA y AISLADA y verifica los datos. NUNCA restaura sobre una base existente (ni sobre diagnet_nueva).
  Uso:
    powershell -ExecutionPolicy Bypass -File deploy\local\restaurar_en_base_nueva.ps1 -Respaldo D:\Respaldos\DiagNET\diagnet_diagnet_nueva_20261022_030000.dump [-BaseNueva diagnet_restaurada_prueba] [-Comparar]
  Conexión del administrador (necesita permiso CREATEDB): -Servidor/-Puerto/-Usuario y la contraseña en la variable PGPASSWORD o, si no está, se pide en este terminal.
  -Comparar  cuenta también las filas de la base de origen (ConnectionStrings__DiagNet / appsettings.Local.json, SOLO LECTURA) y las compara con la restaurada.
  Al terminar, la base restaurada queda para que la revises; para conservar los datos restaurados en producción, ver «Recuperación» en docs\operacion.md.
#>
param(
    [Parameter(Mandatory = $true)][string]$Respaldo,
    [string]$BaseNueva = ('diagnet_restaurada_' + (Get-Date -Format 'yyyyMMdd_HHmmss')),
    [string]$Servidor = 'localhost',
    [string]$Puerto = '5432',
    [string]$Usuario = 'postgres',
    [switch]$Comparar,
    [string]$PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
$raiz = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$psql = Join-Path $PgBin 'psql.exe'; $pgRestore = Join-Path $PgBin 'pg_restore.exe'
foreach ($e in @($psql, $pgRestore)) { if (-not (Test-Path $e)) { throw "No se encontró $e. Ajusta -PgBin." } }
if (-not (Test-Path $Respaldo)) { throw "No existe el respaldo $Respaldo." }

# --- Guardas: nombre válido, distinto de cualquier base protegida y distinto de la base de origen del respaldo.
if ($BaseNueva -notmatch '^[a-z][a-z0-9_]{2,62}$') { throw 'BaseNueva debe ser minúsculas, números y guion bajo (3 a 63 caracteres).' }
$origenDelDump = ''
foreach ($linea in (& $pgRestore --list $Respaldo | Select-Object -First 20)) { if ($linea -match '^;\s*dbname:\s*(\S+)') { $origenDelDump = $Matches[1] } }
if ($BaseNueva -in @('diagnet_nueva', 'postgres', 'template0', 'template1') -or $BaseNueva -eq $origenDelDump) {
    throw "BaseNueva «$BaseNueva» está protegida o es la base de origen: la restauración solo se hace en una base distinta y nueva."
}

# --- Integridad del archivo.
$sumas = "$Respaldo.sha256"
if (Test-Path $sumas) {
    $esperado = ((Get-Content $sumas -Raw).Trim() -split '\s+')[0].ToLower()
    if ((Get-FileHash $Respaldo -Algorithm SHA256).Hash.ToLower() -ne $esperado) { throw 'La suma SHA-256 del respaldo no coincide: el archivo está dañado o fue modificado.' }
    Write-Host 'Suma SHA-256 verificada.'
} else { Write-Warning 'No hay archivo .sha256 junto al respaldo: no se pudo verificar su integridad.' }

if (-not $env:PGPASSWORD) {
    $seguro = Read-Host "Contraseña de $Usuario en $Servidor" -AsSecureString
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($seguro))
}
$conexion = @('-h', $Servidor, '-p', $Puerto, '-U', $Usuario)

try {
    $existe = & $psql @conexion -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$BaseNueva'"
    if ($existe -eq '1') { throw "La base «$BaseNueva» YA EXISTE: no se restaura sobre una base existente. Elige otro nombre." }

    & $psql @conexion -d postgres -v ON_ERROR_STOP=1 -c "CREATE DATABASE `"$BaseNueva`""
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo crear la base nueva (¿el usuario tiene CREATEDB?).' }
    $creada = $true

    & $pgRestore @conexion -d $BaseNueva --no-owner --no-privileges --exit-on-error $Respaldo
    if ($LASTEXITCODE -ne 0) { throw "pg_restore terminó con código $LASTEXITCODE." }

    # El SQL viaja por la entrada estándar: así las comillas de «"__EFMigrationsHistory"» no se pierden al pasar argumentos en Windows PowerShell 5.1.
    function Contar([string[]]$cx, [string]$bd, [string]$tabla) { [int](('SELECT count(*) FROM ' + $tabla) | & $psql @cx -d $bd -tA) }
    $tablas = 'analisis', 'dependencia', 'diagnostico', 'recomendacion', 'metadato_libreria', 'seleccion_paquete', '"__EFMigrationsHistory"'
    $restaurado = [ordered]@{}
    foreach ($t in $tablas) { $restaurado[$t] = Contar $conexion $BaseNueva $t }

    Write-Host "Base restaurada: $BaseNueva"
    $restaurado.GetEnumerator() | ForEach-Object { Write-Host ('  {0,-26} {1,8}' -f $_.Key, $_.Value) }

    if ($Comparar) {
        $cadena = $env:ConnectionStrings__DiagNet
        if (-not $cadena) { $cadena = (Get-Content (Join-Path $raiz 'src\Servidor.Api\appsettings.Local.json') -Raw | ConvertFrom-Json).ConnectionStrings.DiagNet }
        $kv = @{}; foreach ($par in ($cadena -split ';')) { if ($par -match '^\s*([^=]+?)\s*=\s*(.*)$') { $kv[$Matches[1].ToLower()] = $Matches[2] } }
        $guardada = $env:PGPASSWORD; $env:PGPASSWORD = $kv['password']
        try {
            $difiere = $false
            foreach ($t in $tablas) {
                $cxOrigen = @('-h', $(if ($kv['host']) { $kv['host'] } else { 'localhost' }), '-p', $(if ($kv['port']) { $kv['port'] } else { '5432' }), '-U', $kv['username'])
                $origen = Contar $cxOrigen $kv['database'] $t
                $marca = if ($origen -eq $restaurado[$t]) { 'igual' } else { 'DIFERENTE'; $difiere = $true }
                Write-Host ('  {0,-26} origen {1,6}  restaurada {2,6}  {3}' -f $t, $origen, $restaurado[$t], $marca)
            }
            if ($difiere) { Write-Warning 'Hay diferencias: puede ser normal si el origen cambió después del respaldo.' } else { Write-Host 'Los recuentos coinciden con la base de origen.' }
        } finally { $env:PGPASSWORD = $guardada }
    }
    Write-Host "Para borrar esta base de prueba cuando ya no la necesites: psql -U $Usuario -d postgres -c `"DROP DATABASE $BaseNueva`""
} catch {
    if ($creada) { & $psql @conexion -d postgres -c "DROP DATABASE IF EXISTS `"$BaseNueva`" WITH (FORCE)" | Out-Null; Write-Warning "La restauración falló; se eliminó la base nueva incompleta «$BaseNueva» (solo la que este comando creó)." }
    throw
}
