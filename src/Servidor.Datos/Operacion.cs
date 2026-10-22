using Microsoft.EntityFrameworkCore;
using Servidor.Negocio.Operacion;

namespace Servidor.Datos;

/// <summary>Cuenta (sin borrar) lo que una purga tocaría. Mismas condiciones estrictas («anterior a») que las eliminaciones reales.</summary>
internal sealed class PurgaRepositorio(IDbContextFactory<AppDbContext> fabrica) : IContadorPurga
{
    public async Task<int> ContarAnalisisAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        return await db.Analisis.CountAsync(a => a.FechaAnalisis < utc, ct);
    }

    public async Task<int> ContarMetadatosAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        return await db.Metadatos.CountAsync(m => m.FechaConsulta < utc, ct);
    }

    public async Task<int> ContarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        return await db.Selecciones.CountAsync(s => s.FechaConsulta < utc, ct);
    }
}

/// <summary>Comprobación de disponibilidad: conexión y migraciones pendientes, con tiempo máximo. No expone nombres de migraciones ni detalles de la conexión.</summary>
internal sealed class VerificadorBase(IDbContextFactory<AppDbContext> fabrica, TimeSpan? tiempoMaximo = null) : IVerificadorBase
{
    private readonly TimeSpan _tiempoMaximo = tiempoMaximo ?? TimeSpan.FromSeconds(3);

    public async Task<EstadoBaseDatos> ComprobarAsync(CancellationToken ct = default)
    {
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(_tiempoMaximo);
        try
        {
            await using var db = await fabrica.CreateDbContextAsync(limite.Token);
            if (!await db.Database.CanConnectAsync(limite.Token)) return new EstadoBaseDatos(false, false, 0);
            var pendientes = (await db.Database.GetPendingMigrationsAsync(limite.Token)).Count();
            return new EstadoBaseDatos(true, pendientes == 0, pendientes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new EstadoBaseDatos(false, false, 0);
        }
    }
}

/// <summary>
/// Aplica las migraciones pendientes como PASO CONTROLADO (nunca al arrancar la aplicación). No recrea el esquema: aplica únicamente las migraciones pendientes (las existentes solo agregan o amplían columnas en «Up»; una futura que borrara datos debe revisarse y respaldarse antes).
/// Toma un bloqueo consultivo de PostgreSQL (<c>pg_advisory_lock</c>) durante toda la operación y calcula los pendientes DESPUÉS de obtenerlo: dos migradores simultáneos
/// no se pisan (una prueba real lo comprobó: sin este bloqueo el segundo fallaba con «ya existe la columna»).
/// </summary>
public sealed class MigradorBase(IDbContextFactory<AppDbContext> fabrica)
{
    /// <summary>Clave fija del bloqueo (hash estable de «diagnet.migraciones»).</summary>
    private const long ClaveBloqueo = 0x44494147_4E455401;

    public async Task<IReadOnlyList<string>> PendientesAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        // Sin esta comprobación EF devolvería TODAS las migraciones como pendientes si la base no existe, y «migrar» la crearía vacía.
        if (!await db.Database.CanConnectAsync(ct)) throw new InvalidOperationException("No se pudo conectar a la base de datos configurada (¿existe y están bien el host, el usuario y la contraseña?).");
        return (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
    }

    /// <summary>Devuelve las migraciones aplicadas ahora (vacío si no había pendientes).</summary>
    public async Task<IReadOnlyList<string>> MigrarAsync(CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var cadena = db.Database.GetConnectionString() ?? throw new InvalidOperationException("No hay cadena de conexión configurada.");

        // Una conexión aparte guarda el bloqueo mientras dura la migración; al cerrarse (incluso por un fallo) PostgreSQL lo libera solo.
        await using var bloqueo = new Npgsql.NpgsqlConnection(cadena);
        await bloqueo.OpenAsync(ct);
        await using (var tomar = new Npgsql.NpgsqlCommand("SELECT pg_advisory_lock(@k)", bloqueo))
        {
            tomar.Parameters.AddWithValue("k", ClaveBloqueo);
            await tomar.ExecuteNonQueryAsync(ct);
        }

        var pendientes = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pendientes.Count > 0) await db.Database.MigrateAsync(ct);
        return pendientes;
    }
}
