using System.Net;
using System.Net.Http.Json;
using Compartido.Contratos;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Servidor.Api.Tests;

public class ArranqueTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _http;

    public ArranqueTests(WebApplicationFactory<Program> fabrica) => _http = fabrica.CreateClient();

    [Fact]
    public async Task Salud_responde_200_con_estado_ok()
    {
        var respuesta = await _http.GetAsync("/api/salud");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var dto = await respuesta.Content.ReadFromJsonAsync<SaludDto>();
        Assert.NotNull(dto);
        Assert.Equal("ok", dto.Estado);
        Assert.Equal("DiagNET", dto.Servicio);
    }

    [Fact]
    public async Task Ruta_api_desconocida_devuelve_404_y_no_el_index_del_cliente()
    {
        var respuesta = await _http.GetAsync("/api/no-existe");

        Assert.Equal(HttpStatusCode.NotFound, respuesta.StatusCode);
        Assert.DoesNotContain("text/html", respuesta.Content.Headers.ContentType?.ToString() ?? "");
    }

    [Fact]
    public async Task Raiz_sirve_el_cliente_desde_el_mismo_origen()
    {
        var respuesta = await _http.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var html = await respuesta.Content.ReadAsStringAsync();
        Assert.Contains("<title>DiagNET</title>", html);
    }
}
