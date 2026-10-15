using Servidor.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

// El cliente Blazor WebAssembly se sirve desde el mismo origen que la API.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapGroup("/api").MapSalud();

// Una ruta /api desconocida devuelve 404 y nunca index.html (no hay fallback SPA bajo /api).
app.Map("/api/{**resto}", () => Results.Problem(
    title: "Recurso no encontrado",
    statusCode: StatusCodes.Status404NotFound));

app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Expuesta para que las pruebas de integración usen WebApplicationFactory.</summary>
public partial class Program;
