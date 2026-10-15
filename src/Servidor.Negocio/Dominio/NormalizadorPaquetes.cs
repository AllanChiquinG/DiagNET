using System.Text.RegularExpressions;

namespace Servidor.Negocio.Dominio;

/// <summary>
/// Normaliza identificadores y versiones para comparar y para la unicidad de la caché (NuGet no distingue mayúsculas
/// en el identificador y trata <c>1.0</c>, <c>1.0.0</c> y <c>1.0.0.0</c> como la misma versión).
/// </summary>
public static partial class NormalizadorPaquetes
{
    [GeneratedRegex(@"^(?<m>\d+)(\.(?<n>\d+))?(\.(?<p>\d+))?(\.(?<r>\d+))?(-(?<pre>[0-9A-Za-z.\-]+))?(\+[0-9A-Za-z.\-]+)?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Version();

    public static string Identificador(string nombre) => nombre.Trim().ToLowerInvariant();

    /// <summary>
    /// Forma normal de una versión: sin ceros a la izquierda, sin revisión 0, sin metadatos de compilación (<c>+x</c>)
    /// y con la etiqueta de preliminar en minúsculas. Si no es una versión reconocible, se devuelve recortada y en minúsculas.
    /// </summary>
    public static string VersionNormalizada(string version)
    {
        var texto = version.Trim();
        var m = Version().Match(texto);
        if (!m.Success) return texto.ToLowerInvariant();

        // Los componentes se normalizan como texto (sin ceros a la izquierda) para no fallar con números enormes.
        static string Num(Group g)
        {
            if (!g.Success) return "0";
            var recortado = g.Value.TrimStart('0');
            return recortado.Length == 0 ? "0" : recortado;
        }

        var partes = $"{Num(m.Groups["m"])}.{Num(m.Groups["n"])}.{Num(m.Groups["p"])}";
        var revision = Num(m.Groups["r"]);
        if (revision != "0") partes += $".{revision}";
        return m.Groups["pre"].Success ? $"{partes}-{m.Groups["pre"].Value.ToLowerInvariant()}" : partes;
    }
}
