using Microsoft.Extensions.Time.Testing;
using NuGet.Versioning;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.Tests;

public class InferidorCompatibilidadTests
{
    private readonly InferidorCompatibilidad _inferidor = new();

    private ResultadoCompatibilidad Inferir(string tfm, params string[] grupos) => _inferidor.Inferir(tfm, grupos).Resultado;

    // Tabla del diseño de integración (sección 4.3) para proyectos .NET 8 y .NET 10.
    [Theory]
    [InlineData("net8.0", new[] { "net8.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net10.0", new[] { "net8.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net8.0", new[] { "net6.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net10.0", new[] { "net6.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net8.0", new[] { "netstandard2.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net10.0", new[] { "netstandard2.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net8.0", new[] { "net9.0" }, ResultadoCompatibilidad.Incompatible)]
    [InlineData("net10.0", new[] { "net9.0" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net8.0", new[] { ".NETFramework4.8" }, ResultadoCompatibilidad.Incompatible)]
    [InlineData("net10.0", new[] { ".NETFramework4.8" }, ResultadoCompatibilidad.Incompatible)]
    [InlineData("net8.0", new[] { "" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net10.0", new[] { "" }, ResultadoCompatibilidad.Compatible)]
    [InlineData("net8.0", new string[0], ResultadoCompatibilidad.NoDeterminada)]
    [InlineData("net10.0", new string[0], ResultadoCompatibilidad.NoDeterminada)]
    public void Tabla_del_diseno(string tfm, string[] grupos, ResultadoCompatibilidad esperado) =>
        Assert.Equal(esperado, Inferir(tfm, grupos));

    [Theory]
    [InlineData(".NETStandard2.0")]
    [InlineData("netstandard2.1")]
    [InlineData(".NETStandard1.6")]
    [InlineData("netcoreapp3.1")]
    [InlineData(".NETCoreApp3.1")]
    [InlineData("net5.0")]
    public void Acepta_los_nombres_cortos_y_largos_de_framework_compatibles_con_net8(string grupo) =>
        Assert.Equal(ResultadoCompatibilidad.Compatible, Inferir("net8.0", grupo));

    [Theory]
    [InlineData(".NETFramework4.5")]
    [InlineData("net48")]
    [InlineData(".NETPortable0.0-Profile111")]
    [InlineData("net11.0")]
    [InlineData("netstandard2.0-windows")]
    public void Un_grupo_que_no_alcanza_el_framework_es_incompatible(string grupo)
    {
        var r = Inferir("net8.0", grupo);

        Assert.NotEqual(ResultadoCompatibilidad.Compatible, r);
    }

    [Fact]
    public void Basta_un_grupo_compatible_entre_varios_no_compatibles() =>
        Assert.Equal(ResultadoCompatibilidad.Compatible, Inferir("net8.0", ".NETFramework4.8", "net9.0", "net6.0"));

    [Fact]
    public void El_resultado_siempre_se_etiqueta_como_inferido_y_explica_su_limite()
    {
        var incompatible = _inferidor.Inferir("net8.0", [".NETFramework4.8"]);

        Assert.Equal("inferida_dependency_groups", incompatible.Base);
        Assert.Contains("no una prueba", incompatible.Detalle);
    }

    [Theory]
    [InlineData("")]
    [InlineData("desconocido-xyz")]
    public void Un_TFM_de_proyecto_no_interpretable_es_no_determinado(string tfm)
    {
        var r = _inferidor.Inferir(tfm, ["net8.0"]);

        Assert.Equal(ResultadoCompatibilidad.NoDeterminada, r.Resultado);
    }

    [Fact]
    public void Un_proyecto_con_plataforma_acepta_un_grupo_sin_plataforma_pero_no_al_reves()
    {
        Assert.Equal(ResultadoCompatibilidad.Compatible, Inferir("net8.0-windows", "net8.0"));
        Assert.Equal(ResultadoCompatibilidad.Incompatible, Inferir("net8.0", "net8.0-windows"));
    }

    [Fact]
    public void Grupos_no_interpretables_se_ignoran_y_si_no_queda_ninguno_es_no_determinado()
    {
        Assert.Equal(ResultadoCompatibilidad.NoDeterminada, Inferir("net8.0", "no-es-un-framework"));
        Assert.Equal(ResultadoCompatibilidad.Compatible, Inferir("net8.0", "no-es-un-framework", "net8.0"));
    }
}

public class SelectorVersionTests
{
    private static HojaVersion Hoja(string version, bool listada = true, string[]? grupos = null) => new(
        NuGetVersion.Parse(version),
        new VersionPublicada(NuGetVersion.Parse(version).ToNormalizedString(), version, NuGetVersion.Parse(version).IsPrerelease, listada,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), "MIT", TipoLicencia.Expresion, null, grupos ?? ["net8.0"], []));

    private static string? Ultima(IEnumerable<HojaVersion> hojas) => SelectorVersion.UltimaEstable(hojas)?.Datos.Version;

    [Fact]
    public void El_orden_es_semantico_y_no_de_texto()
    {
        // En orden de texto «1.9.0» > «1.10.0» y «2.0.0» < «10.0.0»: aquí debe ganar la versión realmente mayor.
        Assert.Equal("1.10.0", Ultima([Hoja("1.9.0"), Hoja("1.10.0"), Hoja("1.2.0")]));
        Assert.Equal("10.0.0", Ultima([Hoja("2.0.0"), Hoja("10.0.0")]));
    }

    [Fact]
    public void Las_preliminares_no_son_la_ultima_estable_pero_se_informan_aparte()
    {
        var hojas = new[] { Hoja("1.0.0"), Hoja("2.0.0-beta.1"), Hoja("2.0.0-rc.2") };

        Assert.Equal("1.0.0", Ultima(hojas));
        Assert.Equal("2.0.0-rc.2", SelectorVersion.UltimaPrerelease(hojas)?.Datos.Version);
    }

    [Fact]
    public void Las_versiones_no_listadas_no_cuentan_como_ultima()
    {
        var hojas = new[] { Hoja("1.0.0"), Hoja("3.0.0", listada: false), Hoja("2.0.0") };

        Assert.Equal("2.0.0", Ultima(hojas));
    }

    [Fact]
    public void Sin_ninguna_estable_la_ultima_estable_es_nula_y_no_se_inventa()
    {
        Assert.Null(Ultima([Hoja("1.0.0-beta"), Hoja("1.0.0-rc")]));
        Assert.Null(Ultima([]));
        Assert.Null(Ultima([Hoja("1.0.0", listada: false)]));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0+build9", "1.0.0")]
    [InlineData("2.0.0-BETA.1", "2.0.0-beta.1")]
    public void La_declarada_se_localiza_con_las_reglas_de_NuGet(string declarada, string existente)
    {
        var hojas = new[] { Hoja("0.9.0"), Hoja(existente), Hoja("3.0.0") };

        Assert.NotNull(SelectorVersion.Declarada(hojas, NuGetVersion.Parse(declarada)));
    }

    [Fact]
    public void Una_declarada_inexistente_no_se_sustituye_por_otra() =>
        Assert.Null(SelectorVersion.Declarada([Hoja("1.0.0"), Hoja("2.0.0")], NuGetVersion.Parse("1.5.0")));

    [Fact]
    public void Una_declarada_no_listada_se_encuentra_aunque_no_cuente_como_ultima()
    {
        var hojas = new[] { Hoja("1.0.0", listada: false), Hoja("2.0.0") };

        Assert.NotNull(SelectorVersion.Declarada(hojas, NuGetVersion.Parse("1.0.0")));
    }

    [Fact]
    public void La_ultima_estable_y_la_ultima_compatible_pueden_ser_distintas()
    {
        var inferidor = new InferidorCompatibilidad();
        var hojas = new[] { Hoja("1.0.0", grupos: ["net8.0"]), Hoja("2.0.0", grupos: ["net8.0"]), Hoja("3.0.0", grupos: ["net10.0"]) };

        Assert.Equal("3.0.0", Ultima(hojas));
        Assert.Equal("2.0.0", SelectorVersion.UltimaEstableCompatible(hojas, "net8.0", inferidor)?.Datos.Version);
        Assert.Equal("3.0.0", SelectorVersion.UltimaEstableCompatible(hojas, "net10.0", inferidor)?.Datos.Version);
    }

    [Fact]
    public void La_ultima_compatible_ignora_preliminares_y_no_listadas()
    {
        var inferidor = new InferidorCompatibilidad();
        var hojas = new[] { Hoja("1.0.0"), Hoja("2.0.0-beta"), Hoja("3.0.0", listada: false) };

        Assert.Equal("1.0.0", SelectorVersion.UltimaEstableCompatible(hojas, "net8.0", inferidor)?.Datos.Version);
    }

    [Fact]
    public void Si_ninguna_estable_es_compatible_la_ultima_compatible_es_nula()
    {
        var inferidor = new InferidorCompatibilidad();

        Assert.Null(SelectorVersion.UltimaEstableCompatible([Hoja("1.0.0", grupos: [".NETFramework4.8"])], "net8.0", inferidor));
    }

    [Fact]
    public void Un_paquete_sin_grupos_es_no_determinado_y_no_cuenta_como_compatible()
    {
        var inferidor = new InferidorCompatibilidad();

        Assert.Null(SelectorVersion.UltimaEstableCompatible([Hoja("1.0.0", grupos: [])], "net8.0", inferidor));
    }
}

public class ValidadorDireccionesTests
{
    private readonly ValidadorDirecciones _v = new(new OpcionesNuGet());

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json")]
    [InlineData("https://nuget.org/")]
    [InlineData("https://azuresearch-usnc.nuget.org/query")]
    [InlineData("https://API.NuGet.Org/v3/")]
    [InlineData("https://api.nuget.org:443/v3/")]
    public void Acepta_HTTPS_de_nuget_org_y_sus_subdominios(string url) => Assert.True(_v.EsPermitida(new Uri(url)));

    [Theory]
    [InlineData("http://api.nuget.org/v3/index.json")]
    [InlineData("https://evilnuget.org/")]
    [InlineData("https://nuget.org.evil.com/")]
    [InlineData("https://api.nuget.org.evil.com/v3/")]
    [InlineData("https://evil.com/nuget.org")]
    [InlineData("https://evil.com/?x=api.nuget.org")]
    [InlineData("https://nuget.org@evil.com/")]
    [InlineData("https://user:pass@api.nuget.org/")]
    [InlineData("https://api.nuget.org:8443/v3/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("ftp://api.nuget.org/")]
    [InlineData("https://notnuget.org/")]
    public void Rechaza_lo_que_no_es_HTTPS_de_nuget_org(string url) => Assert.False(_v.EsPermitida(new Uri(url)));

    [Fact]
    public void Una_direccion_relativa_o_nula_se_rechaza()
    {
        Assert.False(_v.EsPermitida(null));
        Assert.False(_v.EsPermitida(new Uri("/v3/index.json", UriKind.Relative)));
    }

    [Theory]
    [InlineData("Newtonsoft.Json", true)]
    [InlineData("a", true)]
    [InlineData("My-Package_1.0", true)]
    [InlineData("../etc/passwd", false)]
    [InlineData("a/b", false)]
    [InlineData("a?b=c", false)]
    [InlineData("a b", false)]
    [InlineData("$(Variable)", false)]
    [InlineData("-empieza-con-guion", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Valida_el_identificador_antes_de_formar_la_direccion(string? id, bool valido) => Assert.Equal(valido, _v.EsIdentificadorValido(id));

    [Fact]
    public void El_identificador_respeta_el_maximo_configurado()
    {
        Assert.True(_v.EsIdentificadorValido(new string('a', 100)));
        Assert.False(_v.EsIdentificadorValido(new string('a', 101)));
    }
}

public class DisyuntorTests
{
    private readonly FakeTimeProvider _tiempo = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero));

    private Disyuntor Crear(int umbral = 5) => new(umbral, TimeSpan.FromSeconds(30), _tiempo);

    [Fact]
    public void Se_abre_tras_cinco_fallos_consecutivos_y_no_antes()
    {
        var d = Crear();
        for (var i = 0; i < 4; i++) { Assert.True(d.TryPasar()); d.RegistrarFallo(); }
        Assert.True(d.TryPasar());

        d.RegistrarFallo();

        Assert.True(d.Abierto);
        Assert.False(d.TryPasar());
    }

    [Fact]
    public void Un_exito_reinicia_la_cuenta()
    {
        var d = Crear();
        for (var i = 0; i < 4; i++) d.RegistrarFallo();
        d.RegistrarExito();
        for (var i = 0; i < 4; i++) d.RegistrarFallo();

        Assert.False(d.Abierto);
    }

    [Fact]
    public void Tras_la_pausa_deja_pasar_una_sola_prueba_y_se_cierra_si_tiene_exito()
    {
        var d = Crear();
        for (var i = 0; i < 5; i++) d.RegistrarFallo();
        _tiempo.Advance(TimeSpan.FromSeconds(31));

        Assert.True(d.TryPasar());
        Assert.False(d.TryPasar()); // la segunda espera el resultado de la prueba
        d.RegistrarExito();

        Assert.False(d.Abierto);
        Assert.True(d.TryPasar());
    }

    [Fact]
    public void Si_la_prueba_falla_vuelve_a_abrirse_por_otra_pausa_completa()
    {
        var d = Crear();
        for (var i = 0; i < 5; i++) d.RegistrarFallo();
        _tiempo.Advance(TimeSpan.FromSeconds(31));
        Assert.True(d.TryPasar());

        d.RegistrarFallo();

        Assert.True(d.Abierto);
        _tiempo.Advance(TimeSpan.FromSeconds(29));
        Assert.False(d.TryPasar());
        _tiempo.Advance(TimeSpan.FromSeconds(2));
        Assert.True(d.TryPasar());
    }

    [Fact]
    public void Una_prueba_cancelada_no_deja_el_disyuntor_bloqueado()
    {
        var d = Crear();
        for (var i = 0; i < 5; i++) d.RegistrarFallo();
        _tiempo.Advance(TimeSpan.FromSeconds(31));
        Assert.True(d.TryPasar());

        d.LiberarPrueba();

        Assert.True(d.TryPasar());
    }
}
