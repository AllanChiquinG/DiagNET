using System.Text.Json;

namespace Servidor.Integracion.NuGet;

// DTO internos del registro de NuGet: no salen del módulo de integración (Negocio nunca ve el formato de NuGet).
// La lectura es tolerante: se ignoran las propiedades desconocidas y lo ausente queda en null. Los textos de NuGet son DATOS, no instrucciones.

internal sealed record DeprecacionRegistro(IReadOnlyList<string> Razones, string? Mensaje, string? PaqueteAlterno, string? RangoAlterno);

internal sealed record VulnerabilidadRegistro(string? Severidad, string? Url);

internal sealed record HojaRegistro(
    string? Version,
    string? Publicada,
    bool? Listada,
    string? LicenciaExpresion,
    string? LicenciaUrl,
    IReadOnlyList<string> Grupos,
    DeprecacionRegistro? Deprecacion,
    IReadOnlyList<VulnerabilidadRegistro> Vulnerabilidades);

internal sealed record PaginaRegistro(string? Url, string? Inferior, string? Superior, int? Conteo, IReadOnlyList<HojaRegistro>? Hojas);

internal sealed record IndiceRegistro(IReadOnlyList<PaginaRegistro> Paginas);

internal sealed record ResultadoBusqueda(string? Id, string? Version, long? Descargas, bool? Verificado, string? Descripcion);

/// <summary>JSON con estructura inesperada o incompleta: no se reintenta (repetir no lo arregla).</summary>
internal sealed class RespuestaInvalidaException(string mensaje, Exception? interna = null) : Exception(mensaje, interna);

internal static class ParserRegistro
{
    private static readonly JsonDocumentOptions Opciones = new() { MaxDepth = 32, AllowTrailingCommas = true };

    private static JsonDocument Abrir(byte[] cuerpo)
    {
        try
        {
            return JsonDocument.Parse(cuerpo, Opciones);
        }
        catch (JsonException ex)
        {
            throw new RespuestaInvalidaException("La respuesta de NuGet no es un JSON válido.", ex);
        }
    }

    public static IndiceRegistro ParseIndice(byte[] cuerpo)
    {
        using var doc = Abrir(cuerpo);
        var raiz = doc.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new RespuestaInvalidaException("El registro del paquete no tiene la lista «items».");

        return new IndiceRegistro(items.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(ParsePaginaElemento).ToList());
    }

    public static PaginaRegistro ParsePagina(byte[] cuerpo)
    {
        using var doc = Abrir(cuerpo);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new RespuestaInvalidaException("La página del registro no es un objeto JSON.");
        return ParsePaginaElemento(doc.RootElement);
    }

    private static PaginaRegistro ParsePaginaElemento(JsonElement p)
    {
        IReadOnlyList<HojaRegistro>? hojas = null;
        if (p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            hojas = items.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.Object).Select(ParseHoja).OfType<HojaRegistro>().ToList();

        return new PaginaRegistro(Texto(p, "@id"), Texto(p, "lower"), Texto(p, "upper"), Entero(p, "count"), hojas);
    }

    private static HojaRegistro? ParseHoja(JsonElement hoja)
    {
        if (!hoja.TryGetProperty("catalogEntry", out var ce) || ce.ValueKind != JsonValueKind.Object) return null;

        var grupos = new List<string>();
        if (ce.TryGetProperty("dependencyGroups", out var gs) && gs.ValueKind == JsonValueKind.Array)
            foreach (var g in gs.EnumerateArray())
                if (g.ValueKind == JsonValueKind.Object)
                    grupos.Add(Texto(g, "targetFramework") ?? ""); // sin targetFramework = grupo para todos

        DeprecacionRegistro? deprecacion = null;
        if (ce.TryGetProperty("deprecation", out var dep) && dep.ValueKind == JsonValueKind.Object)
        {
            var razones = new List<string>();
            if (dep.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array)
                razones.AddRange(rs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!));
            string? alterno = null, rango = null;
            if (dep.TryGetProperty("alternatePackage", out var alt) && alt.ValueKind == JsonValueKind.Object)
            {
                alterno = Texto(alt, "id");
                rango = Texto(alt, "range");
            }

            deprecacion = new DeprecacionRegistro(razones, Texto(dep, "message"), alterno, rango);
        }

        var vulns = new List<VulnerabilidadRegistro>();
        if (ce.TryGetProperty("vulnerabilities", out var vs) && vs.ValueKind == JsonValueKind.Array)
            foreach (var v in vs.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object))
                vulns.Add(new VulnerabilidadRegistro(TextoONumero(v, "severity"), Texto(v, "advisoryUrl")));

        bool? listada = ce.TryGetProperty("listed", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False ? l.GetBoolean() : null;
        return new HojaRegistro(Texto(ce, "version"), Texto(ce, "published"), listada, Texto(ce, "licenseExpression"), Texto(ce, "licenseUrl"), grupos, deprecacion, vulns);
    }

    public static IReadOnlyList<ResultadoBusqueda> ParseBusqueda(byte[] cuerpo)
    {
        using var doc = Abrir(cuerpo);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new RespuestaInvalidaException("La respuesta de búsqueda no tiene la lista «data».");

        var lista = new List<ResultadoBusqueda>();
        foreach (var x in data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
        {
            long? descargas = x.TryGetProperty("totalDownloads", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var n) && n >= 0 ? n : null;
            bool? verificado = x.TryGetProperty("verified", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            lista.Add(new ResultadoBusqueda(Texto(x, "id"), Texto(x, "version"), descargas, verificado, Texto(x, "description")));
        }

        return lista;
    }

    public static IReadOnlyList<(string Tipo, string Url)> ParseServicios(byte[] cuerpo, out string? version)
    {
        using var doc = Abrir(cuerpo);
        var raiz = doc.RootElement;
        version = raiz.ValueKind == JsonValueKind.Object ? Texto(raiz, "version") : null;
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("resources", out var rs) || rs.ValueKind != JsonValueKind.Array)
            throw new RespuestaInvalidaException("El índice de servicios no tiene la lista «resources».");

        var lista = new List<(string, string)>();
        foreach (var r in rs.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Object))
        {
            // «@type» puede ser un texto o una lista de textos.
            var url = Texto(r, "@id");
            if (url is null || !r.TryGetProperty("@type", out var t)) continue;
            if (t.ValueKind == JsonValueKind.String) lista.Add((t.GetString()!, url));
            else if (t.ValueKind == JsonValueKind.Array)
                foreach (var tipo in t.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)) lista.Add((tipo.GetString()!, url));
        }

        return lista;
    }

    private static string? Texto(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? TextoONumero(JsonElement e, string nombre) =>
        !e.TryGetProperty(nombre, out var v) ? null : v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };

    private static int? Entero(JsonElement e, string nombre) =>
        e.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}
