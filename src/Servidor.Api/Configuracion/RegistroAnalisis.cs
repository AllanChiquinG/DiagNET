using Microsoft.Extensions.DependencyInjection.Extensions;
using Servidor.Api.Endpoints;
using Servidor.Integracion;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Recomendaciones;
using Servidor.Negocio.Reglas;

namespace Servidor.Api.Configuracion;

/// <summary>
/// Cablea la API con los motores REALES: gateway de NuGet (E4), servicio de metadatos y orquestación (E5), diagnóstico (E6), recomendaciones (E7) y repositorios (E3).
/// Aquí no se registra ningún sustituto de pruebas: los dobles viven solo en los proyectos de pruebas.
/// </summary>
public static class RegistroAnalisis
{
    public static IServiceCollection AddAnalisis(this IServiceCollection servicios, IConfiguration configuracion)
    {
        servicios.AddNuGet(configuracion);
        servicios.TryAddSingleton<IInferidorCompatibilidad, InferidorCompatibilidad>();

        var metadatos = Enlazar(configuracion, "Metadatos", new OpcionesMetadatos());
        var orquestacion = Enlazar(configuracion, "Analisis", new OpcionesOrquestacion());
        var recomendacion = Enlazar(configuracion, "Recomendaciones", new OpcionesRecomendacion());
        var consultas = Enlazar(configuracion, "Historial", new OpcionesConsultas());
        var api = Enlazar(configuracion, "Analisis", new OpcionesAnalisisApi());

        servicios.AddSingleton(orquestacion);
        servicios.AddSingleton(consultas);
        servicios.AddSingleton(api);
        servicios.AddSingleton(sp => new ServicioMetadatos(
            sp.GetRequiredService<IClienteNuGet>(), sp.GetRequiredService<IMetadatoRepositorio>(), sp.GetRequiredService<IInferidorCompatibilidad>(),
            sp.GetRequiredService<TimeProvider>(), metadatos));
        servicios.AddSingleton<IMotorDiagnostico>(_ => new MotorDiagnostico());
        servicios.AddSingleton<IMotorRecomendacion>(sp => new MotorRecomendacion(sp.GetRequiredService<ServicioMetadatos>(), sp.GetRequiredService<IClienteNuGet>(), recomendacion));
        servicios.AddSingleton(sp => new OrquestadorAnalisis(
            sp.GetRequiredService<Servidor.Negocio.Lectura.ILectorProyecto>(), sp.GetRequiredService<ServicioMetadatos>(), sp.GetRequiredService<IMotorDiagnostico>(),
            sp.GetRequiredService<IMotorRecomendacion>(), sp.GetRequiredService<IAnalisisRepositorio>(), sp.GetRequiredService<TimeProvider>(), orquestacion));
        return servicios;
    }

    /// <summary>Sin cadena de conexión no hay base de datos: el servidor arranca, y los análisis responden 503 (no se inventa un almacenamiento en memoria).</summary>
    public static IServiceCollection AddAlmacenamientoNoConfigurado(this IServiceCollection servicios)
    {
        servicios.AddSingleton<AlmacenamientoNoConfigurado>();
        servicios.AddSingleton<IAnalisisRepositorio>(sp => sp.GetRequiredService<AlmacenamientoNoConfigurado>());
        servicios.AddSingleton<IMetadatoRepositorio>(sp => sp.GetRequiredService<AlmacenamientoNoConfigurado>());
        return servicios;
    }

    private static T Enlazar<T>(IConfiguration configuracion, string seccion, T opciones)
    {
        configuracion.GetSection(seccion).Bind(opciones);
        return opciones;
    }
}

/// <summary>Límite de análisis simultáneos (protege memoria y el cupo de consultas a NuGet).</summary>
public sealed class OpcionesAnalisisApi
{
    public int MaxAnalisisSimultaneos { get; set; } = 8;
}

/// <summary>Repositorio que siempre falla: representa «no hay base de datos configurada».</summary>
internal sealed class AlmacenamientoNoConfigurado : IAnalisisRepositorio, IMetadatoRepositorio
{
    private static InvalidOperationException Falla() =>
        new("No hay una cadena de conexión «DiagNet» configurada (appsettings.Local.json o variable de entorno ConnectionStrings__DiagNet).");

    public Task GuardarAsync(Analisis analisis, CancellationToken ct = default) => throw Falla();
    public Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default) => throw Falla();
    public Task<Pagina<ResumenAnalisis>> ListarAsync(int numeroPagina, int tamanoPagina, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw Falla();
    public Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw Falla();
    public Task<MetadatoLibreria?> BuscarAsync(string nombrePaquete, string version, CancellationToken ct = default) => throw Falla();
    public Task GuardarAsync(MetadatoLibreria metadato, CancellationToken ct = default) => throw Falla();
    public Task<int> PurgarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw Falla();
    public Task<SeleccionPaquete?> BuscarSeleccionAsync(string nombrePaquete, CancellationToken ct = default) => throw Falla();
    public Task GuardarSeleccionAsync(SeleccionPaquete seleccion, CancellationToken ct = default) => throw Falla();
    public Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw Falla();
}
