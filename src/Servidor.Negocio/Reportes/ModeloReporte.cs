namespace Servidor.Negocio.Reportes;

// Modelo del reporte PDF (E10). Contiene TEXTO YA FINAL: el generador solo dibuja y pagina, no decide ni redacta nada. Todo sale del snapshot guardado.

/// <summary>Pareja rotulada (indicador, dato de evidencia, fila de identificación). <paramref name="Conocido"/> distingue un valor conocido de «no determinado/no evaluado».</summary>
public sealed record ParReporte(string Titulo, string Valor, string? Nota = null, bool Conocido = true);

public sealed record EncabezadoReporte(
    string NombreArchivo,
    Guid IdAnalisis,
    IReadOnlyList<string> FrameworksDeclarados,
    IReadOnlyList<string> FrameworksEvaluados,
    string FechaAnalisis,
    string FechaGeneracion,
    string EstadoAnalisis,
    string VersionReglas,
    int RetencionDias,
    bool PurgaAutomaticaActiva = false);

public sealed record ResumenReporte(int Total, int Actualizadas, int Desactualizadas, int Incompatibles, int SinSoporteActivo, int NoDeterminadas);

/// <param name="Orden">Posición por gravedad (0 = lo que más atención exige): igual que la tabla en pantalla.</param>
public sealed record FilaReporte(string Nombre, string VersionDeclarada, string UltimaVersion, string Estado, string Origen, string Recomendacion, IReadOnlyList<string> Etiquetas);

public sealed record MotivoReporte(string Gravedad, string Mensaje, IReadOnlyList<ParReporte> Evidencia);

public sealed record RecomendacionReporte(
    string Etiqueta,
    string Paquete,
    string VersionComprobada,
    string Justificacion,
    string CambiosDeCodigo,
    IReadOnlyList<ParReporte> Evidencia,
    IReadOnlyList<string> Limitaciones);

public sealed record DetalleReporte(
    string Nombre,
    string VersionDeclarada,
    string Estado,
    string Titulo,
    string Origen,
    IReadOnlyList<MotivoReporte> Motivos,
    IReadOnlyList<MotivoReporte> EvidenciaFaltante,
    IReadOnlyList<MotivoReporte> Informativos,
    IReadOnlyList<ParReporte> EstadoPorFramework,
    bool TieneIndicadores,
    IReadOnlyList<ParReporte> Indicadores,
    IReadOnlyList<string> Deprecacion,
    IReadOnlyList<string> Compatibilidad,
    IReadOnlyList<string> Avisos,
    IReadOnlyList<string> NotasRecomendacion,
    IReadOnlyList<RecomendacionReporte> Recomendaciones);

public sealed record ModeloReporte(
    EncabezadoReporte Encabezado,
    ResumenReporte Resumen,
    bool DiagnosticoParcial,
    IReadOnlyList<string> AvisosDelAnalisis,
    IReadOnlyList<string> AvisosDeRecomendaciones,
    IReadOnlyList<FilaReporte> Filas,
    IReadOnlyList<DetalleReporte> Detalles,
    IReadOnlyList<string> Limitaciones);
