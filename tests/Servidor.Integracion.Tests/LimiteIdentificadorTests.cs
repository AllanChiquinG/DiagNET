using Servidor.Integracion.NuGet;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.Tests;

/// <summary>
/// DEC-16: el límite de 100 caracteres del identificador es EXTERNO (nuget.org, referencia oficial del .nuspec) y solo decide si se CONSULTA.
/// Es distinto de los límites de lectura (200, E2) y de almacenamiento (200, E3), que no cambian.
/// </summary>
public class LimiteIdentificadorTests
{
    private static string Proyecto(string id) =>
        $"""<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="{id}" Version="1.0.0" /></ItemGroup></Project>""";

    private static ProyectoLeido Leer(string id) =>
        new LectorCsproj(new OpcionesLectura()).Leer("a.csproj", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Proyecto(id))));

    [Theory]
    [InlineData(101)]
    [InlineData(150)]
    [InlineData(200)]
    public async Task Un_identificador_de_101_a_200_caracteres_se_lee_y_se_guarda_pero_no_se_consulta_en_nuget_org(int longitud)
    {
        var id = new string('a', longitud);
        var proyecto = Leer(id); // el límite de lectura (200, E2) lo acepta
        var nuget = new NuGetSimulado().ServiciosEstandar();
        var (gw, _, _) = NuGetSimulado.Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete(proyecto.Dependencias.Single().Nombre, "1.0.0", ["net8.0"]));

        Assert.Equal(longitud, proyecto.Dependencias.Single().Nombre.Length);
        Assert.Equal(EstadoConsulta.NoEncontrado, r.Estado);
        Assert.Contains("nuget.org admite como máximo 100", r.Mensaje);
        Assert.Contains(r.Advertencias, a => a.Codigo == "identificador_excede_limite_nuget");
        Assert.DoesNotContain(r.Advertencias, a => a.Codigo == "identificador_no_valido"); // se distingue del formato inválido
        Assert.Equal(0, nuget.Total); // ni siquiera se descubre el índice: no se envía nada
    }

    [Fact]
    public async Task Un_identificador_de_201_caracteres_ni_siquiera_se_lee_limite_de_lectura_de_E2()
    {
        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer(new string('a', 201)));

        Assert.Equal(CodigosLectura.NombrePaqueteDemasiadoLargo, ex.Codigo);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Un_identificador_de_exactamente_100_caracteres_si_se_consulta()
    {
        var id = new string('a', 100);
        var nuget = new NuGetSimulado().AgregarInline(id, Registro.Hoja("1.0.0"));
        var (gw, _, _) = NuGetSimulado.Crear(nuget, o => o.ConsultarDescargas = false);

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete(id, "1.0.0", ["net8.0"]));

        Assert.True(r.EsCorrecto);
    }

    [Fact]
    public async Task El_formato_invalido_se_distingue_del_exceso_de_longitud()
    {
        var (gw, _, _) = NuGetSimulado.Crear(new NuGetSimulado().ServiciosEstandar());

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete("../malo", "1.0.0", ["net8.0"]));

        Assert.Contains(r.Advertencias, a => a.Codigo == "identificador_no_valido");
        Assert.DoesNotContain(r.Advertencias, a => a.Codigo == "identificador_excede_limite_nuget");
    }

    [Fact]
    public void El_limite_externo_es_configurable_y_no_altera_los_limites_de_lectura_ni_almacenamiento()
    {
        Assert.Equal(100, new OpcionesNuGet().MaxLongitudIdentificador);
        Assert.Equal(200, Servidor.Negocio.Dominio.LimitesDatos.NombrePaquete);
    }

    [Fact]
    public async Task Un_alterno_declarado_por_NuGet_con_mas_de_100_caracteres_se_ignora_con_advertencia()
    {
        var dep = Registro.Deprecacion(alterno: new string('b', 101));
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0", deprecacion: dep));
        var (gw, _, _) = NuGetSimulado.Crear(nuget, o => o.ConsultarDescargas = false);

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete("Ejemplo.Paquete", "1.0.0", ["net8.0"]));

        Assert.Null(r.Valor!.Declarada!.Deprecacion!.PaqueteAlterno);
        Assert.Contains(r.Advertencias, a => a.Codigo == "paquete_alterno_invalido");
    }
}
