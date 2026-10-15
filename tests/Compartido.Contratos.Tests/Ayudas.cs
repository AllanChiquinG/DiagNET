using System.Text.Json;

namespace Compartido.Contratos.Tests;

internal static class Ayudas
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Ejemplo(string nombre) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Ejemplos", nombre));

    public static T Leer<T>(string nombre) =>
        JsonSerializer.Deserialize<T>(Ejemplo(nombre), Json) ?? throw new InvalidOperationException($"{nombre} es nulo.");
}
