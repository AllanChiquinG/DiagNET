using System.Globalization;
using Servidor.Datos;
using Servidor.Negocio.Operacion;

namespace Servidor.Api.Operacion;

/// <summary>
/// Operaciones de mantenimiento por línea de comandos, con la MISMA configuración que la aplicación (incluidas las variables de entorno y <c>appsettings.Local.json</c>):
/// <code>
/// dotnet Servidor.Api.dll operacion estado              migraciones pendientes (no cambia nada)
/// dotnet Servidor.Api.dll operacion migrar              aplica las migraciones pendientes (paso controlado, con bloqueo; haz un respaldo antes)
/// dotnet Servidor.Api.dll operacion purgar              SIMULA la purga: cuenta lo que eliminaría y no borra nada
/// dotnet Servidor.Api.dll operacion purgar --ejecutar   elimina de verdad (requiere la bandera explícita)
/// </code>
/// Nunca imprime la cadena de conexión ni identificadores de análisis.
/// </summary>
public static class Comandos
{
    public static bool EsComando(string[] args) => args.Length >= 1 && string.Equals(args[0], "operacion", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> EjecutarAsync(IServiceProvider servicios, string[] args, TextWriter salida, TextWriter error, CancellationToken ct = default)
    {
        var verbo = args.Length >= 2 ? args[1].ToLowerInvariant() : "";
        try
        {
            switch (verbo)
            {
                case "estado":
                    return await EstadoAsync(servicios, salida, ct);
                case "migrar":
                    return await MigrarAsync(servicios, salida, ct);
                case "purgar":
                    return await PurgarAsync(servicios, args.Skip(2).Any(a => a.Equals("--ejecutar", StringComparison.OrdinalIgnoreCase)), salida, ct);
                default:
                    await error.WriteLineAsync("Uso: operacion <estado | migrar | purgar [--ejecutar]>");
                    return 64;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin cadena de conexión ni trazas internas: solo el tipo y el mensaje.
            await error.WriteLineAsync($"La operación «{verbo}» falló: {ex.GetType().Name}: {Limpiar(ex.Message)}");
            return 1;
        }
    }

    private static async Task<int> EstadoAsync(IServiceProvider servicios, TextWriter salida, CancellationToken ct)
    {
        var migrador = servicios.GetService(typeof(MigradorBase)) as MigradorBase ?? throw new InvalidOperationException("No hay una base de datos configurada (ConnectionStrings__DiagNet).");
        var pendientes = await migrador.PendientesAsync(ct);
        await salida.WriteLineAsync(pendientes.Count == 0
            ? "Base de datos: conectada y con todas las migraciones aplicadas."
            : $"Base de datos: conectada, con {pendientes.Count} migración(es) pendiente(s): {string.Join(", ", pendientes)}. Aplícalas con «operacion migrar».");
        return pendientes.Count == 0 ? 0 : 2;
    }

    private static async Task<int> MigrarAsync(IServiceProvider servicios, TextWriter salida, CancellationToken ct)
    {
        var migrador = servicios.GetService(typeof(MigradorBase)) as MigradorBase ?? throw new InvalidOperationException("No hay una base de datos configurada (ConnectionStrings__DiagNet).");
        var aplicadas = await migrador.MigrarAsync(ct);
        await salida.WriteLineAsync(aplicadas.Count == 0
            ? "No había migraciones pendientes: no se cambió nada."
            : $"Migraciones aplicadas ({aplicadas.Count}): {string.Join(", ", aplicadas)}. Comprueba /api/salud/disponibilidad.");
        return 0;
    }

    private static async Task<int> PurgarAsync(IServiceProvider servicios, bool ejecutar, TextWriter salida, CancellationToken ct)
    {
        var purga = servicios.GetService(typeof(ServicioPurga)) as ServicioPurga ?? throw new InvalidOperationException("No hay una base de datos configurada (ConnectionStrings__DiagNet).");
        var opciones = (OpcionesPurga)servicios.GetService(typeof(OpcionesPurga))!;
        var plan = await purga.PlanearAsync(ct);
        string F(DateTimeOffset f) => f.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        await salida.WriteLineAsync($"Plazos: análisis {opciones.RetencionAnalisisDias} días (anteriores a {F(plan.LimiteAnalisis)}), caché {opciones.RetencionCacheDias} días (anteriores a {F(plan.LimiteCache)}).");
        await salida.WriteLineAsync($"Alcance: {plan.Analisis} análisis (con sus dependencias, diagnósticos y recomendaciones), {plan.MetadatosCache} metadatos de caché y {plan.SeleccionesCache} selecciones de caché.");

        if (!ejecutar)
        {
            await salida.WriteLineAsync("SIMULACIÓN: no se eliminó nada. Para eliminar de verdad, repite el comando con --ejecutar (después de haber hecho un respaldo).");
            return 0;
        }

        var r = await purga.EjecutarAsync(ct);
        await salida.WriteLineAsync($"ELIMINADO: {r.AnalisisEliminados} análisis, {r.MetadatosEliminados} metadatos de caché y {r.SeleccionesEliminadas} selecciones de caché.");
        return 0;
    }

    /// <summary>Quita de un mensaje cualquier fragmento «Clave=valor;» que pudiera venir de una cadena de conexión.</summary>
    private static string Limpiar(string mensaje) =>
        System.Text.RegularExpressions.Regex.Replace(mensaje, @"(?i)(password|pwd|username|user id|host|server)\s*=\s*[^;\s]*", "$1=***");
}
