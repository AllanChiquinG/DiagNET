using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Compartido.Contratos;

namespace Cliente.Web.Servicios;

/// <summary>Error de la API tal como lo ve la interfaz. <see cref="Status"/> 0 = no hubo respuesta del servidor (red caída, servidor apagado).</summary>
public sealed record ErrorApi(string Codigo, int Status, string Titulo, string? Detalle, string? Referencia)
{
    public const string SinConexion = "sin_conexion";
    public const string RespuestaInesperada = "respuesta_inesperada";

    public bool EsNoEncontrado => Status == (int)HttpStatusCode.NotFound;

    /// <summary>Errores de la entrada del usuario (archivo): se muestran junto al archivo y se corrigen eligiendo otro.</summary>
    public bool EsEntradaInvalida => Status is 400 or 413 or 422;
}

public sealed record Resultado<T>(T? Valor, ErrorApi? Error)
{
    public bool Ok => Error is null;

    public static Resultado<T> Exito(T valor) => new(valor, null);

    public static Resultado<T> Falla(ErrorApi error) => new(default, error);
}

/// <summary>Resultado de consultar el historial por lotes: nada se descarta en silencio.</summary>
/// <param name="Encontrados">Resúmenes devueltos por el servidor.</param>
/// <param name="NoDevueltos">Identificadores que el servidor no devolvió (no existen, fueron purgados o no están disponibles). Solo cuentan los de lotes que SÍ respondieron.</param>
/// <param name="SinConsultar">Identificadores de lotes que fallaron: se desconoce su estado; se conservan y se puede reintentar.</param>
/// <param name="Error">El último error de un lote fallido, si lo hubo.</param>
public sealed record ResultadoHistorial(IReadOnlyList<ResumenAnalisisDto> Encontrados, IReadOnlyList<Guid> NoDevueltos, IReadOnlyList<Guid> SinConsultar, ErrorApi? Error);

/// <summary>Único punto por el que la interfaz habla con la API real. No calcula nada: muestra lo que la API devuelve.</summary>
public sealed class ClienteApi(HttpClient http)
{
    public const int TamanoLote = 50;

    public async Task<Resultado<ConfiguracionPublicaDto>> ObtenerConfiguracionAsync(CancellationToken ct = default) =>
        await GetAsync<ConfiguracionPublicaDto>("api/configuracion", ct);

    public async Task<Resultado<ResultadoAnalisisDto>> AnalizarAsync(string nombreArchivo, byte[] contenido, CancellationToken ct = default)
    {
        using var formulario = new MultipartFormDataContent();
        var archivo = new ByteArrayContent(contenido);
        archivo.Headers.ContentType = new MediaTypeHeaderValue("text/xml");
        formulario.Add(archivo, "archivo", nombreArchivo);

        try
        {
            using var respuesta = await http.PostAsync("api/analisis", formulario, ct);
            return await LeerAsync<ResultadoAnalisisDto>(respuesta, ct);
        }
        catch (HttpRequestException)
        {
            return Resultado<ResultadoAnalisisDto>.Falla(SinRespuesta());
        }
    }

    public async Task<Resultado<ResultadoAnalisisDto>> ObtenerAnalisisAsync(Guid id, CancellationToken ct = default) =>
        await GetAsync<ResultadoAnalisisDto>($"api/analisis/{id:D}", ct);

    /// <summary>Consulta el historial en lotes de hasta 50 identificadores. Un lote fallido no pierde los demás ni borra identificadores.</summary>
    public async Task<ResultadoHistorial> ListarAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default)
    {
        var encontrados = new List<ResumenAnalisisDto>();
        var noDevueltos = new List<Guid>();
        var sinConsultar = new List<Guid>();
        ErrorApi? error = null;

        foreach (var lote in ids.Distinct().Chunk(TamanoLote))
        {
            var consulta = string.Join(',', lote.Select(i => i.ToString("D")));
            var resultado = await GetAsync<PaginaDto<ResumenAnalisisDto>>($"api/analisis?ids={consulta}&pagina=1&tamano={TamanoLote}", ct);
            if (!resultado.Ok)
            {
                error = resultado.Error;
                sinConsultar.AddRange(lote);
                continue;
            }

            var devueltos = resultado.Valor!.Elementos;
            encontrados.AddRange(devueltos);
            var devueltosIds = devueltos.Select(e => e.Id).ToHashSet();
            noDevueltos.AddRange(lote.Where(i => !devueltosIds.Contains(i)));
        }

        return new ResultadoHistorial(encontrados, noDevueltos, sinConsultar, error);
    }

    private async Task<Resultado<T>> GetAsync<T>(string ruta, CancellationToken ct)
    {
        try
        {
            using var respuesta = await http.GetAsync(ruta, ct);
            return await LeerAsync<T>(respuesta, ct);
        }
        catch (HttpRequestException)
        {
            return Resultado<T>.Falla(SinRespuesta());
        }
    }

    private static async Task<Resultado<T>> LeerAsync<T>(HttpResponseMessage respuesta, CancellationToken ct)
    {
        if (respuesta.IsSuccessStatusCode)
        {
            try
            {
                var valor = await respuesta.Content.ReadFromJsonAsync<T>(ct);
                return valor is null
                    ? Resultado<T>.Falla(new ErrorApi(ErrorApi.RespuestaInesperada, (int)respuesta.StatusCode, "Respuesta inesperada del servidor", null, null))
                    : Resultado<T>.Exito(valor);
            }
            catch (System.Text.Json.JsonException)
            {
                return Resultado<T>.Falla(new ErrorApi(ErrorApi.RespuestaInesperada, (int)respuesta.StatusCode, "Respuesta inesperada del servidor", null, null));
            }
        }

        // Error de la API: Problem Details con «codigo» y «referencia». Si no lo es (p. ej. una página HTML de un proxy), se conserva solo el estado HTTP.
        ProblemaDto? problema = null;
        try
        {
            if (respuesta.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                problema = await respuesta.Content.ReadFromJsonAsync<ProblemaDto>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
        }

        var status = (int)respuesta.StatusCode;
        return Resultado<T>.Falla(new ErrorApi(
            problema?.Codigo ?? $"http_{status}", status, problema?.Title ?? "El servidor respondió con un error", problema?.Detail, problema?.Referencia));
    }

    private static ErrorApi SinRespuesta() =>
        new(ErrorApi.SinConexion, 0, "No se pudo contactar con el servidor", "Revisa tu conexión o inténtalo de nuevo en unos minutos.", null);
}
