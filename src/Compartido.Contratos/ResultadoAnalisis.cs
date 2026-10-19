namespace Compartido.Contratos;

/// <summary>Hallazgo de una regla: motivo legible, gravedad y los datos que lo respaldan.</summary>
public sealed record HallazgoDto(
    string Regla,
    string Codigo,
    GravedadHallazgo Gravedad,
    string Mensaje,
    IReadOnlyList<EvidenciaDto> Evidencia,
    bool EvidenciaDecisivaFaltante = false);

public sealed record EvaluacionTfmDto(string Tfm, EstadoDiagnostico Estado);

public sealed record DiagnosticoDto(
    EstadoDiagnostico Estado,
    string Resumen,
    IReadOnlyList<HallazgoDto> Hallazgos,
    IReadOnlyList<EvaluacionTfmDto> EvaluacionPorTfm,
    OrigenDato Origen,
    DateTimeOffset? FechaConsulta,
    IndicadoresDto? Indicadores);

public sealed record RecomendacionDto(
    TipoRecomendacion Tipo,
    string Paquete,
    string? VersionSugerida,
    string Justificacion,
    IReadOnlyList<EvidenciaDto> Evidencia,
    IReadOnlyList<string> Limitaciones,
    bool PosiblesCambiosDeCodigo,
    string? Etiqueta = null,
    string? TextoCambiosDeCodigo = null);

public sealed record DependenciaAnalizadaDto(
    string Nombre,
    string? VersionDeclarada,
    TipoVersionDeclarada TipoVersion,
    string? Condicion,
    IReadOnlyList<AdvertenciaDto> Advertencias,
    DiagnosticoDto Diagnostico,
    IReadOnlyList<RecomendacionDto> Recomendaciones);

/// <summary>Total más los cinco estados; se calcula siempre desde las dependencias, nunca se escribe a mano.</summary>
public sealed record ResumenEstadosDto(
    int Total,
    int Actualizadas,
    int Desactualizadas,
    int Incompatibles,
    int SinSoporteActivo,
    int NoDeterminadas)
{
    public static ResumenEstadosDto Calcular(IEnumerable<DependenciaAnalizadaDto> dependencias)
    {
        var lista = dependencias.ToList();
        int Contar(EstadoDiagnostico e) => lista.Count(d => d.Diagnostico.Estado == e);
        return new ResumenEstadosDto(
            lista.Count,
            Contar(EstadoDiagnostico.Actualizada),
            Contar(EstadoDiagnostico.Desactualizada),
            Contar(EstadoDiagnostico.Incompatible),
            Contar(EstadoDiagnostico.SinSoporteActivo),
            Contar(EstadoDiagnostico.NoDeterminada));
    }
}

/// <summary>Resultado persistido y completo de un análisis (respuesta de POST y GET /api/analisis).</summary>
public sealed record ResultadoAnalisisDto(
    Guid Id,
    string NombreArchivo,
    IReadOnlyList<string> FrameworksDeclarados,
    IReadOnlyList<string> FrameworksEvaluados,
    DateTimeOffset FechaAnalisis,
    EstadoAnalisis Estado,
    ResumenEstadosDto Resumen,
    IReadOnlyList<DependenciaAnalizadaDto> Dependencias,
    IReadOnlyList<AdvertenciaDto> Advertencias,
    string VersionReglas,
    int RetencionDias);

/// <summary>Fila del historial (CU-08).</summary>
public sealed record ResumenAnalisisDto(
    Guid Id,
    string NombreArchivo,
    IReadOnlyList<string> FrameworksDeclarados,
    DateTimeOffset FechaAnalisis,
    int TotalDependencias,
    EstadoAnalisis Estado);
