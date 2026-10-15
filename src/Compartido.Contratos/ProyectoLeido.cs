namespace Compartido.Contratos;

public sealed record FrameworkDeclaradoDto(string Valor, bool EnAlcance, string? Nota = null);

/// <summary>Dependencia tal como la declara el .csproj, sin consultar NuGet. La versión se conserva literal.</summary>
public sealed record DependenciaLeidaDto(
    string Nombre,
    string? VersionDeclarada,
    TipoVersionDeclarada TipoVersion,
    string? Condicion,
    IReadOnlyList<AdvertenciaDto> Advertencias);

/// <summary>
/// Respuesta del endpoint temporal POST /api/proyectos/lectura (E2). No persiste ni consulta NuGet.
/// Se retirará o conservará al integrar E8 (decisión de integración pendiente).
/// </summary>
public sealed record ProyectoLeidoDto(
    string NombreArchivo,
    IReadOnlyList<FrameworkDeclaradoDto> Frameworks,
    IReadOnlyList<DependenciaLeidaDto> Dependencias,
    IReadOnlyList<AdvertenciaDto> Advertencias);
