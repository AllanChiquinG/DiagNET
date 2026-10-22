namespace Compartido.Contratos;

/// <summary>Respuesta de GET /api/salud. Sin detalles sensibles.</summary>
public sealed record SaludDto(string Estado, string Servicio, string Version, DateTimeOffset FechaServidor);

/// <summary>
/// Valores públicos de configuración que la interfaz necesita mostrar tal cual los usa el servidor (sin secretos): retención real, límites de entrada y de consulta.
/// La interfaz no inventa estos números.
/// </summary>
public sealed record ConfiguracionPublicaDto(
    int RetencionDias,
    int MaximoIdsPorSolicitud,
    long TamanoMaximoArchivoBytes,
    int MaximoDependencias,
    int PresupuestoAnalisisSegundos);
