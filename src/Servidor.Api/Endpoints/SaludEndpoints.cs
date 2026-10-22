using System.Reflection;
using Compartido.Contratos;
using Servidor.Negocio.Operacion;

namespace Servidor.Api.Endpoints;

public static class SaludEndpoints
{
    public static RouteGroupBuilder MapSalud(this RouteGroupBuilder api)
    {
        api.MapGet("/salud", () =>
        {
            var version = typeof(SaludEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";
            return Results.Ok(new SaludDto("ok", "DiagNET", version, DateTimeOffset.UtcNow));
        }).WithName("Salud");

        // Readiness: ¿puede la aplicación atender análisis? Consulta la base (con tiempo máximo) y las migraciones pendientes; no consulta NuGet.
        api.MapGet("/salud/disponibilidad", async (IVerificadorBase verificador, CancellationToken ct) =>
        {
            var b = await verificador.ComprobarAsync(ct);
            var listo = b.Disponible && b.MigracionesAlDia;
            var cuerpo = new DisponibilidadDto(listo ? "ok" : "no_disponible", "DiagNET", DateTimeOffset.UtcNow, new BaseDatosDto(b.Disponible, b.MigracionesAlDia, b.MigracionesPendientes));
            return Results.Json(cuerpo, statusCode: listo ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        }).WithName("Disponibilidad");
        return api;
    }
}
