using Compartido.Contratos;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Servidor.Api.Errores;

/// <summary>Un único punto para producir y completar Problem Details con <c>codigo</c> y <c>referencia</c>.</summary>
public static class ManejoErrores
{
    public static IServiceCollection AddManejoErrores(this IServiceCollection servicios)
    {
        servicios.AddProblemDetails(opciones => opciones.CustomizeProblemDetails = contexto =>
        {
            var problema = contexto.ProblemDetails;
            problema.Extensions["referencia"] = contexto.HttpContext.TraceIdentifier;
            if (!problema.Extensions.ContainsKey("codigo"))
            {
                problema.Extensions["codigo"] = problema.Status == StatusCodes.Status404NotFound
                    ? CodigosError.RecursoNoEncontrado
                    : CodigosError.ErrorInterno;
            }
        });
        return servicios;
    }

    /// <summary>Convierte excepciones no controladas en 500 Problem Details sin exponer trazas ni mensajes internos.</summary>
    public static IApplicationBuilder UseManejoErrores(this IApplicationBuilder app) =>
        app.UseExceptionHandler(rama => rama.Run(async contexto =>
        {
            var definicion = CodigosError.Definicion(CodigosError.ErrorInterno);
            var servicio = contexto.RequestServices.GetRequiredService<IProblemDetailsService>();
            contexto.Response.StatusCode = definicion.Status;
            await servicio.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = contexto,
                ProblemDetails = Crear(definicion, "Intenta de nuevo. Si continúa, informa la referencia."),
                Exception = contexto.Features.Get<IExceptionHandlerFeature>()?.Error,
            });
        }));

    public static ProblemDetails Crear(DefinicionError definicion, string? detalle = null)
    {
        var problema = new ProblemDetails
        {
            Title = definicion.Titulo,
            Status = definicion.Status,
            Detail = detalle,
        };
        problema.Extensions["codigo"] = definicion.Codigo;
        return problema;
    }

    public static IResult Problema(string codigo, string? detalle = null)
    {
        var definicion = CodigosError.Definicion(codigo);
        return Results.Problem(Crear(definicion, detalle));
    }
}
