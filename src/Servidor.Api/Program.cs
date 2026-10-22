using Compartido.Contratos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Servidor.Api.Configuracion;
using Servidor.Api.Endpoints;
using Servidor.Api.Errores;
using Servidor.Api.Operacion;
using Microsoft.AspNetCore.HttpOverrides;
using Servidor.Datos;
using Servidor.Negocio.Lectura;

var builder = WebApplication.CreateBuilder(args);

// Credenciales y cadenas de conexión reales: appsettings.Local.json (ignorado por Git) o variables de entorno. Nunca en el repositorio.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
// Las variables de entorno y la línea de comandos prevalecen sobre el archivo local (orden habitual): permiten ejecutar contra otra base sin tocar archivos.
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);
builder.Services.AddManejoErrores();

// Registros: los identificadores de análisis viajan en la ruta (/api/analisis/{id}) y en la consulta (?ids=…). Las categorías que escriben la URL completa de cada solicitud
// (ASP.NET, enrutamiento, HttpClient hacia NuGet, EF/Npgsql) quedan en Warning COMO MÍNIMO por código, sin depender de appsettings.json.
foreach (var categoria in new[] { "Microsoft.AspNetCore", "System.Net.Http", "Microsoft.EntityFrameworkCore", "Npgsql" })
    builder.Logging.AddFilter(categoria, LogLevel.Warning);

// En los comandos de mantenimiento la salida es la del comando: EF registraría como «fail» la consulta normal de un historial de migraciones aún inexistente.
if (Comandos.EsComando(args)) builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);

// Persistencia (E3): se registra si hay cadena de conexión. Las migraciones NO se aplican al arrancar (paso controlado: dotnet ef database update).
if (!string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString(RegistroDatos.NombreCadenaConexion)))
    builder.Services.AddDatos(builder.Configuration);
else
    builder.Services.AddAlmacenamientoNoConfigurado();   // el servidor arranca; los análisis responden 503 hasta configurar la base

// Límites de entrada (DEC-09 y plan E2): archivo 1 MB, cuerpo HTTP 2 MB, 100 dependencias distintas.
var lectura = new OpcionesLectura
{
    TamanoMaximoArchivoBytes = LimitesEntrada.TamanoMaximoArchivoBytes,
    MaximoDependencias = LimitesEntrada.MaximoDependencias,
};
var http = new OpcionesApi();
builder.Configuration.GetSection("Lectura").Bind(lectura);
builder.Configuration.GetSection("Lectura").Bind(http);
if (http.TamanoMaximoCuerpoBytes <= lectura.TamanoMaximoArchivoBytes)
    throw new InvalidOperationException("Lectura:TamanoMaximoCuerpoBytes debe ser mayor que Lectura:TamanoMaximoArchivoBytes.");

builder.Services.AddSingleton(lectura);
builder.Services.AddSingleton(http);
builder.Services.AddSingleton<ILectorProyecto, LectorCsproj>();

// Motores reales de análisis (E3–E7): NuGet, metadatos con caché, orquestación, diagnóstico y recomendaciones. Sin sustitutos de pruebas.
builder.Services.AddAnalisis(builder.Configuration);

// Límite de análisis simultáneos: un análisis consulta NuGet y usa memoria; el excedente recibe 429 en lugar de encolarse sin límite.
var analisisApi = builder.Configuration.GetSection("Analisis").Get<OpcionesAnalisisApi>() ?? new OpcionesAnalisisApi();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (contexto, _) =>
        await ManejoErrores.Problema(CodigosError.DemasiadasSolicitudes,
                contexto.HttpContext.Request.Path.Value?.EndsWith("/pdf", StringComparison.Ordinal) == true
                    ? "Hay demasiados PDF generándose a la vez. Inténtalo de nuevo en unos segundos."
                    : "Hay demasiados análisis en curso. Inténtalo de nuevo en unos segundos.")
            .ExecuteAsync(contexto.HttpContext);
    o.AddConcurrencyLimiter(AnalisisEndpoints.PoliticaConcurrencia, c =>
    {
        c.PermitLimit = Math.Max(1, analisisApi.MaxAnalisisSimultaneos);
        c.QueueLimit = 0;
    });
    o.AddConcurrencyLimiter(AnalisisEndpoints.PoliticaReportes, c =>
    {
        c.PermitLimit = Math.Max(1, analisisApi.MaxReportesSimultaneos);
        c.QueueLimit = 0;
    });
});

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = http.TamanoMaximoCuerpoBytes);
builder.Services.Configure<FormOptions>(f =>
{
    // El .csproj nunca se escribe en disco: se mantiene en memoria y se descarta al terminar la solicitud.
    f.MemoryBufferThreshold = (int)http.TamanoMaximoCuerpoBytes;
    f.MultipartBodyLengthLimit = http.TamanoMaximoCuerpoBytes + 1024 * 1024; // Kestrel corta primero
});

// Seguridad de transporte y proxy (E11): HTTPS exigido por defecto fuera de Desarrollo; cabeceras reenviadas solo de redes configuradas.
var seguridad = builder.Configuration.GetSection("Seguridad").Get<OpcionesSeguridad>() ?? new OpcionesSeguridad();
if (seguridad.ReenviarCabeceras) builder.Services.Configure<ForwardedHeadersOptions>(o => CopiarOpciones(seguridad.CrearCabecerasReenviadas(), o));

var app = builder.Build();

// Operaciones de mantenimiento (migrar, estado, purgar) con la misma configuración, sin levantar el servidor web.
if (Comandos.EsComando(args))
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;   // tildes correctas también al redirigir la salida a un archivo
    return await Comandos.EjecutarAsync(app.Services, args, Console.Out, Console.Error);
}

if (seguridad.ReenviarCabeceras) app.UseForwardedHeaders();

app.UseManejoErrores();
app.UseCabecerasApi();

if (seguridad.ExigirHttpsEfectivo(app.Environment.IsDevelopment()))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRateLimiter();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// El cliente Blazor WebAssembly se sirve desde el mismo origen que la API.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapSalud().MapConfiguracion().MapProyectos().MapAnalisis();

// Una ruta /api desconocida devuelve 404 y nunca index.html (no hay fallback SPA bajo /api).
app.Map("/api/{**resto}", () => ManejoErrores.Problema(CodigosError.RecursoNoEncontrado));

app.MapFallbackToFile("index.html");

app.Run();
return 0;

static void CopiarOpciones(ForwardedHeadersOptions origen, ForwardedHeadersOptions destino)
{
    destino.ForwardedHeaders = origen.ForwardedHeaders;
    destino.ForwardLimit = origen.ForwardLimit;
    destino.KnownIPNetworks.Clear();
    foreach (var red in origen.KnownIPNetworks) destino.KnownIPNetworks.Add(red);   // loopback (por defecto) + solo las redes configuradas
}

/// <summary>Expuesta para que las pruebas de integración usen WebApplicationFactory.</summary>
public partial class Program;
