namespace Servidor.Datos.Entidades;

// Filas de las tablas. Son internas de Datos: el resto de la aplicación solo conoce las entidades de dominio de Negocio.

public sealed class AnalisisFila
{
    public Guid Id { get; set; }
    public string NombreArchivo { get; set; } = "";
    public string FrameworkObjetivo { get; set; } = "";
    public DateTimeOffset FechaAnalisis { get; set; }
    public string Estado { get; set; } = "";
    public int TotalDependencias { get; set; }
    public string FrameworksDeclarados { get; set; } = "[]";
    public string FrameworksEvaluados { get; set; } = "[]";
    public string Advertencias { get; set; } = "[]";
    public string VersionReglas { get; set; } = "";
    public List<DependenciaFila> Dependencias { get; set; } = [];
}

public sealed class DependenciaFila
{
    public long Id { get; set; }
    public Guid AnalisisId { get; set; }
    public string NombrePaquete { get; set; } = "";
    public string? VersionDeclarada { get; set; }
    public string TipoVersion { get; set; } = "";
    public string? Condicion { get; set; }
    public string Advertencias { get; set; } = "[]";
    public DiagnosticoFila? Diagnostico { get; set; }
}

public sealed class MetadatoLibreriaFila
{
    public long Id { get; set; }
    public string NombrePaquete { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset? FechaPublicacion { get; set; }
    public long? Descargas { get; set; }
    public string? Licencia { get; set; }
    public string? FrameworksObjetivo { get; set; }
    public bool? CompatibleAot { get; set; }
    public bool? Deprecado { get; set; }
    public DateTimeOffset FechaConsulta { get; set; }
    public string? PaqueteAlterno { get; set; }
    public string? RangoAlterno { get; set; }
    public string? DeprecacionRazones { get; set; }
    public string? DeprecacionMensaje { get; set; }
    public string? TipoLicencia { get; set; }
}

public sealed class DiagnosticoFila
{
    public long Id { get; set; }
    public long DependenciaId { get; set; }
    public long? MetadatoId { get; set; }
    public string Estado { get; set; } = "";
    public string? UltimaVersion { get; set; }
    public string Indicadores { get; set; } = "{}";
    public string Motivos { get; set; } = "";
    public DateTimeOffset? FechaConsulta { get; set; }
    public string Origen { get; set; } = "";
    public string Hallazgos { get; set; } = "[]";
    public string EvaluacionPorTfm { get; set; } = "[]";
    public List<RecomendacionFila> Recomendaciones { get; set; } = [];
}

public sealed class RecomendacionFila
{
    public long Id { get; set; }
    public long DiagnosticoId { get; set; }
    public string Tipo { get; set; } = "";
    public string PaqueteAlternativo { get; set; } = "";
    public string? VersionSugerida { get; set; }
    public string Justificacion { get; set; } = "";
    public string Evidencia { get; set; } = "[]";
    public string Limitaciones { get; set; } = "[]";
    public bool PosiblesCambiosDeCodigo { get; set; }
}

public sealed class SeleccionPaqueteFila
{
    public long Id { get; set; }
    public string NombrePaquete { get; set; } = "";
    public string? UltimaVersionEstable { get; set; }
    public string UltimaCompatiblePorTfm { get; set; } = "{}";
    public bool SeleccionCompleta { get; set; }
    public DateTimeOffset FechaConsulta { get; set; }
}
