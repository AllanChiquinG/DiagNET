using Compartido.Contratos;
using Servidor.Api.Errores;
using Servidor.Api.Mapeo;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Api.Endpoints;

/// <summary>Categoría de registro de los endpoints de análisis (no se registra nunca el contenido del archivo).</summary>
internal sealed class AnalisisLog;

/// <summary>
/// Endpoints de análisis (E8). Solo <c>POST</c> consulta NuGet y recalcula (y siempre crea un análisis NUEVO); los <c>GET</c> de detalle e historial leen el snapshot
/// guardado y no conocen el cliente de NuGet ni los motores: la reapertura no hace llamadas de red. El PDF NO está en esta entrega (E10): su ruta no existe.
/// </summary>
public static class AnalisisEndpoints
{
    public const string PoliticaConcurrencia = "analisis";

    public static RouteGroupBuilder MapAnalisis(this RouteGroupBuilder api)
    {
        api.MapPost("/analisis", CrearAsync).RequireRateLimiting(PoliticaConcurrencia).WithName("CrearAnalisis");
        api.MapGet("/analisis/{id}", ObtenerAsync).WithName("ObtenerAnalisis");
        api.MapGet("/analisis", ListarAsync).WithName("ListarAnalisis");
        return api;
    }

    // ---------------------------------------------------------------- POST /api/analisis

    private static async Task<IResult> CrearAsync(
        HttpRequest solicitud, OrquestadorAnalisis orquestador, IAnalisisRepositorio repositorio, OpcionesApi opciones, OpcionesConsultas consultas, ILogger<AnalisisLog> log,
        CancellationToken ct)
    {
        var formulario = await FormularioArchivo.LeerAsync(solicitud, opciones, ct);
        if (formulario.Error is not null) return formulario.Error;

        try
        {
            await using var contenido = formulario.Archivo!.OpenReadStream();
            var resultado = await orquestador.AnalizarAsync(formulario.Archivo.FileName, contenido, ct);
            // La respuesta es el resultado PERSISTIDO: se relee el snapshot guardado (la base redondea las fechas a microsegundos, el cálculo en memoria no),
            // de modo que el cuerpo del POST y el de un GET posterior son idénticos. Si la relectura falla, el análisis ya está guardado: se responde con el calculado.
            var persistido = resultado.Analisis;
            try
            {
                persistido = await repositorio.ObtenerAsync(resultado.Analisis.Id, ct) ?? persistido;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Un análisis se guardó pero no se pudo releer para responder; se responde con el resultado calculado. Referencia {Referencia}", solicitud.HttpContext.TraceIdentifier);
            }

            var dto = MapeadorAnalisis.ADto(persistido, consultas.RetencionDias);
            return Results.Created($"/api/analisis/{dto.Id}", dto);
        }
        catch (ArchivoInvalidoException ex)
        {
            // Entrada inválida: no se consultó NuGet ni se guardó nada.
            return ManejoErrores.Problema(ex.Codigo, ex.Message);
        }
        catch (AlmacenamientoNoDisponibleException ex)
        {
            log.LogError(ex.InnerException ?? ex, "No se pudo guardar el resultado del análisis. Referencia {Referencia}", solicitud.HttpContext.TraceIdentifier);
            return ManejoErrores.Problema(CodigosError.AlmacenamientoNoDisponible,
                "El análisis se completó, pero no se pudo guardar. Inténtalo de nuevo en unos minutos.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // El cliente cerró la conexión: nada se guardó y no hay a quién responder.
            return Results.StatusCode(StatusCodeClienteCerro);
        }
    }

    /// <summary>Código no estándar, usado por convención cuando el cliente cancela la solicitud (solo aparece en registros).</summary>
    public const int StatusCodeClienteCerro = 499;

    // ---------------------------------------------------------------- GET /api/analisis/{id}

    private static async Task<IResult> ObtenerAsync(
        string id, HttpContext http, IAnalisisRepositorio repositorio, OpcionesConsultas consultas, ILogger<AnalisisLog> log, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var guid))
            return ManejoErrores.Problema(CodigosError.IdentificadorInvalido, "El identificador del análisis debe ser un UUID.");

        try
        {
            var analisis = await repositorio.ObtenerAsync(guid, ct);
            return analisis is null
                ? ManejoErrores.Problema(CodigosError.AnalisisNoEncontrado, "No existe un análisis con ese identificador o ya fue eliminado.")
                : Results.Ok(MapeadorAnalisis.ADto(analisis, consultas.RetencionDias));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "No se pudo leer un análisis. Referencia {Referencia}", http.TraceIdentifier);
            return ManejoErrores.Problema(CodigosError.AlmacenamientoNoDisponible, "No se pudo leer el análisis. Inténtalo de nuevo en unos minutos.");
        }
    }

    // ---------------------------------------------------------------- GET /api/analisis?ids=...&pagina=&tamano=

    /// <summary>
    /// Historial limitado al navegador (DEC-06, PROPUESTA pendiente de aprobación): devuelve SOLO los análisis cuyos identificadores envía el cliente (<c>ids</c>, los suyos).
    /// No existe un listado global: sin <c>ids</c> es un error. Los identificadores desconocidos o eliminados se omiten sin avisar.
    /// </summary>
    private static async Task<IResult> ListarAsync(
        HttpRequest solicitud, IAnalisisRepositorio repositorio, OpcionesConsultas consultas, ILogger<AnalisisLog> log, CancellationToken ct)
    {
        var crudos = solicitud.Query["ids"].SelectMany(v => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        if (crudos.Count == 0)
            return ManejoErrores.Problema(CodigosError.IdentificadorInvalido,
                "Indica los identificadores de tus análisis en el parámetro «ids» (UUID separados por comas). No existe un listado global.");
        if (crudos.Count > consultas.MaximoIds)
            return ManejoErrores.Problema(CodigosError.IdentificadorInvalido, $"Se pueden consultar como máximo {consultas.MaximoIds} identificadores por solicitud.");

        var ids = new List<Guid>();
        foreach (var crudo in crudos)
        {
            if (!Guid.TryParseExact(crudo, "D", out var guid))
                return ManejoErrores.Problema(CodigosError.IdentificadorInvalido, "Todos los identificadores de «ids» deben ser UUID válidos.");
            if (!ids.Contains(guid)) ids.Add(guid);
        }

        if (!EnteroEnRango(solicitud.Query["pagina"], 1, int.MaxValue, 1, out var pagina)
            || !EnteroEnRango(solicitud.Query["tamano"], 1, consultas.TamanoPaginaMaximo, consultas.TamanoPaginaPorDefecto, out var tamano))
            return ManejoErrores.Problema(CodigosError.PaginacionInvalida,
                $"«pagina» debe ser un entero ≥ 1 y «tamano» un entero entre 1 y {consultas.TamanoPaginaMaximo}.");

        try
        {
            var resultado = await repositorio.ListarAsync(pagina, tamano, ids, ct);
            return Results.Ok(new PaginaDto<ResumenAnalisisDto>(
                resultado.Elementos.Select(MapeadorAnalisis.ADto).ToList(), resultado.NumeroPagina, resultado.TamanoPagina, resultado.Total));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "No se pudo leer el historial. Referencia {Referencia}", solicitud.HttpContext.TraceIdentifier);
            return ManejoErrores.Problema(CodigosError.AlmacenamientoNoDisponible, "No se pudo leer el historial. Inténtalo de nuevo en unos minutos.");
        }
    }

    private static bool EnteroEnRango(Microsoft.Extensions.Primitives.StringValues valores, int minimo, int maximo, int predeterminado, out int resultado)
    {
        resultado = predeterminado;
        if (valores.Count == 0 || string.IsNullOrEmpty(valores[0])) return true;
        return valores.Count == 1
               && int.TryParse(valores[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out resultado)
               && resultado >= minimo && resultado <= maximo;
    }
}

/// <summary>Opciones de las consultas de resultados e historial.</summary>
public sealed class OpcionesConsultas
{
    /// <summary>Días que se conserva un análisis (propuesta de retención, RNF-02/DEC pendiente de E11); se informa en cada resultado.</summary>
    public int RetencionDias { get; set; } = 90;

    public int TamanoPaginaPorDefecto { get; set; } = 20;
    public int TamanoPaginaMaximo { get; set; } = 50;

    /// <summary>Identificadores que puede enviar el cliente en una sola consulta de historial.</summary>
    public int MaximoIds { get; set; } = 50;
}
