using NuGet.Versioning;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

/// <summary>Una hoja del registro con su versión ya interpretada con las reglas de NuGet (SemVer 2.0).</summary>
internal sealed record HojaVersion(NuGetVersion Orden, VersionPublicada Datos);

/// <summary>
/// Elección de versiones, separada del acceso a la red y de los metadatos de una versión concreta.
/// Estable = sin etiqueta de preliminar; solo cuentan las versiones LISTADAS para «última». Nunca se ordena por texto.
/// </summary>
internal static class SelectorVersion
{
    public static HojaVersion? UltimaEstable(IEnumerable<HojaVersion> hojas) =>
        hojas.Where(h => h.Datos.Listada && !h.Datos.EsPrerelease).MaxBy(h => h.Orden);

    public static HojaVersion? UltimaPrerelease(IEnumerable<HojaVersion> hojas) =>
        hojas.Where(h => h.Datos.Listada && h.Datos.EsPrerelease).MaxBy(h => h.Orden);

    /// <summary>La hoja cuya versión coincide con la declarada. Ignora los metadatos de compilación (+x) y no distingue mayúsculas en la etiqueta.</summary>
    public static HojaVersion? Declarada(IEnumerable<HojaVersion> hojas, NuGetVersion declarada) =>
        hojas.FirstOrDefault(h => VersionComparer.Default.Equals(h.Orden, declarada));

    /// <summary>La mayor versión estable y listada cuyos grupos son compatibles con el TFM (inferencia). <c>null</c> si no hay ninguna entre las leídas.</summary>
    public static HojaVersion? UltimaEstableCompatible(IEnumerable<HojaVersion> hojas, string tfm, InferidorCompatibilidad inferidor) =>
        hojas.Where(h => h.Datos.Listada && !h.Datos.EsPrerelease)
             .OrderByDescending(h => h.Orden)
             .FirstOrDefault(h => inferidor.Inferir(tfm, h.Datos.GruposFramework).Resultado == ResultadoCompatibilidad.Compatible);
}
