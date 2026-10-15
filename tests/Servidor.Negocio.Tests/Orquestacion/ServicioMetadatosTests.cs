using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Negocio.Tests.Orquestacion;

public class ServicioMetadatosTests
{
    private static readonly string[] Net8 = ["net8.0"];

    private readonly NuGetFalso _nuget = new();
    private readonly CacheFalsa _cache = new();
    private readonly FakeTimeProvider _tiempo = new(Paquetes.Ahora);

    private ServicioMetadatos Crear(OpcionesMetadatos? opciones = null) => new(_nuget, _cache, new InferidorFalso(), _tiempo, opciones);

    private static ConsultaPaquete Consulta(string paquete = "Ejemplo.Uno", string? version = "1.0.0", params string[] tfms) =>
        new(paquete, version, tfms.Length == 0 ? Net8 : tfms);

    /// <summary>Deja en la caché lo que habría guardado una consulta real hecha en <paramref name="fecha"/>.</summary>
    private async Task Sembrar(InformacionPaquete info)
    {
        var paraCache = ConversorCache.Convertir(info);
        foreach (var fila in paraCache.Metadatos) await _cache.GuardarAsync(fila);
        await _cache.GuardarSeleccionAsync(paraCache.Seleccion!);
        _cache.Lecturas = 0;
        _cache.Escrituras = 0;
    }

    private static ResultadoNuGet<InformacionPaquete> NoDisponible(CausaFallo causa = CausaFallo.ErrorServidor) =>
        ResultadoNuGet<InformacionPaquete>.NoDisponible(causa, "NuGet no respondió (503)");

    // ---------- caché vigente ----------

    [Fact]
    public async Task Con_cache_vigente_no_se_llama_a_NuGet_y_se_conserva_la_fecha_de_los_datos()
    {
        var fechaDatos = Paquetes.Ahora.AddHours(-5);
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", fechaDatos));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.CacheVigente, r.Origen);
        Assert.Equal(0, _nuget.Llamadas);
        Assert.Equal(0, r.SolicitudesHttp);
        Assert.Equal(fechaDatos, r.FechaDatos);
        Assert.Equal("2.0.0", r.Informacion!.UltimaEstable!.Version);
        Assert.Equal("1.0.0", r.Informacion.Declarada!.Version);
    }

    [Fact]
    public async Task La_vigencia_se_acaba_exactamente_a_las_24_horas()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora));

        _tiempo.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.Equal(OrigenDato.CacheVigente, (await Crear().ObtenerAsync(Consulta())).Origen);
        Assert.Equal(0, _nuget.Llamadas);

        _tiempo.Advance(TimeSpan.FromMinutes(1)); // exactamente 24 h
        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Equal(1, _nuget.Llamadas);
    }

    [Fact]
    public async Task La_vigencia_es_configurable()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora));
        _tiempo.Advance(TimeSpan.FromHours(2));

        var r = await Crear(new OpcionesMetadatos { VigenciaCache = TimeSpan.FromHours(1) }).ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
    }

    [Fact]
    public async Task Sin_version_declarada_basta_la_seleccion_vigente()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", null));

        var r = await Crear().ObtenerAsync(Consulta(version: null));

        Assert.Equal(OrigenDato.CacheVigente, r.Origen);
        Assert.Equal(EstadoVersionDeclarada.NoSolicitada, r.Informacion!.EstadoDeclarada);
    }

    [Fact]
    public async Task Si_falta_la_fila_de_la_version_declarada_aunque_la_seleccion_sea_vigente_se_consulta_NuGet()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0"));

        var r = await Crear().ObtenerAsync(Consulta(version: "1.5.0"));

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Equal(1, _nuget.Llamadas);
    }

    [Fact]
    public async Task Una_seleccion_incompleta_no_se_considera_vigente_y_se_reintenta()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", completa: false));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Equal(1, _nuget.Llamadas);
    }

    // ---------- caché vencida ----------

    [Fact]
    public async Task Con_cache_vencida_y_NuGet_disponible_se_consulta_y_se_renueva_la_cache()
    {
        var vieja = Paquetes.Ahora.AddDays(-3);
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", vieja, ultima: "2.0.0"));
        _nuget.Comportamiento = (c, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(
            Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora, ultima: "3.0.0"), [], 2));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Equal("3.0.0", r.Informacion!.UltimaEstable!.Version);
        Assert.Equal(Paquetes.Ahora, r.FechaDatos);
        Assert.Equal(2, r.SolicitudesHttp);
        Assert.Equal("3.0.0", _cache.Seleccion("Ejemplo.Uno")!.UltimaVersionEstable); // la caché quedó renovada
        Assert.Equal(Paquetes.Ahora, _cache.Seleccion("Ejemplo.Uno")!.FechaConsulta);
    }

    [Fact]
    public async Task Con_cache_vencida_y_NuGet_caido_se_usa_la_copia_con_su_fecha_ORIGINAL_y_una_advertencia()
    {
        var original = Paquetes.Ahora.AddDays(-3);
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", original));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.CacheExpirada, r.Origen);
        Assert.Equal(original, r.FechaDatos);          // nunca la fecha actual
        Assert.NotEqual(Paquetes.Ahora, r.FechaDatos);
        Assert.Equal("2.0.0", r.Informacion!.UltimaEstable!.Version);
        var aviso = Assert.Single(r.Advertencias, a => a.Codigo == "cache_expirada");
        Assert.Contains("2026-10-12 12:00 UTC", aviso.Mensaje);
        Assert.Contains("503", aviso.Mensaje);
        Assert.Equal(1, _nuget.Llamadas);
    }

    [Theory]
    [InlineData(CausaFallo.CircuitoAbierto)]
    [InlineData(CausaFallo.TiempoAgotado)]
    [InlineData(CausaFallo.LimiteSolicitudes)]
    [InlineData(CausaFallo.RespuestaInvalida)]
    [InlineData(CausaFallo.IndiceNoDisponible)]
    public async Task Cualquier_causa_de_indisponibilidad_cae_a_la_copia_vencida(CausaFallo causa)
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora.AddDays(-2)));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible(causa));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.CacheExpirada, r.Origen);
        Assert.True(r.TieneDatos);
    }

    [Fact]
    public async Task La_fecha_conservada_es_la_mas_antigua_de_las_filas_usadas()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora.AddDays(-5)));        // todo guardado hace 5 días...
        foreach (var fila in new[] { _cache.Fila("Ejemplo.Uno", "1.0.0")!, _cache.Fila("Ejemplo.Uno", "2.0.0")! })
            await _cache.GuardarAsync(fila with { FechaConsulta = Paquetes.Ahora.AddDays(-2) });  // ...y las filas se renovaron hace 2: la selección es la más antigua
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(Paquetes.Ahora.AddDays(-5), r.FechaDatos);
    }

    // ---------- sin caché ----------

    [Fact]
    public async Task Sin_cache_y_con_NuGet_caido_el_resultado_es_no_determinado_sin_inventar_datos()
    {
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.NoDeterminado, r.Origen);
        Assert.Null(r.Informacion);
        Assert.Null(r.FechaDatos);
        Assert.Equal(MotivoSinDatos.NuGetNoDisponible, r.Motivo);
        Assert.Contains(r.Advertencias, a => a.Codigo == "sin_datos");
        Assert.False(r.TieneDatos);
    }

    [Fact]
    public async Task Un_404_es_no_determinado_NoExisteEnNuGet_y_no_se_sustituye_por_una_copia_antigua()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora.AddDays(-9)));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.NoEncontrado("El paquete no existe en nuget.org."));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.NoDeterminado, r.Origen);
        Assert.Equal(MotivoSinDatos.NoExisteEnNuGet, r.Motivo);
        Assert.Null(r.Informacion);
    }

    [Fact]
    public async Task Un_paquete_inexistente_sin_cache_es_no_determinado_NoExisteEnNuGet()
    {
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.NoEncontrado("No existe."));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(MotivoSinDatos.NoExisteEnNuGet, r.Motivo);
    }

    [Theory]
    [InlineData(CausaFallo.PresupuestoAgotado, MotivoSinDatos.PresupuestoAgotado)]
    [InlineData(CausaFallo.CircuitoAbierto, MotivoSinDatos.CircuitoAbierto)]
    [InlineData(CausaFallo.Red, MotivoSinDatos.NuGetNoDisponible)]
    public async Task El_motivo_refleja_la_causa(CausaFallo causa, MotivoSinDatos esperado)
    {
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible(causa));

        Assert.Equal(esperado, (await Crear().ObtenerAsync(Consulta())).Motivo);
    }

    // ---------- origen NuGet ----------

    [Fact]
    public async Task Un_resultado_de_NuGet_se_guarda_en_la_cache_y_conserva_las_advertencias_del_gateway()
    {
        _nuget.Comportamiento = (c, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(
            Paquetes.Info(c.Paquete, c.VersionDeclarada), [new Advertencia("texto_externo_acotado", "Mensaje largo acotado.", c.Paquete)], 3));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Equal(3, r.SolicitudesHttp);
        Assert.Contains(r.Advertencias, a => a.Codigo == "texto_externo_acotado");
        Assert.NotNull(_cache.Fila("Ejemplo.Uno", "1.0.0"));
        Assert.NotNull(_cache.Seleccion("Ejemplo.Uno"));
    }

    [Fact]
    public async Task Una_segunda_consulta_equivalente_ya_sale_de_la_cache_sin_llamar_a_NuGet()
    {
        var servicio = Crear();

        var primera = await servicio.ObtenerAsync(Consulta());
        var segunda = await servicio.ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, primera.Origen);
        Assert.Equal(OrigenDato.CacheVigente, segunda.Origen);
        Assert.Equal(1, _nuget.Llamadas);
    }

    // ---------- fallos de la caché ----------

    [Fact]
    public async Task Si_la_cache_no_se_puede_leer_se_consulta_NuGet_con_una_advertencia()
    {
        _cache.FallaLectura = true;

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.Contains(r.Advertencias, a => a.Codigo == "cache_no_disponible");
    }

    [Fact]
    public async Task Si_la_cache_no_se_puede_escribir_el_resultado_de_NuGet_se_entrega_igual_con_advertencia()
    {
        _cache.FallaEscritura = true;

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.Nuget, r.Origen);
        Assert.True(r.TieneDatos);
        Assert.Contains(r.Advertencias, a => a.Codigo == "cache_no_guardada");
    }

    [Fact]
    public async Task Con_la_base_caida_y_NuGet_caido_no_hay_excepcion_solo_no_determinado()
    {
        _cache.FallaLectura = true;
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.NoDeterminado, r.Origen);
    }

    // ---------- deduplicación ----------

    [Fact]
    public async Task Diez_solicitudes_simultaneas_equivalentes_hacen_una_sola_consulta_a_NuGet()
    {
        var puerta = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var servicio = Crear();

        var tareas = Enumerable.Range(0, 10).Select(_ => servicio.ObtenerAsync(Consulta())).ToList();
        await Esperar(() => _nuget.Llamadas >= 1);
        await Task.Delay(50); // margen para que cualquier consulta de más se hiciera visible
        puerta.SetResult();
        var resultados = await Task.WhenAll(tareas);

        Assert.Equal(1, _nuget.Llamadas);
        Assert.All(resultados, r => Assert.Equal(OrigenDato.Nuget, r.Origen));
        Assert.All(resultados, r => Assert.Equal("2.0.0", r.Informacion!.UltimaEstable!.Version));
        Assert.Equal(0, servicio.ConsultasEnVuelo);
    }

    [Fact]
    public async Task Versiones_distintas_no_se_mezclan()
    {
        var puerta = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var servicio = Crear();

        var tareas = new[] { "1.0.0", "1.1.0", "1.0.0", "2.0.0" }.Select(v => servicio.ObtenerAsync(Consulta(version: v))).ToList();
        await Esperar(() => _nuget.Llamadas >= 3);
        await Task.Delay(50);
        puerta.SetResult();
        var resultados = await Task.WhenAll(tareas);

        Assert.Equal(3, _nuget.Llamadas);
        Assert.Equal(["1.0.0", "1.1.0", "1.0.0", "2.0.0"], resultados.Select(r => r.Informacion!.Declarada!.Version));
    }

    [Fact]
    public async Task Conjuntos_de_TFM_distintos_no_se_mezclan_pero_el_orden_y_las_mayusculas_no_importan()
    {
        var puerta = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var servicio = Crear();

        var tareas = new[]
        {
            servicio.ObtenerAsync(Consulta(tfms: ["net8.0", "net10.0"])),
            servicio.ObtenerAsync(Consulta(tfms: ["NET10.0", "net8.0"])),     // mismo conjunto
            servicio.ObtenerAsync(Consulta(tfms: ["net8.0"])),                 // distinto
            servicio.ObtenerAsync(Consulta(tfms: ["net8.0", "net8.0"])),       // mismo que el anterior
        };
        await Esperar(() => _nuget.Llamadas >= 2);
        await Task.Delay(50);
        puerta.SetResult();
        await Task.WhenAll(tareas);

        Assert.Equal(2, _nuget.Llamadas);
    }

    [Fact]
    public async Task Paquetes_distintos_o_con_distinta_capitalizacion_se_tratan_correctamente()
    {
        var puerta = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var servicio = Crear();

        var tareas = new[] { "Ejemplo.Uno", "EJEMPLO.UNO", "Ejemplo.Dos" }.Select(p => servicio.ObtenerAsync(Consulta(p))).ToList();
        await Esperar(() => _nuget.Llamadas >= 2);
        await Task.Delay(50);
        puerta.SetResult();
        await Task.WhenAll(tareas);

        Assert.Equal(2, _nuget.Llamadas); // «Ejemplo.Uno» y «EJEMPLO.UNO» son el mismo paquete
    }

    [Fact]
    public void La_clave_de_equivalencia_incluye_paquete_version_y_TFM()
    {
        Assert.Equal(ServicioMetadatos.Clave(Consulta("A", "1.0")), ServicioMetadatos.Clave(Consulta("a", "1.0.0")));
        Assert.NotEqual(ServicioMetadatos.Clave(Consulta("A", "1.0.0")), ServicioMetadatos.Clave(Consulta("A", "1.0.1")));
        Assert.NotEqual(ServicioMetadatos.Clave(Consulta("A", null)), ServicioMetadatos.Clave(Consulta("A", "1.0.0")));
        Assert.NotEqual(ServicioMetadatos.Clave(Consulta("A", "1.0.0", "net8.0")), ServicioMetadatos.Clave(Consulta("A", "1.0.0", "net10.0")));
    }

    // ---------- cancelación ----------

    [Fact]
    public async Task Si_un_llamador_cancela_la_consulta_compartida_sigue_para_los_demas()
    {
        var puerta = new TaskCompletionSource();
        var cancelaciones = 0;
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            ct.Register(() => Interlocked.Increment(ref cancelaciones));
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var servicio = Crear();
        using var cts = new CancellationTokenSource();

        var cancelable = servicio.ObtenerAsync(Consulta(), null, cts.Token);
        var otro = servicio.ObtenerAsync(Consulta());
        await Esperar(() => _nuget.Llamadas >= 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelable);
        puerta.SetResult();
        var resultado = await otro;

        Assert.Equal(OrigenDato.Nuget, resultado.Origen);
        Assert.Equal(0, cancelaciones);     // la operación compartida NO se canceló
        Assert.Equal(1, _nuget.Llamadas);
    }

    [Fact]
    public async Task Si_todos_los_llamadores_cancelan_la_consulta_compartida_se_cancela()
    {
        var observada = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            ct.Register(() => observada.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            return default!;
        };
        var servicio = Crear();
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();

        var t1 = servicio.ObtenerAsync(Consulta(), null, a.Token);
        var t2 = servicio.ObtenerAsync(Consulta(), null, b.Token);
        await Esperar(() => _nuget.Llamadas >= 1);
        await a.CancelAsync();
        await Task.Delay(30);
        Assert.False(observada.Task.IsCompleted); // todavía queda uno esperando
        await b.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        await observada.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Un_token_ya_cancelado_lanza_sin_tocar_NuGet_ni_la_cache()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Crear().ObtenerAsync(Consulta(), null, cts.Token));

        Assert.Equal(0, _nuget.Llamadas);
        Assert.Equal(0, _cache.Lecturas);
    }

    // ---------- presupuesto ----------

    [Fact]
    public async Task Con_el_presupuesto_ya_agotado_no_se_consulta_NuGet_y_se_usa_la_cache_vencida()
    {
        var original = Paquetes.Ahora.AddDays(-4);
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", original));
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(5), _tiempo);
        _tiempo.Advance(TimeSpan.FromSeconds(6));

        var r = await Crear().ObtenerAsync(Consulta(), presupuesto);

        Assert.Equal(0, _nuget.Llamadas);
        Assert.Equal(OrigenDato.CacheExpirada, r.Origen);
        Assert.Equal(original, r.FechaDatos);
        Assert.True(r.DegradadoPorPresupuesto);
        Assert.Contains(r.Advertencias, a => a.Codigo == "cache_expirada");
    }

    [Fact]
    public async Task Con_el_presupuesto_agotado_y_sin_cache_el_resultado_es_no_determinado_PresupuestoAgotado()
    {
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(5), _tiempo);
        _tiempo.Advance(TimeSpan.FromSeconds(6));

        var r = await Crear().ObtenerAsync(Consulta(), presupuesto);

        Assert.Equal(OrigenDato.NoDeterminado, r.Origen);
        Assert.Equal(MotivoSinDatos.PresupuestoAgotado, r.Motivo);
        Assert.True(r.DegradadoPorPresupuesto);
        Assert.Equal(0, _nuget.Llamadas);
    }

    [Fact]
    public async Task Con_el_presupuesto_agotado_pero_cache_vigente_se_sirve_la_cache_vigente()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora));
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(5), _tiempo);
        _tiempo.Advance(TimeSpan.FromSeconds(6));

        var r = await Crear().ObtenerAsync(Consulta(), presupuesto);

        Assert.Equal(OrigenDato.CacheVigente, r.Origen);
    }

    [Fact]
    public async Task Si_el_presupuesto_del_llamador_se_agota_durante_la_espera_se_desvincula_y_usa_la_cache()
    {
        var original = Paquetes.Ahora.AddDays(-4);
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", original));
        var puerta = new TaskCompletionSource();
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            await puerta.Task.WaitAsync(ct);
            return ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada, Paquetes.Ahora, "9.0.0"), [], 2);
        };
        var servicio = Crear();
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(5), _tiempo);

        var conPresupuesto = servicio.ObtenerAsync(Consulta(), presupuesto);
        var sinPresupuesto = servicio.ObtenerAsync(Consulta());      // otro análisis que sí espera
        await Esperar(() => _nuget.Llamadas >= 1);
        _tiempo.Advance(TimeSpan.FromSeconds(6));
        var degradado = await conPresupuesto;
        puerta.SetResult();
        var completo = await sinPresupuesto;

        Assert.Equal(OrigenDato.CacheExpirada, degradado.Origen);   // el llamador sin presupuesto no se bloquea
        Assert.Equal(original, degradado.FechaDatos);
        Assert.True(degradado.DegradadoPorPresupuesto);
        Assert.Equal(OrigenDato.Nuget, completo.Origen);            // la consulta compartida siguió para el otro
        Assert.Equal("9.0.0", completo.Informacion!.UltimaEstable!.Version);
        Assert.Equal(1, _nuget.Llamadas);
    }

    [Fact]
    public async Task Si_NuGet_informa_PresupuestoAgotado_el_resultado_queda_marcado_como_degradado_por_presupuesto()
    {
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible(CausaFallo.PresupuestoAgotado));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.True(r.DegradadoPorPresupuesto);
        Assert.Equal(MotivoSinDatos.PresupuestoAgotado, r.Motivo);
    }

    // ---------- avisos derivados de la caché ----------

    [Fact]
    public async Task Al_servir_una_seleccion_incompleta_desde_la_cache_se_conserva_la_advertencia()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", Paquetes.Ahora.AddDays(-2), completa: false));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.CacheExpirada, r.Origen);
        Assert.Contains(r.Advertencias, a => a.Codigo == "seleccion_incompleta");
    }

    [Fact]
    public async Task Las_descargas_ausentes_en_la_cache_se_explican_y_nunca_son_cero()
    {
        await Sembrar(Paquetes.Info("Ejemplo.Uno", "1.0.0", descargas: null));

        var r = await Crear().ObtenerAsync(Consulta());

        Assert.Equal(OrigenDato.CacheVigente, r.Origen);
        Assert.Null(r.Informacion!.Descargas);
        Assert.Contains(r.Advertencias, a => a.Codigo == "descargas_no_disponibles");
    }

    private static async Task Esperar(Func<bool> condicion)
    {
        for (var i = 0; i < 400 && !condicion(); i++) await Task.Delay(5);
        Assert.True(condicion(), "la condición esperada no se cumplió a tiempo");
    }
}
