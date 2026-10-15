using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Servidor.Datos.Repositorios;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos;

public static class RegistroDatos
{
    public const string NombreCadenaConexion = "DiagNet";

    /// <summary>Registra la fábrica de contextos y los repositorios. Las migraciones NO se aplican al arrancar (paso controlado).</summary>
    public static IServiceCollection AddDatos(this IServiceCollection servicios, IConfiguration configuracion)
    {
        var cadena = configuracion.GetConnectionString(NombreCadenaConexion)
            ?? throw new InvalidOperationException(
                $"Falta la cadena de conexión «{NombreCadenaConexion}». Defínela en appsettings.Local.json (no se versiona).");
        return servicios.AddDatos(cadena);
    }

    public static IServiceCollection AddDatos(this IServiceCollection servicios, string cadenaConexion)
    {
        servicios.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(cadenaConexion));
        servicios.AddSingleton<IAnalisisRepositorio, AnalisisRepositorio>();
        servicios.AddSingleton<IMetadatoRepositorio, MetadatoRepositorio>();
        return servicios;
    }
}
