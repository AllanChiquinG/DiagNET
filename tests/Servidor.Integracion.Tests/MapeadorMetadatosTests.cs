using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;

namespace Servidor.Integracion.Tests;

public class MapeadorMetadatosTests
{
    private readonly OpcionesNuGet _opciones = new();
    private readonly MapeadorMetadatos _mapeador;

    public MapeadorMetadatosTests() => _mapeador = new MapeadorMetadatos(_opciones, new ValidadorDirecciones(_opciones));

    private (HojaVersion? Hoja, List<Advertencia> Avisos) Mapear(string hojaJson)
    {
        var json = $"{{\"items\":[{{\"items\":[{hojaJson}]}}]}}";
        var indice = ParserRegistro.ParseIndice(System.Text.Encoding.UTF8.GetBytes(json));
        var avisos = new List<Advertencia>();
        var hoja = _mapeador.Mapear(indice.Paginas[0].Hojas![0], "Ejemplo.Paquete", avisos);
        return (hoja, avisos);
    }

    [Fact]
    public void Mapea_los_campos_basicos_con_la_version_normalizada()
    {
        var (hoja, avisos) = Mapear(Registro.Hoja("2.1", publicada: "2025-03-14T10:22:05+00:00", grupos: ["net8.0", ".NETStandard2.0"]));

        Assert.Empty(avisos);
        var d = hoja!.Datos;
        Assert.Equal("2.1.0", d.Version);
        Assert.Equal("2.1", d.VersionOriginal);
        Assert.False(d.EsPrerelease);
        Assert.True(d.Listada);
        Assert.Equal(new DateTimeOffset(2025, 3, 14, 10, 22, 5, TimeSpan.Zero), d.Publicada);
        Assert.Equal("MIT", d.Licencia);
        Assert.Equal(TipoLicencia.Expresion, d.TipoLicencia);
        Assert.Equal(["net8.0", ".NETStandard2.0"], d.GruposFramework);
        Assert.Null(d.Deprecacion);
    }

    [Fact]
    public void Una_version_preliminar_se_marca_como_tal() =>
        Assert.True(Mapear(Registro.Hoja("2.0.0-beta.1")).Hoja!.Datos.EsPrerelease);

    [Fact]
    public void La_fecha_1900_de_las_no_listadas_pasa_a_nula_y_la_hoja_no_es_listada()
    {
        var (hoja, _) = Mapear(Registro.Hoja("1.0.0", listada: null, publicada: "1900-01-01T00:00:00+00:00"));

        Assert.Null(hoja!.Datos.Publicada);
        Assert.False(hoja.Datos.Listada);
    }

    [Fact]
    public void Si_listed_es_explicito_manda_sobre_la_fecha()
    {
        Assert.False(Mapear(Registro.Hoja("1.0.0", listada: false)).Hoja!.Datos.Listada);
        Assert.True(Mapear(Registro.Hoja("1.0.0", listada: true, publicada: "1900-01-01T00:00:00+00:00")).Hoja!.Datos.Listada);
    }

    [Fact]
    public void Una_fecha_ilegible_queda_nula_pero_no_vuelve_no_listada_a_una_version_listada()
    {
        var (hoja, _) = Mapear(Registro.Hoja("1.0.0", listada: null, publicada: "no-es-fecha"));

        Assert.Null(hoja!.Datos.Publicada);
        Assert.True(hoja.Datos.Listada);
    }

    [Fact]
    public void Sin_fecha_publicada_la_fecha_queda_nula_y_no_se_inventa()
    {
        var (hoja, _) = Mapear(Registro.Hoja("1.0.0", publicada: null));

        Assert.Null(hoja!.Datos.Publicada);
    }

    [Theory]
    [InlineData("MIT", null, "MIT", TipoLicencia.Expresion)]
    [InlineData("", "https://example.org/licencia", "https://example.org/licencia", TipoLicencia.Url)]
    [InlineData("  ", "https://example.org/l", "https://example.org/l", TipoLicencia.Url)]
    [InlineData("Apache-2.0", "https://example.org/l", "Apache-2.0", TipoLicencia.Expresion)]
    public void La_licencia_prefiere_la_expresion_y_distingue_una_URL(string? expr, string? url, string? esperada, TipoLicencia tipo)
    {
        var d = Mapear(Registro.Hoja("1.0.0", licencia: expr, urlLicencia: url)).Hoja!.Datos;

        Assert.Equal(esperada, d.Licencia);
        Assert.Equal(tipo, d.TipoLicencia);
    }

    [Fact]
    public void Sin_licencia_ni_URL_la_licencia_es_nula()
    {
        var d = Mapear(Registro.Hoja("1.0.0", licencia: null)).Hoja!.Datos;

        Assert.Null(d.Licencia);
        Assert.Null(d.TipoLicencia);
    }

    [Fact]
    public void Una_licencia_larga_se_conserva_completa_en_el_resultado_el_limite_lo_aplica_la_cache()
    {
        var url = "https://example.org/" + new string('x', 400);

        var d = Mapear(Registro.Hoja("1.0.0", licencia: null, urlLicencia: url)).Hoja!.Datos;

        Assert.Equal(url, d.Licencia);
    }

    [Fact]
    public void Un_grupo_sin_targetFramework_se_representa_como_cadena_vacia_y_sin_grupos_la_lista_es_vacia()
    {
        Assert.Equal([""], Mapear(Registro.Hoja("1.0.0", grupos: [""])).Hoja!.Datos.GruposFramework);
        Assert.Empty(Mapear(Registro.Hoja("1.0.0", incluirGrupos: false)).Hoja!.Datos.GruposFramework);
    }

    [Fact]
    public void Mapea_la_deprecacion_con_paquete_alterno_y_rango()
    {
        var dep = Registro.Deprecacion("Reemplazado.", ["Legacy", "CriticalBugs"], "Paquete.Nuevo", "[1.0.0, )");

        var d = Mapear(Registro.Hoja("1.0.0", deprecacion: dep)).Hoja!.Datos.Deprecacion!;

        Assert.Equal(["Legacy", "CriticalBugs"], d.Razones);
        Assert.Equal("Reemplazado.", d.Mensaje);
        Assert.Equal("Paquete.Nuevo", d.PaqueteAlterno);
        Assert.Equal("[1.0.0, )", d.RangoVersionAlterno);
    }

    [Fact]
    public void Una_deprecacion_sin_alterno_deja_alterno_y_rango_nulos()
    {
        var d = Mapear(Registro.Hoja("1.0.0", deprecacion: Registro.Deprecacion(alterno: null))).Hoja!.Datos.Deprecacion!;

        Assert.Null(d.PaqueteAlterno);
        Assert.Null(d.RangoVersionAlterno);
    }

    [Fact]
    public void Un_alterno_con_identificador_invalido_se_ignora_con_advertencia_y_no_se_usa()
    {
        var dep = Registro.Deprecacion(alterno: "../malo/ruta");

        var (hoja, avisos) = Mapear(Registro.Hoja("1.0.0", deprecacion: dep));

        Assert.Null(hoja!.Datos.Deprecacion!.PaqueteAlterno);
        Assert.Null(hoja.Datos.Deprecacion.RangoVersionAlterno);
        Assert.Contains(avisos, a => a.Codigo == "paquete_alterno_invalido");
    }

    [Fact]
    public void Un_mensaje_de_deprecacion_demasiado_largo_se_acota_con_advertencia()
    {
        var dep = Registro.Deprecacion(new string('m', 5000));

        var (hoja, avisos) = Mapear(Registro.Hoja("1.0.0", deprecacion: dep));

        var mensaje = hoja!.Datos.Deprecacion!.Mensaje!;
        Assert.Equal(_opciones.MaxTextoMensaje + 1, mensaje.Length); // límite + «…»
        Assert.EndsWith("…", mensaje);
        var aviso = Assert.Single(avisos, a => a.Codigo == "texto_externo_acotado");
        Assert.Contains("5000", aviso.Mensaje);
    }

    [Fact]
    public void Las_razones_se_acotan_en_cantidad_y_longitud()
    {
        var razones = Enumerable.Range(0, 30).Select(i => new string('r', 300)).ToArray();

        var d = Mapear(Registro.Hoja("1.0.0", deprecacion: Registro.Deprecacion(razones: razones))).Hoja!.Datos.Deprecacion!;

        Assert.Equal(10, d.Razones.Count);
        Assert.All(d.Razones, r => Assert.Equal(100, r.Length));
    }

    [Theory]
    [InlineData("no-es-version")]
    [InlineData("")]
    [InlineData("1.2.3.4.5.6")]
    public void Una_version_no_interpretable_se_omite_con_advertencia_y_no_se_inventa(string version)
    {
        var json = $"{{\"catalogEntry\":{{\"version\":\"{version}\"}}}}";
        var indice = ParserRegistro.ParseIndice(System.Text.Encoding.UTF8.GetBytes($"{{\"items\":[{{\"items\":[{json}]}}]}}"));
        var avisos = new List<Advertencia>();

        var hoja = _mapeador.Mapear(indice.Paginas[0].Hojas![0], "Ejemplo.Paquete", avisos);

        Assert.Null(hoja);
        Assert.Contains(avisos, a => a.Codigo == "version_no_interpretable");
    }

    [Fact]
    public void Una_hoja_sin_catalogEntry_se_ignora_sin_fallar()
    {
        var indice = ParserRegistro.ParseIndice(System.Text.Encoding.UTF8.GetBytes("{\"items\":[{\"items\":[{\"@id\":\"x\"}]}]}"));

        Assert.Empty(indice.Paginas[0].Hojas!);
    }

    [Fact]
    public void Las_vulnerabilidades_aceptan_severidad_como_texto_o_numero_y_se_leen_de_forma_tolerante()
    {
        var vulns = "[{\"severity\":\"2\",\"advisoryUrl\":\"https://example.org/a\"},{\"severity\":3,\"advisoryUrl\":\"https://example.org/b\"},{\"otra\":1}]";

        var d = Mapear(Registro.Hoja("1.0.0", vulnerabilidades: vulns)).Hoja!.Datos;

        Assert.Equal(["2", "3", null], d.Vulnerabilidades.Select(v => v.Severidad));
        Assert.Equal("https://example.org/a", d.Vulnerabilidades[0].Url);
    }

    [Fact]
    public void Las_propiedades_desconocidas_se_ignoran()
    {
        var json = "{\"items\":[{\"items\":[{\"catalogEntry\":{\"version\":\"1.0.0\",\"campoNuevo\":{\"x\":[1,2,3]},\"listed\":true}}],\"otra\":true}],\"nueva\":1}";

        var indice = ParserRegistro.ParseIndice(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Equal("1.0.0", indice.Paginas[0].Hojas![0].Version);
    }

    [Theory]
    [InlineData("esto no es json")]
    [InlineData("[]")]
    [InlineData("{\"sin_items\":true}")]
    [InlineData("{\"items\":5}")]
    public void Un_registro_con_estructura_inesperada_lanza_RespuestaInvalida(string json) =>
        Assert.Throws<RespuestaInvalidaException>(() => ParserRegistro.ParseIndice(System.Text.Encoding.UTF8.GetBytes(json)));
}
