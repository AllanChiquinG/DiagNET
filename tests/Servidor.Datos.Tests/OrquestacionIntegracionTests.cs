using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Recomendaciones;
using Servidor.Negocio.Reglas;

namespace Servidor.Datos.Tests;

/// <summary>
/// Integración de E5 con los módulos REALES disponibles: lector (E2), repositorios sobre PostgreSQL real (E3), gateway de NuGet (E4) con respuestas
/// SIMULADAS (sin internet) y los MOTORES REALES de diagnóstico (E6) y de recomendaciones (E7). Solo NuGet (HTTP simulado) es un doble de pruebas.
/// </summary>
public class OrquestacionIntegracionTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
        // ---------------- NuGet simulado ----------------

    private sealed class NuGetHttpFalso : HttpMessageHandler
    {
        private const string Base = "https://api.nuget.org/v3/registration5-gz-semver2/";
        public bool Caido { get; set; }
        public bool BusquedaCaida { get; set; }
        public ConcurrentBag<string> Solicitudes { get; } = [];
        public int Registros => Solicitudes.Count(u => u.StartsWith(Base));

        private static string Hoja(string version, string deprecacion = "") =>
            "{\"catalogEntry\":{\"version\":\"" + version + "\",\"listed\":true,\"published\":\"2025-01-01T00:00:00+00:00\",\"licenseExpression\":\"MIT\",\"dependencyGroups\":[{\"targetFramework\":\"net8.0\"}]"
            + (deprecacion.Length == 0 ? "" : ",\"deprecation\":" + deprecacion) + "}}";

        private const string DeprecadoConReemplazo = "{\"reasons\":[\"Legacy\"],\"message\":\"Usa Ejemplo.Nueva.\",\"alternatePackage\":{\"id\":\"Ejemplo.Nueva\",\"range\":\"[1.0.0, )\"}}";
        private const string DeprecadoSinReemplazo = "{\"reasons\":[\"Legacy\"],\"message\":\"Sin mantenimiento.\"}";

        private static string Indice(params string[] hojas) =>
            "{\"items\":[{\"lower\":\"1.0.0\",\"upper\":\"2.0.0\",\"items\":[" + string.Join(",", hojas) + "]}]}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Solicitudes.Add(url);
            if (url == "https://api.nuget.org/v3/index.json")
                return Json("{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + Base + "\",\"@type\":\"RegistrationsBaseUrl/3.6.0\"},{\"@id\":\"https://azuresearch-usnc.nuget.org/query\",\"@type\":\"SearchQueryService/3.5.0\"}]}");
            if (Caido) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (url.StartsWith("https://azuresearch-usnc.nuget.org/query"))
            {
                if (BusquedaCaida) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                return url.Contains("q=Ejemplo.SinAlterno") || url.Contains("q=ejemplo.sinalterno")
                    ? Json("{\"data\":[{\"id\":\"Ejemplo.Candidata\",\"version\":\"1.0.0\",\"totalDownloads\":7,\"verified\":false,\"description\":\"Otro paquete\"}]}")
                    : Json("{\"data\":[]}");
            }
            if (url.Contains("/ejemplo.lento")) { await Task.Delay(Timeout.Infinite, ct); }
            if (url.EndsWith("/ejemplo.noexiste/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (url.EndsWith("/ejemplo.vieja/index.json")) return Json(Indice(Hoja("1.0.0", DeprecadoConReemplazo)));
            if (url.EndsWith("/ejemplo.sinalterno/index.json")) return Json(Indice(Hoja("1.0.0", DeprecadoSinReemplazo)));
            if (url.StartsWith(Base) && url.EndsWith("/index.json"))
                return Json("{\"items\":[{\"lower\":\"1.0.0\",\"upper\":\"2.0.0\",\"items\":[" + Hoja("1.0.0") + "," + Hoja("2.0.0") + "]}]}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed record Entorno(OrquestadorAnalisis Orquestador, NuGetHttpFalso Http, FakeTimeProvider Tiempo, ServicioMetadatos Metadatos);

    private Entorno Crear(NuGetHttpFalso? http = null, FakeTimeProvider? tiempo = null, IAnalisisRepositorio? repositorio = null, OpcionesNuGet? nuget = null,
        OpcionesOrquestacion? opciones = null)
    {
        http ??= new NuGetHttpFalso();
        tiempo ??= new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var gateway = new NuGetGateway(new HttpClient(http) { Timeout = Timeout.InfiniteTimeSpan }, nuget ?? new OpcionesNuGet { ConsultarDescargas = false }, tiempo);
        var metadatos = new ServicioMetadatos(gateway, bd.Metadatos, new InferidorCompatibilidad(), tiempo);
        var orquestador = new OrquestadorAnalisis(new LectorCsproj(new OpcionesLectura()), metadatos, new MotorDiagnostico(),
            new MotorRecomendacion(metadatos, gateway), repositorio ?? bd.Analisis, tiempo, opciones);
        return new Entorno(orquestador, http, tiempo, metadatos);
    }

    private static string Csproj(params (string Nombre, string Version)[] paquetes) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>"
        + string.Concat(paquetes.Select(p => $"<PackageReference Include=\"{p.Nombre}\" Version=\"{p.Version}\" />")) + "</ItemGroup></Project>";

    private static MemoryStream Flujo(string xml) => new(Encoding.UTF8.GetBytes(xml));

    private static string Unico(string prefijo) => $"{prefijo}{Guid.NewGuid():N}"[..28];

    // ---------------- pruebas ----------------

    [Fact]
    public async Task Un_analisis_de_punta_a_punta_se_guarda_en_PostgreSQL_y_se_recupera_igual_tras_reiniciar()
    {
        var uno = Unico("Ejemplo.A");
        var dos = Unico("Ejemplo.B");
        var e = Crear();

        var r = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((uno, "2.0.0"), (dos, "1.0.0"))));

        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
        await using var reinicio = bd.Reiniciar();
        var leido = await reinicio.GetRequiredService<IAnalisisRepositorio>().ObtenerAsync(r.Analisis.Id);
        Assert.Equal(Muestras.Json(r.Analisis), Muestras.Json(leido));
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada], leido!.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.Equal("e6-1.1|ant=24m|agr=pes+e7-1.0", leido.VersionReglas);                                  // las reglas usadas quedan guardadas con el análisis
        Assert.Contains(leido.Dependencias[1].Diagnostico.Hallazgos, h => h.Codigo == "version_posterior_compatible" && h.Evidencia.Count > 0);
        Assert.Equal(Disponibilidad.NoEvaluado, leido.Dependencias[0].Diagnostico.Indicadores!.Aot.Disponibilidad);
        Assert.Single(leido.Dependencias[1].Recomendaciones);
    }

    [Fact]
    public async Task Una_licencia_de_tipo_desconocido_y_los_indicadores_del_motor_real_se_guardan_y_se_recuperan_sin_cambios()
    {
        var uno = Unico("Ejemplo.L");
        var r = await Crear().Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((uno, "2.0.0"))));
        var original = r.Analisis.Dependencias.Single();
        var conTipoDesconocido = original with
        {
            Diagnostico = original.Diagnostico with
            {
                Indicadores = original.Diagnostico.Indicadores! with
                {
                    Licencia = new Indicador<Licencia?>(Disponibilidad.Conocido, new Licencia(null, "https://ejemplo.org/licencia"))
                }
            }
        };
        var analisis = r.Analisis with { Id = Guid.NewGuid(), Dependencias = [conTipoDesconocido] };

        await bd.Analisis.GuardarAsync(analisis);
        var leido = (await bd.Analisis.ObtenerAsync(analisis.Id))!;

        Assert.Equal(Muestras.Json(analisis), Muestras.Json(leido));
        Assert.Null(leido.Dependencias.Single().Diagnostico.Indicadores!.Licencia.Valor!.Tipo);
    }

    [Fact]
    public async Task Los_analisis_guardados_con_reglas_anteriores_se_leen_intactos_y_la_marca_de_evidencia_faltante_se_guarda_y_se_recupera()
    {
        var r = await Crear().Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((Unico("Ejemplo.M"), "2.0.0"), ("Ejemplo.NoExiste", "1.0.0"))));
        var id = r.Analisis.Id;

        // La marca nueva se guarda y se recupera.
        var leido = (await bd.Analisis.ObtenerAsync(id))!;
        Assert.Contains(leido.Dependencias[1].Diagnostico.Hallazgos, h => h.EvidenciaDecisivaFaltante);
        Assert.Equal(Muestras.Json(r.Analisis), Muestras.Json(leido));

        // Un análisis guardado con la versión anterior de reglas (hallazgos sin el campo nuevo y version_reglas e6-1.0) se lee igual, con la marca en false.
        await bd.EjecutarAsync("UPDATE analisis SET version_reglas = 'e6-1.0|ant=24m|agr=pes' WHERE id = '" + id + "'");
        await bd.EjecutarAsync(
            "UPDATE diagnostico SET hallazgos = (SELECT COALESCE(jsonb_agg(elem - 'evidenciaDecisivaFaltante' - 'EvidenciaDecisivaFaltante'), '[]'::jsonb) FROM jsonb_array_elements(hallazgos) elem)");
        var antiguo = (await bd.Analisis.ObtenerAsync(id))!;

        Assert.Equal("e6-1.0|ant=24m|agr=pes", antiguo.VersionReglas);     // la versión original NO se reinterpreta
        Assert.All(antiguo.Dependencias.SelectMany(d => d.Diagnostico.Hallazgos), h => Assert.False(h.EvidenciaDecisivaFaltante));
        Assert.Equal(leido.Dependencias.Select(d => d.Diagnostico.Estado), antiguo.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.Equal(leido.Dependencias[0].Diagnostico.Motivos, antiguo.Dependencias[0].Diagnostico.Motivos);
    }

    [Fact]
    public async Task Las_recomendaciones_con_su_justificacion_evidencia_y_limitaciones_se_guardan_y_reabrir_el_resultado_no_consulta_NuGet()
    {
        var e = Crear();
        var r = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj(("Ejemplo.Vieja", "1.0.0"), (Unico("Ejemplo.Dos"), "1.0.0"))));

        var vieja = r.Analisis.Dependencias[0];
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, vieja.Diagnostico.Estado);
        var alternativa = Assert.Single(vieja.Recomendaciones);
        Assert.Equal((TipoRecomendacion.Alternativa, "Ejemplo.Nueva"), (alternativa.Tipo, alternativa.Paquete));   // reemplazo declarado por el autor y comprobado
        Assert.Equal(EstadoDiagnostico.Desactualizada, r.Analisis.Dependencias[1].Diagnostico.Estado);
        Assert.Equal(TipoRecomendacion.ActualizarVersion, Assert.Single(r.Analisis.Dependencias[1].Recomendaciones).Tipo);

        var solicitudes = e.Http.Solicitudes.Count;
        await using var reinicio = bd.Reiniciar();
        var leido = (await reinicio.GetRequiredService<IAnalisisRepositorio>().ObtenerAsync(r.Analisis.Id))!;

        Assert.Equal(solicitudes, e.Http.Solicitudes.Count);                       // reabrir no consulta NuGet
        Assert.Equal(Muestras.Json(r.Analisis), Muestras.Json(leido));              // el snapshot es idéntico, recomendaciones incluidas
        var guardada = leido.Dependencias[0].Recomendaciones.Single();
        Assert.Equal(alternativa.Justificacion, guardada.Justificacion);
        Assert.NotEmpty(guardada.Evidencia);
        Assert.Contains(guardada.Evidencia, x => x.Clave == "declarado_por");
        Assert.Contains(guardada.Limitaciones, l => l.Contains("Puede requerir cambios de código"));
        Assert.StartsWith("Alternativa sugerida por el autor", guardada.Justificacion);
        Assert.Contains(guardada.Evidencia, x => x.Clave == "lo_que_no_demuestra_la_declaracion");
        Assert.True(guardada.PosiblesCambiosDeCodigo);
    }

    [Fact]
    public async Task Un_candidato_de_busqueda_se_guarda_solo_para_revision_y_si_la_busqueda_cae_se_conserva_el_diagnostico_con_su_nota()
    {
        var e = Crear();
        var conBusqueda = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj(("Ejemplo.SinAlterno", "1.0.0"))));
        var candidato = Assert.Single(Assert.Single(conBusqueda.Analisis.Dependencias).Recomendaciones);
        Assert.Equal((TipoRecomendacion.CandidatoParaRevision, "Ejemplo.Candidata"), (candidato.Tipo, candidato.Paquete));
        Assert.Contains("NO está demostrada", candidato.Justificacion);

        var e2 = Crear(nuget: new OpcionesNuGet { ConsultarDescargas = false, MaxReintentos = 0 });   // sin reintentos: el reloj de la prueba es simulado
        e2.Http.BusquedaCaida = true;
        var sinBusqueda = await e2.Orquestador.AnalizarAsync("Otro.csproj", Flujo(Csproj(("Ejemplo.SinAlterno", "1.0.0"))));

        var leido = (await bd.Analisis.ObtenerAsync(sinBusqueda.Analisis.Id))!;
        var d = Assert.Single(leido.Dependencias);
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Diagnostico.Estado);      // el diagnóstico no se pierde
        Assert.Empty(d.Recomendaciones);
        Assert.Contains(d.Advertencias, a => a.Codigo == "busqueda_alternativas_no_disponible");
        Assert.Contains(d.Advertencias, a => a.Codigo == "recomendaciones_no_verificadas");
        Assert.DoesNotContain(d.Advertencias, a => a.Codigo == "sin_recomendaciones");  // no se afirma «no hay»: no se pudo comprobar
        Assert.Equal(EstadoAnalisis.Completo, leido.Estado);                          // no es parcial: la búsqueda no es evidencia decisiva
        Assert.Contains(leido.Advertencias, a => a.Codigo == "recomendaciones_incompletas");
    }

    [Fact]
    public async Task Un_analisis_guardado_con_reglas_y_textos_de_recomendacion_anteriores_se_lee_con_su_contenido_original()
    {
        var r = await Crear().Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((Unico("Ejemplo.N"), "1.0.0"))));
        var dep = r.Analisis.Dependencias.Single();
        var antigua = new Recomendacion(TipoRecomendacion.Alternativa, "Ejemplo.Vieja.Alterna", "1.0.0",
            "Reemplazo declarado por NuGet (texto de una versión anterior de las reglas).",
            [new Evidencia("declarado_por", "el autor")], ["No se garantiza que el cambio no requiera modificar el código."], false);
        var analisis = r.Analisis with
        {
            Id = Guid.NewGuid(),
            VersionReglas = "e6-1.1|ant=24m|agr=pes",                       // revisión anterior: sin la parte de recomendaciones
            Dependencias = [dep with { Recomendaciones = [antigua] }],
        };

        await bd.Analisis.GuardarAsync(analisis);
        var leido = (await bd.Analisis.ObtenerAsync(analisis.Id))!;

        Assert.Equal("e6-1.1|ant=24m|agr=pes", leido.VersionReglas);        // no se reescribe con la revisión actual
        var guardada = leido.Dependencias.Single().Recomendaciones.Single();
        Assert.Equal(antigua.Justificacion, guardada.Justificacion);          // el texto anterior no se re-genera ni se reinterpreta
        Assert.Equal(antigua.Limitaciones, guardada.Limitaciones);
        Assert.False(guardada.PosiblesCambiosDeCodigo);
        Assert.Equal(Muestras.Json(analisis), Muestras.Json(leido));
    }

    [Fact]
    public async Task Un_segundo_analisis_de_las_mismas_librerias_no_hace_ninguna_consulta_a_los_registros_de_NuGet()
    {
        var uno = Unico("Ejemplo.C");
        var e = Crear();
        var proyecto = Csproj((uno, "2.0.0"));

        await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(proyecto));
        var registros = e.Http.Registros;
        var segundo = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(proyecto));

        Assert.Equal(1, registros);
        Assert.Equal(registros, e.Http.Registros);
        Assert.Equal(OrigenDato.CacheVigente, segundo.Analisis.Dependencias.Single().Diagnostico.Origen);
        Assert.Equal(0, await bd.EscalarAsync<int>("SELECT count(*) FROM diagnostico WHERE origen = 'cache_vigente' AND fecha_consulta IS NULL"));
        Assert.Equal(1, await bd.EscalarAsync<int>(
            "SELECT count(*) FROM diagnostico g JOIN dependencia d ON d.id = g.dependencia_id WHERE d.analisis_id = @id AND g.origen = 'cache_vigente'", ("id", segundo.Analisis.Id)));
    }

    [Fact]
    public async Task Con_la_cache_vencida_y_NuGet_caido_se_guarda_el_resultado_con_origen_cache_expirada_y_la_fecha_original()
    {
        var uno = Unico("Ejemplo.D");
        var tiempo = new FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var fechaOriginal = tiempo.GetUtcNow();
        var proyecto = Csproj((uno, "1.0.0"));
        await Crear(tiempo: tiempo).Orquestador.AnalizarAsync("Mi.csproj", Flujo(proyecto)); // llena la caché

        tiempo.Advance(TimeSpan.FromHours(30)); // la caché vence
        var caido = new NuGetHttpFalso { Caido = true };
        var e = Crear(caido, tiempo, nuget: new OpcionesNuGet { ConsultarDescargas = false, MaxReintentos = 0 });
        var r = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(proyecto));

        var d = r.Analisis.Dependencias.Single();
        Assert.Equal(OrigenDato.CacheExpirada, d.Diagnostico.Origen);
        Assert.Equal(fechaOriginal, d.Diagnostico.FechaConsulta);
        Assert.Contains(d.Advertencias, a => a.Codigo == "cache_expirada");
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado); // la caché vencida por sí sola no vuelve parcial el análisis
        // y así quedó persistido
        Assert.Equal(fechaOriginal, await bd.EscalarAsync<DateTime>(
            "SELECT g.fecha_consulta FROM diagnostico g JOIN dependencia d ON d.id = g.dependencia_id WHERE d.analisis_id = @id", ("id", r.Analisis.Id)) is var f ? new DateTimeOffset(DateTime.SpecifyKind(f, DateTimeKind.Utc)) : default);
        Assert.Equal("cache_expirada", await bd.EscalarAsync<string>(
            "SELECT g.origen FROM diagnostico g JOIN dependencia d ON d.id = g.dependencia_id WHERE d.analisis_id = @id", ("id", r.Analisis.Id)));
    }

    [Fact]
    public async Task Un_paquete_inexistente_no_elimina_los_demas_y_el_parcial_se_persiste()
    {
        var bueno = Unico("Ejemplo.E");
        var e = Crear();

        var r = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((bueno, "2.0.0"), ("Ejemplo.NoExiste", "1.0.0"), (Unico("Ejemplo.F"), "2.0.0"))));

        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.NoDeterminada, EstadoDiagnostico.Actualizada], r.Analisis.Dependencias.Select(d => d.Diagnostico.Estado));
        var leido = (await bd.Analisis.ObtenerAsync(r.Analisis.Id))!;
        Assert.Equal(EstadoAnalisis.Parcial, leido.Estado);
        Assert.Null(leido.Dependencias[1].Diagnostico.FechaConsulta);   // sin fecha inventada
        Assert.Null(leido.Dependencias[1].Diagnostico.Indicadores);
        Assert.Equal(OrigenDato.NoDeterminado, leido.Dependencias[1].Diagnostico.Origen);
    }

    [Fact]
    public async Task Al_agotarse_el_presupuesto_de_120_segundos_se_guarda_el_resultado_parcial_en_PostgreSQL()
    {
        var rapido = Unico("Ejemplo.G");
        var e = Crear();
        await e.Orquestador.AnalizarAsync("Previo.csproj", Flujo(Csproj((rapido, "2.0.0")))); // deja «rapido» en caché vigente: se resuelve sin esperar a NuGet

        var tarea = e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((rapido, "2.0.0"), ("Ejemplo.Lento", "1.0.0"))));
        for (var i = 0; i < 300 && !e.Http.Solicitudes.Any(u => u.Contains("/ejemplo.lento")); i++) await Task.Delay(5);
        e.Tiempo.Advance(TimeSpan.FromSeconds(121));
        var r = await tarea.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(r.PresupuestoAgotado);
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        var leido = (await bd.Analisis.ObtenerAsync(r.Analisis.Id))!;
        Assert.Equal(EstadoDiagnostico.Actualizada, leido.Dependencias[0].Diagnostico.Estado);
        Assert.Equal(EstadoDiagnostico.NoDeterminada, leido.Dependencias[1].Diagnostico.Estado);
        Assert.Contains(leido.Advertencias, a => a.Codigo == "presupuesto_agotado");
    }

    [Fact]
    public async Task Con_la_base_de_datos_caida_el_fallo_es_global_y_no_queda_nada_a_medias()
    {
        var uno = Unico("Ejemplo.H");
        var cadenaRota = new Npgsql.NpgsqlConnectionStringBuilder(bd.CadenaConexion) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 3 }.ConnectionString;
        await using var servicios = new ServiceCollection().AddDatos(cadenaRota).BuildServiceProvider();
        var e = Crear(repositorio: servicios.GetRequiredService<IAnalisisRepositorio>());
        var antes = await bd.EscalarAsync<int>("SELECT count(*) FROM analisis");

        var ex = await Assert.ThrowsAsync<AlmacenamientoNoDisponibleException>(() => e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((uno, "2.0.0")))));

        Assert.NotNull(ex.InnerException);
        Assert.Equal(antes, await bd.EscalarAsync<int>("SELECT count(*) FROM analisis"));
    }

    [Fact]
    public async Task Una_entrada_invalida_no_consulta_NuGet_ni_guarda_nada_en_la_base()
    {
        var e = Crear();
        var antes = await bd.EscalarAsync<int>("SELECT count(*) FROM analisis");

        await Assert.ThrowsAsync<ArchivoInvalidoException>(() => e.Orquestador.AnalizarAsync("Mi.csproj", Flujo("<html/>")));

        Assert.Empty(e.Http.Solicitudes);
        Assert.Equal(antes, await bd.EscalarAsync<int>("SELECT count(*) FROM analisis"));
    }

    [Fact]
    public async Task Cancelar_el_analisis_no_deja_nada_guardado()
    {
        var e = Crear();
        var antes = await bd.EscalarAsync<int>("SELECT count(*) FROM analisis");
        using var cts = new CancellationTokenSource();

        var tarea = e.Orquestador.AnalizarAsync(new ProyectoLeido("Mi.csproj", [new FrameworkDeclarado("net8.0", true)],
            [new DependenciaDeclarada("Ejemplo.Lento", "1.0.0", TipoVersion.Exacta, null, [])], []), cts.Token);
        for (var i = 0; i < 300 && !e.Http.Solicitudes.Any(u => u.Contains("/ejemplo.lento")); i++) await Task.Delay(5);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
        Assert.Equal(antes, await bd.EscalarAsync<int>("SELECT count(*) FROM analisis"));
    }

    [Fact]
    public async Task Dos_analisis_simultaneos_de_las_mismas_librerias_comparten_las_consultas_y_ambos_se_guardan()
    {
        var uno = Unico("Ejemplo.I");
        var e = Crear();
        var proyecto = Csproj((uno, "2.0.0"));

        var resultados = await Task.WhenAll(
            e.Orquestador.AnalizarAsync("A.csproj", Flujo(proyecto)),
            e.Orquestador.AnalizarAsync("B.csproj", Flujo(proyecto)),
            e.Orquestador.AnalizarAsync("C.csproj", Flujo(proyecto)));

        Assert.True(e.Http.Registros <= 3); // sin la deduplicación habría 3 como mínimo y varios más si hubiera paquetes repetidos
        Assert.Equal(3, resultados.Select(r => r.Analisis.Id).Distinct().Count());
        foreach (var r in resultados) Assert.NotNull(await bd.Analisis.ObtenerAsync(r.Analisis.Id));
    }

    [Fact]
    public async Task Un_identificador_de_150_caracteres_se_lee_y_se_guarda_pero_no_se_consulta_en_NuGet()
    {
        var largo = new string('a', 150);
        var e = Crear();

        var r = await e.Orquestador.AnalizarAsync("Mi.csproj", Flujo(Csproj((largo, "1.0.0"))));

        Assert.Empty(e.Http.Solicitudes);                          // límite externo de 100: ni siquiera se descubre el índice
        var d = (await bd.Analisis.ObtenerAsync(r.Analisis.Id))!.Dependencias.Single();
        Assert.Equal(largo, d.Nombre);                             // se guardó (límite de almacenamiento: 200)
        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Diagnostico.Estado);
    }
}
