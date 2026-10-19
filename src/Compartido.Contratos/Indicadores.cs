namespace Compartido.Contratos;

/// <summary>El tipo es el que informó NuGet; <c>null</c> = desconocido (nunca se deduce del texto).</summary>
public sealed record LicenciaDto(TipoLicencia? Tipo, string Valor);

/// <summary>Detalle de deprecación publicado por NuGet. Es evidencia publicada, no prueba de falta de soporte.</summary>
public sealed record DeprecacionDto(
    IReadOnlyList<string> Razones,
    string? Mensaje,
    string? PaqueteAlterno,
    string? RangoVersionAlterno);

/// <summary>
/// Compatibilidad inferida para un TFM concreto. <paramref name="Base"/> indica el método (p. ej. <c>inferida_dependency_groups</c>);
/// nunca es una certificación de assets ni de ejecución.
/// </summary>
public sealed record CompatibilidadTfmDto(string Tfm, ResultadoCompatibilidad Resultado, string Base, string? Detalle = null);

/// <summary>Copia de los indicadores usados por el diagnóstico; se guarda en el snapshot y no cambia con la caché (RNF-07).</summary>
public sealed record IndicadoresDto(
    IndicadorDto<DateTimeOffset?> FechaPublicacionDeclarada,
    IndicadorDto<string?> UltimaVersionEstable,
    IndicadorDto<string?> UltimaVersionEstableCompatible,
    bool SeleccionVersionesCompleta,
    IndicadorDto<int?> MesesDesdeUltimaPublicacion,
    IndicadorDto<long?> Descargas,
    IndicadorDto<LicenciaDto?> Licencia,
    IndicadorDto<bool?> Deprecado,
    DeprecacionDto? Deprecacion,
    IReadOnlyList<CompatibilidadTfmDto> CompatibilidadPorTfm,
    IndicadorDto<bool?> Aot);
