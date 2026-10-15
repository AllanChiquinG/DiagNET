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
using Servidor.Negocio.Reglas;

namespace Servidor.Datos.Tests;

/// <summary>
/// Integración de E5 con los módulos REALES disponibles: lector (E2), repositorios sobre PostgreSQL real (E3), gateway de NuGet (E4) con respuestas
/// SIMULADAS (sin internet) y el MOTOR REAL de diagnóstico (E6). Solo el motor de recomendaciones es un SUSTITUTO EXCLUSIVO DE PRUEBAS (E7 aún no implementado).
/// </summary>
public class OrquestacionIntegracionTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    // ---------------- sustitutos de pruebas (NO son los motores de E6/E7) ----------------

    private sealed class MotorRecomendacionDePrueba : IMotorRecomendacion
    {
        public Task<IReadOnlyList<Recomendacion>> RecomendarAsync(ContextoDiagnostico c, Diagnostico d, PresupuestoTiempo? p, CancellationToken ct)
        {
            IReadOnlyList<Recomendacion> r = [new Recomendacion(TipoRecomendacion.ActualizarVersion, c.Dependencia.Nombre, d.UltimaVersion, "Recomendación de prueba.", [], [], false)];
            return Task.FromResult(r);
        }
    }

    // ---------------- NuGet simulado ----------------

    private sealed class NuGetHttpFalso : HttpMessageHandler
    {
        private const string Base = "https://api.nuget.org/v3/registration5-gz-semver2/";
        public bool Caido { get; set; }
        public ConcurrentBag<string> Solicitudes { get; } = [];
        public int Registros => Solicitudes.Count(u => u.StartsWith(Base));

        private static string Hoja(string version) =>
            "{\"catalogEntry\":{\"version\":\"" + version + "\",\"listed\":true,\"published\":\"2025-01-01T00:00:00+00:00\",\"licenseExpression\":\"MIT\",\"dependencyGroups\":[{\"targetFramework\":\"net8.0\"}]}}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Solicitudes.Add(url);
            if (url == "https://api.nuget.org/v3/index.json")
                return Json("{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + Base + "\",\"@type\":\"RegistrationsBaseUrl/3.6.0\"},{\"@id\":\"https://azuresearch-usnc.nuget.org/query\",\"@type\":\"SearchQueryService/3.5.0\"}]}");
            if (Caido) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (url.StartsWith("https://azuresearch-usnc.nuget.org/query")) return Json("{\"data\":[]}");
            if (url.Contains("/ejemplo.lento")) { await Task.Delay(Timeout.Infinite, ct); }
            if (url.EndsWith("/ejemplo.noexiste/index.json")) return new HttpResponseMessage(HttpStatusCode.NotFound);
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
            new MotorRecomendacionDePrueba(), repositorio ?? bd.Analisis, tiempo, opciones);
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
        Assert.Equal("e6-1.1|ant=24m|agr=pes", leido.VersionReglas);                                  // las reglas usadas quedan guardadas con el análisis
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
