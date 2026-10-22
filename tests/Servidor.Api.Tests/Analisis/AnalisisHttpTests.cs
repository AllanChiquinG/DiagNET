using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Compartido.Contratos;
using Microsoft.Extensions.DependencyInjection;
using Servidor.Datos;
using Dominio = Servidor.Negocio.Dominio;
using Servidor.Negocio.Orquestacion;
using static Servidor.Api.Tests.Analisis.Ayuda;

namespace Servidor.Api.Tests.Analisis;

/// <summary>
/// Flujo HTTP COMPLETO de E8 contra la aplicación real: Program + gateway de NuGet real (HTTP simulado) + motores reales + repositorios sobre PostgreSQL REAL (base temporal).
/// </summary>
public sealed class AnalisisHttpTests : IClassFixture<BaseDeDatosApi>, IDisposable
{
    private readonly BaseDeDatosApi _bd;
    private readonly NuGetSimulado _nuget = new();
    private readonly FabricaAnalisis _fabrica;
    private readonly HttpClient _http;

    public AnalisisHttpTests(BaseDeDatosApi bd)
    {
        _bd = bd;
        _fabrica = new FabricaAnalisis(bd.CadenaConexion, _nuget);
        _http = _fabrica.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
    }

    private Task<HttpResponseMessage> Analizar(string contenido, string nombre = "Mi.csproj") => _http.PostAsync("/api/analisis", Formulario(contenido, nombre));

    private async Task<ResultadoAnalisisDto> AnalizarOk(string contenido)
    {
        var r = await Analizar(contenido);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ResultadoAnalisisDto>())!;
    }

    private static async Task<JsonElement> Cuerpo(HttpResponseMessage r) => Json(await r.Content.ReadAsStringAsync());

    // ---------------------------------------------------------------- POST /api/analisis

    [Fact]
    public async Task Post_de_un_archivo_valido_devuelve_201_con_Location_y_el_resultado_persistido()
    {
        var antes = await _bd.ContarAsync("analisis");

        var respuesta = await Analizar(Csproj("net8.0", (Unico("Ejemplo.A"), "2.0.0"), (Unico("Ejemplo.B"), "1.0.0")));

        Assert.Equal(HttpStatusCode.Created, respuesta.StatusCode);
        Assert.Equal("application/json", respuesta.Content.Headers.ContentType?.MediaType);
        var dto = (await respuesta.Content.ReadFromJsonAsync<ResultadoAnalisisDto>())!;
        Assert.Equal($"/api/analisis/{dto.Id}", respuesta.Headers.Location?.OriginalString);
        Assert.Equal(EstadoAnalisis.Completo, dto.Estado);
        Assert.Equal(["net8.0"], dto.FrameworksEvaluados);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada], dto.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.Equal(new ResumenEstadosDto(2, 1, 1, 0, 0, 0), dto.Resumen);
        Assert.Equal("e6-1.1|ant=24m|agr=pes+e7-1.0", dto.VersionReglas);
        Assert.Equal(90, dto.RetencionDias);
        Assert.Equal(antes + 1, await _bd.ContarAsync("analisis"));        // quedó persistido (una sola vez)
    }

    [Fact]
    public async Task El_resultado_incluye_recomendaciones_con_etiquetas_evidencia_y_limitaciones_de_E7()
    {
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.C"), "1.0.0"), ("Ejemplo.Obsoleto", "1.0.0")));

        var actualizacion = Assert.Single(dto.Dependencias[0].Recomendaciones);
        Assert.Equal(TipoRecomendacion.ActualizarVersion, actualizacion.Tipo);
        Assert.Equal("Actualización del mismo paquete", actualizacion.Etiqueta);
        Assert.True(actualizacion.PosiblesCambiosDeCodigo);                                          // 1.0.0 → 2.0.0 es un cambio de versión mayor
        Assert.Equal("Puede requerir cambios de código.", actualizacion.TextoCambiosDeCodigo);
        Assert.NotEmpty(actualizacion.Evidencia);

        var obsoleta = dto.Dependencias[1];
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, obsoleta.Diagnostico.Estado);
        var alternativa = Assert.Single(obsoleta.Recomendaciones);
        Assert.Equal((TipoRecomendacion.Alternativa, "Ejemplo.Nueva"), (alternativa.Tipo, alternativa.Paquete));
        Assert.Equal("Alternativa sugerida por el autor", alternativa.Etiqueta);
        Assert.Equal("Puede requerir cambios de código.", alternativa.TextoCambiosDeCodigo);
        Assert.Contains(alternativa.Limitaciones, l => l.Contains("no demuestra equivalencia funcional"));
        Assert.Contains(alternativa.Evidencia, e => e.Clave == "compatibilidad_inferida");
        Assert.Contains(obsoleta.Diagnostico.Hallazgos, h => h.Codigo == "paquete_obsoleto");
        Assert.Equal(Disponibilidad.NoEvaluado, obsoleta.Diagnostico.Indicadores!.Aot.Disponibilidad);
    }

    [Fact]
    public async Task Un_resultado_parcial_no_es_un_error_es_201_con_estado_parcial_y_advertencias()
    {
        var bueno = Unico("Ejemplo.D");

        var dto = await AnalizarOk(Csproj("net8.0", (bueno, "2.0.0"), ("Ejemplo.NoExiste", "1.0.0")));

        Assert.Equal(EstadoAnalisis.Parcial, dto.Estado);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.NoDeterminada], dto.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.Contains(dto.Advertencias, a => a.Codigo == "analisis_parcial");
        Assert.Null(dto.Dependencias[1].Diagnostico.Indicadores);
        Assert.Null(dto.Dependencias[1].Diagnostico.FechaConsulta);          // no se inventa una fecha
        Assert.Equal(1, dto.Resumen.NoDeterminadas);
    }

    [Fact]
    public async Task Un_proyecto_sin_paquetes_es_201_completo_con_cero_dependencias()
    {
        var dto = await AnalizarOk(Csproj("net8.0"));

        Assert.Equal(EstadoAnalisis.Completo, dto.Estado);
        Assert.Empty(dto.Dependencias);
        Assert.Contains(dto.Advertencias, a => a.Codigo == "sin_paquetes");
    }

    [Fact]
    public async Task Con_NuGet_caido_el_post_responde_201_parcial_y_no_un_error()
    {
        _nuget.Caido = true;

        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.E"), "1.0.0")));

        Assert.Equal(EstadoAnalisis.Parcial, dto.Estado);
        Assert.Equal(EstadoDiagnostico.NoDeterminada, Assert.Single(dto.Dependencias).Diagnostico.Estado);
    }

    // ---------------------------------------------------------------- GET /api/analisis/{id}

    [Fact]
    public async Task Get_del_detalle_devuelve_el_mismo_snapshot_guardado_sin_consultar_NuGet()
    {
        var creado = await Analizar(Csproj("net8.0", (Unico("Ejemplo.F"), "1.0.0"), ("Ejemplo.Obsoleto", "1.0.0"), ("Ejemplo.NoExiste", "1.0.0")));
        var textoPost = await creado.Content.ReadAsStringAsync();
        var solicitudes = _nuget.Solicitudes.Count;

        var respuesta = await _http.GetAsync(creado.Headers.Location);
        var textoGet = await respuesta.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(solicitudes, _nuget.Solicitudes.Count);                  // reabrir NO hace ninguna llamada a NuGet
        Assert.Equal(Json(textoPost).ToString(), Json(textoGet).ToString());   // snapshot idéntico: diagnósticos, recomendaciones, advertencias y limitaciones
        var dto = JsonSerializer.Deserialize<ResultadoAnalisisDto>(textoGet, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Contains(dto.Advertencias, a => a.Codigo == "analisis_parcial");
        Assert.Contains(dto.Dependencias.SelectMany(d => d.Recomendaciones).SelectMany(r => r.Limitaciones), l => l.Contains("se infiere de los grupos de dependencias"));
    }

    [Fact]
    public async Task Tras_reiniciar_la_aplicacion_el_analisis_se_recupera_igual_y_la_nueva_instancia_no_llama_a_NuGet()
    {
        var creado = await Analizar(Csproj("net8.0", (Unico("Ejemplo.G"), "1.0.0")));
        var textoPost = await creado.Content.ReadAsStringAsync();

        var otroNuGet = new NuGetSimulado();
        using var reiniciada = new FabricaAnalisis(_bd.CadenaConexion, otroNuGet);
        using var cliente = reiniciada.CreateClient();
        var respuesta = await cliente.GetAsync(creado.Headers.Location);

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(Json(textoPost).ToString(), Json(await respuesta.Content.ReadAsStringAsync()).ToString());
        Assert.Empty(otroNuGet.Solicitudes);
    }

    [Fact]
    public async Task Get_de_un_analisis_inexistente_o_purgado_devuelve_404_analisis_no_encontrado()
    {
        var inexistente = await _http.GetAsync($"/api/analisis/{Guid.NewGuid()}");
        await VerificarProblema(inexistente, HttpStatusCode.NotFound, CodigosError.AnalisisNoEncontrado);

        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.H"), "1.0.0")));
        await using (var servicios = new ServiceCollection().AddDatos(_bd.CadenaConexion).BuildServiceProvider())
            await servicios.GetRequiredService<Dominio.IAnalisisRepositorio>().EliminarAnterioresAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        var purgado = await _http.GetAsync($"/api/analisis/{dto.Id}");

        await VerificarProblema(purgado, HttpStatusCode.NotFound, CodigosError.AnalisisNoEncontrado);
    }

    [Theory]
    [InlineData("no-es-un-uuid")]
    [InlineData("123")]
    [InlineData("00000000-0000-0000-0000-00000000000z")]
    public async Task Get_con_un_identificador_que_no_es_uuid_devuelve_400(string id)
    {
        await VerificarProblema(await _http.GetAsync($"/api/analisis/{id}"), HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
    }

    // ---------------------------------------------------------------- entrada inválida

    public static TheoryData<string, string, int, string> EntradasInvalidas => new()
    {
        { "extension", "x", 400, CodigosError.ExtensionInvalida },
        { "vacio", "", 400, CodigosError.ArchivoVacio },
        { "no_xml", "esto no es xml", 400, CodigosError.XmlInvalido },
        { "no_proyecto", "<html><body/></html>", 400, CodigosError.ProyectoInvalido },
        { "fuera_de_alcance", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>", 422, CodigosError.FrameworkFueraDeAlcance },
    };

    [Theory]
    [MemberData(nameof(EntradasInvalidas))]
    public async Task Una_entrada_invalida_devuelve_su_error_y_no_consulta_NuGet_ni_guarda_nada(string caso, string contenido, int estado, string codigo)
    {
        var filas = await _bd.ContarAsync("analisis");
        var solicitudes = _nuget.Solicitudes.Count;

        var respuesta = await Analizar(contenido, caso == "extension" ? "notas.txt" : "Mi.csproj");

        await VerificarProblema(respuesta, (HttpStatusCode)estado, codigo);
        Assert.Equal(solicitudes, _nuget.Solicitudes.Count);
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
    }

    [Fact]
    public async Task Mas_de_100_dependencias_distintas_devuelve_422_sin_consultar_NuGet_ni_guardar()
    {
        var filas = await _bd.ContarAsync("analisis");
        var muchas = Enumerable.Range(0, 101).Select(i => ($"Ejemplo.P{i}", "1.0.0")).ToArray();

        var respuesta = await Analizar(Csproj("net8.0", muchas));

        await VerificarProblema(respuesta, HttpStatusCode.UnprocessableEntity, CodigosError.LimiteDependenciasExcedido);
        Assert.Empty(_nuget.Solicitudes);
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
    }

    [Fact]
    public async Task Un_archivo_de_mas_de_1_MB_devuelve_400_y_un_cuerpo_de_mas_de_2_MB_devuelve_413()
    {
        var grande = Csproj("net8.0") + "<!--" + new string('x', (int)LimitesEntrada.TamanoMaximoArchivoBytes) + "-->";
        var enorme = "<!--" + new string('x', (int)LimitesEntrada.TamanoMaximoCuerpoBytes + 10) + "-->";

        await VerificarProblema(await Analizar(grande), HttpStatusCode.BadRequest, CodigosError.ArchivoExcedeTamano);
        await VerificarProblema(await Analizar(enorme), HttpStatusCode.RequestEntityTooLarge, CodigosError.CuerpoExcedeTamano);
        Assert.Empty(_nuget.Solicitudes);
    }

    [Fact]
    public async Task Sin_el_campo_archivo_o_sin_multipart_devuelve_400_archivo_ausente()
    {
        await VerificarProblema(await _http.PostAsync("/api/analisis", Formulario("<Project/>", campo: "otro")), HttpStatusCode.BadRequest, CodigosError.ArchivoAusente);
        await VerificarProblema(await _http.PostAsync("/api/analisis", new StringContent("{}", Encoding.UTF8, "application/json")), HttpStatusCode.BadRequest, CodigosError.ArchivoAusente);
    }

    // ---------------------------------------------------------------- historial (DEC-06, propuesta)

    [Fact]
    public async Task El_historial_devuelve_solo_los_analisis_pedidos_por_id_ordenados_y_paginados_sin_consultar_NuGet()
    {
        var a = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.I"), "1.0.0")));
        await Task.Delay(20);
        var b = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.J"), "2.0.0")));
        await Task.Delay(20);
        var c = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.K"), "2.0.0")));
        var ajeno = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.L"), "2.0.0")));   // existe pero NO se pide: no debe aparecer
        var solicitudes = _nuget.Solicitudes.Count;

        var todo = (await (await _http.GetAsync($"/api/analisis?ids={a.Id},{b.Id},{c.Id}")).Content.ReadFromJsonAsync<PaginaDto<ResumenAnalisisDto>>())!;
        var pagina2 = (await (await _http.GetAsync($"/api/analisis?ids={a.Id}&ids={b.Id}&ids={c.Id}&pagina=2&tamano=2")).Content.ReadFromJsonAsync<PaginaDto<ResumenAnalisisDto>>())!;

        Assert.Equal(3, todo.Total);
        Assert.Equal([c.Id, b.Id, a.Id], todo.Elementos.Select(e => e.Id));            // más reciente primero
        Assert.DoesNotContain(todo.Elementos, e => e.Id == ajeno.Id);
        Assert.Equal((2, 2, 3), (pagina2.Pagina, pagina2.TamanoPagina, pagina2.Total));
        Assert.Equal([a.Id], pagina2.Elementos.Select(e => e.Id));
        Assert.All(todo.Elementos, e => Assert.Equal(1, e.TotalDependencias));
        Assert.Equal(solicitudes, _nuget.Solicitudes.Count);                            // el historial no consulta NuGet
    }

    [Fact]
    public async Task El_historial_omite_los_ids_desconocidos_sin_revelar_nada_y_acepta_duplicados()
    {
        var a = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.M"), "1.0.0")));

        var pagina = (await (await _http.GetAsync($"/api/analisis?ids={a.Id},{Guid.NewGuid()},{a.Id}")).Content.ReadFromJsonAsync<PaginaDto<ResumenAnalisisDto>>())!;

        Assert.Equal([a.Id], pagina.Elementos.Select(e => e.Id));
        Assert.Equal(1, pagina.Total);
    }

    [Fact]
    public async Task No_existe_un_listado_global_sin_ids_es_un_error()
    {
        await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.N"), "1.0.0")));

        var sinIds = await _http.GetAsync("/api/analisis");
        var vacio = await _http.GetAsync("/api/analisis?ids=");

        await VerificarProblema(sinIds, HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
        await VerificarProblema(vacio, HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
        Assert.DoesNotContain("nombreArchivo", await sinIds.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Un_id_invalido_o_demasiados_ids_devuelven_400()
    {
        var muchos = string.Join(',', Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()));

        await VerificarProblema(await _http.GetAsync($"/api/analisis?ids={Guid.NewGuid()},x"), HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
        await VerificarProblema(await _http.GetAsync($"/api/analisis?ids={muchos}"), HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
    }

    [Theory]
    [InlineData("pagina=0")]
    [InlineData("pagina=-1")]
    [InlineData("pagina=abc")]
    [InlineData("tamano=0")]
    [InlineData("tamano=51")]
    [InlineData("tamano=2.5")]
    public async Task Una_paginacion_invalida_devuelve_400_paginacion_invalida(string consulta)
    {
        await VerificarProblema(await _http.GetAsync($"/api/analisis?ids={Guid.NewGuid()}&{consulta}"), HttpStatusCode.BadRequest, CodigosError.PaginacionInvalida);
    }

    // ---------------------------------------------------------------- rutas, errores y cabeceras

    [Fact]
    public async Task Las_rutas_api_desconocidas_devuelven_404_problem_details_y_nunca_el_html_del_cliente()
    {
        foreach (var (metodo, ruta) in new[] { ("GET", "/api/no-existe"), ("POST", "/api/no-existe"), ("GET", "/api/analisis/x/y/z"), ("DELETE", "/api/analisis/" + Guid.NewGuid()) })
        {
            var respuesta = await _http.SendAsync(new HttpRequestMessage(new HttpMethod(metodo), ruta));

            Assert.Contains(respuesta.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
            Assert.DoesNotContain("text/html", respuesta.Content.Headers.ContentType?.ToString() ?? "");
            Assert.DoesNotContain("<title>DiagNET</title>", await respuesta.Content.ReadAsStringAsync());
        }

        await VerificarProblema(await _http.GetAsync("/api/no-existe"), HttpStatusCode.NotFound, CodigosError.RecursoNoEncontrado);
    }

    [Fact]
    public async Task Las_respuestas_de_la_api_no_se_guardan_en_cache_ni_permiten_adivinar_el_tipo_ni_envian_referer()
    {
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.P"), "1.0.0")));

        foreach (var ruta in new[] { $"/api/analisis/{dto.Id}", $"/api/analisis?ids={dto.Id}", "/api/salud", "/api/no-existe" })
        {
            var r = await _http.GetAsync(ruta);
            Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
            Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        }
    }

    [Fact]
    public async Task El_endpoint_de_lectura_de_E2_sigue_funcionando_sin_consultar_NuGet_ni_guardar()
    {
        var filas = await _bd.ContarAsync("analisis");

        var respuesta = await _http.PostAsync("/api/proyectos/lectura", Formulario(Csproj("net8.0", ("Ejemplo.Lectura", "1.0.0"))));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var dto = (await respuesta.Content.ReadFromJsonAsync<ProyectoLeidoDto>())!;
        Assert.Equal("Ejemplo.Lectura", Assert.Single(dto.Dependencias).Nombre);
        Assert.Empty(_nuget.Solicitudes);
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
    }

    [Fact]
    public async Task La_lectura_y_el_analisis_rechazan_las_mismas_entradas_con_los_mismos_codigos()
    {
        foreach (var contenido in new[] { "", "esto no es xml", "<html/>" })
        {
            var lectura = await _http.PostAsync("/api/proyectos/lectura", Formulario(contenido));
            var analisis = await Analizar(contenido);

            Assert.Equal(lectura.StatusCode, analisis.StatusCode);
            Assert.Equal((await Cuerpo(lectura)).GetProperty("codigo").GetString(), (await Cuerpo(analisis)).GetProperty("codigo").GetString());
        }
    }

    [Fact]
    public async Task La_configuracion_publica_entrega_la_retencion_y_los_limites_reales_sin_secretos()
    {
        var respuesta = await _http.GetAsync("/api/configuracion");

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var dto = (await respuesta.Content.ReadFromJsonAsync<ConfiguracionPublicaDto>())!;
        Assert.Equal(new ConfiguracionPublicaDto(90, 50, LimitesEntrada.TamanoMaximoArchivoBytes, LimitesEntrada.MaximoDependencias, 120), dto);
        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ConnectionString", texto, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- cancelación, límite de simultáneos

    [Fact]
    public async Task Si_el_cliente_cancela_el_analisis_no_se_guarda_nada_y_se_cancela_la_consulta_a_NuGet()
    {
        var filas = await _bd.ContarAsync("analisis");
        using var cancelacion = new CancellationTokenSource();

        var tarea = _http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", ("Ejemplo.Lento", "1.0.0"))), cancelacion.Token);
        await EsperarAsync(() => _nuget.Solicitudes.Any(u => u.Contains("/ejemplo.lento/")));
        await cancelacion.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
        await EsperarAsync(() => _nuget.Cancelaciones > 0);                  // la solicitud HTTP a NuGet se canceló: no queda trabajo activo
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
        Assert.Equal(HttpStatusCode.Created, (await Analizar(Csproj("net8.0", (Unico("Ejemplo.Q"), "1.0.0")))).StatusCode);   // la aplicación sigue sirviendo
    }

    [Fact]
    public async Task Con_el_limite_de_analisis_simultaneos_alcanzado_el_excedente_recibe_429()
    {
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, nuget, new Dictionary<string, string?> { ["Analisis:MaxAnalisisSimultaneos"] = "1" });
        using var cliente = fabrica.CreateClient();
        using var cancelacion = new CancellationTokenSource();

        var primero = cliente.PostAsync("/api/analisis", Formulario(Csproj("net8.0", ("Ejemplo.Lento", "1.0.0"))), cancelacion.Token);
        await EsperarAsync(() => nuget.Solicitudes.Any(u => u.Contains("/ejemplo.lento/")));
        var segundo = await cliente.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (Unico("Ejemplo.R"), "1.0.0"))));

        await VerificarProblema(segundo, HttpStatusCode.TooManyRequests, CodigosError.DemasiadasSolicitudes);
        await cancelacion.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primero);
    }

    // ---------------------------------------------------------------- cableado real

    [Fact]
    public void La_aplicacion_usa_los_motores_y_repositorios_reales_y_ningun_sustituto_de_pruebas()
    {
        var sp = _fabrica.Services;
        var tipos = new object[]
        {
            sp.GetRequiredService<IMotorDiagnostico>(), sp.GetRequiredService<IMotorRecomendacion>(), sp.GetRequiredService<OrquestadorAnalisis>(),
            sp.GetRequiredService<ServicioMetadatos>(), sp.GetRequiredService<Servidor.Negocio.Nuget.IClienteNuGet>(),
            sp.GetRequiredService<Dominio.IAnalisisRepositorio>(), sp.GetRequiredService<Dominio.IMetadatoRepositorio>(),
        };

        Assert.Equal("MotorDiagnostico", tipos[0].GetType().Name);
        Assert.Equal("MotorRecomendacion", tipos[1].GetType().Name);
        Assert.Equal("NuGetGateway", tipos[4].GetType().Name);
        Assert.Equal("AnalisisRepositorio", tipos[5].GetType().Name);
        Assert.All(tipos, t => Assert.DoesNotContain("Test", t.GetType().Assembly.GetName().Name!));
        Assert.All(typeof(Program).Assembly.GetReferencedAssemblies(), a => Assert.DoesNotContain("Tests", a.Name!));
    }

    // ---------------------------------------------------------------- utilidades

    private static async Task VerificarProblema(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        var problema = (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!;
        Assert.Equal(codigo, problema.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
        Assert.Equal((int)estado, problema.Status);
        Assert.False(string.IsNullOrWhiteSpace(problema.Title));
    }

    private static async Task EsperarAsync(Func<bool> condicion)
    {
        for (var i = 0; i < 500 && !condicion(); i++) await Task.Delay(10);
        Assert.True(condicion(), "La condición no se cumplió a tiempo.");
    }
}

/// <summary>Fallos de almacenamiento: la base de datos no responde. Nada queda guardado y los errores son uniformes.</summary>
public sealed class AnalisisAlmacenamientoTests : IClassFixture<BaseDeDatosApi>, IDisposable
{
    private readonly NuGetSimulado _nuget = new();
    private readonly FabricaAnalisis _fabrica;
    private readonly HttpClient _http;
    private readonly BaseDeDatosApi _bd;

    public AnalisisAlmacenamientoTests(BaseDeDatosApi bd)
    {
        _bd = bd;
        var rota = new Npgsql.NpgsqlConnectionStringBuilder(bd.CadenaConexion) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 3 }.ConnectionString;
        _fabrica = new FabricaAnalisis(rota, _nuget);
        _http = _fabrica.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
    }

    [Fact]
    public async Task Post_con_la_base_caida_devuelve_503_almacenamiento_no_disponible_y_no_guarda_nada_en_la_base_real()
    {
        var filas = await _bd.ContarAsync("analisis");

        var respuesta = await _http.PostAsync("/api/analisis", Formulario(Csproj("net8.0", (Unico("Ejemplo.S"), "1.0.0"))));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        var problema = (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!;
        Assert.Equal(CodigosError.AlmacenamientoNoDisponible, problema.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
        Assert.DoesNotContain("Npgsql", await respuesta.Content.ReadAsStringAsync());        // sin detalles internos
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
    }

    [Theory]
    [InlineData("/api/analisis/00000000-0000-4000-8000-000000000001")]
    [InlineData("/api/analisis?ids=00000000-0000-4000-8000-000000000001")]
    public async Task Get_con_la_base_caida_devuelve_503_uniforme_y_no_consulta_NuGet(string ruta)
    {
        var respuesta = await _http.GetAsync(ruta);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, respuesta.StatusCode);
        Assert.Equal(CodigosError.AlmacenamientoNoDisponible, (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!.Codigo);
        Assert.Empty(_nuget.Solicitudes);
    }
}
