namespace Compartido.Contratos;

/// <summary>Respuesta de GET /api/salud. Sin detalles sensibles.</summary>
public sealed record SaludDto(string Estado, string Servicio, string Version, DateTimeOffset FechaServidor);

/// <summary>Estado de la base de datos en la comprobación de disponibilidad. Sin cadenas de conexión, nombres de migraciones ni mensajes internos.</summary>
public sealed record BaseDatosDto(bool Disponible, bool MigracionesAlDia, int MigracionesPendientes);

/// <summary>
/// Respuesta de GET /api/salud/disponibilidad (readiness): <c>ok</c> con HTTP 200 solo si la base responde y no hay migraciones pendientes; si no, <c>no_disponible</c> con HTTP 503.
/// A diferencia de /api/salud (el proceso está vivo), esta comprobación SÍ consulta la base, con un tiempo máximo corto. Nunca consulta NuGet.
/// </summary>
public sealed record DisponibilidadDto(string Estado, string Servicio, DateTimeOffset FechaServidor, BaseDatosDto BaseDatos);

/// <summary>
/// Valores públicos de configuración que la interfaz necesita mostrar tal cual los usa el servidor (sin secretos): retención real, límites de entrada y de consulta.
/// La interfaz no inventa estos números. <c>PurgaAutomaticaActiva</c> dice si la eliminación automática por antigüedad está ENCENDIDA: mientras no lo esté, la interfaz no promete que los análisis se eliminen.
/// </summary>
public sealed record ConfiguracionPublicaDto(
    int RetencionDias,
    int MaximoIdsPorSolicitud,
    long TamanoMaximoArchivoBytes,
    int MaximoDependencias,
    int PresupuestoAnalisisSegundos,
    bool PurgaAutomaticaActiva = false);
