using Compartido.Contratos;
using Servidor.Negocio.Lectura;

namespace Servidor.Api.Mapeo;

/// <summary>Convierte los tipos de Negocio en contratos públicos. Negocio no conoce Contratos: el mapeo vive aquí.</summary>
public static class MapeadorProyecto
{
    public static ProyectoLeidoDto ADto(ProyectoLeido proyecto) => new(
        proyecto.NombreArchivo,
        proyecto.Frameworks.Select(f => new FrameworkDeclaradoDto(f.Valor, f.EnAlcance, f.Nota)).ToList(),
        proyecto.Dependencias.Select(d => new DependenciaLeidaDto(
            d.Nombre,
            d.Version,
            Enum.Parse<TipoVersionDeclarada>(d.TipoVersion.ToString()),
            d.Condicion,
            d.Advertencias.Select(ADto).ToList())).ToList(),
        proyecto.Advertencias.Select(ADto).ToList());

    private static AdvertenciaDto ADto(AdvertenciaLectura a) => new(a.Codigo, a.Mensaje, a.Dependencia);
}
