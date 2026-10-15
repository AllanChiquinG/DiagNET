using Microsoft.EntityFrameworkCore;
using Servidor.Datos.Mapeo;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Repositorios;

/// <summary>Cada operación usa su propio <see cref="AppDbContext"/> (un DbContext nunca se comparte entre tareas paralelas).</summary>
public sealed class AnalisisRepositorio(IDbContextFactory<AppDbContext> fabrica) : IAnalisisRepositorio
{
    public async Task GuardarAsync(Analisis analisis, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(analisis);
        ValidadorLimites.Validar(analisis); // antes de abrir contexto o transacción: error claro y nada que deshacer
        var fila = MapeadorAnalisis.AFila(analisis);

        await using var db = await fabrica.CreateDbContextAsync(ct);

        // Trazabilidad opcional: enlaza cada diagnóstico con la fila de caché de su versión declarada, si existe.
        var nombres = analisis.Dependencias.Where(d => d.VersionDeclarada is not null)
            .Select(d => NormalizadorPaquetes.Identificador(d.Nombre)).Distinct().ToList();
        if (nombres.Count > 0)
        {
            var filas = await db.Metadatos.AsNoTracking()
                .Where(m => nombres.Contains(m.NombrePaquete.ToLower()))
                .Select(m => new { m.Id, m.NombrePaquete, m.Version })
                .ToListAsync(ct);
            var porClave = filas.ToDictionary(m => (NormalizadorPaquetes.Identificador(m.NombrePaquete), m.Version), m => m.Id);
            foreach (var dep in fila.Dependencias)
            {
                if (dep.VersionDeclarada is null || dep.Diagnostico is null) continue;
                var clave = (NormalizadorPaquetes.Identificador(dep.NombrePaquete), NormalizadorPaquetes.VersionNormalizada(dep.VersionDeclarada));
                if (porClave.TryGetValue(clave, out var id)) dep.Diagnostico.MetadatoId = id;
            }
        }

        // Una sola llamada a SaveChanges = una transacción: se guarda todo el resultado o nada.
        // La red nunca se consulta mientras esta transacción está abierta (los datos ya llegan resueltos).
        db.Analisis.Add(fila);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var fila = await db.Analisis.AsNoTracking().AsSplitQuery()
            .Include(a => a.Dependencias).ThenInclude(d => d.Diagnostico).ThenInclude(g => g!.Recomendaciones)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        return fila is null ? null : MapeadorAnalisis.ADominio(fila);
    }

    public async Task<Pagina<ResumenAnalisis>> ListarAsync(
        int numeroPagina, int tamanoPagina, IReadOnlyCollection<Guid>? ids = null, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(numeroPagina, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tamanoPagina, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tamanoPagina, 100);

        await using var db = await fabrica.CreateDbContextAsync(ct);
        var consulta = db.Analisis.AsNoTracking().AsQueryable();
        if (ids is not null)
        {
            var lista = ids.ToList();
            consulta = consulta.Where(a => lista.Contains(a.Id));
        }

        var total = await consulta.CountAsync(ct);
        var filas = await consulta
            .OrderByDescending(a => a.FechaAnalisis).ThenByDescending(a => a.Id)
            .Skip((numeroPagina - 1) * tamanoPagina).Take(tamanoPagina)
            .ToListAsync(ct);
        return new Pagina<ResumenAnalisis>(filas.Select(MapeadorAnalisis.AResumen).ToList(), numeroPagina, tamanoPagina, total);
    }

    public async Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        // Las dependencias, diagnósticos y recomendaciones se eliminan por ON DELETE CASCADE en la base.
        return await db.Analisis.Where(a => a.FechaAnalisis < utc).ExecuteDeleteAsync(ct);
    }
}
