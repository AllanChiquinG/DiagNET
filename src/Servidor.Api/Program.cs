using Compartido.Contratos;
using Servidor.Api.Endpoints;
using Servidor.Api.Errores;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddManejoErrores();

var app = builder.Build();

app.UseManejoErrores();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// El cliente Blazor WebAssembly se sirve desde el mismo origen que la API.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapSalud();

// Una ruta /api desconocida devuelve 404 y nunca index.html (no hay fallback SPA bajo /api).
app.Map("/api/{**resto}", () => ManejoErrores.Problema(CodigosError.RecursoNoEncontrado));

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Expuesta para que las pruebas de integración usen WebApplicationFactory.</summary>
public partial class Program;
