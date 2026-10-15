using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Nuget;

/// <summary>Lo que se puede guardar en la caché de la base y las advertencias de lo que no cupo.</summary>
public sealed record ParaCache(
    IReadOnlyList<MetadatoLibreria> Metadatos,
    SeleccionPaquete? Seleccion,
    IReadOnlyList<Advertencia> Advertencias);

/// <summary>
/// Convierte lo leído de NuGet en filas de caché y reconstruye un <see cref="InformacionPaquete"/> desde ellas, de modo que un análisis servido
/// desde la caché produzca el MISMO diagnóstico y las mismas recomendaciones que uno hecho con NuGet.
/// <para>
/// Se conserva en la caché: fecha de publicación, licencia (texto completo, sin límite) <b>y su tipo tal como lo recibió NuGet</b>, grupos de framework
/// (incluido el grupo «para todos»), deprecado, <b>razones y mensaje de la deprecación</b>, <b>paquete alterno y su rango</b>, descargas y la selección de
/// versiones (última estable y última compatible por TFM, con su bandera de completitud).
/// </para>
/// <para>
/// NO se conserva (limitación documentada; no cambia estados ni recomendaciones): vulnerabilidades (no se persisten), la última preliminar y si la versión
/// estaba listada (se aproxima por la existencia de fecha). El tipo de licencia NUNCA se deduce del texto: una fila sin tipo (p. ej. anterior a esta
/// migración) lo reconstruye como desconocido. La «última publicación» reconstruida es la de la última estable.
/// </para>
/// Tratamiento por campo de lo que excede los límites de la base (nunca se recorta en silencio ni se descarta el análisis): un identificador de más de 200
/// caracteres o una versión normalizada de más de 100 no se cachea (advertencia); el rango alterno de más de 100 ya lo descarta el mapeador (advertencia).
/// </summary>
public static class ConversorCache
{
    public static ParaCache Convertir(InformacionPaquete info)
    {
        var avisos = new List<Advertencia>();
        var filas = new List<MetadatoLibreria>();

        foreach (var version in VersionesACachear(info))
            if (Metadato(info, version, avisos) is { } fila)
                filas.Add(fila);

        return new ParaCache(filas, Seleccion(info, avisos), avisos);
    }

    private static IEnumerable<VersionPublicada> VersionesACachear(InformacionPaquete info)
    {
        var vistas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in new[] { info.Declarada, info.UltimaEstable })
            if (v is not null && vistas.Add(v.Version))
                yield return v;
    }

    private static MetadatoLibreria? Metadato(InformacionPaquete info, VersionPublicada v, List<Advertencia> avisos)
    {
        if (LimitesDatos.Excede(info.Paquete, LimitesDatos.NombrePaquete))
        {
            avisos.Add(new("identificador_demasiado_largo_para_cache",
                $"El identificador de «{LimitesDatos.Extracto(info.Paquete)}» supera {LimitesDatos.NombrePaquete} caracteres; no se guarda en la caché.", info.Paquete));
            return null;
        }

        var version = NormalizadorPaquetes.VersionNormalizada(v.Version);
        if (LimitesDatos.Excede(version, LimitesDatos.Version))
        {
            avisos.Add(new("version_demasiado_larga_para_cache",
                $"La versión «{LimitesDatos.Extracto(version)}» de {info.Paquete} supera {LimitesDatos.Version} caracteres; no se guarda en la caché.", info.Paquete));
            return null;
        }

        var alterno = v.Deprecacion?.PaqueteAlterno;
        var rango = v.Deprecacion?.RangoVersionAlterno;
        if (alterno is not null && LimitesDatos.Excede(alterno, LimitesDatos.NombrePaquete))
        {
            avisos.Add(new("alterno_demasiado_largo_para_cache", $"El paquete alterno de {info.Paquete} supera {LimitesDatos.NombrePaquete} caracteres; no se guarda en la caché.", info.Paquete));
            alterno = null;
            rango = null;
        }

        if (rango is not null && LimitesDatos.Excede(rango, LimitesDatos.RangoAlterno))
        {
            avisos.Add(new("rango_alterno_demasiado_largo_para_cache", $"El rango del paquete alterno de {info.Paquete} supera {LimitesDatos.RangoAlterno} caracteres; no se guarda en la caché.", info.Paquete));
            rango = null;
        }

        return new MetadatoLibreria(
            info.Paquete,
            version,
            v.Publicada,
            info.Descargas,                  // total de la búsqueda de NuGet; null si no se obtuvo (nunca 0)
            v.Licencia,                      // texto completo: la columna es text
            v.GruposFramework.Count == 0 ? null : v.GruposFramework.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), // conserva el grupo «para todos» ("")
            CompatibleAot: null,             // AOT no se evalúa en esta versión
            Deprecado: v.Deprecacion is not null,
            info.FechaConsulta,
            alterno,
            rango,
            v.Deprecacion?.Razones.ToList(),
            v.Deprecacion?.Mensaje,
            v.TipoLicencia);   // el tipo recibido (null = desconocido)
    }

    private static SeleccionPaquete? Seleccion(InformacionPaquete info, List<Advertencia> avisos)
    {
        if (LimitesDatos.Excede(info.Paquete, LimitesDatos.NombrePaquete)) return null; // ya advertido en el metadato

        var ultima = info.UltimaEstable?.Version;
        if (ultima is not null && LimitesDatos.Excede(ultima, LimitesDatos.Version))
        {
            avisos.Add(new("version_demasiado_larga_para_cache",
                $"La última versión estable de {info.Paquete} supera {LimitesDatos.Version} caracteres; no se guarda en la caché de selección.", info.Paquete));
            return null;
        }

        var compatibles = new Dictionary<string, string>();
        foreach (var (tfm, version) in info.UltimaEstableCompatiblePorTfm)
        {
            if (version is null) continue;
            if (LimitesDatos.Excede(version.Version, LimitesDatos.Version))
            {
                avisos.Add(new("version_demasiado_larga_para_cache",
                    $"Una versión compatible de {info.Paquete} para {tfm} supera {LimitesDatos.Version} caracteres; se omite de la caché de selección.", info.Paquete));
                continue;
            }

            compatibles[tfm] = version.Version;
        }

        return new SeleccionPaquete(info.Paquete, ultima, compatibles, info.SeleccionCompleta, info.FechaConsulta);
    }

    /// <summary>
    /// Reconstruye lo que se sabe de un paquete desde la caché. <paramref name="declarada"/> y <paramref name="ultimaEstable"/> son las filas de
    /// <c>metadato_libreria</c> (si existen); <paramref name="seleccion"/> es la fila de <c>seleccion_paquete</c>. La compatibilidad de la declarada
    /// se vuelve a inferir con los grupos guardados.
    /// </summary>
    public static InformacionPaquete Reconstruir(
        string paquete, MetadatoLibreria? declarada, MetadatoLibreria? ultimaEstable, SeleccionPaquete? seleccion,
        IReadOnlyList<string> tfms, bool versionDeclaradaSolicitada, IInferidorCompatibilidad inferidor)
    {
        var fechas = new[] { declarada?.FechaConsulta, ultimaEstable?.FechaConsulta, seleccion?.FechaConsulta }.OfType<DateTimeOffset>().ToList();
        if (fechas.Count == 0) throw new InvalidOperationException("No hay datos en la caché para reconstruir el paquete.");

        var decl = declarada is null ? null : ADeVersion(declarada);
        var estado = !versionDeclaradaSolicitada ? EstadoVersionDeclarada.NoSolicitada
            : declarada is not null ? EstadoVersionDeclarada.Publicada
            : EstadoVersionDeclarada.NoDeterminada;

        var tfmsUnicos = tfms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var compatibilidad = decl is null ? [] : tfmsUnicos.Select(t => inferidor.Inferir(t, decl.GruposFramework)).ToList();

        VersionPublicada? estable = null;
        if (seleccion?.UltimaVersionEstable is { } vEstable)
            estable = ultimaEstable is not null && NormalizadorPaquetes.VersionNormalizada(ultimaEstable.Version) == NormalizadorPaquetes.VersionNormalizada(vEstable)
                ? ADeVersion(ultimaEstable)
                : Minima(vEstable);

        var porTfm = new Dictionary<string, VersionPublicada?>(StringComparer.OrdinalIgnoreCase);
        foreach (var tfm in tfmsUnicos)
            porTfm[tfm] = seleccion is not null && seleccion.UltimaCompatiblePorTfm.TryGetValue(tfm, out var v) ? Minima(v) : null;

        var descargas = declarada?.Descargas ?? ultimaEstable?.Descargas;
        return new InformacionPaquete(
            paquete, estado, decl, compatibilidad, estable,
            UltimaPrerelease: null,
            porTfm,
            UltimaPublicacion: estable?.Publicada,
            SeleccionCompleta: seleccion?.SeleccionCompleta ?? false,
            PaginasLeidas: 0, PaginasTotales: 0, VersionesLeidas: 0,
            FechaConsulta: fechas.Min(),
            Descargas: descargas,
            DescargasNota: descargas is null ? "La caché no tiene el total de descargas (NuGet no lo informó); no se rellena con 0." : null);
    }

    private static VersionPublicada ADeVersion(MetadatoLibreria m)
    {
        var normal = NormalizadorPaquetes.VersionNormalizada(m.Version);
        // El tipo es el recibido de NuGet y guardado; si la fila no lo tiene, queda desconocido (no se deduce del texto).
        var deprecacion = m.Deprecado == true ? new Deprecacion(m.DeprecacionRazones ?? [], m.DeprecacionMensaje, m.PaqueteAlterno, m.RangoAlterno) : null;
        return new VersionPublicada(
            normal, m.Version, normal.Contains('-'), m.FechaPublicacion is not null, m.FechaPublicacion, m.Licencia, m.TipoLicencia, deprecacion,
            m.FrameworksObjetivo ?? [], []);
    }

    /// <summary>Solo se sabe el número de versión (viene de la selección): el resto queda sin determinar.</summary>
    private static VersionPublicada Minima(string version) =>
        new(NormalizadorPaquetes.VersionNormalizada(version), version, false, true, null, null, null, null, [], []);
}
