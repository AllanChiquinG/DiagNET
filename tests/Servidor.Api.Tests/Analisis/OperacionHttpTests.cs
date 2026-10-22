using System.Net;
using System.Net.Http.Json;
using Compartido.Contratos;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Servidor.Api.Configuracion;
using Servidor.Api.Endpoints;
using Servidor.Api.Operacion;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Operacion;
using static Servidor.Api.Tests.Analisis.Ayuda;
using Dominio = Servidor.Negocio.Dominio;

namespace Servidor.Api.Tests.Analisis;

/// <summary>Operación (E11) contra la aplicación REAL con PostgreSQL real en base temporal: disponibilidad, comandos de mantenimiento, HTTPS y purga desactivada por defecto.</summary>
public sealed class OperacionHttpTests(BaseDeDatosApi bd) : IClassFixture<BaseDeDatosApi>
{
    private static string CadenaRota(string cadena) =>
        new Npgsql.NpgsqlConnectionStringBuilder(cadena) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 3, Pooling = false }.ConnectionString;

    // ---------------------------------------------------------------- disponibilidad (readiness)

    [Fact]
    public async Task La_disponibilidad_responde_200_con_la_base_al_dia_sin_consultar_NuGet_y_con_las_cabeceras_de_E8()
    {
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, nuget);
        using var http = fabrica.CreateClient();

        var r = await http.GetAsync("/api/salud/disponibilidad");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var cuerpo = (await r.Content.ReadFromJsonAsync<DisponibilidadDto>())!;
        Assert.Equal(("ok", "DiagNET"), (cuerpo.Estado, cuerpo.Servicio));
        Assert.Equal(new BaseDatosDto(true, true, 0), cuerpo.BaseDatos);
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Empty(nuget.Solicitudes);
    }

    [Fact]
    public async Task Con_la_base_caida_la_disponibilidad_responde_503_sin_secretos_y_la_vitalidad_sigue_en_200()
    {
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(CadenaRota(bd.CadenaConexion), nuget);
        using var http = fabrica.CreateClient();

        var lista = await http.GetAsync("/api/salud/disponibilidad");
        var viva = await http.GetAsync("/api/salud");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, lista.StatusCode);
        var texto = await lista.Content.ReadAsStringAsync();
        var cuerpo = System.Text.Json.JsonSerializer.Deserialize<DisponibilidadDto>(texto, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(("no_disponible", false), (cuerpo.Estado, cuerpo.BaseDatos.Disponible));
        var cadena = new Npgsql.NpgsqlConnectionStringBuilder(bd.CadenaConexion);
        Assert.DoesNotContain(cadena.Password!, texto);
        Assert.DoesNotContain("Host=", texto);
        Assert.DoesNotContain("Npgsql", texto);
        Assert.Equal(HttpStatusCode.OK, viva.StatusCode);              // el proceso está vivo aunque la base no responda: son dos comprobaciones distintas
        Assert.Empty(nuget.Solicitudes);
    }

    [Fact]
    public async Task Sin_cadena_de_conexion_configurada_la_aplicacion_arranca_y_la_disponibilidad_lo_dice_con_503()
    {
        using var fabrica = new SinBaseFabrica();
        using var http = fabrica.CreateClient();

        var r = await http.GetAsync("/api/salud/disponibilidad");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.False((await r.Content.ReadFromJsonAsync<DisponibilidadDto>())!.BaseDatos.Disponible);
    }

    private sealed class SinBaseFabrica : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DiagNet", "");
            builder.UseSetting("NuGet:MaxReintentos", "0");
        }
    }

    // ---------------------------------------------------------------- HTTPS

    [Theory]
    [InlineData("Production", null, true)]
    [InlineData("Production", "false", false)]
    [InlineData("Development", null, false)]
    [InlineData("Development", "true", true)]
    public async Task HTTPS_se_exige_por_defecto_fuera_de_Desarrollo_y_se_puede_decidir_de_forma_explicita(string entorno, string? exigir, bool redirige)
    {
        var ajustes = new Dictionary<string, string?> { ["environment"] = entorno, ["https_port"] = "443" };   // sin puerto HTTPS conocido el middleware no puede redirigir
        if (exigir is not null) ajustes["Seguridad:ExigirHttps"] = exigir;
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado(), ajustes);
        using var http = fabrica.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var r = await http.GetAsync("/api/salud");

        if (redirige)
        {
            Assert.Equal(HttpStatusCode.TemporaryRedirect, r.StatusCode);
            Assert.StartsWith("https://", r.Headers.Location?.ToString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        }
    }

    [Theory]
    [InlineData(null, true, true)]        // Desarrollo sin configurar: sin HTTPS
    [InlineData(null, false, false)]      // Producción sin configurar: HTTPS exigido
    [InlineData(false, false, true)]      // Producción con HTTPS desactivado de forma explícita (demo local en http://localhost)
    [InlineData(true, true, false)]       // Desarrollo con HTTPS exigido de forma explícita
    public void ExigirHttpsEfectivo_respeta_la_decision_explicita(bool? configurado, bool esDesarrollo, bool esperadoSinHttps)
    {
        var o = new OpcionesSeguridad { ExigirHttps = configurado };

        Assert.Equal(!esperadoSinHttps, o.ExigirHttpsEfectivo(esDesarrollo));
    }

    /// <summary>Hace que el servidor de pruebas vea la solicitud como llegada desde <paramref name="ip"/> (TestServer no tiene dirección remota).</summary>
    private sealed class DesdeIp(IPAddress ip) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> siguiente) => app =>
        {
            app.Use((contexto, continuar) =>
            {
                contexto.Connection.RemoteIpAddress = ip;
                return continuar();
            });
            siguiente(app);
        };
    }

    [Theory]
    [InlineData("172.28.0.10", false)]    // el proxy está en la red configurada: se acepta «X-Forwarded-Proto: https», no hay redirección
    [InlineData("203.0.113.9", true)]     // una dirección cualquiera NO puede afirmar que viene de HTTPS: se ignora la cabecera y se redirige
    public async Task El_esquema_reenviado_solo_se_acepta_de_la_red_del_proxy_configurada(string origen, bool redirige)
    {
        var ajustes = new Dictionary<string, string?>
        {
            ["environment"] = "Production", ["https_port"] = "443", ["Seguridad:ExigirHttps"] = "true",
            ["Seguridad:ReenviarCabeceras"] = "true", ["Seguridad:RedesConfiables:0"] = "172.28.0.0/24",
        };
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado(), ajustes,
            personalizar: s => s.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new DesdeIp(IPAddress.Parse(origen))));
        using var http = fabrica.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var solicitud = new HttpRequestMessage(HttpMethod.Get, "/api/salud");
        solicitud.Headers.Add("X-Forwarded-Proto", "https");

        var r = await http.SendAsync(solicitud);

        Assert.Equal(redirige ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public void Las_cabeceras_reenviadas_solo_confian_en_las_redes_configuradas_y_nunca_en_todo_Internet()
    {
        var o = new OpcionesSeguridad { ReenviarCabeceras = true, RedesConfiables = ["172.16.0.0/12", "10.1.2.0/24"] };

        var opciones = o.CrearCabecerasReenviadas();

        Assert.Contains("172.16.0.0/12", opciones.KnownIPNetworks.Select(n => n.ToString()));
        Assert.Contains("10.1.2.0/24", opciones.KnownIPNetworks.Select(n => n.ToString()));
        Assert.DoesNotContain(opciones.KnownIPNetworks, n => n.PrefixLength == 0);
        Assert.Equal(1, opciones.ForwardLimit);
        Assert.Throws<InvalidOperationException>(() => new OpcionesSeguridad { RedesConfiables = ["0.0.0.0/0"] }.CrearCabecerasReenviadas());
        Assert.Throws<FormatException>(() => new OpcionesSeguridad { RedesConfiables = ["no-es-una-red"] }.CrearCabecerasReenviadas());
    }

    // ---------------------------------------------------------------- purga desactivada y plazos

    [Fact]
    public void La_purga_esta_desactivada_por_defecto_con_los_plazos_propuestos_y_la_retencion_de_analisis_es_la_que_ve_el_usuario()
    {
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado());
        var purga = fabrica.Services.GetRequiredService<OpcionesPurga>();
        var consultas = fabrica.Services.GetRequiredService<OpcionesConsultas>();

        Assert.False(purga.Habilitada);
        Assert.Equal((90, 30, 24), (purga.RetencionAnalisisDias, purga.RetencionCacheDias, purga.IntervaloHoras));
        Assert.Equal(consultas.RetencionDias, purga.RetencionAnalisisDias);
    }

    [Fact]
    public void Cambiar_la_retencion_del_historial_cambia_la_purga_una_sola_fuente_de_verdad()
    {
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado(), new Dictionary<string, string?> { ["Historial:RetencionDias"] = "45", ["Purga:RetencionAnalisisDias"] = "999" });

        Assert.Equal(45, fabrica.Services.GetRequiredService<OpcionesPurga>().RetencionAnalisisDias);
        Assert.Equal(45, fabrica.Services.GetRequiredService<OpcionesConsultas>().RetencionDias);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    public async Task La_configuracion_publica_dice_si_la_eliminacion_automatica_esta_activada_y_el_PDF_lo_refleja(string? habilitada, bool esperado)
    {
        var ajustes = new Dictionary<string, string?>();
        if (habilitada is not null) ajustes["Purga:Habilitada"] = habilitada;
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado(), ajustes);
        using var http = fabrica.CreateClient();
        var creado = await http.PostAsync("/api/analisis", Formulario(Csproj("net8.0"), "Retencion.csproj"));
        var id = (await creado.Content.ReadFromJsonAsync<ResultadoAnalisisDto>())!.Id;

        var config = (await (await http.GetAsync("/api/configuracion")).Content.ReadFromJsonAsync<ConfiguracionPublicaDto>())!;
        var pdf = await (await http.GetAsync($"/api/analisis/{id}/pdf")).Content.ReadAsByteArrayAsync();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var texto = string.Join(" ", doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text))).Replace("\u200B", "");

        Assert.Equal(esperado, config.PurgaAutomaticaActiva);
        if (esperado) Assert.Contains("se conserva 90 días y luego se elimina automáticamente", texto);
        else
        {
            Assert.Contains("Plazo de conservación previsto: 90 días", texto);
            Assert.Contains("eliminación automática todavía no está activada", texto);
            Assert.DoesNotContain("luego se elimina automáticamente", texto);
        }
    }

    // ---------------------------------------------------------------- comandos de mantenimiento

    private static async Task<(int Codigo, string Salida, string Error)> Ejecutar(IServiceProvider sp, params string[] args)
    {
        var salida = new StringWriter();
        var error = new StringWriter();
        var codigo = await Comandos.EjecutarAsync(sp, ["operacion", .. args], salida, error);
        return (codigo, salida.ToString(), error.ToString());
    }

    [Fact]
    public async Task Los_comandos_estado_y_migrar_informan_sin_cambiar_nada_en_una_base_al_dia()
    {
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado());
        var filas = await bd.ContarAsync("analisis");

        var estado = await Ejecutar(fabrica.Services, "estado");
        var migrar = await Ejecutar(fabrica.Services, "migrar");

        Assert.Equal(0, estado.Codigo);
        Assert.Contains("todas las migraciones aplicadas", estado.Salida);
        Assert.Equal(0, migrar.Codigo);
        Assert.Contains("No había migraciones pendientes", migrar.Salida);
        Assert.Equal(filas, await bd.ContarAsync("analisis"));
    }

    [Fact]
    public async Task Purgar_sin_la_bandera_solo_simula_y_con_ella_elimina_unicamente_lo_anterior_al_plazo()
    {
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, new NuGetSimulado());
        var repo = fabrica.Services.GetRequiredService<IAnalisisRepositorio>();
        var viejo = AnalisisDe(DateTimeOffset.UtcNow.AddDays(-200));
        var reciente = AnalisisDe(DateTimeOffset.UtcNow.AddDays(-5));
        await repo.GuardarAsync(viejo);
        await repo.GuardarAsync(reciente);

        var simulacion = await Ejecutar(fabrica.Services, "purgar");

        Assert.Equal(0, simulacion.Codigo);
        Assert.Contains("SIMULACIÓN: no se eliminó nada", simulacion.Salida);
        Assert.NotNull(await repo.ObtenerAsync(viejo.Id));                  // la simulación no borró nada
        Assert.NotNull(await repo.ObtenerAsync(reciente.Id));

        var real = await Ejecutar(fabrica.Services, "purgar", "--ejecutar");

        Assert.Equal(0, real.Codigo);
        Assert.Contains("ELIMINADO:", real.Salida);
        Assert.Null(await repo.ObtenerAsync(viejo.Id));
        Assert.NotNull(await repo.ObtenerAsync(reciente.Id));               // lo reciente se conserva
        Assert.DoesNotContain(viejo.Id.ToString(), simulacion.Salida + real.Salida);   // nunca imprime identificadores
    }

    [Fact]
    public async Task Un_comando_desconocido_da_uso_y_un_fallo_no_filtra_la_cadena_de_conexion()
    {
        using var fabrica = new FabricaAnalisis(CadenaRota(bd.CadenaConexion), new NuGetSimulado());
        var cadena = new Npgsql.NpgsqlConnectionStringBuilder(bd.CadenaConexion);

        var desconocido = await Ejecutar(fabrica.Services, "borrar-todo");
        var caido = await Ejecutar(fabrica.Services, "estado");

        Assert.Equal(64, desconocido.Codigo);
        Assert.Contains("Uso:", desconocido.Error);
        Assert.Equal(1, caido.Codigo);
        Assert.DoesNotContain(cadena.Password!, caido.Error + caido.Salida);
    }

    private static Dominio.Analisis AnalisisDe(DateTimeOffset fecha) =>
        new(Guid.NewGuid(), "Operacion.csproj", ["net8.0"], ["net8.0"], fecha, Dominio.EstadoAnalisis.Completo, [], [new Advertencia("sin_paquetes", "Sin paquetes.")], "e6-1.1|ant=24m|agr=pes+e7-1.0");

    // ---------------------------------------------------------------- publicación sin secretos

    [Fact]
    public void La_publicacion_excluye_appsettings_Local_json_para_que_los_secretos_nunca_viajen_en_el_despliegue()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagNET.slnx"))) dir = dir.Parent;
        var csproj = File.ReadAllText(Path.Combine(dir!.FullName, "src", "Servidor.Api", "Servidor.Api.csproj"));

        Assert.Contains("<Content Remove=\"appsettings.Local.json\" />", csproj);
        Assert.Contains("CopyToPublishDirectory=\"Never\"", csproj);
        Assert.Contains("appsettings.Local.json", File.ReadAllText(Path.Combine(dir.FullName, ".gitignore")));
    }
}

/// <summary>La purga programada no hace NADA mientras esté desactivada, y cuando se activa corre en su intervalo y sobrevive a los fallos.</summary>
public class PurgaProgramadaTests
{
    private sealed class Contador : IContadorPurga
    {
        public int Consultas { get; private set; }
        public Task<int> ContarAnalisisAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Consultas++; return Task.FromResult(0); }
        public Task<int> ContarMetadatosAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> ContarSeleccionesAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class Repos(Func<int> alEliminar) : IAnalisisRepositorio, IMetadatoRepositorio
    {
        public int Eliminaciones { get; private set; }
        public Task GuardarAsync(Dominio.Analisis a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dominio.Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Pagina<ResumenAnalisis>> ListarAsync(int p, int t, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) { Eliminaciones++; return Task.FromResult(alEliminar()); }
        public Task<MetadatoLibreria?> BuscarAsync(string n, string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GuardarAsync(MetadatoLibreria m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgarAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) => Task.FromResult(0);
        public Task<SeleccionPaquete?> BuscarSeleccionAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GuardarSeleccionAsync(SeleccionPaquete s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) => Task.FromResult(0);
    }

    private static (PurgaProgramada Servicio, Repos Repos, Contador Contador, FakeTimeProvider Reloj) Crear(OpcionesPurga opciones, Func<int>? alEliminar = null)
    {
        var repos = new Repos(alEliminar ?? (() => 0));
        var contador = new Contador();
        var reloj = new FakeTimeProvider(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        var servicio = new ServicioPurga(repos, repos, contador, reloj, opciones);
        return (new PurgaProgramada(servicio, opciones, reloj, Microsoft.Extensions.Logging.Abstractions.NullLogger<PurgaProgramada>.Instance), repos, contador, reloj);
    }

    [Fact]
    public async Task Desactivada_no_consulta_ni_elimina_nada_aunque_pase_el_tiempo()
    {
        var (servicio, repos, contador, reloj) = Crear(new OpcionesPurga { Habilitada = false });

        await servicio.StartAsync(CancellationToken.None);
        reloj.Advance(TimeSpan.FromDays(10));
        await servicio.StopAsync(CancellationToken.None);

        Assert.Equal((0, 0), (repos.Eliminaciones, contador.Consultas));
    }

    [Fact]
    public async Task Activada_corre_al_iniciar_y_en_cada_intervalo_y_un_fallo_no_la_detiene()
    {
        var llamadas = 0;
        var (servicio, repos, _, reloj) = Crear(new OpcionesPurga { Habilitada = true, IntervaloHoras = 24 }, () => ++llamadas == 1 ? throw new TimeoutException("base lenta") : 0);

        await servicio.StartAsync(CancellationToken.None);
        await EsperarAsync(() => repos.Eliminaciones == 1);              // primera pasada (falla)
        reloj.Advance(TimeSpan.FromHours(24));
        await EsperarAsync(() => repos.Eliminaciones == 2);              // la siguiente se ejecuta igual
        await servicio.StopAsync(CancellationToken.None);

        Assert.Equal(2, repos.Eliminaciones);
    }

    [Fact]
    public async Task Con_plazos_invalidos_no_arranca_y_no_toca_la_base()
    {
        var (servicio, repos, contador, _) = Crear(new OpcionesPurga { Habilitada = true, RetencionCacheDias = 0 });

        await servicio.StartAsync(CancellationToken.None);
        await servicio.StopAsync(CancellationToken.None);

        Assert.Equal((0, 0), (repos.Eliminaciones, contador.Consultas));
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        var fin = DateTime.UtcNow.AddSeconds(10);
        while (!condicion() && DateTime.UtcNow < fin) await Task.Delay(20);
        Assert.True(condicion(), "No ocurrió lo esperado a tiempo.");
    }
}
