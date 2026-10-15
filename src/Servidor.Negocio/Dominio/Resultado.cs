using Servidor.Negocio.Lectura;

namespace Servidor.Negocio.Dominio;

// Entidades de dominio del resultado de un análisis (EN-01 a EN-05). Son inmutables: un análisis guardado no se edita (RNF-07).
// No dependen de Contratos, EF ni HTTP; la API las convierte a DTO y Datos las convierte a filas.

public enum EstadoAnalisis { Completo, Parcial }

public enum EstadoDiagnostico { Actualizada, Desactualizada, Incompatible, SinSoporteActivo, NoDeterminada }

public enum OrigenDato { Nuget, CacheVigente, CacheExpirada, NoDeterminado }

public enum Disponibilidad { Conocido, NoDeterminado, NoEvaluado }

public enum ResultadoCompatibilidad { Compatible, Incompatible, NoDeterminada }

public enum GravedadHallazgo { Informativo, Aviso, Grave }

public enum TipoRecomendacion { ActualizarVersion, Alternativa, CandidatoParaRevision }

public enum TipoLicencia { Expresion, Url, Archivo }

public sealed record Advertencia(string Codigo, string Mensaje, string? Dependencia = null);

public sealed record Evidencia(string Clave, string Valor);

/// <summary><typeparamref name="T"/> debe admitir nulo (<c>bool?</c>, <c>long?</c>, <c>string?</c>): «sin valor» nunca es <c>false</c> ni <c>0</c>.</summary>
public sealed record Indicador<T>(Disponibilidad Disponibilidad, T? Valor = default, string? Nota = null);

public sealed record Licencia(TipoLicencia Tipo, string Valor);

public sealed record Deprecacion(IReadOnlyList<string> Razones, string? Mensaje, string? PaqueteAlterno, string? RangoVersionAlterno);

public sealed record CompatibilidadTfm(string Tfm, ResultadoCompatibilidad Resultado, string Base, string? Detalle = null);

/// <summary>Copia de los indicadores usados por un diagnóstico. Se guarda tal cual y no cambia aunque la caché se renueve.</summary>
public sealed record IndicadoresLibreria(
    Indicador<DateTimeOffset?> FechaPublicacionDeclarada,
    Indicador<string?> UltimaVersionEstable,
    Indicador<string?> UltimaVersionEstableCompatible,
    bool SeleccionVersionesCompleta,
    Indicador<int?> MesesDesdeUltimaPublicacion,
    Indicador<long?> Descargas,
    Indicador<Licencia?> Licencia,
    Indicador<bool?> Deprecado,
    Deprecacion? Deprecacion,
    IReadOnlyList<CompatibilidadTfm> CompatibilidadPorTfm,
    Indicador<bool?> Aot);

public sealed record Hallazgo(string Regla, string Codigo, GravedadHallazgo Gravedad, string Mensaje, IReadOnlyList<Evidencia> Evidencia);

public sealed record EvaluacionTfm(string Tfm, EstadoDiagnostico Estado);

/// <param name="FechaConsulta"><c>null</c> cuando no se obtuvo ningún dato: nunca se inventa una fecha.</param>
/// <param name="Indicadores"><c>null</c> cuando no hay indicadores (origen no determinado).</param>
public sealed record Diagnostico(
    EstadoDiagnostico Estado,
    string Motivos,
    string? UltimaVersion,
    IReadOnlyList<Hallazgo> Hallazgos,
    IReadOnlyList<EvaluacionTfm> EvaluacionPorTfm,
    OrigenDato Origen,
    DateTimeOffset? FechaConsulta,
    IndicadoresLibreria? Indicadores);

public sealed record Recomendacion(
    TipoRecomendacion Tipo,
    string Paquete,
    string? VersionSugerida,
    string Justificacion,
    IReadOnlyList<Evidencia> Evidencia,
    IReadOnlyList<string> Limitaciones,
    bool PosiblesCambiosDeCodigo);

public sealed record DependenciaAnalizada(
    string Nombre,
    string? VersionDeclarada,
    TipoVersion TipoVersion,
    string? Condicion,
    IReadOnlyList<Advertencia> Advertencias,
    Diagnostico Diagnostico,
    IReadOnlyList<Recomendacion> Recomendaciones);

/// <summary>Resultado completo de un análisis, tal como se guarda y se vuelve a abrir.</summary>
public sealed record Analisis(
    Guid Id,
    string NombreArchivo,
    IReadOnlyList<string> FrameworksDeclarados,
    IReadOnlyList<string> FrameworksEvaluados,
    DateTimeOffset FechaAnalisis,
    EstadoAnalisis Estado,
    IReadOnlyList<DependenciaAnalizada> Dependencias,
    IReadOnlyList<Advertencia> Advertencias,
    string VersionReglas);

/// <summary>Fila del historial (CU-08).</summary>
public sealed record ResumenAnalisis(
    Guid Id,
    string NombreArchivo,
    IReadOnlyList<string> FrameworksDeclarados,
    DateTimeOffset FechaAnalisis,
    int TotalDependencias,
    EstadoAnalisis Estado);

public sealed record Pagina<T>(IReadOnlyList<T> Elementos, int NumeroPagina, int TamanoPagina, int Total);
