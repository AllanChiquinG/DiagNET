using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Servidor.Integracion.NuGet;

namespace Servidor.Integracion.Tests;

/// <summary>
/// NuGet SIMULADO: un manejador HTTP con respuestas guardadas. Las pruebas habituales nunca tocan internet.
/// Registra cada solicitud (URL, hora simulada, concurrencia) para comprobar cuántas consultas se hicieron.
/// </summary>
internal sealed class NuGetSimulado : HttpMessageHandler
{
    public const string UrlIndice = "https://api.nuget.org/v3/index.json";
    public const string BaseRegistro = "https://api.nuget.org/v3/registration5-gz-semver2/";
    public const string UrlBusqueda = "https://azuresearch-usnc.nuget.org/query";

    private readonly Dictionary<string, Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>> _rutas = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Prefijo, Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> Ruta)> _prefijos = [];
    private readonly Dictionary<string, int> _llamadas = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _candado = new();
    private int _concurrentes;

    public List<string> Solicitudes { get; } = [];
    public int MaximoConcurrente { get; private set; }

    public int Llamadas(string url) { lock (_candado) return _llamadas.GetValueOrDefault(url); }

    public int Total { get { lock (_candado) return Solicitudes.Count; } }

    public int Contar(Func<string, bool> filtro) { lock (_candado) return Solicitudes.Count(filtro); }

    // ---- Configuración de rutas ----
    public NuGetSimulado Ruta(string url, Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respuesta)
    {
        _rutas[url] = respuesta;
        return this;
    }

    /// <summary>Responde a cualquier URL que empiece por el prefijo (útil para las búsquedas, cuya dirección lleva parámetros).</summary>
    public NuGetSimulado Prefijo(string prefijo, Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respuesta)
    {
        _prefijos.Add((prefijo, respuesta));
        return this;
    }

    public NuGetSimulado Json(string url, string json, HttpStatusCode estado = HttpStatusCode.OK) =>
        Ruta(url, (_, _, _) => Task.FromResult(Respuesta(estado, json)));

    public NuGetSimulado Estado(string url, HttpStatusCode estado) =>
        Ruta(url, (_, _, _) => Task.FromResult(new HttpResponseMessage(estado)));

    /// <summary>Responde con cada elemento en orden y repite el último.</summary>
    public NuGetSimulado Secuencia(string url, params Func<HttpResponseMessage>[] respuestas) =>
        Ruta(url, (_, n, _) => Task.FromResult(respuestas[Math.Min(n, respuestas.Length - 1)]()));

    public NuGetSimulado ServiciosEstandar() => Json(UrlIndice, Servicios());

    public static HttpResponseMessage Respuesta(HttpStatusCode estado, string json) =>
        new(estado) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Servicios(string registro = BaseRegistro, string busqueda = UrlBusqueda, string version = "3.0.0") => $$"""
        { "version": "{{version}}", "resources": [
            { "@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl" },
            { "@id": "{{registro}}", "@type": "RegistrationsBaseUrl/3.6.0" },
            { "@id": "{{busqueda}}", "@type": "SearchQueryService/3.5.0" },
            { "@id": "{{busqueda}}", "@type": "SearchQueryService" } ] }
        """;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        int n;
        lock (_candado)
        {
            Solicitudes.Add(url);
            n = _llamadas.GetValueOrDefault(url);
            _llamadas[url] = n + 1;
            MaximoConcurrente = Math.Max(MaximoConcurrente, ++_concurrentes);
        }

        try
        {
            if (_rutas.TryGetValue(url, out var ruta)) return await ruta(request, n, cancellationToken);
            var porPrefijo = _prefijos.FirstOrDefault(p => url.StartsWith(p.Prefijo, StringComparison.OrdinalIgnoreCase));
            if (porPrefijo.Ruta is not null)
            {
                int k;
                lock (_candado) k = _llamadas[url] - 1;
                return await porPrefijo.Ruta(request, k, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        finally
        {
            lock (_candado) _concurrentes--;
        }
    }

    // ---- Construcción de gateways y utilidades de tiempo ----
    public static (NuGetGateway Gateway, FakeTimeProvider Tiempo, OpcionesNuGet Opciones) Crear(NuGetSimulado manejador, Action<OpcionesNuGet>? ajustar = null)
    {
        var opciones = new OpcionesNuGet();
        ajustar?.Invoke(opciones);
        var tiempo = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var http = new HttpClient(manejador) { Timeout = Timeout.InfiniteTimeSpan };
        return (new NuGetGateway(http, opciones, tiempo, NullLogger.Instance, () => 0.0), tiempo, opciones);
    }

    public static (EjecutorHttp Ejecutor, FakeTimeProvider Tiempo, Disyuntor Disyuntor) CrearEjecutor(NuGetSimulado manejador, Action<OpcionesNuGet>? ajustar = null)
    {
        var opciones = new OpcionesNuGet();
        ajustar?.Invoke(opciones);
        var tiempo = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var disyuntor = new Disyuntor(opciones.UmbralDisyuntor, TimeSpan.FromSeconds(opciones.PausaDisyuntorSeg), tiempo);
        var http = new HttpClient(manejador) { Timeout = Timeout.InfiniteTimeSpan };
        return (new EjecutorHttp(http, opciones, new ValidadorDirecciones(opciones), disyuntor, tiempo, NullLogger.Instance, () => 0.0), tiempo, disyuntor);
    }

    /// <summary>Hace avanzar el reloj simulado mientras la tarea espera (reintentos, tiempos de espera, pausas).</summary>
    public static async Task<T> ConTiempo<T>(Task<T> tarea, FakeTimeProvider tiempo, int pasoMs = 250, int maxPasos = 2000)
    {
        for (var i = 0; i < maxPasos && !tarea.IsCompleted; i++)
        {
            await Task.Delay(1);
            tiempo.Advance(TimeSpan.FromMilliseconds(pasoMs));
        }

        return await tarea;
    }
}
