namespace Servidor.Api.Errores;

/// <summary>
/// Cabeceras de las respuestas de /api: sin caché (un resultado o un historial no debe quedar en cachés compartidas), sin adivinación de tipos y sin enviar el
/// origen como referer. Un identificador de análisis no es un secreto, pero tampoco debe propagarse por cabeceras o cachés.
/// </summary>
public static class CabecerasApi
{
    public static IApplicationBuilder UseCabecerasApi(this IApplicationBuilder app) =>
        app.Use(async (contexto, siguiente) =>
        {
            if (contexto.Request.Path.StartsWithSegments("/api"))
            {
                contexto.Response.OnStarting(() =>
                {
                    var cabeceras = contexto.Response.Headers;
                    cabeceras["X-Content-Type-Options"] = "nosniff";
                    cabeceras["Cache-Control"] = "no-store";
                    cabeceras["Referrer-Policy"] = "no-referrer";
                    return Task.CompletedTask;
                });
            }

            await siguiente();
        });
}
