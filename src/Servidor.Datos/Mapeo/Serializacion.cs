using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servidor.Datos.Mapeo;

/// <summary>JSON de las columnas jsonb y textos de las enumeraciones (snake_case, igual que los CHECK de la base).</summary>
internal static class Serializacion
{
    public static readonly JsonSerializerOptions Json = Crear();

    private static JsonSerializerOptions Crear()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = false,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }

    public static string AJson<T>(T valor) => JsonSerializer.Serialize(valor, Json);

    public static T DeJson<T>(string texto) =>
        JsonSerializer.Deserialize<T>(texto, Json) ?? throw new InvalidOperationException($"El JSON guardado de tipo {typeof(T).Name} es nulo.");

    /// <summary>Texto de una enumeración tal como se guarda en la base (p. ej. <c>sin_soporte_activo</c>).</summary>
    public static string Texto<TEnum>(TEnum valor) where TEnum : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(valor.ToString());

    public static TEnum Enumeracion<TEnum>(string texto) where TEnum : struct, Enum
    {
        foreach (var v in Enum.GetValues<TEnum>())
            if (Texto(v) == texto) return v;
        throw new InvalidOperationException($"Valor «{texto}» no válido para {typeof(TEnum).Name}.");
    }
}
