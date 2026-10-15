using Microsoft.EntityFrameworkCore;
using Servidor.Datos.Entidades;
using Servidor.Datos.Mapeo;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Repositorios;

public sealed class MetadatoRepositorio(IDbContextFactory<AppDbContext> fabrica) : IMetadatoRepositorio
{
    public async Task<MetadatoLibreria?> BuscarAsync(string nombrePaquete, string version, CancellationToken ct = default)
    {
        var nombre = NormalizadorPaquetes.Identificador(nombrePaquete);
        var normal = NormalizadorPaquetes.VersionNormalizada(version);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var fila = await db.Metadatos.AsNoTracking()
            .FirstOrDefaultAsync(m => m.NombrePaquete.ToLower() == nombre && m.Version == normal, ct);
        return fila is null ? null : ADominio(fila);
    }

    public async Task GuardarAsync(MetadatoLibreria m, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(m);
        ValidadorLimites.Validar(m);
        var version = NormalizadorPaquetes.VersionNormalizada(m.Version);
        // La normalización puede alargar la versión (p. ej. «1» pasa a «1.0.0»): el límite se comprueba sobre lo que realmente se guarda.
        ValidadorLimites.Exigir(version, "metadato_libreria.version (normalizada)", LimitesDatos.Version);
        var frameworks = m.FrameworksObjetivo is null ? null : Serializacion.AJson(m.FrameworksObjetivo);
        var publicacion = m.FechaPublicacion?.ToUniversalTime();
        var consulta = m.FechaConsulta.ToUniversalTime();

        await using var db = await fabrica.CreateDbContextAsync(ct);
        // Upsert atómico: varias solicitudes simultáneas por el mismo paquete y versión no duplican ni fallan.
        // Un dato más antiguo nunca pisa a uno más reciente. El nombre conserva las mayúsculas de la primera inserción.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO metadato_libreria
                (nombre_paquete, version, fecha_publicacion, descargas, licencia, frameworks_objetivo, compatible_aot, deprecado, fecha_consulta)
            VALUES
                ({m.NombrePaquete.Trim()}, {version}, {publicacion}::timestamptz, {m.Descargas}::bigint, {m.Licencia}::varchar,
                 {frameworks}::jsonb, {m.CompatibleAot}::boolean, {m.Deprecado}::boolean, {consulta}::timestamptz)
            ON CONFLICT (lower(nombre_paquete), version) DO UPDATE SET
                fecha_publicacion   = EXCLUDED.fecha_publicacion,
                descargas           = EXCLUDED.descargas,
                licencia            = EXCLUDED.licencia,
                frameworks_objetivo = EXCLUDED.frameworks_objetivo,
                compatible_aot      = EXCLUDED.compatible_aot,
                deprecado           = EXCLUDED.deprecado,
                fecha_consulta      = EXCLUDED.fecha_consulta
            WHERE metadato_libreria.fecha_consulta <= EXCLUDED.fecha_consulta
            """, ct);
    }

    public async Task<int> PurgarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        // Los diagnósticos conservan su copia de indicadores; su metadato_id pasa a NULL por ON DELETE SET NULL.
        return await db.Metadatos.Where(m => m.FechaConsulta < utc).ExecuteDeleteAsync(ct);
    }

    public async Task<SeleccionPaquete?> BuscarSeleccionAsync(string nombrePaquete, CancellationToken ct = default)
    {
        var nombre = NormalizadorPaquetes.Identificador(nombrePaquete);
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var fila = await db.Selecciones.AsNoTracking().FirstOrDefaultAsync(s => s.NombrePaquete.ToLower() == nombre, ct);
        return fila is null
            ? null
            : new SeleccionPaquete(
                fila.NombrePaquete,
                fila.UltimaVersionEstable,
                Serializacion.DeJson<Dictionary<string, string>>(fila.UltimaCompatiblePorTfm),
                fila.SeleccionCompleta,
                fila.FechaConsulta);
    }

    public async Task GuardarSeleccionAsync(SeleccionPaquete s, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(s);
        ValidadorLimites.Validar(s);
        var compatibles = Serializacion.AJson(s.UltimaCompatiblePorTfm);
        var consulta = s.FechaConsulta.ToUniversalTime();
        await using var db = await fabrica.CreateDbContextAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO seleccion_paquete
                (nombre_paquete, ultima_version_estable, ultima_compatible_por_tfm, seleccion_completa, fecha_consulta)
            VALUES
                ({s.NombrePaquete.Trim()}, {s.UltimaVersionEstable}::varchar, {compatibles}::jsonb, {s.SeleccionCompleta}, {consulta}::timestamptz)
            ON CONFLICT (lower(nombre_paquete)) DO UPDATE SET
                ultima_version_estable    = EXCLUDED.ultima_version_estable,
                ultima_compatible_por_tfm = EXCLUDED.ultima_compatible_por_tfm,
                seleccion_completa        = EXCLUDED.seleccion_completa,
                fecha_consulta            = EXCLUDED.fecha_consulta
            WHERE seleccion_paquete.fecha_consulta <= EXCLUDED.fecha_consulta
            """, ct);
    }

    public async Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default)
    {
        await using var db = await fabrica.CreateDbContextAsync(ct);
        var utc = limite.ToUniversalTime();
        return await db.Selecciones.Where(s => s.FechaConsulta < utc).ExecuteDeleteAsync(ct);
    }

    private static MetadatoLibreria ADominio(MetadatoLibreriaFila f) => new(
        f.NombrePaquete,
        f.Version,
        f.FechaPublicacion,
        f.Descargas,
        f.Licencia,
        f.FrameworksObjetivo is null ? null : Serializacion.DeJson<List<string>>(f.FrameworksObjetivo),
        f.CompatibleAot,
        f.Deprecado,
        f.FechaConsulta);
}
