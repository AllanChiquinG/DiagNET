using Compartido.Contratos;
using Microsoft.AspNetCore.Http.Features;
using Servidor.Api.Endpoints;
using Servidor.Api.Errores;
using Servidor.Negocio.Lectura;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddManejoErrores();

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

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = http.TamanoMaximoCuerpoBytes);
builder.Services.Configure<FormOptions>(f =>
{
    // El .csproj nunca se escribe en disco: se mantiene en memoria y se descarta al terminar la solicitud.
    f.MemoryBufferThreshold = (int)http.TamanoMaximoCuerpoBytes;
    f.MultipartBodyLengthLimit = http.TamanoMaximoCuerpoBytes + 1024 * 1024; // Kestrel corta primero
});

var app = builder.Build();

app.UseManejoErrores();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// El cliente Blazor WebAssembly se sirve desde el mismo origen que la API.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapSalud().MapProyectos();

// Una ruta /api desconocida devuelve 404 y nunca index.html (no hay fallback SPA bajo /api).
app.Map("/api/{**resto}", () => ManejoErrores.Problema(CodigosError.RecursoNoEncontrado));

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Expuesta para que las pruebas de integración usen WebApplicationFactory.</summary>
public partial class Program;
