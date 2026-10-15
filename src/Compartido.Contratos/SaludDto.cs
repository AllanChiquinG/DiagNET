namespace Compartido.Contratos;

/// <summary>Respuesta de GET /api/salud. Sin detalles sensibles.</summary>
public sealed record SaludDto(string Estado, string Servicio, string Version, DateTimeOffset FechaServidor);
