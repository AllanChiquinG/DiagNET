using System.Net;
using System.Net.Http.Json;
using Compartido.Contratos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Servidor.Api.Errores;

namespace Servidor.Api.Tests;

public class ErroresTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public ErroresTests(WebApplicationFactory<Program> fabrica) => _http = fabrica.CreateClient();

    [Fact]
    public async Task Ruta_api_desconocida_es_problem_details_con_codigo_y_referencia()
    {
        var respuesta = await _http.GetAsync("/api/no-existe");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        var problema = await respuesta.Content.ReadFromJsonAsync<ProblemaDto>();
        Assert.NotNull(problema);
        Assert.Equal(CodigosError.RecursoNoEncontrado, problema.Codigo);
        Assert.Equal(404, problema.Status);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
    }

    [Fact]
    public async Task Una_excepcion_no_controlada_devuelve_500_sin_exponer_detalles_internos()
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => s.AddManejoErrores())
                .Configure(app =>
                {
                    app.UseManejoErrores();
                    app.Run(_ => throw new InvalidOperationException("secreto-interno"));
                }))
            .StartAsync();

        var respuesta = await host.GetTestClient().GetAsync("/cualquiera");

        Assert.Equal(HttpStatusCode.InternalServerError, respuesta.StatusCode);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secreto-interno", texto);
        Assert.DoesNotContain("InvalidOperationException", texto);
        var problema = System.Text.Json.JsonSerializer.Deserialize<ProblemaDto>(texto,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal(CodigosError.ErrorInterno, problema!.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
    }
}
