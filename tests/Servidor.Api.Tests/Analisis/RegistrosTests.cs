using System.Net;
using System.Net.Http.Json;
using Compartido.Contratos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Servidor.Negocio.Lectura;
using static Servidor.Api.Tests.Analisis.Ayuda;

namespace Servidor.Api.Tests.Analisis;

/// <summary>
/// Los registros habituales no deben guardar la URL completa con identificadores de análisis (ruta de detalle ni consulta ?ids=), ni el contenido del proyecto
/// (nombre del archivo, paquetes, XML), pero los errores conservan una referencia útil para encontrarlos. Se prueba con el nivel Trace como configuración por defecto.
/// </summary>
public sealed class RegistrosTests : IClassFixture<BaseDeDatosApi>
{
    private readonly BaseDeDatosApi _bd;

    public RegistrosTests(BaseDeDatosApi bd) => _bd = bd;

    private static void SinDatosSensibles(IEnumerable<CapturadorDeRegistros.Entrada> entradas, IEnumerable<string> prohibidos)
    {
        foreach (var entrada in entradas)
            foreach (var prohibido in prohibidos)
                Assert.False(entrada.Texto.Contains(prohibido, StringComparison.OrdinalIgnoreCase),
                    $"El registro [{entrada.Categoria}] contiene «{prohibido}»: {entrada.Texto}");
    }

    [Fact]
    public async Task El_flujo_completo_no_deja_en_los_registros_ni_identificadores_ni_urls_ni_contenido_del_proyecto_ni_siquiera_con_nivel_Trace()
    {
        var registros = new CapturadorDeRegistros();
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, nuget, registros: registros);
        using var http = fabrica.CreateClient();
        var paquete = Unico("Ejemplo.Reg");

        var creado = await http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (paquete, "1.0.0"), ("Ejemplo.NoExiste", "1.0.0"), ("Ejemplo.Obsoleto", "1.0.0")), "MiSecreto.csproj"));
        var id = (await creado.Content.ReadFromJsonAsync<ResultadoAnalisisDto>())!.Id;
        await http.GetAsync($"/api/analisis/{id}");
        await http.GetAsync($"/api/analisis?ids={id},{Guid.NewGuid()}&pagina=1&tamano=20");
        await http.GetAsync($"/api/analisis/{Guid.NewGuid()}");                   // 404
        await http.GetAsync("/api/analisis/no-es-un-uuid");                         // 400
        await http.GetAsync("/api/analisis");                                       // 400
        await http.PostAsync("/api/analisis", Formulario("esto no es xml", "OtroSecreto.csproj"));
        nuget.Caido = true;
        await http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (Unico("Ejemplo.Caido"), "1.0.0")), "TercerSecreto.csproj"));

        var entradas = registros.Entradas;
        Assert.NotEmpty(entradas);   // se capturó algo: la prueba no es vacía
        SinDatosSensibles(entradas, [id.ToString(), id.ToString("N"), "/api/analisis", "ids=", "MiSecreto", "OtroSecreto", "TercerSecreto", ".csproj", "Ejemplo.", "<Project", "PackageReference"]);
        // Las categorías que escriben cada solicitud (Hosting, enrutamiento, HttpClient, EF) quedan en Warning como mínimo.
        Assert.DoesNotContain(entradas, e => (e.Categoria.StartsWith("Microsoft.AspNetCore") || e.Categoria.StartsWith("System.Net.Http")
                                              || e.Categoria.StartsWith("Microsoft.EntityFrameworkCore") || e.Categoria.StartsWith("Npgsql")) && e.Nivel < LogLevel.Warning);
    }

    [Fact]
    public async Task Un_fallo_de_almacenamiento_deja_la_referencia_que_ve_el_usuario_sin_el_identificador_ni_el_archivo()
    {
        var registros = new CapturadorDeRegistros();
        var rota = new Npgsql.NpgsqlConnectionStringBuilder(_bd.CadenaConexion) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 3 }.ConnectionString;
        using var fabrica = new FabricaAnalisis(rota, new NuGetSimulado(), registros: registros);
        using var http = fabrica.CreateClient();
        var id = Guid.NewGuid();
        var cadena = new Npgsql.NpgsqlConnectionStringBuilder(rota);   // valores REALES de las credenciales (no palabras sueltas, que aparecen en nombres de parámetros de las trazas)

        var post = await http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (Unico("Ejemplo.S"), "1.0.0")), "Confidencial.csproj"));
        var get = await http.GetAsync($"/api/analisis/{id}");
        var lista = await http.GetAsync($"/api/analisis?ids={id}");

        foreach (var respuesta in new[] { post, get, lista })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
            var referencia = (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!.Referencia;
            Assert.False(string.IsNullOrWhiteSpace(referencia));
            Assert.Contains(registros.Entradas, e => e.Nivel == LogLevel.Error && e.Texto.Contains(referencia!));   // la referencia permite encontrar el error
        }

        SinDatosSensibles(registros.Entradas, [id.ToString(), id.ToString("N"), "Confidencial", ".csproj", "ids=", "/api/analisis", "Ejemplo.", cadena.Password!, cadena.Username!]);
    }

    [Fact]
    public async Task Un_error_no_controlado_responde_500_y_registra_la_referencia_sin_el_contenido_del_proyecto()
    {
        var registros = new CapturadorDeRegistros();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, new NuGetSimulado(), registros: registros,
            personalizar: servicios => servicios.AddSingleton<ILectorProyecto>(new LectorQueFalla()));
        using var http = fabrica.CreateClient();

        var respuesta = await http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (Unico("Ejemplo.X"), "1.0.0")), "Privado.csproj"));

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        var problema = (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!;
        Assert.Equal(CodigosError.ErrorInterno, problema.Codigo);
        Assert.Contains(registros.Entradas, e => e.Nivel == LogLevel.Error && e.Texto.Contains(problema.Referencia!));
        SinDatosSensibles(registros.Entradas, ["Privado", "Ejemplo.", "<Project", "/api/analisis"]);
    }

    private sealed class LectorQueFalla : ILectorProyecto
    {
        public ProyectoLeido Leer(string? nombreArchivo, Stream contenido) => throw new InvalidOperationException("fallo inesperado del lector (prueba)");
    }
}
