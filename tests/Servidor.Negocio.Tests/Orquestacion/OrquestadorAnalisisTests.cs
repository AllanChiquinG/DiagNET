using System.Text;
using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using static Servidor.Negocio.Tests.Orquestacion.Paquetes;

namespace Servidor.Negocio.Tests.Orquestacion;

public class OrquestadorAnalisisTests
{
    private readonly NuGetFalso _nuget = new();
    private readonly CacheFalsa _cache = new();
    private readonly AnalisisRepositorioFalso _repo = new();
    private readonly MotorDiagnosticoDePrueba _motor = new();
    private readonly MotorRecomendacionDePrueba _recomendador = new();
    private readonly FakeTimeProvider _tiempo = new(Ahora);

    private OrquestadorAnalisis Crear(OpcionesOrquestacion? opciones = null, OpcionesMetadatos? opcionesMeta = null) => new(
        new LectorCsproj(new OpcionesLectura()),
        new ServicioMetadatos(_nuget, _cache, new InferidorFalso(), _tiempo, opcionesMeta),
        _motor, _recomendador, _repo, _tiempo, opciones);

    private static ResultadoNuGet<InformacionPaquete> NoDisponible() => ResultadoNuGet<InformacionPaquete>.NoDisponible(CausaFallo.ErrorServidor, "NuGet no respondió (503)");

    private async Task Sembrar(string paquete, string version, DateTimeOffset fecha)
    {
        var paraCache = ConversorCache.Convertir(Info(paquete, version, fecha));
        foreach (var fila in paraCache.Metadatos) await _cache.GuardarAsync(fila);
        await _cache.GuardarSeleccionAsync(paraCache.Seleccion!);
    }

    // ---------- flujo feliz ----------

    [Fact]
    public async Task Un_analisis_completo_se_guarda_una_sola_vez_con_las_dependencias_en_su_orden()
    {
        var proyecto = Proyecto(Dep("Ejemplo.Uno", "2.0.0"), Dep("Ejemplo.Dos", "1.0.0"), Dep("Ejemplo.Tres", "2.0.0"));

        var r = await Crear().AnalizarAsync(proyecto);

        var guardado = Assert.Single(_repo.Guardados);
        Assert.Same(guardado, r.Analisis);
        Assert.Equal(["Ejemplo.Uno", "Ejemplo.Dos", "Ejemplo.Tres"], guardado.Dependencias.Select(d => d.Nombre));
        Assert.Equal(EstadoAnalisis.Completo, guardado.Estado);
        Assert.Equal(["net8.0"], guardado.FrameworksEvaluados);
        Assert.Equal("prueba-e6+prueba-e7", guardado.VersionReglas);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada, EstadoDiagnostico.Actualizada], guardado.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.Equal(1, _repo.IntentosDeGuardado);
    }

    [Fact]
    public async Task El_origen_y_la_fecha_de_los_datos_siempre_los_pone_el_orquestador_aunque_el_motor_se_equivoque()
    {
        var vieja = Ahora.AddDays(-3);
        await Sembrar("Ejemplo.Uno", "1.0.0", vieja);
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0")));

        var d = Assert.Single(r.Analisis.Dependencias).Diagnostico;
        Assert.Equal(OrigenDato.CacheExpirada, d.Origen);   // el motor de prueba devuelve «Nuget» y la fecha cero: no prevalece
        Assert.Equal(vieja, d.FechaConsulta);                // fecha ORIGINAL de la copia
    }

    [Fact]
    public async Task Las_advertencias_de_lectura_y_de_metadatos_se_conservan_por_dependencia()
    {
        await Sembrar("Ejemplo.Uno", "1.0.0", Ahora.AddDays(-3));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());
        var dep = new DependenciaDeclarada("Ejemplo.Uno", "1.0.0", TipoVersion.Exacta, "'$(X)' == 'y'",
            [new AdvertenciaLectura("condicion_no_evaluada", "Condición sin evaluar.", "Ejemplo.Uno")]);

        var r = await Crear().AnalizarAsync(Proyecto(dep));

        var codigos = r.Analisis.Dependencias.Single().Advertencias.Select(a => a.Codigo).ToList();
        Assert.Contains("condicion_no_evaluada", codigos);
        Assert.Contains("cache_expirada", codigos);
    }

    [Fact]
    public async Task Las_advertencias_del_proyecto_pasan_al_resultado_y_el_analisis_sin_paquetes_se_guarda_completo()
    {
        var proyecto = new ProyectoLeido("Vacio.csproj", [new FrameworkDeclarado("net8.0", true)], [],
            [new AdvertenciaLectura("sin_paquetes", "El proyecto no declara ningún PackageReference.")]);

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
        Assert.Empty(r.Analisis.Dependencias);
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "sin_paquetes");
        Assert.Equal(1, _repo.IntentosDeGuardado);
        Assert.Equal(0, _nuget.Llamadas);
    }

    // ---------- fallos por paquete (criterio de cierre de E5) ----------

    [Fact]
    public async Task Un_paquete_inexistente_no_elimina_los_diagnosticos_de_los_demas()
    {
        _nuget.Comportamiento = (c, _, _) => Task.FromResult(c.Paquete == "Ejemplo.NoExiste"
            ? ResultadoNuGet<InformacionPaquete>.NoEncontrado("No existe en nuget.org.")
            : ResultadoNuGet<InformacionPaquete>.Correcto(Info(c.Paquete, c.VersionDeclarada), [], 2));
        var proyecto = Proyecto(Dep("Ejemplo.Uno", "2.0.0"), Dep("Ejemplo.NoExiste"), Dep("Ejemplo.Dos", "2.0.0"));

        var r = await Crear().AnalizarAsync(proyecto);

        var estados = r.Analisis.Dependencias.ToDictionary(d => d.Nombre, d => d.Diagnostico.Estado);
        Assert.Equal(EstadoDiagnostico.Actualizada, estados["Ejemplo.Uno"]);
        Assert.Equal(EstadoDiagnostico.NoDeterminada, estados["Ejemplo.NoExiste"]);
        Assert.Equal(EstadoDiagnostico.Actualizada, estados["Ejemplo.Dos"]);
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "analisis_parcial");
        Assert.Single(_repo.Guardados); // se guarda el resultado útil, no hay 502/503 por un paquete
    }

    [Fact]
    public async Task Si_NuGet_cae_para_un_paquete_los_otros_siguen_y_el_que_tiene_cache_vencida_se_sirve_con_su_fecha_original()
    {
        var vieja = Ahora.AddDays(-2);
        await Sembrar("Ejemplo.ConCache", "1.0.0", vieja);
        var proyecto = Proyecto(Dep("Ejemplo.Bien", "2.0.0"), Dep("Ejemplo.CaeSinCache"), Dep("Ejemplo.ConCache", "1.0.0"));
        _nuget.Comportamiento = (c, _, _) => Task.FromResult(c.Paquete is "Ejemplo.CaeSinCache" or "Ejemplo.ConCache"
            ? NoDisponible()
            : ResultadoNuGet<InformacionPaquete>.Correcto(Info(c.Paquete, c.VersionDeclarada), [], 2));

        var r = await Crear().AnalizarAsync(proyecto);

        var d = r.Analisis.Dependencias.ToDictionary(x => x.Nombre);
        Assert.Equal(OrigenDato.Nuget, d["Ejemplo.Bien"].Diagnostico.Origen);
        Assert.Equal(OrigenDato.NoDeterminado, d["Ejemplo.CaeSinCache"].Diagnostico.Origen);
        Assert.Null(d["Ejemplo.CaeSinCache"].Diagnostico.FechaConsulta);
        Assert.Equal(OrigenDato.CacheExpirada, d["Ejemplo.ConCache"].Diagnostico.Origen);
        Assert.Equal(vieja, d["Ejemplo.ConCache"].Diagnostico.FechaConsulta);
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado); // por la dependencia sin datos; la caché vencida por sí sola no lo habría vuelto parcial
    }

    [Fact]
    public async Task La_cache_vencida_por_si_sola_no_vuelve_parcial_el_analisis()
    {
        await Sembrar("Ejemplo.Uno", "1.0.0", Ahora.AddDays(-2));
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(NoDisponible());

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0")));

        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
        Assert.Equal(OrigenDato.CacheExpirada, r.Analisis.Dependencias.Single().Diagnostico.Origen);
    }

    [Fact]
    public async Task Un_error_inesperado_del_servicio_de_metadatos_afecta_solo_a_esa_dependencia()
    {
        _nuget.Comportamiento = (c, _, _) => c.Paquete == "Ejemplo.Rompe"
            ? throw new InvalidOperationException("error inesperado")
            : Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(Info(c.Paquete, c.VersionDeclarada), [], 2));

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "2.0.0"), Dep("Ejemplo.Rompe"), Dep("Ejemplo.Dos", "2.0.0")));

        var d = r.Analisis.Dependencias.ToDictionary(x => x.Nombre);
        Assert.Equal(EstadoDiagnostico.Actualizada, d["Ejemplo.Uno"].Diagnostico.Estado);
        Assert.Equal(EstadoDiagnostico.NoDeterminada, d["Ejemplo.Rompe"].Diagnostico.Estado);
        Assert.Contains(d["Ejemplo.Rompe"].Advertencias, a => a.Codigo == "error_interno");
        Assert.Equal(EstadoDiagnostico.Actualizada, d["Ejemplo.Dos"].Diagnostico.Estado);
        Assert.Single(_repo.Guardados);
    }

    [Fact]
    public async Task Un_error_del_motor_de_diagnostico_en_una_dependencia_es_local()
    {
        _motor.Anular = c => c.Dependencia.Nombre == "Ejemplo.Rompe" ? throw new InvalidOperationException("bug del motor") : null;

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "2.0.0"), Dep("Ejemplo.Rompe", "2.0.0")));

        var rompe = r.Analisis.Dependencias.Single(x => x.Nombre == "Ejemplo.Rompe");
        Assert.Equal(EstadoDiagnostico.NoDeterminada, rompe.Diagnostico.Estado);
        Assert.Contains("referencia", rompe.Diagnostico.Motivos);
        Assert.Contains(rompe.Advertencias, a => a.Codigo == "error_interno");
        Assert.Equal(EstadoDiagnostico.Actualizada, r.Analisis.Dependencias.Single(x => x.Nombre == "Ejemplo.Uno").Diagnostico.Estado);
    }

    // ---------- recomendaciones (solo puerto; sustituto de pruebas) ----------

    [Fact]
    public async Task Solo_las_dependencias_deficientes_piden_recomendaciones()
    {
        var proyecto = Proyecto(Dep("Ejemplo.AlDia", "2.0.0"), Dep("Ejemplo.Vieja", "1.0.0"));

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.Equal(["Ejemplo.Vieja"], _recomendador.Paquetes.ToList());
        Assert.Empty(r.Analisis.Dependencias.Single(d => d.Nombre == "Ejemplo.AlDia").Recomendaciones);
        Assert.Single(r.Analisis.Dependencias.Single(d => d.Nombre == "Ejemplo.Vieja").Recomendaciones);
    }

    [Fact]
    public async Task Un_fallo_del_motor_de_recomendacion_conserva_el_diagnostico_con_una_advertencia()
    {
        _recomendador.Falla = true;

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Vieja", "1.0.0")));

        var d = r.Analisis.Dependencias.Single();
        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Diagnostico.Estado);
        Assert.Empty(d.Recomendaciones);
        Assert.Contains(d.Advertencias, a => a.Codigo == "recomendacion_no_disponible");
        Assert.Single(_repo.Guardados);
    }

    // ---------- deduplicación y consultas redundantes ----------

    [Fact]
    public async Task La_misma_dependencia_repetida_con_la_misma_version_se_consulta_una_sola_vez()
    {
        var proyecto = Proyecto(Dep("Ejemplo.Uno", "1.0.0"), Dep("EJEMPLO.UNO", "1.0.0"), Dep("Ejemplo.Uno", "1.0.0", condicion: "'$(X)'=='1'"));

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.Equal(1, _nuget.Llamadas);
        Assert.Equal(3, r.Analisis.Dependencias.Count); // las declaraciones se conservan, no se eliminan
        Assert.Equal(2, r.SolicitudesHttp);             // una sola consulta contada
    }

    [Fact]
    public async Task Versiones_conflictivas_del_mismo_paquete_se_consultan_por_separado_y_se_conservan_ambas()
    {
        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0"), Dep("Ejemplo.Uno", "2.0.0")));

        // Claves distintas (no se fusionan en una sola consulta). Si la primera consulta ya guardó en caché la fila de 2.0.0, la segunda puede salir de la caché:
        // por eso el recuento depende del orden de ejecución (1 o 2), pero nunca se pierde una versión declarada.
        Assert.InRange(_nuget.Llamadas, 1, 2);
        Assert.Equal(["1.0.0", "2.0.0"], r.Analisis.Dependencias.Select(d => d.VersionDeclarada));
    }

    [Fact]
    public async Task Las_versiones_no_exactas_consultan_solo_la_ultima_y_no_se_resuelven()
    {
        var proyecto = Proyecto(Dep("Ejemplo.Rango", "[1.0.0,2.0.0)", TipoVersion.Rango), Dep("Ejemplo.Prop", "$(V)", TipoVersion.Propiedad), Dep("Ejemplo.Sin", null, TipoVersion.Ausente));

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.All(_nuget.Consultas, c => Assert.Null(c.VersionDeclarada));
        Assert.All(r.Analisis.Dependencias, d => Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Diagnostico.Estado));
        Assert.Equal(["[1.0.0,2.0.0)", "$(V)", null], r.Analisis.Dependencias.Select(d => d.VersionDeclarada));
    }

    [Fact]
    public async Task Un_segundo_analisis_de_las_mismas_librerias_sale_de_la_cache_sin_llamar_a_NuGet()
    {
        var proyecto = Proyecto(Dep("Ejemplo.Uno", "2.0.0"), Dep("Ejemplo.Dos", "1.0.0"));
        var orquestador = Crear();

        var primero = await orquestador.AnalizarAsync(proyecto);
        var llamadas = _nuget.Llamadas;
        var segundo = await orquestador.AnalizarAsync(proyecto);

        Assert.Equal(2, llamadas);
        Assert.Equal(llamadas, _nuget.Llamadas); // ninguna consulta nueva
        Assert.All(segundo.Analisis.Dependencias, d => Assert.Equal(OrigenDato.CacheVigente, d.Diagnostico.Origen));
        Assert.Equal(0, segundo.SolicitudesHttp);
        Assert.NotEqual(primero.Analisis.Id, segundo.Analisis.Id); // cada POST es un análisis nuevo
        Assert.Equal(2, _repo.Guardados.Count);
    }

    [Fact]
    public async Task Las_estadisticas_cuentan_los_origenes_y_las_solicitudes()
    {
        await Sembrar("Ejemplo.Cache", "1.0.0", Ahora.AddHours(-1));

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Cache", "1.0.0"), Dep("Ejemplo.Nuevo", "2.0.0")));

        Assert.Equal(1, r.DependenciasPorOrigen[OrigenDato.CacheVigente]);
        Assert.Equal(1, r.DependenciasPorOrigen[OrigenDato.Nuget]);
        Assert.Equal(2, r.SolicitudesHttp);
    }

    // ---------- presupuesto global ----------

    [Fact]
    public async Task Al_agotarse_el_presupuesto_lo_pendiente_queda_no_determinado_el_analisis_es_parcial_y_se_guarda()
    {
        _nuget.Comportamiento = async (c, _, ct) =>
        {
            if (c.Paquete.StartsWith("Ejemplo.Lento")) await Task.Delay(Timeout.Infinite, ct); // nunca responde
            return ResultadoNuGet<InformacionPaquete>.Correcto(Info(c.Paquete, c.VersionDeclarada), [], 2);
        };
        var proyecto = Proyecto(Dep("Ejemplo.Rapido1", "2.0.0"), Dep("Ejemplo.Lento1", "1.0.0"), Dep("Ejemplo.Rapido2", "2.0.0"), Dep("Ejemplo.Lento2", "1.0.0"));
        var orquestador = Crear();

        var tarea = orquestador.AnalizarAsync(proyecto);
        for (var i = 0; i < 200 && _nuget.Llamadas < 4; i++) await Task.Delay(5);
        _tiempo.Advance(TimeSpan.FromSeconds(121)); // se agota el presupuesto de 120 s
        var r = await tarea.WaitAsync(TimeSpan.FromSeconds(10));

        var d = r.Analisis.Dependencias.ToDictionary(x => x.Nombre);
        Assert.Equal(EstadoDiagnostico.Actualizada, d["Ejemplo.Rapido1"].Diagnostico.Estado);    // lo útil se conserva
        Assert.Equal(EstadoDiagnostico.Actualizada, d["Ejemplo.Rapido2"].Diagnostico.Estado);
        Assert.Equal(EstadoDiagnostico.NoDeterminada, d["Ejemplo.Lento1"].Diagnostico.Estado);   // lo pendiente
        Assert.Equal(EstadoDiagnostico.NoDeterminada, d["Ejemplo.Lento2"].Diagnostico.Estado);
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        Assert.True(r.PresupuestoAgotado);
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "presupuesto_agotado");
        Assert.Single(_repo.Guardados);                                                          // el parcial se persiste
    }

    [Fact]
    public async Task Con_presupuesto_agotado_las_dependencias_con_cache_se_resuelven_con_ella_y_no_quedan_sin_datos()
    {
        await Sembrar("Ejemplo.ConCache", "1.0.0", Ahora.AddDays(-2));
        _nuget.Comportamiento = async (c, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return default!; };
        var orquestador = Crear(new OpcionesOrquestacion { PresupuestoAnalisis = TimeSpan.FromSeconds(30) });

        var tarea = orquestador.AnalizarAsync(Proyecto(Dep("Ejemplo.SinCache", "1.0.0"), Dep("Ejemplo.ConCache", "1.0.0")));
        for (var i = 0; i < 200 && _nuget.Llamadas < 2; i++) await Task.Delay(5);
        _tiempo.Advance(TimeSpan.FromSeconds(31));
        var r = await tarea.WaitAsync(TimeSpan.FromSeconds(10));

        var d = r.Analisis.Dependencias.ToDictionary(x => x.Nombre);
        Assert.Equal(OrigenDato.CacheExpirada, d["Ejemplo.ConCache"].Diagnostico.Origen);
        Assert.Equal(Ahora.AddDays(-2), d["Ejemplo.ConCache"].Diagnostico.FechaConsulta);
        Assert.Equal(OrigenDato.NoDeterminado, d["Ejemplo.SinCache"].Diagnostico.Origen);
    }

    [Fact]
    public async Task El_presupuesto_por_defecto_es_de_120_segundos()
    {
        Assert.Equal(TimeSpan.FromSeconds(120), new OpcionesOrquestacion().PresupuestoAnalisis);
        _nuget.Comportamiento = async (c, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return default!; };
        var tarea = Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0")));
        for (var i = 0; i < 200 && _nuget.Llamadas < 1; i++) await Task.Delay(5);

        _tiempo.Advance(TimeSpan.FromSeconds(119));
        await Task.Delay(50);
        Assert.False(tarea.IsCompleted); // a los 119 s todavía espera
        _tiempo.Advance(TimeSpan.FromSeconds(2));

        var r = await tarea.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.PresupuestoAgotado);
    }

    // ---------- cancelación ----------

    [Fact]
    public async Task Una_cancelacion_del_llamador_se_propaga_y_no_se_guarda_nada()
    {
        _nuget.Comportamiento = async (c, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return default!; };
        using var cts = new CancellationTokenSource();

        var tarea = Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0"), Dep("Ejemplo.Dos", "1.0.0")), cts.Token);
        for (var i = 0; i < 200 && _nuget.Llamadas < 2; i++) await Task.Delay(5);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tarea);
        Assert.Equal(0, _repo.IntentosDeGuardado);
    }

    [Fact]
    public async Task Un_token_cancelado_desde_el_inicio_no_consulta_ni_guarda()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "1.0.0")), cts.Token));

        Assert.Equal(0, _nuget.Llamadas);
        Assert.Equal(0, _repo.IntentosDeGuardado);
    }

    // ---------- persistencia ----------

    [Fact]
    public async Task Si_la_base_no_puede_guardar_el_fallo_es_global_y_distinto_de_un_fallo_de_paquete()
    {
        _repo.FallaGuardado = true;

        var ex = await Assert.ThrowsAsync<AlmacenamientoNoDisponibleException>(() => Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "2.0.0"))));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Empty(_repo.Guardados);
        Assert.Equal(1, _repo.IntentosDeGuardado);
    }

    [Fact]
    public async Task La_cache_caida_no_impide_guardar_el_analisis()
    {
        _cache.FallaLectura = true;
        _cache.FallaEscritura = true;

        var r = await Crear().AnalizarAsync(Proyecto(Dep("Ejemplo.Uno", "2.0.0")));

        Assert.Single(_repo.Guardados);
        Assert.Contains(r.Analisis.Dependencias.Single().Advertencias, a => a.Codigo is "cache_no_disponible" or "cache_no_guardada");
    }

    [Fact]
    public async Task El_guardado_ocurre_una_sola_vez_y_despues_de_haber_resuelto_todas_las_dependencias()
    {
        var momentos = new List<string>();
        _motor.Anular = c => { lock (momentos) momentos.Add("diagnostico"); return null; };
        var repo = new RepoQueRegistra(momentos);
        var orquestador = new OrquestadorAnalisis(new LectorCsproj(new OpcionesLectura()),
            new ServicioMetadatos(_nuget, _cache, new InferidorFalso(), _tiempo), _motor, _recomendador, repo, _tiempo);

        await orquestador.AnalizarAsync(Proyecto(Dep("A", "2.0.0"), Dep("B", "2.0.0"), Dep("C", "2.0.0")));

        Assert.Equal(["diagnostico", "diagnostico", "diagnostico", "guardar"], momentos);
    }

    private sealed class RepoQueRegistra(List<string> momentos) : IAnalisisRepositorio
    {
        public Task GuardarAsync(Analisis analisis, CancellationToken ct = default) { lock (momentos) momentos.Add("guardar"); return Task.CompletedTask; }
        public Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Pagina<ResumenAnalisis>> ListarAsync(int numeroPagina, int tamanoPagina, IReadOnlyCollection<Guid>? ids = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // ---------- entrada ----------

    [Theory]
    [InlineData("Mi.txt", "<Project />")]                                                                   // extensión
    [InlineData("Mi.csproj", "esto no es xml")]                                                             // XML roto
    [InlineData("Mi.csproj", "<html />")]                                                                   // no es un proyecto
    [InlineData("Mi.csproj", "<Project><PropertyGroup><TargetFramework>net6.0</TargetFramework></PropertyGroup></Project>")] // fuera de alcance
    public async Task Una_entrada_invalida_no_consulta_NuGet_ni_guarda_nada(string nombre, string xml)
    {
        await Assert.ThrowsAsync<ArchivoInvalidoException>(() => Crear().AnalizarAsync(nombre, new MemoryStream(Encoding.UTF8.GetBytes(xml))));

        Assert.Equal(0, _nuget.Llamadas);
        Assert.Equal(0, _cache.Lecturas);
        Assert.Equal(0, _cache.Escrituras);
        Assert.Equal(0, _repo.IntentosDeGuardado);
    }

    [Fact]
    public async Task Un_archivo_valido_se_lee_con_el_lector_real_y_se_analiza()
    {
        const string xml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Ejemplo.Uno" Version="2.0.0" /><PackageReference Include="Ejemplo.Dos" Version="1.0.0" /></ItemGroup></Project>""";

        var r = await Crear().AnalizarAsync(@"C:\ruta\larga\Mi.csproj", new MemoryStream(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal("Mi.csproj", r.Analisis.NombreArchivo);
        Assert.Equal(["Ejemplo.Uno", "Ejemplo.Dos"], r.Analisis.Dependencias.Select(d => d.Nombre));
        Assert.Equal(["net8.0"], r.Analisis.FrameworksDeclarados);
    }

    // ---------- frameworks ----------

    [Fact]
    public async Task Sin_ningun_framework_resuelto_el_analisis_es_parcial_con_advertencia_y_no_afirma_compatibilidad()
    {
        var proyecto = new ProyectoLeido("Mi.csproj", [new FrameworkDeclarado("$(Objetivo)", null, "variable")], [Dep("Ejemplo.Uno", "2.0.0")],
            [new AdvertenciaLectura("framework_no_resuelto", "Sin framework.")]);

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        Assert.Empty(r.Analisis.FrameworksEvaluados);
        Assert.Equal(["$(Objetivo)"], r.Analisis.FrameworksDeclarados);
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "sin_framework_evaluable");
        Assert.Empty(_nuget.Consultas.Single().FrameworksEvaluados);
    }

    [Fact]
    public async Task Solo_se_evaluan_los_frameworks_en_alcance_y_se_declaran_todos()
    {
        var proyecto = new ProyectoLeido("Mi.csproj",
            [new FrameworkDeclarado("net8.0", true), new FrameworkDeclarado("net48", false, "fuera"), new FrameworkDeclarado("net10.0-windows", true)],
            [Dep("Ejemplo.Uno", "2.0.0")], []);

        var r = await Crear().AnalizarAsync(proyecto);

        Assert.Equal(["net8.0", "net10.0-windows"], r.Analisis.FrameworksEvaluados);
        Assert.Equal(["net8.0", "net48", "net10.0-windows"], r.Analisis.FrameworksDeclarados);
        Assert.Equal(["net8.0", "net10.0-windows"], _nuget.Consultas.Single().FrameworksEvaluados);
    }

    // ---------- paralelismo ----------

    [Fact]
    public async Task El_paralelismo_se_limita_y_el_resultado_conserva_el_orden()
    {
        var deps = Enumerable.Range(0, 12).Select(i => Dep($"Ejemplo.P{i:00}", "2.0.0")).ToArray();
        _motor.Retardo = TimeSpan.FromMilliseconds(15);

        var r = await Crear(new OpcionesOrquestacion { MaxParalelismo = 3 }).AnalizarAsync(Proyecto(deps));

        Assert.True(_motor.MaximoSimultaneo <= 3, $"máximo simultáneo: {_motor.MaximoSimultaneo}");
        Assert.Equal(deps.Select(d => d.Nombre), r.Analisis.Dependencias.Select(d => d.Nombre));
    }
}
