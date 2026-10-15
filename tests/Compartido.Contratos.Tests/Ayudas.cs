using System.Text.Json;

namespace Compartido.Contratos.Tests;

internal static class Ayudas
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string RaizRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagNET.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró DiagNET.slnx.");
    }

    public static string Ejemplo(string nombre) =>
        File.ReadAllText(Path.Combine(RaizRepositorio(), "docs", "ejemplos", nombre));

    public static T Leer<T>(string nombre) =>
        JsonSerializer.Deserialize<T>(Ejemplo(nombre), Json) ?? throw new InvalidOperationException($"{nombre} es nulo.");
}
