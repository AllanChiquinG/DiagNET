using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion;

public static class RegistroNuGet
{
    public const string ClienteHttp = "nuget";

    /// <summary>
    /// Registra el gateway de NuGet. La resiliencia (tiempo por intento, reintentos, concurrencia, disyuntor, tamaño de respuesta y redirecciones)
    /// la aplica el propio gateway con la configuración de la sección «NuGet»; el HttpClient no sigue redirecciones por su cuenta.
    /// </summary>
    public static IServiceCollection AddNuGet(this IServiceCollection servicios, IConfiguration configuracion) =>
        servicios.AddNuGet(configuracion.GetSection(OpcionesNuGet.Seccion).Get<OpcionesNuGet>() ?? new OpcionesNuGet());

    public static IServiceCollection AddNuGet(this IServiceCollection servicios, OpcionesNuGet opciones)
    {
        servicios.TryAddSingleton(TimeProvider.System);
        servicios.AddSingleton(opciones);
        servicios.AddHttpClient(ClienteHttp, cliente =>
            {
                cliente.Timeout = Timeout.InfiniteTimeSpan; // el tiempo por intento y el presupuesto global los controla el gateway
                cliente.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgentSeguro(opciones.UserAgent));
                cliente.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, // cada salto se valida a mano (HTTPS y nuget.org)
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, opciones.TiempoEsperoSeg)),
            });
        servicios.AddSingleton<IClienteNuGet>(sp => new NuGetGateway(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ClienteHttp),
            opciones,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<NuGetGateway>()));
        return servicios;
    }

    /// <summary>Los encabezados HTTP admiten solo ASCII imprimible: se eliminan otros caracteres en lugar de fallar al arrancar.</summary>
    private static string UserAgentSeguro(string valor)
    {
        var limpio = new string(valor.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
        return limpio.Length == 0 ? "DiagNET/1.0" : limpio;
    }
}
