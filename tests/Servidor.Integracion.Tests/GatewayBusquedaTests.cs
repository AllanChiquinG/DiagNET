using System.Net;
using System.Text;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.Tests;

public class GatewayBusquedaTests
{
    private static readonly string[] Net8 = ["net8.0"];

    private static string Item(string id, long? descargas = 1000, bool? verificado = false, string version = "1.0.0", string descripcion = "Un paquete.") =>
        $"{{\"id\":\"{id}\",\"version\":\"{version}\",\"description\":\"{descripcion}\""
        + (descargas is null ? "" : $",\"totalDownloads\":{descargas}") + (verificado is null ? "" : $",\"verified\":{(verificado.Value ? "true" : "false")}") + "}";

    private static string Busqueda(params string[] items) => "{\"totalHits\":" + items.Length + ",\"data\":[" + string.Join(',', items) + "]}";

    private static NuGetSimulado ConBusqueda(string json) =>
        new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"))
            .Prefijo(NuGetSimulado.UrlBusqueda, (_, _, _) => Task.FromResult(NuGetSimulado.Respuesta(HttpStatusCode.OK, json)));

    private static (NuGetGateway, Microsoft.Extensions.Time.Testing.FakeTimeProvider) Crear(NuGetSimulado n, Action<Servidor.Integracion.NuGet.OpcionesNuGet>? ajustar = null)
    {
        var (gw, t, _) = NuGetSimulado.Crear(n, o => ajustar?.Invoke(o));
        return (gw, t);
    }

    private static ConsultaPaquete Consulta() => new("Ejemplo.Paquete", "1.0.0", Net8);

    private static int Busquedas(NuGetSimulado n) => n.Contar(u => u.StartsWith(NuGetSimulado.UrlBusqueda));

    // ---------- descargas por paquete (DEC-05) ----------

    [Fact]
    public async Task Cada_consulta_de_paquete_incluye_sus_descargas_con_una_busqueda_por_identificador_exacto()
    {
        var nuget = ConBusqueda(Busqueda(Item("Ejemplo.Paquete", 9876543)));
        var (gw, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(9876543L, r.Valor!.Descargas);
        Assert.Null(r.Valor.DescargasNota);
        Assert.Equal(1, Busquedas(nuget));
        Assert.Contains(nuget.Solicitudes, u => u.StartsWith(NuGetSimulado.UrlBusqueda) && u.Contains("packageid%3Aejemplo.paquete", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, r.SolicitudesHttp); // índice + registro + búsqueda
    }

    [Fact]
    public async Task Si_la_busqueda_falla_las_descargas_quedan_en_null_con_explicacion_y_el_paquete_se_entrega()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0")).Prefijo(NuGetSimulado.UrlBusqueda, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var (gw, tiempo) = Crear(nuget);

        var r = await NuGetSimulado.ConTiempo(gw.ObtenerPaqueteAsync(Consulta()), tiempo);

        Assert.True(r.EsCorrecto);
        Assert.Null(r.Valor!.Descargas);
        Assert.NotNull(r.Valor.DescargasNota);
        Assert.Contains("No se pudieron consultar las descargas", r.Valor.DescargasNota);
        Assert.Contains(r.Advertencias, a => a.Codigo == "descargas_no_disponibles");
        Assert.Equal("1.0.0", r.Valor.UltimaEstable!.Version); // el resto del resultado no se pierde
    }

    [Theory]
    [InlineData("{\"id\":\"Ejemplo.Paquete\",\"version\":\"1.0.0\"}")]                                // sin totalDownloads
    [InlineData("{\"id\":\"Ejemplo.Paquete\",\"version\":\"1.0.0\",\"totalDownloads\":-5}")]          // valor imposible
    [InlineData("{\"id\":\"Ejemplo.Paquete\",\"version\":\"1.0.0\",\"totalDownloads\":\"mucho\"}")]   // tipo inesperado
    public async Task Si_NuGet_no_informa_un_total_valido_las_descargas_son_null_con_explicacion_nunca_0(string item)
    {
        var nuget = ConBusqueda(Busqueda(item));
        var (gw, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Null(r.Valor!.Descargas);
        Assert.NotNull(r.Valor.DescargasNota);
        Assert.Contains(r.Advertencias, a => a.Codigo == "descargas_no_disponibles");
    }

    [Fact]
    public async Task Si_la_busqueda_no_devuelve_el_paquete_las_descargas_son_null_con_explicacion()
    {
        var nuget = ConBusqueda(Busqueda(Item("Otro.Paquete", 5)));
        var (gw, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Null(r.Valor!.Descargas);
        Assert.Contains("no devolvió", r.Valor.DescargasNota);
    }

    [Fact]
    public async Task Un_total_real_de_cero_se_conserva_como_cero_porque_NuGet_lo_informo()
    {
        var nuget = ConBusqueda(Busqueda(Item("Ejemplo.Paquete", 0)));
        var (gw, _) = Crear(nuget);

        Assert.Equal(0L, (await gw.ObtenerPaqueteAsync(Consulta())).Valor!.Descargas);
    }

    [Fact]
    public async Task El_identificador_se_compara_sin_distinguir_mayusculas()
    {
        var nuget = ConBusqueda(Busqueda(Item("EJEMPLO.PAQUETE", 77)));
        var (gw, _) = Crear(nuget);

        Assert.Equal(77L, (await gw.ObtenerPaqueteAsync(Consulta())).Valor!.Descargas);
    }

    [Fact]
    public async Task Las_descargas_usan_la_cache_10_minutos_y_despues_se_consultan_de_nuevo()
    {
        var nuget = ConBusqueda(Busqueda(Item("Ejemplo.Paquete", 123)));
        var (gw, tiempo) = Crear(nuget);

        await gw.ObtenerPaqueteAsync(Consulta());
        var segunda = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(1, Busquedas(nuget));
        Assert.Equal(123L, segunda.Valor!.Descargas);
        Assert.Equal(0, segunda.SolicitudesHttp);

        tiempo.Advance(TimeSpan.FromMinutes(11));
        await gw.ObtenerPaqueteAsync(Consulta());
        Assert.Equal(2, Busquedas(nuget));
    }

    [Fact]
    public async Task Con_el_presupuesto_agotado_las_descargas_quedan_en_null_con_explicacion_y_el_paquete_se_entrega()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"))
            .Prefijo(NuGetSimulado.UrlBusqueda, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return new HttpResponseMessage(HttpStatusCode.OK); });
        var (gw, tiempo) = Crear(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(3), tiempo);

        var r = await NuGetSimulado.ConTiempo(gw.ObtenerPaqueteAsync(Consulta(), presupuesto), tiempo, pasoMs: 500);

        Assert.True(r.EsCorrecto);
        Assert.Null(r.Valor!.Descargas);
        Assert.Contains("tiempo", r.Valor.DescargasNota!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Se_pueden_desactivar_las_descargas_por_configuracion()
    {
        var nuget = ConBusqueda(Busqueda(Item("Ejemplo.Paquete", 5)));
        var (gw, _) = Crear(nuget, o => o.ConsultarDescargas = false);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(0, Busquedas(nuget));
        Assert.Null(r.Valor!.Descargas);
    }

    [Fact]
    public async Task Si_el_paquete_no_existe_no_se_consultan_descargas_inutilmente_pero_la_busqueda_en_paralelo_no_altera_el_resultado()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Estado(Registro.UrlIndice(), HttpStatusCode.NotFound)
            .Prefijo(NuGetSimulado.UrlBusqueda, (_, _, _) => Task.FromResult(NuGetSimulado.Respuesta(HttpStatusCode.OK, Busqueda())));
        var (gw, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(EstadoConsulta.NoEncontrado, r.Estado);
    }

    [Fact]
    public async Task ObtenerDescargas_directo_devuelve_el_total_o_null()
    {
        var nuget = ConBusqueda(Busqueda(Item("Ejemplo.Paquete", 42)));
        var (gw, _) = Crear(nuget);

        Assert.Equal(42L, (await gw.ObtenerDescargasAsync("Ejemplo.Paquete")).Valor);
        Assert.Null((await gw.ObtenerDescargasAsync("Inexistente.Paquete")).Valor);
        Assert.Equal(EstadoConsulta.NoEncontrado, (await gw.ObtenerDescargasAsync("../malo")).Estado);
    }

    // ---------- búsqueda de candidatos ----------

    [Fact]
    public async Task Los_candidatos_se_ordenan_por_verificados_y_luego_por_descargas_y_excluyen_el_propio_paquete()
    {
        var nuget = ConBusqueda(Busqueda(
            Item("Candidato.Popular", 5000, false), Item("Ejemplo.Paquete", 99999, true), Item("Candidato.Verificado", 10, true),
            Item("Candidato.Mediano", 700, false), Item("Candidato.SinDatos", null, null)));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("ejemplo", "Ejemplo.Paquete", 5);

        Assert.Equal(["Candidato.Verificado", "Candidato.Popular", "Candidato.Mediano", "Candidato.SinDatos"], r.Valor!.Select(c => c.Id));
        var ultimo = r.Valor![^1];
        Assert.Null(ultimo.Descargas); // sin dato: null, no 0
        Assert.Null(ultimo.Verificado);
    }

    [Fact]
    public async Task Respeta_el_maximo_pedido_y_pide_margen_para_descartar_el_propio_paquete()
    {
        var items = Enumerable.Range(0, 12).Select(i => Item($"Candidato.N{i:00}", 100 + i)).ToArray();
        var nuget = ConBusqueda(Busqueda(items));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("texto", null, 3);

        Assert.Equal(3, r.Valor!.Count);
        Assert.Contains(nuget.Solicitudes, u => u.StartsWith(NuGetSimulado.UrlBusqueda) && u.Contains("take=8"));
    }

    [Fact]
    public async Task Un_identificador_inesperado_en_los_resultados_nunca_se_usa()
    {
        var nuget = ConBusqueda(Busqueda(Item("Valido.Uno"), Item("../malo"), Item("otro/malo"), Item("Valido.Dos")));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("texto", null, 5);

        Assert.Equal(["Valido.Dos", "Valido.Uno"], r.Valor!.Select(c => c.Id).Order());
    }

    [Fact]
    public async Task Una_descripcion_larga_se_acota_y_se_trata_como_dato()
    {
        var nuget = ConBusqueda(Busqueda(Item("Candidato.Uno", descripcion: new string('d', 2000) + "<script>alert(1)</script>")));
        var (gw, _) = Crear(nuget);

        var c = (await gw.BuscarCandidatosAsync("texto", null, 5)).Valor!.Single();

        Assert.True(c.Descripcion!.Length <= 301);
        Assert.DoesNotContain("<script>", c.Descripcion);
    }

    [Fact]
    public async Task La_version_del_candidato_se_normaliza_y_una_ilegible_queda_nula()
    {
        var nuget = ConBusqueda(Busqueda(Item("Candidato.Uno", version: "2.1"), Item("Candidato.Dos", version: "no-es-version")));
        var (gw, _) = Crear(nuget);

        var r = (await gw.BuscarCandidatosAsync("texto", null, 5)).Valor!;

        Assert.Equal("2.1.0", r.Single(c => c.Id == "Candidato.Uno").UltimaVersion);
        Assert.Null(r.Single(c => c.Id == "Candidato.Dos").UltimaVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tiene\ncontrol")]
    public async Task Un_texto_de_busqueda_invalido_no_se_envia_y_devuelve_vacio_con_advertencia(string texto)
    {
        var nuget = ConBusqueda(Busqueda());
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync(texto, null, 5);

        Assert.Empty(r.Valor!);
        Assert.Contains(r.Advertencias, a => a.Codigo == "consulta_no_valida");
        Assert.Equal(0, nuget.Total);
    }

    [Fact]
    public async Task Un_texto_de_mas_de_100_caracteres_no_se_envia()
    {
        var nuget = ConBusqueda(Busqueda());
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync(new string('x', 101), null, 5);

        Assert.Empty(r.Valor!);
        Assert.Equal(0, nuget.Total);
    }

    [Fact]
    public async Task El_texto_se_codifica_en_la_direccion_y_no_puede_inyectar_parametros()
    {
        var nuget = ConBusqueda(Busqueda());
        var (gw, _) = Crear(nuget);

        await gw.BuscarCandidatosAsync("json&take=1000&x=y #frag", null, 5);

        var url = nuget.Solicitudes.Single(u => u.StartsWith(NuGetSimulado.UrlBusqueda));
        Assert.Contains("q=json%26take%3D1000%26x%3Dy%20%23frag", url);
        Assert.Contains("take=10", url);
        Assert.DoesNotContain("take=1000", url);
    }

    [Fact]
    public async Task Si_la_busqueda_falla_el_resultado_es_NoDisponible_y_no_inventa_candidatos()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Prefijo(NuGetSimulado.UrlBusqueda, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        var (gw, tiempo) = Crear(nuget);

        var r = await NuGetSimulado.ConTiempo(gw.BuscarCandidatosAsync("texto", null, 5), tiempo);

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(CausaFallo.ErrorServidor, r.Causa);
    }

    [Fact]
    public async Task Una_busqueda_con_json_invalido_es_RespuestaInvalida_sin_reintentos()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Prefijo(NuGetSimulado.UrlBusqueda, (_, _, _) => Task.FromResult(NuGetSimulado.Respuesta(HttpStatusCode.OK, "<html>no json</html>")));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("texto", null, 5);

        Assert.Equal(CausaFallo.RespuestaInvalida, r.Causa);
        Assert.Equal(1, Busquedas(nuget));
    }

    [Fact]
    public async Task Sin_servicio_de_busqueda_en_el_indice_el_resultado_es_NoDisponible()
    {
        var servicios = """{ "version": "3.0.0", "resources": [ { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" } ] }""";
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios);
        var (gw, _) = Crear(nuget);

        Assert.Equal(EstadoConsulta.NoDisponible, (await gw.BuscarCandidatosAsync("texto", null, 5)).Estado);
    }

    [Fact]
    public async Task La_direccion_de_busqueda_fuera_de_nuget_org_se_rechaza()
    {
        var servicios = """{ "version": "3.0.0", "resources": [ { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" }, { "@id": "https://evil.com/query", "@type": "SearchQueryService/3.5.0" } ] }""";
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios).Prefijo("https://evil.com", (_, _, _) => Task.FromResult(NuGetSimulado.Respuesta(HttpStatusCode.OK, Busqueda(Item("Malo.Uno")))));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("texto", null, 5);

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(0, nuget.Contar(u => u.Contains("evil.com")));
    }

    [Fact]
    public async Task Un_maximo_menor_a_1_devuelve_vacio_sin_consultar()
    {
        var nuget = ConBusqueda(Busqueda(Item("Candidato.Uno")));
        var (gw, _) = Crear(nuget);

        var r = await gw.BuscarCandidatosAsync("texto", null, 0);

        Assert.Empty(r.Valor!);
        Assert.Equal(0, nuget.Total);
    }
}
