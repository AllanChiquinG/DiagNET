using System.Reflection;
using Compartido.Contratos;

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
        return api;
    }
}
