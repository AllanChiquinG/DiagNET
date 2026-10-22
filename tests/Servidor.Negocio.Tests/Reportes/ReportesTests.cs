using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Reportes;

namespace Servidor.Negocio.Tests.Reportes;

/// <summary>Consolidación del snapshot en el modelo del reporte (E10). Todo con fixtures identificados («Ejemplo.*»); ninguna consulta a NuGet.</summary>
public class ConsolidadorReporteTests
{
    private static readonly DateTimeOffset Fecha = new(2026, 10, 5, 15, 42, 0, TimeSpan.FromHours(-5));
    private static readonly DateTimeOffset Generado = new(2026, 10, 21, 9, 30, 0, TimeSpan.Zero);

    private static IndicadoresLibreria Indicadores(long? descargas = 1000, Deprecacion? dep = null) =>
        new(new Indicador<DateTimeOffset?>(Disponibilidad.Conocido, Fecha),
            new Indicador<string?>(Disponibilidad.Conocido, "2.0.0"), new Indicador<string?>(Disponibilidad.Conocido, "2.0.0"), true,
            new Indicador<int?>(Disponibilidad.Conocido, 5),
            descargas is null ? new Indicador<long?>(Disponibilidad.NoDeterminado, null, "NuGet no informó descargas.") : new Indicador<long?>(Disponibilidad.Conocido, descargas),
            new Indicador<Licencia?>(Disponibilidad.Conocido, new Licencia(null, "LicenciaPropia")),
            new Indicador<bool?>(Disponibilidad.Conocido, false), dep,
            [new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups")],
            new Indicador<bool?>(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));

    private static Diagnostico Diag(EstadoDiagnostico e, string motivos = "Motivo.", IReadOnlyList<Hallazgo>? h = null, OrigenDato o = OrigenDato.Nuget, IndicadoresLibreria? i = null) =>
        new(e, motivos, null, h ?? [], [new EvaluacionTfm("net8.0", e)], o, o == OrigenDato.NoDeterminado ? null : Fecha, o == OrigenDato.NoDeterminado ? null : i ?? Indicadores());

    private static DependenciaAnalizada Dep(string n, EstadoDiagnostico e, Diagnostico? d = null, IReadOnlyList<Recomendacion>? r = null, IReadOnlyList<Advertencia>? a = null) =>
        new(n, "1.0.0", TipoVersion.Exacta, null, a ?? [], d ?? Diag(e), r ?? []);

    private static Analisis Analisis(IReadOnlyList<DependenciaAnalizada> deps, EstadoAnalisis estado = EstadoAnalisis.Completo, IReadOnlyList<Advertencia>? adv = null, string nombre = "Mi.csproj") =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), nombre, ["net8.0", "net10.0"], ["net8.0"], Fecha, estado, deps, adv ?? [], "e6-1.1|ant=24m|agr=pes+e7-1.0");

    [Fact]
    public void Ordena_las_filas_por_gravedad_igual_que_la_pantalla_y_calcula_el_resumen_desde_las_dependencias()
    {
        var a = Analisis([
            Dep("Ejemplo.Ok", EstadoDiagnostico.Actualizada), Dep("Ejemplo.Nd", EstadoDiagnostico.NoDeterminada), Dep("Ejemplo.Viejo", EstadoDiagnostico.Desactualizada),
            Dep("Ejemplo.Eol", EstadoDiagnostico.SinSoporteActivo), Dep("Ejemplo.Inc", EstadoDiagnostico.Incompatible), Dep("Ejemplo.Ok2", EstadoDiagnostico.Actualizada)]);

        var m = ConsolidadorReporte.Consolidar(a, 90, Generado);

        Assert.Equal(["Ejemplo.Inc", "Ejemplo.Eol", "Ejemplo.Viejo", "Ejemplo.Nd", "Ejemplo.Ok", "Ejemplo.Ok2"], m.Filas.Select(f => f.Nombre));
        Assert.Equal(m.Filas.Select(f => f.Nombre), m.Detalles.Select(d => d.Nombre));
        Assert.Equal(new ResumenReporte(6, 2, 1, 1, 1, 1), m.Resumen);
    }

    [Fact]
    public void Identificacion_con_frameworks_fechas_en_UTC_estado_y_version_de_reglas()
    {
        var m = ConsolidadorReporte.Consolidar(Analisis([], EstadoAnalisis.Parcial, nombre: "Aplicación.csproj"), 45, Generado);

        Assert.Equal("Aplicación.csproj", m.Encabezado.NombreArchivo);
        Assert.Equal(["net8.0", "net10.0"], m.Encabezado.FrameworksDeclarados);
        Assert.Equal(["net8.0"], m.Encabezado.FrameworksEvaluados);
        Assert.Equal("5 oct 2026, 20:42 UTC", m.Encabezado.FechaAnalisis);        // 15:42 en UTC-5 = 20:42 UTC
        Assert.Equal("21 oct 2026, 09:30 UTC", m.Encabezado.FechaGeneracion);
        Assert.Equal("Parcial", m.Encabezado.EstadoAnalisis);
        Assert.Equal("e6-1.1|ant=24m|agr=pes+e7-1.0", m.Encabezado.VersionReglas);
        Assert.Equal(45, m.Encabezado.RetencionDias);
        Assert.True(m.DiagnosticoParcial);
    }

    [Fact]
    public void Separa_los_avisos_del_analisis_de_las_recomendaciones_incompletas()
    {
        var m = ConsolidadorReporte.Consolidar(Analisis([], EstadoAnalisis.Completo, [
            new Advertencia("framework_fuera_de_alcance_omitido", "Aviso de frameworks."),
            new Advertencia(ConsolidadorReporte.CodigoRecomendacionesIncompletas, "Faltan recomendaciones.")]), 90, Generado);

        Assert.False(m.DiagnosticoParcial);                       // un análisis COMPLETO puede tener recomendaciones incompletas
        Assert.Equal(["Aviso de frameworks."], m.AvisosDelAnalisis);
        Assert.Equal(["Faltan recomendaciones."], m.AvisosDeRecomendaciones);
    }

    [Fact]
    public void Usa_los_textos_aprobados_de_E7_para_etiquetas_y_cambios_de_codigo()
    {
        Recomendacion R(TipoRecomendacion t, bool cambios) => new(t, "Ejemplo.X", t == TipoRecomendacion.CandidatoParaRevision ? null : "2.0.0", "Justificación.", [new Evidencia("clave_larga", "v")], ["Limitación."], cambios);
        var a = Analisis([Dep("Ejemplo.A", EstadoDiagnostico.Desactualizada, r: [R(TipoRecomendacion.Alternativa, true), R(TipoRecomendacion.CandidatoParaRevision, false), R(TipoRecomendacion.ActualizarVersion, false)])]);

        var d = ConsolidadorReporte.Consolidar(a, 90, Generado).Detalles.Single();

        Assert.Equal(["Alternativa sugerida por el autor", "Candidato para revisión (relación funcional no demostrada)", "Actualización del mismo paquete"], d.Recomendaciones.Select(r => r.Etiqueta));
        Assert.Equal(["Puede requerir cambios de código.", "Cambios de código no determinados.", "Cambios de código no determinados."], d.Recomendaciones.Select(r => r.CambiosDeCodigo));
        Assert.Equal("sin versión comprobada", d.Recomendaciones[1].VersionComprobada);
        Assert.Equal("clave larga", d.Recomendaciones[0].Evidencia.Single().Titulo);
        var fila = ConsolidadorReporte.Consolidar(a, 90, Generado).Filas.Single();
        Assert.Equal("Alternativa sugerida por el autor: Ejemplo.X 2.0.0 (+2)", fila.Recomendacion);
    }

    [Fact]
    public void Una_dependencia_sin_datos_no_inventa_indicadores_ni_fecha_y_marca_AOT_como_no_evaluado()
    {
        var a = Analisis([Dep("Ejemplo.Sin", EstadoDiagnostico.NoDeterminada, Diag(EstadoDiagnostico.NoDeterminada, o: OrigenDato.NoDeterminado))]);

        var m = ConsolidadorReporte.Consolidar(a, 90, Generado);
        var d = m.Detalles.Single();

        Assert.False(d.TieneIndicadores);
        Assert.Equal("Sin datos de NuGet", d.Origen);
        Assert.Equal(["Origen de los datos", "Compatibilidad con AOT"], d.Indicadores.Select(i => i.Titulo));
        Assert.All(d.Indicadores, i => Assert.False(i.Conocido));
        Assert.Equal("No evaluado", d.Indicadores[1].Valor);
        Assert.Equal("—", m.Filas.Single().UltimaVersion);
    }

    [Fact]
    public void Un_indicador_no_determinado_nunca_se_muestra_como_cero_y_un_cero_conocido_si()
    {
        var sinDato = ConsolidadorReporte.Consolidar(Analisis([Dep("Ejemplo.A", EstadoDiagnostico.Actualizada, Diag(EstadoDiagnostico.Actualizada, i: Indicadores(descargas: null)))]), 90, Generado)
            .Detalles.Single().Indicadores.Single(i => i.Titulo == "Descargas");
        var cero = ConsolidadorReporte.Consolidar(Analisis([Dep("Ejemplo.B", EstadoDiagnostico.Actualizada, Diag(EstadoDiagnostico.Actualizada, i: Indicadores(descargas: 0)))]), 90, Generado)
            .Detalles.Single().Indicadores.Single(i => i.Titulo == "Descargas");

        Assert.Equal(("No determinado", false), (sinDato.Valor, sinDato.Conocido));
        Assert.Equal(("0", true), (cero.Valor, cero.Conocido));
    }

    [Fact]
    public void Las_copias_guardadas_conservan_su_fecha_original_y_la_evidencia_incompleta_se_distingue()
    {
        var falta = new Hallazgo("regla", "falta", GravedadHallazgo.Aviso, "Falta evidencia.", [new Evidencia("revisadas", "1 de 3")], true);
        var grave = new Hallazgo("regla", "grave", GravedadHallazgo.Grave, "Problema.", []);
        var info = new Hallazgo("regla", "info", GravedadHallazgo.Informativo, "Dato.", []);
        var a = Analisis([Dep("Ejemplo.C", EstadoDiagnostico.Incompatible, Diag(EstadoDiagnostico.Incompatible, "Incompatible. Motivos: 1.", [grave, falta, info], OrigenDato.CacheExpirada))]);

        var m = ConsolidadorReporte.Consolidar(a, 90, Generado);
        var d = m.Detalles.Single();

        Assert.Equal("Copia guardada vencida · 5 oct 2026, 20:42 UTC", m.Filas.Single().Origen);
        Assert.Equal(["Evidencia incompleta"], m.Filas.Single().Etiquetas);
        Assert.Equal("Incompatible.", d.Titulo);
        Assert.Equal(["Problema."], d.Motivos.Select(x => x.Mensaje));
        Assert.Equal(["Falta evidencia."], d.EvidenciaFaltante.Select(x => x.Mensaje));
        Assert.Equal("revisadas", d.EvidenciaFaltante.Single().Evidencia.Single().Titulo);
        Assert.Equal(["Dato."], d.Informativos.Select(x => x.Mensaje));
    }

    [Fact]
    public void Las_notas_de_recomendacion_se_separan_de_los_avisos_de_la_libreria()
    {
        var a = Analisis([Dep("Ejemplo.D", EstadoDiagnostico.Actualizada, a: [
            new Advertencia("sin_recomendaciones", "No se encontraron recomendaciones."), new Advertencia("version_no_resuelta", "La versión no se pudo resolver.")])]);

        var d = ConsolidadorReporte.Consolidar(a, 90, Generado).Detalles.Single();

        Assert.Equal(["No se encontraron recomendaciones."], d.NotasRecomendacion);
        Assert.Equal(["La versión no se pudo resolver."], d.Avisos);
    }

    [Fact]
    public void Las_limitaciones_generales_incluyen_inferencia_AOT_alternativas_candidatos_y_cambios_de_codigo()
    {
        var texto = string.Join(" ", ConsolidadorReporte.Consolidar(Analisis([]), 90, Generado).Limitaciones);

        Assert.Contains("INFERIDA", texto);
        Assert.Contains("AOT no se evalúa", texto);
        Assert.Contains("Alternativa sugerida por el autor", texto);
        Assert.Contains("Candidato para revisión", texto);
        Assert.Contains("Cambios de código no determinados", texto);
        Assert.Contains("No consulta NuGet ni recalcula", texto);
    }

    [Theory]
    [InlineData("Mi Tienda.Api.csproj", "DiagNET_Mi_Tienda.Api_20261005.pdf")]
    [InlineData(@"C:\ruta\secreta\Proyecto.csproj", "DiagNET_Proyecto_20261005.pdf")]
    [InlineData("../../etc/pas swd.csproj", "DiagNET_pas_swd_20261005.pdf")]
    [InlineData("Ñandú-Gestión.csproj", "DiagNET_Nandu-Gestion_20261005.pdf")]
    [InlineData("日本語.csproj", "DiagNET_analisis_20261005.pdf")]
    [InlineData(".csproj", "DiagNET_analisis_20261005.pdf")]
    public void El_nombre_del_archivo_descargado_es_ASCII_seguro_y_sin_rutas(string entrada, string esperado)
    {
        var nombre = TextosReporte.NombreArchivoPdf(entrada, new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(esperado, nombre);
    }

    [Fact]
    public void El_nombre_del_archivo_descargado_tiene_una_longitud_acotada()
    {
        var nombre = TextosReporte.NombreArchivoPdf(new string('a', 300) + ".csproj", Fecha);

        Assert.True(nombre.Length <= 90, nombre);
        Assert.EndsWith(".pdf", nombre);
    }
}

public class ServicioReportesTests
{
    private static readonly DateTimeOffset Fecha = new(2026, 10, 5, 15, 42, 0, TimeSpan.Zero);

    private static Analisis Muestra() => new(Guid.NewGuid(), "Mi.csproj", ["net8.0"], ["net8.0"], Fecha, EstadoAnalisis.Completo, [], [], "e6-1.1|ant=24m|agr=pes+e7-1.0");

    private sealed class RepoFalso(Analisis? analisis, Exception? falla = null) : IAnalisisRepositorio
    {
        public int Lecturas { get; private set; }
        public Task GuardarAsync(Analisis a, CancellationToken ct = default) => throw new InvalidOperationException("el reporte no puede guardar");
        public Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default)
        {
            Lecturas++;
            if (falla is not null) throw falla;
            return Task.FromResult(analisis);
        }

        public Task<Pagina<ResumenAnalisis>> ListarAsync(int p, int t, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw new InvalidOperationException();
    }

    private sealed class GeneradorFalso(Func<ModeloReporte, byte[]>? f = null) : IGeneradorPdf
    {
        public List<ModeloReporte> Modelos { get; } = [];
        public byte[] Generar(ModeloReporte m)
        {
            Modelos.Add(m);
            return f?.Invoke(m) ?? [1, 2, 3];
        }
    }

    [Fact]
    public async Task Lee_el_snapshot_una_vez_lo_consolida_y_genera_sin_tocar_nada_mas()
    {
        var a = Muestra();
        var repo = new RepoFalso(a);
        var generador = new GeneradorFalso();
        var reloj = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero));

        var r = await new ServicioReportes(repo, generador, reloj).GenerarAsync(a.Id, 90);

        Assert.NotNull(r);
        Assert.Equal([1, 2, 3], r!.Contenido);
        Assert.Equal("DiagNET_Mi_20261005.pdf", r.NombreArchivo);
        Assert.Equal(1, repo.Lecturas);
        var modelo = Assert.Single(generador.Modelos);
        Assert.Equal(a.Id, modelo.Encabezado.IdAnalisis);
        Assert.Equal("21 oct 2026, 09:30 UTC", modelo.Encabezado.FechaGeneracion);
    }

    [Fact]
    public async Task Un_analisis_inexistente_devuelve_null_y_no_genera()
    {
        var generador = new GeneradorFalso();

        var r = await new ServicioReportes(new RepoFalso(null), generador, TimeProvider.System).GenerarAsync(Guid.NewGuid(), 90);

        Assert.Null(r);
        Assert.Empty(generador.Modelos);
    }

    [Fact]
    public async Task Un_fallo_del_generador_se_distingue_de_un_fallo_de_almacenamiento()
    {
        var a = Muestra();
        var roto = new ServicioReportes(new RepoFalso(a), new GeneradorFalso(_ => throw new InvalidOperationException("dibujo")), TimeProvider.System);
        var sinBase = new ServicioReportes(new RepoFalso(null, new TimeoutException("base")), new GeneradorFalso(), TimeProvider.System);

        var porGenerador = await Assert.ThrowsAsync<ReporteNoGeneradoException>(() => roto.GenerarAsync(a.Id, 90));
        await Assert.ThrowsAsync<TimeoutException>(() => sinBase.GenerarAsync(a.Id, 90));

        Assert.IsType<InvalidOperationException>(porGenerador.InnerException);
    }

    [Fact]
    public async Task La_cancelacion_no_se_convierte_en_fallo_de_generacion()
    {
        var a = Muestra();
        var servicio = new ServicioReportes(new RepoFalso(a), new GeneradorFalso(_ => throw new OperationCanceledException()), TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(() => servicio.GenerarAsync(a.Id, 90));
    }

    [Fact]
    public void El_servicio_de_reportes_solo_depende_del_repositorio_del_generador_y_del_reloj_no_de_NuGet_ni_de_los_motores()
    {
        var parametros = typeof(ServicioReportes).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal([typeof(IAnalisisRepositorio), typeof(IGeneradorPdf), typeof(TimeProvider)], parametros);
    }
}
