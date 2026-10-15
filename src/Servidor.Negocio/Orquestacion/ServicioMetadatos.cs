using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Orquestacion;

public sealed class OpcionesMetadatos
{
    /// <summary>Vida de un metadato en la base (RNF-02): 24 h, configurable. Un dato de exactamente esa edad ya no es vigente.</summary>
    public TimeSpan VigenciaCache { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Tiempo máximo de una operación compartida (la consulta real a NuGet). Cada llamador además respeta su propio presupuesto.</summary>
    public TimeSpan TiempoMaximoOperacion { get; set; } = TimeSpan.FromSeconds(120);
}

/// <summary>
/// Política de caché primero con modo degradado (CU-04). Orden: (1) caché vigente → sin tocar NuGet; (2) NuGet → se guarda en la caché;
/// (3) NuGet no responde → copia VENCIDA con su fecha original y una advertencia; (4) ni respuesta ni copia → «no determinado» con motivo.
/// Peticiones simultáneas equivalentes comparten una sola consulta (la clave incluye paquete, versión declarada y TFM: nunca se mezclan).
/// Un 404 de NuGet es una respuesta autoritativa: no se sustituye por una copia antigua. Los fallos de la caché (base de datos) nunca hacen fallar
/// la consulta: se continúa con NuGet y se advierte. Las escrituras de la caché ocurren fuera de cualquier transacción de resultados.
/// </summary>
public sealed class ServicioMetadatos(
    IClienteNuGet nuget, IMetadatoRepositorio cache, IInferidorCompatibilidad inferidor, TimeProvider tiempo, OpcionesMetadatos? opciones = null)
{
    private readonly OpcionesMetadatos _opciones = opciones ?? new OpcionesMetadatos();
    private readonly Deduplicador<string, ResultadoMetadato> _enVuelo = new();

    public int ConsultasEnVuelo => _enVuelo.EnVuelo;

    /// <summary>Clave de equivalencia: paquete (sin distinguir mayúsculas) + versión declarada normalizada + conjunto de TFM. Versiones o TFM distintos nunca comparten consulta.</summary>
    public static string Clave(ConsultaPaquete c)
    {
        var version = string.IsNullOrWhiteSpace(c.VersionDeclarada) ? "(ultima)" : NormalizadorPaquetes.VersionNormalizada(c.VersionDeclarada);
        var tfms = string.Join(',', c.FrameworksEvaluados.Select(t => t.Trim().ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal));
        return $"{NormalizadorPaquetes.Identificador(c.Paquete)}|{version}|{tfms}";
    }

    public async Task<ResultadoMetadato> ObtenerAsync(ConsultaPaquete consulta, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        ct.ThrowIfCancellationRequested();

        // Presupuesto ya agotado: no se inicia ninguna consulta a NuGet, pero la caché (local y rápida) todavía puede aportar un resultado útil.
        if (presupuesto?.Agotado == true)
            return await SoloCacheAsync(consulta, MotivoSinDatos.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.", ct);

        try
        {
            return await _enVuelo.EjecutarAsync(Clave(consulta), t => ResolverAsync(consulta, t), ct, presupuesto?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && presupuesto?.Agotado == true)
        {
            // Se agotó el presupuesto de ESTE llamador: deja de esperar (la consulta compartida sigue para quien la necesite) y usa solo la caché.
            return await SoloCacheAsync(consulta, MotivoSinDatos.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis mientras se consultaba NuGet.", ct);
        }
    }

    // ---------- operación compartida ----------

    private sealed record DatosCache(MetadatoLibreria? Declarada, MetadatoLibreria? UltimaEstable, SeleccionPaquete? Seleccion)
    {
        public bool HayDatos => Declarada is not null || Seleccion is not null;
    }

    private async Task<ResultadoMetadato> ResolverAsync(ConsultaPaquete c, CancellationToken ct)
    {
        var avisos = new List<Advertencia>();
        var datos = await LeerCacheAsync(c, avisos, ct);
        if (EsVigente(datos, c))
            return DesdeCache(c, datos, OrigenDato.CacheVigente, avisos, 0);

        using var presupuesto = new PresupuestoTiempo(_opciones.TiempoMaximoOperacion, tiempo);
        ResultadoNuGet<InformacionPaquete> r;
        try
        {
            r = await nuget.ObtenerPaqueteAsync(c, presupuesto, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            r = ResultadoNuGet<InformacionPaquete>.NoDisponible(CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo de la consulta.");
        }

        switch (r.Estado)
        {
            case EstadoConsulta.Correcto:
                avisos.AddRange(r.Advertencias);
                await GuardarEnCacheAsync(r.Valor!, avisos, ct);
                return new ResultadoMetadato(OrigenDato.Nuget, r.Valor, r.Valor!.FechaConsulta, null, null, avisos, r.SolicitudesHttp);

            case EstadoConsulta.NoEncontrado:
                avisos.AddRange(r.Advertencias);
                // Respuesta autoritativa de NuGet: no se reemplaza por una copia antigua.
                return Sin(MotivoSinDatos.NoExisteEnNuGet, r.Mensaje ?? "El paquete no existe en nuget.org.", avisos, r.SolicitudesHttp);

            default:
                avisos.AddRange(r.Advertencias);
                var motivo = Motivo(r.Causa);
                var porPresupuesto = r.Causa == CausaFallo.PresupuestoAgotado;
                if (datos.HayDatos)
                {
                    avisos.Add(new("cache_expirada",
                        $"NuGet no respondió ({r.Mensaje}); se usan datos guardados el {Fecha(datos)} (caché vencida).", c.Paquete));
                    return DesdeCache(c, datos, OrigenDato.CacheExpirada, avisos, r.SolicitudesHttp, porPresupuesto);
                }

                avisos.Add(new("sin_datos", "NuGet no respondió y no hay una copia guardada.", c.Paquete));
                return Sin(motivo, r.Mensaje ?? "NuGet no respondió y no hay copia guardada.", avisos, r.SolicitudesHttp, porPresupuesto);
        }
    }

    private async Task<ResultadoMetadato> SoloCacheAsync(ConsultaPaquete c, MotivoSinDatos motivo, string causa, CancellationToken ct)
    {
        var avisos = new List<Advertencia>();
        var datos = await LeerCacheAsync(c, avisos, ct);
        if (EsVigente(datos, c)) return DesdeCache(c, datos, OrigenDato.CacheVigente, avisos, 0, porPresupuesto: true);
        if (datos.HayDatos)
        {
            avisos.Add(new("cache_expirada", $"{causa} Se usan datos guardados el {Fecha(datos)} (caché vencida).", c.Paquete));
            return DesdeCache(c, datos, OrigenDato.CacheExpirada, avisos, 0, porPresupuesto: true);
        }

        avisos.Add(new("sin_datos", $"{causa} No hay una copia guardada.", c.Paquete));
        return Sin(motivo, causa, avisos, 0, porPresupuesto: true);
    }

    // ---------- caché ----------

    private async Task<DatosCache> LeerCacheAsync(ConsultaPaquete c, List<Advertencia> avisos, CancellationToken ct)
    {
        try
        {
            var declarada = string.IsNullOrWhiteSpace(c.VersionDeclarada) ? null : await cache.BuscarAsync(c.Paquete, c.VersionDeclarada, ct);
            var seleccion = await cache.BuscarSeleccionAsync(c.Paquete, ct);
            var ultima = seleccion?.UltimaVersionEstable is { } v ? await cache.BuscarAsync(c.Paquete, v, ct) : null;
            return new DatosCache(declarada, ultima, seleccion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La base de datos no responde: no se pierde el análisis, se consulta NuGet directamente.
            avisos.Add(new("cache_no_disponible", "No se pudo leer la caché; se consultó NuGet directamente.", c.Paquete));
            return new DatosCache(null, null, null);
        }
    }

    /// <summary>Vigente = selección completa y reciente, y (si hay versión declarada) su fila también reciente. Una selección incompleta se reintenta.</summary>
    private bool EsVigente(DatosCache d, ConsultaPaquete c)
    {
        if (d.Seleccion is null || !d.Seleccion.SeleccionCompleta || !Fresca(d.Seleccion.FechaConsulta)) return false;
        return string.IsNullOrWhiteSpace(c.VersionDeclarada) || d.Declarada is not null && Fresca(d.Declarada.FechaConsulta);
    }

    private bool Fresca(DateTimeOffset fecha) => tiempo.GetUtcNow() - fecha < _opciones.VigenciaCache;

    private async Task GuardarEnCacheAsync(InformacionPaquete info, List<Advertencia> avisos, CancellationToken ct)
    {
        var paraCache = ConversorCache.Convertir(info);
        avisos.AddRange(paraCache.Advertencias);
        try
        {
            foreach (var fila in paraCache.Metadatos) await cache.GuardarAsync(fila, ct);
            if (paraCache.Seleccion is not null) await cache.GuardarSeleccionAsync(paraCache.Seleccion, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El resultado obtenido de NuGet sigue siendo válido aunque no se pudo guardar en la caché.
            avisos.Add(new("cache_no_guardada", "No se pudo guardar el resultado en la caché; el análisis continúa con los datos obtenidos.", info.Paquete));
        }
    }

    private ResultadoMetadato DesdeCache(ConsultaPaquete c, DatosCache d, OrigenDato origen, List<Advertencia> avisos, int solicitudes, bool porPresupuesto = false)
    {
        var info = ConversorCache.Reconstruir(c.Paquete, d.Declarada, d.UltimaEstable, d.Seleccion, c.FrameworksEvaluados,
            !string.IsNullOrWhiteSpace(c.VersionDeclarada), inferidor);
        if (!info.SeleccionCompleta)
            avisos.Add(new("seleccion_incompleta",
                $"La selección de versiones de {c.Paquete} guardada en la caché es incompleta: la última versión estable y la compatible pueden no ser las más recientes.", c.Paquete));
        if (!string.IsNullOrWhiteSpace(c.VersionDeclarada) && info.EstadoDeclarada == EstadoVersionDeclarada.NoDeterminada)
            avisos.Add(new("version_declarada_no_determinada", $"La caché no tiene datos de la versión declarada de {c.Paquete}.", c.Paquete));
        if (info.Descargas is null && info.DescargasNota is not null)
            avisos.Add(new("descargas_no_disponibles", info.DescargasNota, c.Paquete));

        return new ResultadoMetadato(origen, info, info.FechaConsulta, null, null, avisos, solicitudes, porPresupuesto);
    }

    private static ResultadoMetadato Sin(MotivoSinDatos motivo, string mensaje, List<Advertencia> avisos, int solicitudes, bool porPresupuesto = false) =>
        new(OrigenDato.NoDeterminado, null, null, motivo, mensaje, avisos, solicitudes, porPresupuesto);

    private static MotivoSinDatos Motivo(CausaFallo? causa) => causa switch
    {
        CausaFallo.PresupuestoAgotado => MotivoSinDatos.PresupuestoAgotado,
        CausaFallo.CircuitoAbierto => MotivoSinDatos.CircuitoAbierto,
        _ => MotivoSinDatos.NuGetNoDisponible,
    };

    private static string Fecha(DatosCache d)
    {
        var fechas = new[] { d.Declarada?.FechaConsulta, d.UltimaEstable?.FechaConsulta, d.Seleccion?.FechaConsulta }.OfType<DateTimeOffset>();
        return fechas.Min().ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
    }
}
