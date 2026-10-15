namespace Servidor.Negocio.Dominio;

/// <summary>Metadatos de NuGet de una versión de un paquete (EN-03). Funciona como caché compartida entre análisis.</summary>
/// <remarks>Los valores <c>null</c> significan «no determinado»; nunca se rellenan con 0 ni <c>false</c>. <c>PaqueteAlterno</c> y <c>RangoAlterno</c> son el reemplazo declarado por el autor al deprecar; <c>DeprecacionRazones</c> y <c>DeprecacionMensaje</c> explican la deprecación al usuario; <c>TipoLicencia</c> es el tipo RECIBIDO de NuGet (<c>null</c> = desconocido: nunca se deduce del texto).</remarks>
public sealed record MetadatoLibreria(
    string NombrePaquete,
    string Version,
    DateTimeOffset? FechaPublicacion,
    long? Descargas,
    string? Licencia,
    IReadOnlyList<string>? FrameworksObjetivo,
    bool? CompatibleAot,
    bool? Deprecado,
    DateTimeOffset FechaConsulta,
    string? PaqueteAlterno = null,
    string? RangoAlterno = null,
    IReadOnlyList<string>? DeprecacionRazones = null,
    string? DeprecacionMensaje = null,
    TipoLicencia? TipoLicencia = null);

/// <summary>
/// Selección de versiones de un paquete (DEC-11): cuál es la última estable y la última compatible por TFM, con vigencia.
/// No se deduce de «el máximo de las filas locales»; <see cref="SeleccionCompleta"/> indica si se recorrieron todas las páginas del registro.
/// </summary>
public sealed record SeleccionPaquete(
    string NombrePaquete,
    string? UltimaVersionEstable,
    IReadOnlyDictionary<string, string> UltimaCompatiblePorTfm,
    bool SeleccionCompleta,
    DateTimeOffset FechaConsulta);
