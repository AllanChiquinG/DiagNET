using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Servidor.Datos;
using Servidor.Integracion;
using Servidor.Negocio.Dominio;

namespace Servidor.Api.Tests.Analisis;

/// <summary>
/// Base PostgreSQL REAL y aislada (diagnet_test_xxx) para cada clase de prueba: aplica las migraciones y se borra al terminar. Nunca toca diagnet_nueva.
/// Conexión: variable DIAGNET_TEST_CONNECTION o el appsettings.Local.json de las pruebas de Datos (ConnectionStrings:Pruebas), ambos fuera de Git.
/// </summary>
public sealed class BaseDeDatosApi : IAsyncLifetime
{
    private NpgsqlConnectionStringBuilder _administracion = null!;

    public string NombreBase { get; } = "diagnet_test_" + Guid.NewGuid().ToString("N");
    public string CadenaConexion { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _administracion = new NpgsqlConnectionStringBuilder(LeerCadenaDePruebas());
        await EjecutarAdminAsync($"CREATE DATABASE \"{NombreBase}\"");
        CadenaConexion = new NpgsqlConnectionStringBuilder(_administracion.ConnectionString) { Database = NombreBase, Pooling = true, MaxPoolSize = 25 }.ConnectionString;

        var servicios = new ServiceCollection().AddDatos(CadenaConexion).BuildServiceProvider();
        await using (servicios)
        {
            await using var db = await servicios.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();
            await db.Database.MigrateAsync();
        }
    }

    public async Task<long> ContarAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(CadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"SELECT count(*) FROM {tabla}", conexion);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        if (_administracion is not null) await EjecutarAdminAsync($"DROP DATABASE IF EXISTS \"{NombreBase}\" WITH (FORCE)");
    }

    private async Task EjecutarAdminAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_administracion.ConnectionString);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private static string LeerCadenaDePruebas()
    {
        var entorno = Environment.GetEnvironmentVariable("DIAGNET_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(entorno)) return entorno;

        var archivo = Path.Combine(AppContext.BaseDirectory, "appsettings.Pruebas.json");
        if (File.Exists(archivo))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(archivo));
            if (json.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("Pruebas", out var c) && c.GetString() is { Length: > 0 } cadena)
                return cadena;
        }

        throw new InvalidOperationException(
            "Faltan las credenciales de las pruebas de PostgreSQL. Ejecuta deploy/local/crear_base_local.ps1 o define DIAGNET_TEST_CONNECTION. Estas pruebas NO se omiten.");
    }
}

/// <summary>
/// DOBLE DE PRUEBAS: un nuget.org simulado (sin internet). Registra cada solicitud, simula paquetes inexistentes, lentos, caídos, obsoletos con reemplazo declarado
/// y una búsqueda. La API usa el gateway REAL de E4 sobre este manejador.
/// </summary>
public sealed class NuGetSimulado : HttpMessageHandler
{
    private const string Base = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private int _cancelaciones;

    public ConcurrentQueue<string> Solicitudes { get; } = new();
    public bool Caido { get; set; }
    public bool BusquedaCaida { get; set; }
    public int Cancelaciones => _cancelaciones;
    public int SolicitudesAlRegistro => Solicitudes.Count(u => u.StartsWith(Base) || u.Contains("/query"));

    private static string Hoja(string version, string grupos = "net8.0", string deprecacion = "") =>
        "{\"catalogEntry\":{\"version\":\"" + version + "\",\"listed\":true,\"published\":\"2025-01-01T00:00:00+00:00\",\"licenseExpression\":\"MIT\",\"dependencyGroups\":[{\"targetFramework\":\""
        + grupos + "\"}]" + (deprecacion.Length == 0 ? "" : ",\"deprecation\":" + deprecacion) + "}}";

    private static string Indice(params string[] hojas) =>
        "{\"items\":[{\"lower\":\"1.0.0\",\"upper\":\"2.0.0\",\"items\":[" + string.Join(",", hojas) + "]}]}";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Solicitudes.Enqueue(url);
        if (url == "https://api.nuget.org/v3/index.json")
            return Json("{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + Base + "\",\"@type\":\"RegistrationsBaseUrl/3.6.0\"},{\"@id\":\"https://azuresearch-usnc.nuget.org/query\",\"@type\":\"SearchQueryService/3.5.0\"}]}");
        if (Caido) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        if (url.StartsWith("https://azuresearch-usnc.nuget.org/query"))
        {
            if (BusquedaCaida) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return url.Contains("q=Ejemplo.SinAlterno", StringComparison.OrdinalIgnoreCase)
                ? Json("{\"data\":[{\"id\":\"Ejemplo.Candidata\",\"version\":\"1.0.0\",\"totalDownloads\":7,\"verified\":false,\"description\":\"Otro paquete\"}]}")
                : Json("{\"data\":[]}");
        }

        if (url.Contains("/ejemplo.lento/"))
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelaciones);
                throw;
            }
        }

        if (url.EndsWith("/ejemplo.noexiste/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.EndsWith("/ejemplo.obsoleto/index.json"))
            return Json(Indice(Hoja("1.0.0", deprecacion: "{\"reasons\":[\"Legacy\"],\"message\":\"Usa Ejemplo.Nueva.\",\"alternatePackage\":{\"id\":\"Ejemplo.Nueva\",\"range\":\"[1.0.0, )\"}}")));
        if (url.EndsWith("/ejemplo.sinalterno/index.json"))
            return Json(Indice(Hoja("1.0.0", deprecacion: "{\"reasons\":[\"Legacy\"],\"message\":\"Sin mantenimiento.\"}")));
        if (url.StartsWith(Base) && url.EndsWith("/index.json")) return Json(Indice(Hoja("1.0.0"), Hoja("2.0.0")));
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>
/// La aplicación REAL (Program, Minimal API, motores reales) con dos cambios de prueba: la base de datos es la temporal indicada y el HTTP de NuGet es el simulado.
/// </summary>
public sealed class FabricaAnalisis(
    string cadenaConexion, NuGetSimulado nuget, IReadOnlyDictionary<string, string?>? ajustes = null, Microsoft.Extensions.Logging.ILoggerProvider? registros = null,
    Action<IServiceCollection>? personalizar = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("NuGet:MaxReintentos", "0");          // el reloj es real: sin esperas de reintento en las pruebas
        builder.UseSetting("NuGet:ConsultarDescargas", "false");
        foreach (var (clave, valor) in ajustes ?? new Dictionary<string, string?>()) builder.UseSetting(clave, valor);
        if (registros is not null)
        {
            builder.UseSetting("Logging:LogLevel:Default", "Trace");   // el peor caso: si algo filtrara por configuración, aquí se vería
            builder.ConfigureLogging(l => l.AddProvider(registros));
        }

        builder.ConfigureTestServices(servicios =>
        {
            // El archivo appsettings.Local.json de desarrollo apunta a diagnet_nueva: nunca se usa en pruebas.
            servicios.RemoveAll<IDbContextFactory<AppDbContext>>();
            servicios.RemoveAll<DbContextOptions<AppDbContext>>();
            servicios.RemoveAll<DbContextOptions>();
            servicios.RemoveAll<IAnalisisRepositorio>();
            servicios.RemoveAll<IMetadatoRepositorio>();
            servicios.AddDatos(cadenaConexion);
            servicios.AddHttpClient(RegistroNuGet.ClienteHttp).ConfigurePrimaryHttpMessageHandler(() => nuget);
            personalizar?.Invoke(servicios);
        });
    }
}

internal static class Ayuda
{
    public static string Csproj(string tfms = "net8.0", params (string Nombre, string Version)[] paquetes) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>{tfms}</TargetFrameworks></PropertyGroup><ItemGroup>"
        + string.Concat(paquetes.Select(p => $"<PackageReference Include=\"{p.Nombre}\" Version=\"{p.Version}\" />")) + "</ItemGroup></Project>";

    public static MultipartFormDataContent Formulario(string contenido, string nombre = "Mi.csproj", string campo = "archivo")
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(contenido)), campo, nombre);
        return form;
    }

    public static string Unico(string prefijo) => $"{prefijo}{Guid.NewGuid():N}"[..28];

    public static JsonElement Json(string texto) => JsonDocument.Parse(texto).RootElement.Clone();
}

/// <summary>DOBLE DE PRUEBAS: guarda todo lo que la aplicación registra (categoría, nivel, mensaje, valores estructurados y excepción) para inspeccionarlo.</summary>
public sealed class CapturadorDeRegistros : ILoggerProvider
{
    public sealed record Entrada(string Categoria, LogLevel Nivel, string Texto);

    private readonly ConcurrentQueue<Entrada> _entradas = new();

    public IReadOnlyList<Entrada> Entradas => _entradas.ToArray();

    public ILogger CreateLogger(string categoria) => new Registro(categoria, _entradas);

    public void Dispose()
    {
    }

    private sealed class Registro(string categoria, ConcurrentQueue<Entrada> entradas) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel nivel) => nivel != LogLevel.None;

        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excepcion, Func<TState, Exception?, string> formateador)
        {
            var sb = new StringBuilder(formateador(estado, excepcion));
            if (estado is IEnumerable<KeyValuePair<string, object?>> pares)
                foreach (var par in pares) sb.Append(" | ").Append(par.Key).Append('=').Append(par.Value);
            if (excepcion is not null) sb.Append(" | ").Append(excepcion);
            entradas.Enqueue(new Entrada(categoria, nivel, sb.ToString()));
        }
    }
}
