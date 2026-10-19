using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

internal enum EstadoLectura { Correcto, NoEncontrado, Fallo }

internal sealed record ResultadoLectura(
    EstadoLectura Estado, InformacionPaquete? Info, CausaFallo? Causa, string? Mensaje, IReadOnlyList<Advertencia> Advertencias, int Solicitudes);

internal sealed record DocumentoLeido<T>(EstadoHttp Estado, T? Valor, CausaFallo? Causa, string? Detalle, int Solicitudes) where T : class;

/// <summary>
/// Lee el registro de un paquete (índice → páginas → hojas) con paginación acotada y elige versiones.
/// Las páginas se recorren de la más NUEVA a la más antigua y solo se descargan las necesarias (las que no traen hojas incluidas) hasta que:
/// (a) se halla la última estable, (b) se halla la última estable compatible de cada TFM, y (c) se localiza la versión declarada.
/// Una última página con solo preliminares o no listadas, o con estables incompatibles, NO detiene la búsqueda. Si el presupuesto de páginas se agota
/// antes de saberlo, la selección se marca incompleta: un máximo parcial jamás se presenta como global.
/// </summary>
internal sealed class LectorRegistro(
    EjecutorHttp http, OpcionesNuGet opciones, MapeadorMetadatos mapeador, InferidorCompatibilidad inferidor,
    CacheDocumentos cache, TimeProvider tiempo, ILogger log)
{
    public async Task<ResultadoLectura> LeerAsync(
        Uri registroBase, string id, NuGetVersion? declarada, bool declaradaNoInterpretable, IReadOnlyList<string> tfms,
        PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        var avisos = new List<Advertencia>();
        var solicitudes = 0;

        var indiceDoc = await DocumentoAsync(new Uri(registroBase, id.ToLowerInvariant() + "/index.json"), ParserRegistro.ParseIndice, presupuesto, ct);
        solicitudes += indiceDoc.Solicitudes;
        if (indiceDoc.Estado == EstadoHttp.NoEncontrado)
            return new(EstadoLectura.NoEncontrado, null, null, "El paquete no existe en nuget.org (puede ser un paquete privado).", avisos, solicitudes);
        if (indiceDoc.Estado == EstadoHttp.Fallo)
            return new(EstadoLectura.Fallo, null, indiceDoc.Causa, indiceDoc.Detalle, avisos, solicitudes);

        var paginas = indiceDoc.Valor!.Paginas;
        var claves = paginas.Select(p => (Pagina: p, Orden: Interpretar(p.Superior))).ToList();
        var ordenConfiable = claves.All(c => c.Orden is not null);
        // NuGet entrega las páginas en orden ascendente; sin cotas interpretables solo se confía en ese orden para recorrer, no para dar por definitivo un máximo.
        var ordenadas = ordenConfiable
            ? claves.OrderByDescending(c => c.Orden!, VersionComparer.Default).Select(c => c.Pagina).ToList()
            : paginas.Reverse().ToList();

        var hojas = new List<HojaVersion>();
        var pendientes = new List<PaginaRegistro>();
        var sinLeer = new List<PaginaRegistro>();
        var paginasLeidas = 0;

        void Agregar(IEnumerable<HojaRegistro> lista)
        {
            foreach (var h in lista)
                if (mapeador.Mapear(h, id, avisos) is { } hoja && !hojas.Any(x => VersionComparer.Default.Equals(x.Orden, hoja.Orden)))
                    hojas.Add(hoja);
        }

        foreach (var p in ordenadas)
        {
            if (p.Hojas is not null)
            {
                Agregar(p.Hojas);
                paginasLeidas++;
            }
            else
            {
                pendientes.Add(p);
            }
        }

        var descargadas = 0;
        CausaFallo? causaParcial = null;

        while (pendientes.Count > 0)
        {
            var necesitaDeclarada = declarada is not null && !hojas.Any(h => VersionComparer.Default.Equals(h.Orden, declarada));
            var puedeMejorar = MejoraDe(hojas, tfms);

            PaginaRegistro? siguiente = null;
            if (necesitaDeclarada) siguiente = pendientes.FirstOrDefault(p => PuedeContener(p, declarada!));
            // Una página solo se omite si NO puede contener nada mayor que lo ya hallado (las pendientes van de la más nueva a la más antigua).
            // Hallar una «última estable» en una página antigua (p. ej. la de la versión declarada) no la vuelve definitiva.
            if (siguiente is null) siguiente = pendientes.FirstOrDefault(puedeMejorar);
            if (siguiente is null) break;

            if (descargadas >= opciones.MaxPaginas) break; // presupuesto de páginas agotado: lo no leído queda en «pendientes»

            pendientes.Remove(siguiente);
            if (!Uri.TryCreate(siguiente.Url, UriKind.Absolute, out var urlPagina))
            {
                avisos.Add(new("pagina_registro_invalida", $"Una página del registro de {id} no tiene una dirección válida y no se leyó.", id));
                sinLeer.Add(siguiente);
                continue;
            }

            var pagina = await DocumentoAsync(urlPagina, ParserRegistro.ParsePagina, presupuesto, ct);
            solicitudes += pagina.Solicitudes;
            descargadas++;
            if (pagina.Estado != EstadoHttp.Ok)
            {
                sinLeer.Add(siguiente);
                causaParcial ??= pagina.Causa ?? CausaFallo.RespuestaNoEsperada;
                avisos.Add(new("pagina_registro_no_leida", $"No se pudo leer una página del registro de {id}: {pagina.Detalle ?? "no encontrada"}.", id));
                if (pagina.Causa is CausaFallo.PresupuestoAgotado or CausaFallo.CircuitoAbierto) break;
                continue;
            }

            Agregar(pagina.Valor!.Hojas ?? []);
            paginasLeidas++;
        }

        if (hojas.Count == 0 && (sinLeer.Count > 0 || pendientes.Count > 0) && causaParcial is not null)
            return new(EstadoLectura.Fallo, null, causaParcial, "No se pudo leer ninguna versión del paquete.", avisos, solicitudes);

        var noLeidas = pendientes.Concat(sinLeer).ToList();
        var todasLeidas = noLeidas.Count == 0;

        var estable = SelectorVersion.UltimaEstable(hojas);
        var prerelease = SelectorVersion.UltimaPrerelease(hojas);
        var porTfm = new Dictionary<string, VersionPublicada?>(StringComparer.OrdinalIgnoreCase);
        foreach (var tfm in tfms.Distinct(StringComparer.OrdinalIgnoreCase))
            porTfm[tfm] = SelectorVersion.UltimaEstableCompatible(hojas, tfm, inferidor)?.Datos;

        // Definitiva = ninguna página sin leer (pendiente o fallida) puede contener algo mayor que lo hallado.
        // Sin cotas interpretables no se puede descartar nada: solo es definitiva si se leyó todo.
        var mejoraFinal = MejoraDe(hojas, tfms);
        var definitiva = !noLeidas.Any(mejoraFinal);

        if (!definitiva)
            avisos.Add(new("seleccion_incompleta",
                $"No se recorrió todo el registro de {id} ({paginasLeidas} de {paginas.Count} páginas): la última versión estable y la compatible " +
                "pueden no ser las más recientes. No se afirma que estén al día.", id));

        var estadoDeclarada = EstadoVersionDeclarada.NoSolicitada;
        HojaVersion? hojaDeclarada = null;
        if (declaradaNoInterpretable)
        {
            estadoDeclarada = EstadoVersionDeclarada.NoDeterminada;
        }
        else if (declarada is not null)
        {
            hojaDeclarada = SelectorVersion.Declarada(hojas, declarada);
            if (hojaDeclarada is not null) estadoDeclarada = EstadoVersionDeclarada.Publicada;
            else if (todasLeidas || ordenConfiable && noLeidas.All(p => !PuedeContener(p, declarada))) estadoDeclarada = EstadoVersionDeclarada.NoPublicada;
            else estadoDeclarada = EstadoVersionDeclarada.NoDeterminada;
        }

        if (estadoDeclarada == EstadoVersionDeclarada.NoDeterminada && !declaradaNoInterpretable)
            avisos.Add(new("version_declarada_no_determinada",
                $"No se pudo confirmar si la versión declarada de {id} existe: no se leyó todo el registro.", id));

        var compatibilidadDeclarada = hojaDeclarada is null
            ? []
            : tfms.Distinct(StringComparer.OrdinalIgnoreCase).Select(t => inferidor.Inferir(t, hojaDeclarada.Datos.GruposFramework)).ToList();

        var ultimaPublicacion = hojas.Where(h => h.Datos is { Listada: true, Publicada: not null }).Select(h => h.Datos.Publicada).Max();

        var info = new InformacionPaquete(
            id, estadoDeclarada, hojaDeclarada?.Datos, compatibilidadDeclarada, estable?.Datos, prerelease?.Datos, porTfm, ultimaPublicacion,
            definitiva, paginasLeidas, paginas.Count, hojas.Count, tiempo.GetUtcNow());
        return new(EstadoLectura.Correcto, info, null, null, avisos, solicitudes);
    }

    /// <summary>
    /// Devuelve el predicado «esta página podría contener algo mejor que lo hallado»: mejor = una versión mayor que la última estable
    /// o que la última estable compatible de algún TFM. Si algo aún no se halló, cualquier página puede mejorarlo.
    /// </summary>
    private Func<PaginaRegistro, bool> MejoraDe(IReadOnlyList<HojaVersion> hojas, IReadOnlyList<string> tfms)
    {
        var estable = SelectorVersion.UltimaEstable(hojas);
        var mejores = new List<NuGetVersion?> { estable?.Orden };
        mejores.AddRange(tfms.Distinct(StringComparer.OrdinalIgnoreCase).Select(t => SelectorVersion.UltimaEstableCompatible(hojas, t, inferidor)?.Orden));
        return pagina =>
        {
            var superior = Interpretar(pagina.Superior);
            return mejores.Any(m => m is null || superior is null || VersionComparer.Default.Compare(superior, m) > 0);
        };
    }

    private static NuGetVersion? Interpretar(string? texto) => NuGetVersion.TryParse(texto, out var v) ? v : null;

    /// <summary>Si la página puede contener la versión (cotas interpretables); sin cotas interpretables, se asume que sí.</summary>
    private static bool PuedeContener(PaginaRegistro pagina, NuGetVersion version)
    {
        var inf = Interpretar(pagina.Inferior);
        var sup = Interpretar(pagina.Superior);
        if (inf is null || sup is null) return true;
        return VersionComparer.Default.Compare(inf, version) <= 0 && VersionComparer.Default.Compare(version, sup) <= 0;
    }

    private async Task<DocumentoLeido<T>> DocumentoAsync<T>(Uri url, Func<byte[], T> parser, PresupuestoTiempo? presupuesto, CancellationToken ct) where T : class
    {
        if (cache.TryGet<T>(url, out var enCache)) return new(EstadoHttp.Ok, enCache, null, null, 0);

        var respuesta = await http.ObtenerAsync(url, presupuesto, ct);
        if (respuesta.Estado != EstadoHttp.Ok) return new(respuesta.Estado, null, respuesta.Causa, respuesta.Detalle, respuesta.Solicitudes);

        try
        {
            var valor = parser(respuesta.Cuerpo!);
            cache.Set(url, valor);
            return new(EstadoHttp.Ok, valor, null, null, respuesta.Solicitudes);
        }
        catch (RespuestaInvalidaException ex)
        {
            // No se reintenta (repetir no mejora un JSON inválido); se registra con una referencia para el administrador. Sin datos del usuario.
            var referencia = Guid.NewGuid().ToString("N")[..8];
            log.LogError(ex, "Respuesta inesperada de NuGet. Referencia {Referencia}, host {Host}", referencia, url.Host);   // sin la ruta: contiene el identificador del paquete del proyecto
            return new(EstadoHttp.Fallo, null, CausaFallo.RespuestaInvalida, $"La respuesta de NuGet tiene un formato inesperado (referencia {referencia}).", respuesta.Solicitudes);
        }
    }
}
