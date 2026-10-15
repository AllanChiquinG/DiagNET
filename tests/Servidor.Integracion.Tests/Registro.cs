using System.Text;

namespace Servidor.Integracion.Tests;

/// <summary>Construye respuestas del registro de NuGet con la estructura real (índice → páginas → hojas con catalogEntry). Los datos son ilustrativos.</summary>
internal static class Registro
{
    public static string Hoja(
        string version, bool? listada = true, string? publicada = "2025-03-14T10:22:05+00:00", string[]? grupos = null,
        string? licencia = "MIT", string? urlLicencia = null, string? deprecacion = null, string? vulnerabilidades = null, bool incluirGrupos = true)
    {
        var sb = new StringBuilder();
        sb.Append("{\"@id\":\"x\",\"catalogEntry\":{");
        sb.Append($"\"id\":\"Ejemplo.Paquete\",\"version\":\"{version}\"");
        if (listada is not null) sb.Append($",\"listed\":{(listada.Value ? "true" : "false")}");
        if (publicada is not null) sb.Append($",\"published\":\"{publicada}\"");
        if (licencia is not null) sb.Append($",\"licenseExpression\":\"{licencia}\"");
        if (urlLicencia is not null) sb.Append($",\"licenseUrl\":\"{urlLicencia}\"");
        if (incluirGrupos)
        {
            var g = grupos ?? ["net8.0"];
            sb.Append(",\"dependencyGroups\":[" + string.Join(',', g.Select(x => x.Length == 0 ? "{}" : $"{{\"targetFramework\":\"{x}\"}}")) + "]");
        }

        if (deprecacion is not null) sb.Append($",\"deprecation\":{deprecacion}");
        if (vulnerabilidades is not null) sb.Append($",\"vulnerabilities\":{vulnerabilidades}");
        sb.Append("}}");
        return sb.ToString();
    }

    public static string Deprecacion(string mensaje = "Obsoleto.", string[]? razones = null, string? alterno = null, string? rango = "[1.0.0, )") =>
        "{\"reasons\":[" + string.Join(',', (razones ?? ["Legacy"]).Select(r => $"\"{r}\"")) + "]," + $"\"message\":\"{mensaje}\""
        + (alterno is null ? "" : $",\"alternatePackage\":{{\"id\":\"{alterno}\",\"range\":\"{rango}\"}}") + "}";

    /// <summary>Página con hojas incluidas (<paramref name="hojas"/> no nulo) o sin ellas (hay que descargarla por su @id).</summary>
    public static string Pagina(string url, string inferior, string superior, IEnumerable<string>? hojas)
    {
        var lista = hojas?.ToList();
        return $"{{\"@id\":\"{url}\",\"count\":{lista?.Count ?? 64},\"lower\":\"{inferior}\",\"upper\":\"{superior}\""
               + (lista is null ? "" : ",\"items\":[" + string.Join(',', lista) + "]") + "}";
    }

    public static string Indice(IEnumerable<string> paginas)
    {
        var lista = paginas.ToList();
        return $"{{\"count\":{lista.Count},\"items\":[" + string.Join(',', lista) + "]}";
    }

    public static string UrlIndice(string id = "ejemplo.paquete") => $"{NuGetSimulado.BaseRegistro}{id}/index.json";

    public static string UrlPagina(int n, string id = "ejemplo.paquete") => $"{NuGetSimulado.BaseRegistro}{id}/page/{n}.json";

    /// <summary>Registro con todas las hojas incluidas en el índice (paquete pequeño).</summary>
    public static string Inline(params string[] hojas) => Indice([Pagina(UrlIndice() + "#page/a", "0.0.0", "999.0.0", hojas)]);

    public static NuGetSimulado AgregarInline(this NuGetSimulado nuget, string id = "ejemplo.paquete", params string[] hojas) =>
        nuget.ServiciosEstandar().Json(UrlIndice(id), Inline(hojas));

    /// <summary>
    /// Registro paginado: <paramref name="paginas"/> son listas de hojas (en orden ascendente de versión). El índice las lista SIN hojas y
    /// cada página se sirve en su propia dirección (la última es la de las versiones más nuevas).
    /// </summary>
    public static NuGetSimulado AgregarPaginado(this NuGetSimulado nuget, string[][] hojasPorPagina, string[][] versionesPorPagina, string id = "ejemplo.paquete")
    {
        var entradas = new List<string>();
        for (var i = 0; i < hojasPorPagina.Length; i++)
        {
            var url = UrlPagina(i, id);
            var v = versionesPorPagina[i];
            entradas.Add(Pagina(url, v[0], v[^1], null));
            nuget.Json(url, Pagina(url, v[0], v[^1], hojasPorPagina[i]));
        }

        return nuget.ServiciosEstandar().Json(UrlIndice(id), Indice(entradas));
    }
}
