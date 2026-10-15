using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Tests.Nuget;

public class ConversorCacheTests
{
    private static readonly DateTimeOffset Fecha = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class InferidorFalso : IInferidorCompatibilidad
    {
        public CompatibilidadTfm Inferir(string tfm, IReadOnlyList<string> grupos) => new(tfm,
            grupos.Count == 0 ? ResultadoCompatibilidad.NoDeterminada
            : grupos.Any(g => g.Length == 0 || g == tfm) ? ResultadoCompatibilidad.Compatible
            : ResultadoCompatibilidad.Incompatible, "inferida_dependency_groups");
    }

    // El tipo de licencia del constructor es el «recibido de NuGet» de la prueba; cada prueba lo cambia con «with» cuando lo necesita.
    private static VersionPublicada Version(string v, string[]? grupos = null, string? licencia = "MIT", Deprecacion? dep = null, bool prerelease = false, DateTimeOffset? publicada = null) => new(
        v, v, prerelease, true, publicada ?? Fecha.AddMonths(-2), licencia,
        licencia is null ? null : licencia.StartsWith("http") ? TipoLicencia.Url : TipoLicencia.Expresion, dep, grupos ?? ["net8.0"], []);

    private static InformacionPaquete Info(
        VersionPublicada? declarada = null, VersionPublicada? estable = null, string paquete = "Ejemplo.Paquete", long? descargas = 5000,
        bool completa = true, Dictionary<string, VersionPublicada?>? compat = null, EstadoVersionDeclarada? estado = null) => new(
        paquete, estado ?? (declarada is null ? EstadoVersionDeclarada.NoSolicitada : EstadoVersionDeclarada.Publicada), declarada,
        declarada is null ? [] : [new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups")],
        estable, null, compat ?? new Dictionary<string, VersionPublicada?> { ["net8.0"] = estable }, estable?.Publicada, completa, 1, 1, 10, Fecha, descargas, null);

    // ---------- conversión a filas ----------

    [Fact]
    public void Guarda_la_version_declarada_y_la_ultima_estable_como_filas_distintas_una_sola_si_coinciden()
    {
        var distintas = ConversorCache.Convertir(Info(Version("1.0.0"), Version("2.0.0")));
        var iguales = ConversorCache.Convertir(Info(Version("2.0.0"), Version("2.0.0")));

        Assert.Equal(["1.0.0", "2.0.0"], distintas.Metadatos.Select(m => m.Version));
        Assert.Single(iguales.Metadatos);
    }

    [Fact]
    public void Conserva_la_licencia_completa_aunque_sea_una_URL_larga_y_el_reemplazo_declarado()
    {
        var url = "https://example.org/licencias/" + new string('x', 1500);
        var dep = new Deprecacion(["Legacy"], "Obsoleto", "Ejemplo.Moderno", "[2.0.0, )");

        var fila = ConversorCache.Convertir(Info(Version("1.0.0", licencia: url, dep: dep))).Metadatos.Single();

        Assert.Equal(url, fila.Licencia);
        Assert.True(fila.Deprecado);
        Assert.Equal("Ejemplo.Moderno", fila.PaqueteAlterno);
        Assert.Equal("[2.0.0, )", fila.RangoAlterno);
    }

    [Fact]
    public void Sin_deprecacion_deprecado_es_falso_y_el_alterno_es_nulo()
    {
        var fila = ConversorCache.Convertir(Info(Version("1.0.0"))).Metadatos.Single();

        Assert.False(fila.Deprecado);
        Assert.Null(fila.PaqueteAlterno);
        Assert.Null(fila.RangoAlterno);
    }

    [Fact]
    public void AOT_siempre_es_nulo_y_las_descargas_vienen_de_la_busqueda_o_son_nulas_nunca_cero()
    {
        var con = ConversorCache.Convertir(Info(Version("1.0.0"), descargas: 777)).Metadatos.Single();
        var sin = ConversorCache.Convertir(Info(Version("1.0.0"), descargas: null)).Metadatos.Single();

        Assert.Null(con.CompatibleAot);
        Assert.Equal(777L, con.Descargas);
        Assert.Null(sin.Descargas);
    }

    [Fact]
    public void Conserva_el_grupo_para_todos_los_frameworks_en_la_lista_de_frameworks()
    {
        var fila = ConversorCache.Convertir(Info(Version("1.0.0", grupos: ["", "net8.0"]))).Metadatos.Single();

        Assert.Equal(["", "net8.0"], fila.FrameworksObjetivo);
    }

    [Fact]
    public void Un_paquete_sin_grupos_guarda_frameworks_nulos()
    {
        var fila = ConversorCache.Convertir(Info(Version("1.0.0", grupos: []))).Metadatos.Single();

        Assert.Null(fila.FrameworksObjetivo);
    }

    [Fact]
    public void No_guarda_vulnerabilidades_ni_las_pierde_el_resultado_del_analisis()
    {
        var v = Version("1.0.0") with { Vulnerabilidades = [new AvisoVulnerabilidad("3", "https://example.org/a")] };

        var fila = ConversorCache.Convertir(Info(v)).Metadatos.Single();

        Assert.DoesNotContain("Vulnerabilidades", typeof(MetadatoLibreria).GetProperties().Select(p => p.Name)); // no hay dónde persistirlas
        Assert.Equal("1.0.0", fila.Version);
    }

    // ---------- límites por campo: no se recorta en silencio ni se descarta el análisis ----------

    [Fact]
    public void Un_identificador_demasiado_largo_no_se_cachea_y_deja_una_advertencia()
    {
        var r = ConversorCache.Convertir(Info(Version("1.0.0"), Version("1.0.0"), paquete: new string('p', 201)));

        Assert.Empty(r.Metadatos);
        Assert.Null(r.Seleccion);
        Assert.Contains(r.Advertencias, a => a.Codigo == "identificador_demasiado_largo_para_cache");
    }

    [Fact]
    public void Una_version_normalizada_demasiado_larga_no_se_cachea_pero_las_demas_si()
    {
        var larga = "1.0.0-" + new string('b', 120);

        var r = ConversorCache.Convertir(Info(Version(larga), Version("2.0.0")));

        Assert.Equal(["2.0.0"], r.Metadatos.Select(m => m.Version));
        Assert.Contains(r.Advertencias, a => a.Codigo == "version_demasiado_larga_para_cache");
    }

    [Fact]
    public void Un_alterno_demasiado_largo_se_omite_junto_con_su_rango_con_advertencia_y_se_conserva_el_resto()
    {
        var dep = new Deprecacion([], null, new string('a', 201), "[1.0.0, )");

        var r = ConversorCache.Convertir(Info(Version("1.0.0", dep: dep)));

        var fila = r.Metadatos.Single();
        Assert.True(fila.Deprecado);          // sigue deprecado
        Assert.Null(fila.PaqueteAlterno);
        Assert.Null(fila.RangoAlterno);
        Assert.Equal("MIT", fila.Licencia);
        Assert.Contains(r.Advertencias, a => a.Codigo == "alterno_demasiado_largo_para_cache");
    }

    [Fact]
    public void Un_rango_alterno_demasiado_largo_se_omite_con_advertencia_y_se_conserva_el_alterno()
    {
        var dep = new Deprecacion([], null, "Ejemplo.Moderno", new string('r', 101));

        var r = ConversorCache.Convertir(Info(Version("1.0.0", dep: dep)));

        Assert.Equal("Ejemplo.Moderno", r.Metadatos.Single().PaqueteAlterno);
        Assert.Null(r.Metadatos.Single().RangoAlterno);
        Assert.Contains(r.Advertencias, a => a.Codigo == "rango_alterno_demasiado_largo_para_cache");
    }

    [Fact]
    public void Todo_lo_que_se_convierte_cumple_ValidadorLimites()
    {
        var r = ConversorCache.Convertir(Info(Version("1.0.0", dep: new Deprecacion([], null, new string('a', 200), new string('r', 100))), Version("2.0.0")));

        Assert.All(r.Metadatos, ValidadorLimites.Validar);
        ValidadorLimites.Validar(r.Seleccion!);
    }

    // ---------- selección ----------

    [Fact]
    public void La_seleccion_guarda_la_ultima_estable_la_compatible_por_TFM_y_si_es_completa()
    {
        var compat = new Dictionary<string, VersionPublicada?> { ["net8.0"] = Version("2.0.0"), ["net10.0"] = Version("3.0.0"), ["net48"] = null };

        var s = ConversorCache.Convertir(Info(null, Version("3.0.0"), compat: compat, completa: false)).Seleccion!;

        Assert.Equal("3.0.0", s.UltimaVersionEstable);
        Assert.Equal(new Dictionary<string, string> { ["net8.0"] = "2.0.0", ["net10.0"] = "3.0.0" }, s.UltimaCompatiblePorTfm); // net48 sin compatible: no se inventa
        Assert.False(s.SeleccionCompleta);
    }

    // ---------- reconstrucción desde la caché ----------

    private static InformacionPaquete Reconstruir(ParaCache cache, bool solicitada = true, params string[] tfms) =>
        ConversorCache.Reconstruir("Ejemplo.Paquete",
            cache.Metadatos.FirstOrDefault(m => m.Version == "1.0.0"),
            cache.Metadatos.FirstOrDefault(m => m.Version == "2.0.0"),
            cache.Seleccion, tfms.Length == 0 ? ["net8.0"] : tfms, solicitada, new InferidorFalso());

    [Fact]
    public void La_reconstruccion_devuelve_licencia_reemplazo_declarado_y_compatibilidad_iguales_a_los_originales()
    {
        var dep = new Deprecacion(["Legacy"], "Reemplazado", "Ejemplo.Moderno", "[2.0.0, )");
        var original = Info(Version("1.0.0", grupos: ["", "net6.0"], licencia: "https://example.org/l", dep: dep), Version("2.0.0", licencia: "MIT"),
            compat: new Dictionary<string, VersionPublicada?> { ["net8.0"] = Version("2.0.0"), ["net48"] = null });

        var rec = Reconstruir(ConversorCache.Convertir(original), true, "net8.0", "net48");

        Assert.Equal("https://example.org/l", rec.Declarada!.Licencia);
        Assert.Equal(TipoLicencia.Url, rec.Declarada.TipoLicencia);
        Assert.Equal("Ejemplo.Moderno", rec.Declarada.Deprecacion!.PaqueteAlterno);
        Assert.Equal("[2.0.0, )", rec.Declarada.Deprecacion.RangoVersionAlterno);
        Assert.Equal(["", "net6.0"], rec.Declarada.GruposFramework);
        Assert.Equal(EstadoVersionDeclarada.Publicada, rec.EstadoDeclarada);
        Assert.Equal("2.0.0", rec.UltimaEstable!.Version);
        Assert.Equal("2.0.0", rec.UltimaEstableCompatiblePorTfm["net8.0"]!.Version);
        Assert.Null(rec.UltimaEstableCompatiblePorTfm["net48"]);
        Assert.Equal(5000L, rec.Descargas);
        Assert.True(rec.SeleccionCompleta);
        Assert.Equal(2, rec.CompatibilidadDeclarada.Count);
    }

    [Theory]
    [InlineData("MIT", TipoLicencia.Expresion)]
    [InlineData("https://example.org/licencia", TipoLicencia.Url)]
    [InlineData("LICENSE.txt", TipoLicencia.Archivo)]
    [InlineData("https://example.org/esto-NuGet-lo-declaro-como-expresion", TipoLicencia.Expresion)] // el tipo recibido manda aunque el texto parezca una URL
    public void El_tipo_de_licencia_recibido_se_conserva_en_la_cache_y_se_reconstruye_igual(string texto, TipoLicencia tipo)
    {
        var original = Info(Version("1.0.0", licencia: texto) with { TipoLicencia = tipo }, Version("2.0.0"));

        var rec = Reconstruir(ConversorCache.Convertir(original));

        Assert.Equal(texto, rec.Declarada!.Licencia);
        Assert.Equal(tipo, rec.Declarada.TipoLicencia);
    }

    [Theory]
    [InlineData("https://example.org/licencia")]
    [InlineData("MIT")]
    public void Si_el_tipo_no_se_recibio_queda_desconocido_y_no_se_deduce_del_texto(string texto)
    {
        var original = Info(Version("1.0.0", licencia: texto) with { TipoLicencia = null }, Version("2.0.0"));

        var rec = Reconstruir(ConversorCache.Convertir(original));

        Assert.Equal(texto, rec.Declarada!.Licencia);
        Assert.Null(rec.Declarada.TipoLicencia);
    }

    [Fact]
    public void Una_fila_antigua_con_licencia_pero_sin_tipo_se_reconstruye_con_tipo_desconocido()
    {
        var fila = new MetadatoLibreria("Ejemplo.Paquete", "1.0.0", Fecha, null, "https://example.org/l", ["net8.0"], null, false, Fecha); // sin TipoLicencia: anterior a la migración

        var rec = ConversorCache.Reconstruir("Ejemplo.Paquete", fila, null, null, ["net8.0"], true, new InferidorFalso());

        Assert.Equal("https://example.org/l", rec.Declarada!.Licencia);
        Assert.Null(rec.Declarada.TipoLicencia);
    }

    [Fact]
    public void Las_razones_y_el_mensaje_de_la_deprecacion_se_conservan_para_explicar_el_diagnostico()
    {
        var dep = new Deprecacion(["Legacy", "CriticalBugs"], "Este paquete ya no se mantiene; usa Ejemplo.Moderno.", "Ejemplo.Moderno", "[2.0.0, )");
        var original = Info(Version("1.0.0", dep: dep), Version("2.0.0"));

        var paraCache = ConversorCache.Convertir(original);
        var rec = Reconstruir(paraCache);

        var fila = paraCache.Metadatos.Single(m => m.Version == "1.0.0");
        Assert.Equal(["Legacy", "CriticalBugs"], fila.DeprecacionRazones);
        Assert.Equal("Este paquete ya no se mantiene; usa Ejemplo.Moderno.", fila.DeprecacionMensaje);
        Assert.Equal(["Legacy", "CriticalBugs"], rec.Declarada!.Deprecacion!.Razones);
        Assert.Equal("Este paquete ya no se mantiene; usa Ejemplo.Moderno.", rec.Declarada.Deprecacion.Mensaje);
        Assert.Equal("Ejemplo.Moderno", rec.Declarada.Deprecacion.PaqueteAlterno);
    }

    [Fact]
    public void Un_paquete_sin_deprecacion_no_guarda_razones_ni_mensaje()
    {
        var fila = ConversorCache.Convertir(Info(Version("1.0.0"), Version("2.0.0"))).Metadatos.First();

        Assert.Null(fila.DeprecacionRazones);
        Assert.Null(fila.DeprecacionMensaje);
    }

    [Fact]
    public void Una_deprecacion_sin_mensaje_ni_razones_no_las_inventa()
    {
        var rec = Reconstruir(ConversorCache.Convertir(Info(Version("1.0.0", dep: new Deprecacion([], null, null, null)), Version("2.0.0"))));

        Assert.Empty(rec.Declarada!.Deprecacion!.Razones);
        Assert.Null(rec.Declarada.Deprecacion.Mensaje);
    }

    [Fact]
    public void Una_version_no_deprecada_se_reconstruye_sin_deprecacion_y_una_deprecada_sin_alterno_tampoco_inventa_uno()
    {
        var sin = Reconstruir(ConversorCache.Convertir(Info(Version("1.0.0"), Version("2.0.0"))));
        var depSinAlterno = Reconstruir(ConversorCache.Convertir(Info(Version("1.0.0", dep: new Deprecacion(["Legacy"], null, null, null)), Version("2.0.0"))));

        Assert.Null(sin.Declarada!.Deprecacion);
        Assert.NotNull(depSinAlterno.Declarada!.Deprecacion);
        Assert.Null(depSinAlterno.Declarada.Deprecacion!.PaqueteAlterno);
    }

    [Fact]
    public void Sin_descargas_en_la_cache_la_reconstruccion_deja_null_con_explicacion_nunca_cero()
    {
        var rec = Reconstruir(ConversorCache.Convertir(Info(Version("1.0.0"), Version("2.0.0"), descargas: null)));

        Assert.Null(rec.Descargas);
        Assert.Contains("no se rellena con 0", rec.DescargasNota);
    }

    [Fact]
    public void Sin_fila_para_la_declarada_el_estado_es_no_determinado_y_sin_version_solicitada_es_no_solicitada()
    {
        var cache = ConversorCache.Convertir(Info(null, Version("2.0.0")));

        Assert.Equal(EstadoVersionDeclarada.NoDeterminada, Reconstruir(cache, true).EstadoDeclarada);
        Assert.Equal(EstadoVersionDeclarada.NoSolicitada, Reconstruir(cache, false).EstadoDeclarada);
        Assert.Empty(Reconstruir(cache, true).CompatibilidadDeclarada);
    }

    [Fact]
    public void Sin_datos_en_la_cache_no_se_puede_reconstruir()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ConversorCache.Reconstruir("X", null, null, null, ["net8.0"], true, new InferidorFalso()));
    }

    [Fact]
    public void Una_seleccion_incompleta_se_conserva_como_incompleta_tras_la_cache()
    {
        var rec = Reconstruir(ConversorCache.Convertir(Info(Version("1.0.0"), Version("2.0.0"), completa: false)));

        Assert.False(rec.SeleccionCompleta);
    }

    [Fact]
    public void La_fecha_de_los_datos_reconstruidos_es_la_mas_antigua_de_las_filas_usadas()
    {
        var antigua = Fecha.AddDays(-3);
        var cache = new ParaCache(
            [new MetadatoLibreria("Ejemplo.Paquete", "1.0.0", null, null, "MIT", ["net8.0"], null, false, antigua)],
            new SeleccionPaquete("Ejemplo.Paquete", "1.0.0", new Dictionary<string, string>(), true, Fecha), []);

        var rec = ConversorCache.Reconstruir("Ejemplo.Paquete", cache.Metadatos[0], null, cache.Seleccion, ["net8.0"], true, new InferidorFalso());

        Assert.Equal(antigua, rec.FechaConsulta);
    }
}

public class PresupuestoTiempoTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Restante_disminuye_con_el_tiempo_y_nunca_es_negativo()
    {
        var tiempo = new FakeTimeProvider(Inicio);
        using var p = new PresupuestoTiempo(TimeSpan.FromSeconds(120), tiempo);

        Assert.Equal(TimeSpan.FromSeconds(120), p.Restante);
        tiempo.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(20), p.Restante);
        Assert.False(p.Agotado);
        tiempo.Advance(TimeSpan.FromSeconds(500));
        Assert.Equal(TimeSpan.Zero, p.Restante);
        Assert.True(p.Agotado);
    }

    [Fact]
    public void Al_agotarse_cancela_su_token()
    {
        var tiempo = new FakeTimeProvider(Inicio);
        using var p = new PresupuestoTiempo(TimeSpan.FromSeconds(5), tiempo);
        Assert.False(p.Token.IsCancellationRequested);

        tiempo.Advance(TimeSpan.FromSeconds(6));

        Assert.True(p.Token.IsCancellationRequested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Un_total_no_positivo_se_rechaza(int segundos) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PresupuestoTiempo(TimeSpan.FromSeconds(segundos)));
}
