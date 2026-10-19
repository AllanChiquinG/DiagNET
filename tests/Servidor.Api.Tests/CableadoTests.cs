using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Api.Tests;

/// <summary>La aplicación TAL COMO ARRANCA (sin cambios de prueba) resuelve todo el grafo de análisis con los motores reales; no abre conexiones al resolverlo.</summary>
public class CableadoTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _fabrica;

    public CableadoTests(WebApplicationFactory<Program> fabrica) => _fabrica = fabrica;

    [Fact]
    public void El_grafo_de_analisis_se_resuelve_con_motores_reales()
    {
        using var alcance = _fabrica.Services.CreateScope();
        var sp = alcance.ServiceProvider;

        Assert.NotNull(sp.GetRequiredService<OrquestadorAnalisis>());
        Assert.Equal("MotorDiagnostico", sp.GetRequiredService<IMotorDiagnostico>().GetType().Name);
        Assert.Equal("MotorRecomendacion", sp.GetRequiredService<IMotorRecomendacion>().GetType().Name);
        Assert.Equal("NuGetGateway", sp.GetRequiredService<Servidor.Negocio.Nuget.IClienteNuGet>().GetType().Name);
        Assert.Equal("MotorDiagnostico", sp.GetRequiredService<IMotorDiagnostico>().GetType().Name);
        Assert.DoesNotContain("Test", sp.GetRequiredService<IMotorRecomendacion>().GetType().Assembly.GetName().Name!);
    }

    [Fact]
    public async Task Los_post_a_analisis_con_una_entrada_invalida_responden_antes_de_tocar_la_base_aunque_no_haya_una_configurada()
    {
        using var cliente = _fabrica.CreateClient();
        using var contenido = new MultipartFormDataContent { { new ByteArrayContent([]), "archivo", "Mi.csproj" } };

        var respuesta = await cliente.PostAsync("/api/analisis", contenido);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, respuesta.StatusCode);   // archivo_vacio: se valida antes de cualquier consulta o persistencia
    }
}
