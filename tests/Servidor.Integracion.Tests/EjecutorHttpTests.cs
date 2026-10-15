using System.Net;
using System.Net.Http.Headers;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.Tests;

public class EjecutorHttpTests
{
    private const string Url = "https://api.nuget.org/v3/prueba.json";
    private static readonly Uri Uri_ = new(Url);

    private static HttpResponseMessage Ok(string json = "{}") => NuGetSimulado.Respuesta(HttpStatusCode.OK, json);

    private static Func<HttpResponseMessage> Estado(HttpStatusCode e) => () => new HttpResponseMessage(e);

    private static Task<RespuestaHttp> Pedir(EjecutorHttp ej, PresupuestoTiempo? p = null, CancellationToken ct = default) =>
        ej.ObtenerAsync(Uri_, p, ct);

    // ---------- respuestas básicas ----------

    [Fact]
    public async Task Una_respuesta_correcta_se_devuelve_con_su_contenido_y_una_solicitud()
    {
        var nuget = new NuGetSimulado().Json(Url, "{\"a\":1}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal("{\"a\":1}", System.Text.Encoding.UTF8.GetString(r.Cuerpo!));
        Assert.Equal(1, r.Solicitudes);
    }

    [Fact]
    public async Task Un_404_no_se_reintenta()
    {
        var nuget = new NuGetSimulado().Estado(Url, HttpStatusCode.NotFound);
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(EstadoHttp.NoEncontrado, r.Estado);
        Assert.Equal(1, nuget.Llamadas(Url));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Gone)]
    public async Task Otros_4xx_no_se_reintentan_ni_cuentan_como_fallo_de_NuGet(HttpStatusCode codigo)
    {
        var nuget = new NuGetSimulado().Estado(Url, codigo);
        var (ej, _, disyuntor) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.RespuestaNoEsperada, r.Causa);
        Assert.Equal(1, nuget.Llamadas(Url));
        for (var i = 0; i < 10; i++) await Pedir(ej);
        Assert.False(disyuntor.Abierto);
    }

    // ---------- reintentos con espera creciente ----------

    [Fact]
    public async Task Dos_503_y_luego_correcta_reintenta_dos_veces_esperando_1_y_2_segundos()
    {
        var nuget = new NuGetSimulado().Secuencia(Url, Estado(HttpStatusCode.ServiceUnavailable), Estado(HttpStatusCode.ServiceUnavailable), () => Ok("{\"ok\":true}"));
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal(3, nuget.Llamadas(Url));
        Assert.Equal(3, r.Solicitudes);
        var esperado = TimeSpan.FromSeconds(3);
        var transcurrido = tiempo.GetUtcNow() - inicio;
        Assert.InRange(transcurrido, esperado, esperado + TimeSpan.FromSeconds(0.5));
    }

    [Fact]
    public async Task Un_fallo_permanente_hace_un_intento_mas_tres_reintentos_esperando_1_2_y_4_segundos()
    {
        var nuget = new NuGetSimulado().Estado(Url, HttpStatusCode.BadGateway);
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(CausaFallo.ErrorServidor, r.Causa);
        Assert.Equal(4, nuget.Llamadas(Url));
        Assert.InRange(tiempo.GetUtcNow() - inicio, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7.5));
    }

    [Fact]
    public async Task Se_reintenta_ante_408_y_429_y_ante_fallas_de_red()
    {
        var nuget = new NuGetSimulado().Secuencia(Url, Estado(HttpStatusCode.RequestTimeout), Estado((HttpStatusCode)429), () => throw new HttpRequestException("DNS"), () => Ok());
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal(4, nuget.Llamadas(Url));
    }

    [Fact]
    public async Task Una_falla_de_red_permanente_termina_con_causa_Red()
    {
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => throw new HttpRequestException("sin conexión"));
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(CausaFallo.Red, r.Causa);
        Assert.Equal(4, nuget.Llamadas(Url));
    }

    [Fact]
    public async Task Retry_After_de_3_segundos_se_respeta_en_lugar_de_la_espera_normal()
    {
        HttpResponseMessage Limitada()
        {
            var m = new HttpResponseMessage((HttpStatusCode)429);
            m.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return m;
        }

        var nuget = new NuGetSimulado().Secuencia(Url, Limitada, () => Ok());
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.InRange(tiempo.GetUtcNow() - inicio, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3.5));
    }

    [Fact]
    public async Task Retry_After_mayor_a_10_segundos_no_espera_y_pasa_al_modo_degradado()
    {
        HttpResponseMessage Limitada()
        {
            var m = new HttpResponseMessage((HttpStatusCode)429);
            m.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return m;
        }

        var nuget = new NuGetSimulado().Secuencia(Url, Limitada);
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.LimiteSolicitudes, r.Causa);
        Assert.Equal(1, nuget.Llamadas(Url));
        Assert.Equal(inicio, tiempo.GetUtcNow()); // no esperó
    }

    [Fact]
    public async Task Retry_After_con_fecha_se_convierte_en_espera()
    {
        HttpResponseMessage Limitada() => new((HttpStatusCode)503) { Headers = { RetryAfter = new RetryConditionHeaderValue(new DateTimeOffset(2026, 10, 15, 12, 0, 4, TimeSpan.Zero)) } };

        var nuget = new NuGetSimulado().Secuencia(Url, Limitada, () => Ok());
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.InRange(tiempo.GetUtcNow() - inicio, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4.5));
    }

    [Fact]
    public async Task La_variacion_aleatoria_se_suma_a_la_espera()
    {
        var nuget = new NuGetSimulado().Secuencia(Url, Estado(HttpStatusCode.ServiceUnavailable), () => Ok());
        var opciones = new OpcionesNuGet();
        var tiempo = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));
        var ej = new EjecutorHttp(new HttpClient(nuget), opciones, new ValidadorDirecciones(opciones),
            new Disyuntor(5, TimeSpan.FromSeconds(30), tiempo), tiempo, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, () => 1.0);
        var inicio = tiempo.GetUtcNow();

        await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.InRange(tiempo.GetUtcNow() - inicio, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.7)); // 1 s + 20 %
    }

    // ---------- tiempo máximo por intento ----------

    [Fact]
    public async Task Un_intento_que_no_responde_en_10_segundos_se_corta_y_se_reintenta()
    {
        var nuget = new NuGetSimulado().Ruta(Url, async (_, n, ct) =>
        {
            if (n == 0) await Task.Delay(Timeout.Infinite, ct); // el primer intento nunca responde
            return Ok("{\"ok\":1}");
        });
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal(2, nuget.Llamadas(Url));
        Assert.True(tiempo.GetUtcNow() - inicio >= TimeSpan.FromSeconds(11)); // 10 s de espera + 1 s de pausa
    }

    [Fact]
    public async Task Si_todos_los_intentos_agotan_el_tiempo_la_causa_es_TiempoAgotado()
    {
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget, o => { o.MaxReintentos = 1; o.TiempoEsperoSeg = 5; });

        var r = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo, pasoMs: 500);

        Assert.Equal(CausaFallo.TiempoAgotado, r.Causa);
        Assert.Equal(2, nuget.Llamadas(Url));
    }

    // ---------- concurrencia ----------

    [Fact]
    public async Task Nunca_hay_mas_de_4_solicitudes_simultaneas()
    {
        var puerta = new SemaphoreSlim(0);
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await puerta.WaitAsync(ct); return Ok(); });
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var tareas = Enumerable.Range(0, 12).Select(_ => Pedir(ej)).ToList();
        for (var i = 0; i < 200 && nuget.Total < 4; i++) await Task.Delay(5);
        await Task.Delay(100); // margen para que cualquier solicitud de más se hiciera visible

        Assert.Equal(4, nuget.Total); // las demás esperan su turno
        puerta.Release(12);
        var resultados = await Task.WhenAll(tareas);

        Assert.All(resultados, r => Assert.Equal(EstadoHttp.Ok, r.Estado));
        Assert.Equal(12, nuget.Total);
        Assert.Equal(4, nuget.MaximoConcurrente);
    }

    [Fact]
    public async Task El_limite_de_concurrencia_es_configurable()
    {
        var puerta = new SemaphoreSlim(0);
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await puerta.WaitAsync(ct); return Ok(); });
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget, o => o.MaxConcurrentes = 2);

        var tareas = Enumerable.Range(0, 6).Select(_ => Pedir(ej)).ToList();
        for (var i = 0; i < 200 && nuget.Total < 2; i++) await Task.Delay(5);
        await Task.Delay(100);

        Assert.Equal(2, nuget.Total);
        puerta.Release(6);
        await Task.WhenAll(tareas);
        Assert.Equal(2, nuget.MaximoConcurrente);
    }

    // ---------- cancelación ----------

    [Fact]
    public async Task Un_token_ya_cancelado_lanza_sin_enviar_nada()
    {
        var nuget = new NuGetSimulado().Json(Url, "{}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pedir(ej, ct: cts.Token));

        Assert.Equal(0, nuget.Total);
    }

    [Fact]
    public async Task Cancelar_durante_la_solicitud_propaga_la_cancelacion_sin_reintentos()
    {
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var cts = new CancellationTokenSource();

        var tarea = Pedir(ej, ct: cts.Token);
        for (var i = 0; i < 100 && nuget.Total == 0; i++) await Task.Delay(5);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
        Assert.Equal(1, nuget.Total);
    }

    [Fact]
    public async Task Cancelar_durante_la_espera_de_un_reintento_la_interrumpe_y_no_envia_otra_solicitud()
    {
        var nuget = new NuGetSimulado().Estado(Url, HttpStatusCode.ServiceUnavailable);
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var cts = new CancellationTokenSource();

        var tarea = Pedir(ej, ct: cts.Token);
        for (var i = 0; i < 200 && nuget.Total == 0; i++) await Task.Delay(5);
        await Task.Delay(30); // ya está esperando el primer reintento
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
        Assert.Equal(1, nuget.Total);
    }

    [Fact]
    public async Task Una_cancelacion_no_cuenta_como_fallo_del_disyuntor()
    {
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var (ej, _, disyuntor) = NuGetSimulado.CrearEjecutor(nuget);

        for (var i = 0; i < 8; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Pedir(ej, ct: cts.Token));
        }

        Assert.False(disyuntor.Abierto);
    }

    // ---------- presupuesto global ----------

    [Fact]
    public async Task Un_presupuesto_ya_agotado_no_envia_ninguna_solicitud()
    {
        var nuget = new NuGetSimulado().Json(Url, "{}");
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(2), tiempo);
        tiempo.Advance(TimeSpan.FromSeconds(3));

        var r = await Pedir(ej, presupuesto);

        Assert.Equal(CausaFallo.PresupuestoAgotado, r.Causa);
        Assert.Equal(0, nuget.Total);
    }

    [Fact]
    public async Task No_se_inicia_una_espera_que_ya_no_cabe_en_el_presupuesto()
    {
        var nuget = new NuGetSimulado().Estado(Url, HttpStatusCode.ServiceUnavailable);
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(2.5), tiempo); // cabe la espera de 1 s, no la de 2 s

        var r = await NuGetSimulado.ConTiempo(Pedir(ej, presupuesto), tiempo);

        Assert.Equal(CausaFallo.PresupuestoAgotado, r.Causa);
        Assert.Equal(2, nuget.Llamadas(Url));
    }

    [Fact]
    public async Task Si_el_presupuesto_se_agota_durante_la_solicitud_el_resultado_es_PresupuestoAgotado_y_no_una_excepcion()
    {
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(3), tiempo);

        var r = await NuGetSimulado.ConTiempo(Pedir(ej, presupuesto), tiempo, pasoMs: 500);

        Assert.Equal(CausaFallo.PresupuestoAgotado, r.Causa);
        Assert.Equal(1, nuget.Total);
    }

    [Fact]
    public async Task El_tiempo_por_intento_no_reemplaza_al_presupuesto_global()
    {
        // Cada intento permite 10 s y hay 4 intentos (≈ 40 s + esperas); con un presupuesto de 15 s la operación termina antes.
        var nuget = new NuGetSimulado().Ruta(Url, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return Ok(); });
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(15), tiempo);
        var inicio = tiempo.GetUtcNow();

        var r = await NuGetSimulado.ConTiempo(Pedir(ej, presupuesto), tiempo, pasoMs: 500);

        Assert.Equal(CausaFallo.PresupuestoAgotado, r.Causa);
        Assert.True(tiempo.GetUtcNow() - inicio <= TimeSpan.FromSeconds(16));
        Assert.True(nuget.Total < 4);
    }

    // ---------- redirecciones ----------

    [Fact]
    public async Task Sigue_una_redireccion_hacia_nuget_org_y_cuenta_ambas_solicitudes()
    {
        var nuget = new NuGetSimulado()
            .Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://azuresearch-usnc.nuget.org/nuevo.json") } }))
            .Json("https://azuresearch-usnc.nuget.org/nuevo.json", "{\"destino\":1}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal(2, r.Solicitudes);
    }

    [Fact]
    public async Task Una_redireccion_relativa_se_resuelve_contra_la_direccion_original()
    {
        var nuget = new NuGetSimulado()
            .Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/v3/otro.json", UriKind.Relative) } }))
            .Json("https://api.nuget.org/v3/otro.json", "{}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        Assert.Equal(EstadoHttp.Ok, (await Pedir(ej)).Estado);
    }

    [Theory]
    [InlineData("https://evil.com/robo.json")]
    [InlineData("https://nuget.org.evil.com/robo.json")]
    [InlineData("http://api.nuget.org/v3/inseguro.json")]
    [InlineData("https://user@api.nuget.org/v3/x.json")]
    public async Task Una_redireccion_hacia_otro_dominio_o_sin_HTTPS_se_rechaza_y_nunca_se_envia(string destino)
    {
        var nuget = new NuGetSimulado()
            .Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(destino) } }))
            .Json(destino, "{\"secreto\":1}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.DireccionNoPermitida, r.Causa);
        Assert.Equal(0, nuget.Llamadas(destino));
    }

    [Fact]
    public async Task Mas_de_3_redirecciones_se_cortan()
    {
        var nuget = new NuGetSimulado();
        for (var i = 0; i < 10; i++)
        {
            var siguiente = $"https://api.nuget.org/v3/salto{i + 1}.json";
            nuget.Ruta(i == 0 ? Url : $"https://api.nuget.org/v3/salto{i}.json",
                (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(siguiente) } }));
        }

        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.RespuestaNoEsperada, r.Causa);
        Assert.Equal(4, nuget.Total); // la original y 3 saltos
    }

    [Fact]
    public async Task Exactamente_3_redirecciones_se_aceptan()
    {
        var nuget = new NuGetSimulado()
            .Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.nuget.org/v3/s1.json") } }))
            .Ruta("https://api.nuget.org/v3/s1.json", (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.nuget.org/v3/s2.json") } }))
            .Ruta("https://api.nuget.org/v3/s2.json", (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://api.nuget.org/v3/s3.json") } }))
            .Json("https://api.nuget.org/v3/s3.json", "{}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        Assert.Equal(EstadoHttp.Ok, (await Pedir(ej)).Estado);
    }

    [Fact]
    public async Task Una_redireccion_sin_destino_se_rechaza()
    {
        var nuget = new NuGetSimulado().Estado(Url, HttpStatusCode.Found);
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        Assert.Equal(CausaFallo.RespuestaNoEsperada, (await Pedir(ej)).Causa);
    }

    // ---------- dirección inicial ----------

    [Theory]
    [InlineData("http://api.nuget.org/v3/x.json")]
    [InlineData("https://evil.com/v3/x.json")]
    [InlineData("https://nuget.org.evil.com/")]
    public async Task Una_direccion_no_permitida_no_se_envia(string direccion)
    {
        var nuget = new NuGetSimulado().Json(direccion, "{}");
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await ej.ObtenerAsync(new Uri(direccion), null, CancellationToken.None);

        Assert.Equal(CausaFallo.DireccionNoPermitida, r.Causa);
        Assert.Equal(0, nuget.Total);
    }

    // ---------- tamaño de la respuesta ----------

    private sealed class ContenidoSinLongitud(long bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var bloque = new byte[64 * 1024];
            Array.Fill(bloque, (byte)' ');
            for (long escritos = 0; escritos < bytes; escritos += bloque.Length)
                await stream.WriteAsync(bloque.AsMemory(0, (int)Math.Min(bloque.Length, bytes - escritos)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false; // sin Content-Length: la respuesta llega «por partes»
        }
    }

    [Fact]
    public async Task Una_respuesta_con_Content_Length_mayor_a_5_MB_se_descarta_sin_leerla()
    {
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]),
        }));
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.RespuestaExcesiva, r.Causa);
        Assert.Equal(1, nuget.Llamadas(Url)); // no se reintenta
    }

    [Fact]
    public async Task Una_respuesta_por_partes_que_supera_5_MB_aborta_la_lectura()
    {
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ContenidoSinLongitud(6 * 1024 * 1024) }));
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.RespuestaExcesiva, r.Causa);
        Assert.Equal(1, nuget.Llamadas(Url));
    }

    [Fact]
    public async Task Una_respuesta_de_exactamente_5_MB_se_acepta()
    {
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ContenidoSinLongitud(5 * 1024 * 1024) }));
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget);

        var r = await Pedir(ej);

        Assert.Equal(EstadoHttp.Ok, r.Estado);
        Assert.Equal(5 * 1024 * 1024, r.Cuerpo!.Length);
    }

    [Fact]
    public async Task El_limite_de_respuesta_es_configurable()
    {
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ContenidoSinLongitud(2000) }));
        var (ej, _, _) = NuGetSimulado.CrearEjecutor(nuget, o => o.MaxRespuestaBytes = 1000);

        Assert.Equal(CausaFallo.RespuestaExcesiva, (await Pedir(ej)).Causa);
    }

    // ---------- disyuntor (DEC-15: cuenta operaciones fallidas tras agotar reintentos, no intentos) ----------

    private static NuGetSimulado Caido() => new NuGetSimulado().Estado(Url, HttpStatusCode.ServiceUnavailable);

    [Fact]
    public async Task Una_sola_operacion_con_cuatro_intentos_fallidos_cuenta_como_un_fallo_y_no_abre_el_disyuntor()
    {
        var nuget = Caido();
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => { o.UmbralDisyuntor = 3; o.EsperasReintentoSeg = [1, 1, 1]; });

        await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.Equal(4, nuget.Llamadas(Url)); // 4 intentos...
        Assert.False(disyuntor.Abierto);      // ...pero 1 sola operación fallida (umbral 3)
    }

    [Fact]
    public async Task Se_abre_en_la_quinta_operacion_fallida_consecutiva_no_antes()
    {
        var nuget = Caido();
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);

        for (var i = 1; i <= 4; i++)
        {
            await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
            Assert.False(disyuntor.Abierto, $"abierto tras {i} operaciones");
        }

        await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.True(disyuntor.Abierto);
        Assert.Equal(20, nuget.Llamadas(Url)); // 5 operaciones × 4 intentos
    }

    [Fact]
    public async Task Con_el_disyuntor_abierto_las_demas_consultas_no_envian_nada_y_dicen_CircuitoAbierto()
    {
        var nuget = Caido();
        var (ej, tiempo, _) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);
        for (var i = 0; i < 5; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
        var enviadas = nuget.Total;

        var r = await Pedir(ej);

        Assert.Equal(CausaFallo.CircuitoAbierto, r.Causa);
        Assert.Equal(0, r.Solicitudes);
        Assert.Equal(enviadas, nuget.Total);
    }

    [Fact]
    public async Task Un_exito_intercalado_reinicia_la_cuenta()
    {
        var modo = "falla";
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(modo == "falla" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok()));
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);

        for (var i = 0; i < 4; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
        modo = "ok";
        await Pedir(ej);
        modo = "falla";
        for (var i = 0; i < 4; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.False(disyuntor.Abierto); // 4 + éxito + 4: nunca 5 seguidas
    }

    [Fact]
    public async Task Un_404_cuenta_como_respuesta_valida_y_reinicia_la_cuenta()
    {
        var modo = "falla";
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(new HttpResponseMessage(modo == "falla" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound)));
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);

        for (var i = 0; i < 4; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
        modo = "404";
        await Pedir(ej);
        modo = "falla";
        for (var i = 0; i < 4; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);

        Assert.False(disyuntor.Abierto);
    }

    [Fact]
    public async Task Pasada_la_pausa_de_30_segundos_una_prueba_exitosa_cierra_el_disyuntor()
    {
        var modo = "falla";
        var nuget = new NuGetSimulado().Ruta(Url, (_, _, _) => Task.FromResult(modo == "falla" ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok()));
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);
        for (var i = 0; i < 5; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
        Assert.True(disyuntor.Abierto);

        tiempo.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(CausaFallo.CircuitoAbierto, (await Pedir(ej)).Causa); // todavía en pausa
        tiempo.Advance(TimeSpan.FromSeconds(2));
        modo = "ok";
        var prueba = await Pedir(ej);

        Assert.Equal(EstadoHttp.Ok, prueba.Estado);
        Assert.False(disyuntor.Abierto);
        Assert.Equal(EstadoHttp.Ok, (await Pedir(ej)).Estado); // vuelve a funcionar normalmente
    }

    [Fact]
    public async Task Si_la_prueba_tras_la_pausa_falla_el_disyuntor_vuelve_a_abrirse_otra_pausa_completa()
    {
        var nuget = Caido();
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);
        for (var i = 0; i < 5; i++) await NuGetSimulado.ConTiempo(Pedir(ej), tiempo);
        tiempo.Advance(TimeSpan.FromSeconds(31));

        var prueba = await NuGetSimulado.ConTiempo(Pedir(ej), tiempo); // la única prueba permitida, también falla (4 intentos)

        Assert.NotEqual(CausaFallo.CircuitoAbierto, prueba.Causa);
        Assert.True(disyuntor.Abierto);
        tiempo.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(CausaFallo.CircuitoAbierto, (await Pedir(ej)).Causa);
    }

    [Fact]
    public async Task Un_Retry_After_mayor_a_10_segundos_cuenta_como_operacion_fallida()
    {
        HttpResponseMessage Limitada() => new((HttpStatusCode)429) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60)) } };
        var nuget = new NuGetSimulado().Secuencia(Url, Limitada);
        var (ej, _, disyuntor) = NuGetSimulado.CrearEjecutor(nuget);

        for (var i = 0; i < 5; i++) await Pedir(ej);

        Assert.True(disyuntor.Abierto);
        Assert.Equal(5, nuget.Llamadas(Url)); // una solicitud por operación: no se esperó ni reintentó
    }

    [Fact]
    public async Task Los_fallos_de_otras_direcciones_comparten_el_mismo_disyuntor()
    {
        var nuget = new NuGetSimulado().Estado("https://api.nuget.org/v3/a.json", HttpStatusCode.BadGateway).Json("https://api.nuget.org/v3/b.json", "{}");
        var (ej, tiempo, disyuntor) = NuGetSimulado.CrearEjecutor(nuget, o => o.EsperasReintentoSeg = [1, 1, 1]);
        for (var i = 0; i < 5; i++) await NuGetSimulado.ConTiempo(ej.ObtenerAsync(new Uri("https://api.nuget.org/v3/a.json"), null, CancellationToken.None), tiempo);

        var r = await ej.ObtenerAsync(new Uri("https://api.nuget.org/v3/b.json"), null, CancellationToken.None);

        Assert.True(disyuntor.Abierto);
        Assert.Equal(CausaFallo.CircuitoAbierto, r.Causa); // NuGet está caído: no se insiste con otros recursos
        Assert.Equal(0, nuget.Llamadas("https://api.nuget.org/v3/b.json"));
    }
}
