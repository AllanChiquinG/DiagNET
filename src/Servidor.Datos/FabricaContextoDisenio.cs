using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servidor.Datos;

/// <summary>
/// Solo para <c>dotnet ef</c> (migraciones). Toma la cadena de conexión de la variable de entorno DIAGNET_CONNECTION o, si no existe,
/// de <c>src/Servidor.Api/appsettings.Local.json</c> (ignorado por Git). Nunca hay credenciales en el código ni en el repositorio.
/// </summary>
internal sealed class FabricaContextoDisenio : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cadena = Environment.GetEnvironmentVariable("DIAGNET_CONNECTION") ?? LeerDeArchivoLocal()
            ?? throw new InvalidOperationException(
                "No hay cadena de conexión. Define DIAGNET_CONNECTION o crea src/Servidor.Api/appsettings.Local.json (ver README).");
        var opciones = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(cadena).Options;
        return new AppDbContext(opciones);
    }

    private static string? LeerDeArchivoLocal()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var archivo = Path.Combine(dir.FullName, "src", "Servidor.Api", "appsettings.Local.json");
            if (!File.Exists(archivo)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(archivo));
            return json.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("DiagNet", out var c)
                ? c.GetString()
                : null;
        }

        return null;
    }
}
