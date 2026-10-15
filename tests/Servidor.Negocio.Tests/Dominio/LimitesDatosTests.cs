using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

namespace Servidor.Negocio.Tests.Dominio;

public class LimitesDatosTests
{
    private static string Emojis(int cantidad) => string.Concat(Enumerable.Repeat("😀", cantidad));

    [Fact]
    public void La_longitud_cuenta_caracteres_Unicode_como_PostgreSQL_y_no_unidades_UTF16()
    {
        Assert.Equal(3, LimitesDatos.Longitud("abc"));
        Assert.Equal(0, LimitesDatos.Longitud(null));
        Assert.Equal(0, LimitesDatos.Longitud(""));
        Assert.Equal(5, LimitesDatos.Longitud(Emojis(5)));
        Assert.Equal(10, Emojis(5).Length); // en UTF-16 serían 10
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(201, true)]
    public void Excede_distingue_exactamente_el_limite(int longitud, bool esperado) =>
        Assert.Equal(esperado, LimitesDatos.Excede(new string('a', longitud), 200));

    [Fact]
    public void Recortar_no_parte_un_par_sustituto_ni_pasa_del_maximo()
    {
        var recortado = LimitesDatos.Recortar(Emojis(10), 4);

        Assert.Equal(Emojis(4), recortado);
        Assert.Equal("abc", LimitesDatos.Recortar("abc", 10));
    }

    [Fact]
    public void El_extracto_de_un_valor_largo_no_lo_vuelca_completo()
    {
        var largo = new string('x', 500);

        var extracto = LimitesDatos.Extracto(largo);

        Assert.True(extracto.Length < 50);
        Assert.EndsWith("…", extracto);
        Assert.Equal("corto", LimitesDatos.Extracto("corto"));
    }

    // ---------- ValidadorLimites ----------

    private static Analisis Analisis(string archivo = "a.csproj", string reglas = "0.1", string nombre = "P", string? version = "1.0.0",
        string? ultima = "1.0.0", string alternativo = "Q", string? sugerida = "2.0.0")
    {
        var recomendacion = new Recomendacion(TipoRecomendacion.Alternativa, alternativo, sugerida, "j", [], [], false);
        var diagnostico = new Diagnostico(EstadoDiagnostico.Actualizada, "m", ultima, [], [], OrigenDato.Nuget, DateTimeOffset.UnixEpoch, null);
        var dependencia = new DependenciaAnalizada(nombre, version, TipoVersion.Exacta, null, [], diagnostico, [recomendacion]);
        return new Analisis(Guid.NewGuid(), archivo, ["net8.0"], ["net8.0"], DateTimeOffset.UnixEpoch, EstadoAnalisis.Completo, [dependencia], [], reglas);
    }

    [Fact]
    public void Un_analisis_en_los_limites_exactos_es_valido()
    {
        var a = Analisis(new string('a', 260), new string('r', 30), new string('p', 200), new string('1', 100), new string('u', 100), new string('q', 200), new string('s', 100));

        ValidadorLimites.Validar(a);
    }

    [Fact]
    public void Un_analisis_con_emojis_en_el_limite_es_valido_aunque_UTF16_lo_duplique() =>
        ValidadorLimites.Validar(Analisis(nombre: Emojis(200), alternativo: Emojis(200)));

    [Theory]
    [InlineData("archivo", 261, "analisis.nombre_archivo", 260)]
    [InlineData("reglas", 31, "analisis.version_reglas", 30)]
    [InlineData("nombre", 201, "dependencia.nombre_paquete", 200)]
    [InlineData("version", 101, "dependencia.version_declarada", 100)]
    [InlineData("ultima", 101, "diagnostico.ultima_version", 100)]
    [InlineData("alternativo", 201, "recomendacion.paquete_alternativo", 200)]
    [InlineData("sugerida", 101, "recomendacion.version_sugerida", 100)]
    public void Un_valor_que_excede_el_limite_se_rechaza_con_el_campo_y_los_numeros(string campo, int longitud, string nombreCampo, int maximo)
    {
        var valor = new string('x', longitud);
        var a = campo switch
        {
            "archivo" => Analisis(archivo: valor),
            "reglas" => Analisis(reglas: valor),
            "nombre" => Analisis(nombre: valor),
            "version" => Analisis(version: valor),
            "ultima" => Analisis(ultima: valor),
            "alternativo" => Analisis(alternativo: valor),
            _ => Analisis(sugerida: valor),
        };

        var ex = Assert.Throws<ValorExcedeLimiteException>(() => ValidadorLimites.Validar(a));

        Assert.Equal(nombreCampo, ex.Campo);
        Assert.Equal(maximo, ex.Maximo);
        Assert.Equal(longitud, ex.Longitud);
        Assert.Contains(nombreCampo, ex.Message);
        Assert.DoesNotContain(valor, ex.Message); // no se vuelca el valor completo
    }

    private static MetadatoLibreria Metadato(string nombre = "P", string version = "1.0.0", string? licencia = "MIT") =>
        new(nombre, version, null, null, licencia, null, null, null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Un_metadato_en_los_limites_exactos_es_valido() =>
        ValidadorLimites.Validar(Metadato(new string('p', 200), new string('1', 100), new string('l', 5000)) with { PaqueteAlterno = new string('a', 200), RangoAlterno = new string('r', 100) }); // la licencia es text: sin límite en la base

    [Theory]
    [InlineData("nombre", 201, "metadato_libreria.nombre_paquete")]
    [InlineData("version", 101, "metadato_libreria.version")]
    [InlineData("alterno", 201, "metadato_libreria.paquete_alterno")]
    [InlineData("rango", 101, "metadato_libreria.rango_alterno")]
    public void Un_metadato_que_excede_un_limite_se_rechaza(string campo, int longitud, string esperado)
    {
        var valor = new string('x', longitud);
        var m = campo switch
        {
            "nombre" => Metadato(nombre: valor),
            "version" => Metadato(version: valor),
            "alterno" => Metadato() with { PaqueteAlterno = valor },
            _ => Metadato() with { PaqueteAlterno = "Ejemplo.Nuevo", RangoAlterno = valor },
        };

        Assert.Equal(esperado, Assert.Throws<ValorExcedeLimiteException>(() => ValidadorLimites.Validar(m)).Campo);
    }

    [Theory]
    [InlineData(200, 100, true)]
    [InlineData(201, 100, false)]
    [InlineData(200, 101, false)]
    public void La_seleccion_de_versiones_respeta_sus_limites(int nombre, int version, bool valida)
    {
        var s = new SeleccionPaquete(new string('n', nombre), new string('1', version), new Dictionary<string, string>(), true, DateTimeOffset.UnixEpoch);

        if (valida) ValidadorLimites.Validar(s);
        else Assert.Throws<ValorExcedeLimiteException>(() => ValidadorLimites.Validar(s));
    }

    // ---------- lectura: validador de archivo y lector ----------

    private readonly ValidadorArchivoCsproj _validador = new(new OpcionesLectura());
    private readonly LectorCsproj _lector = new(new OpcionesLectura());

    private static string Xml(string nombre, string version = "1.0.0") =>
        $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="{nombre}" Version="{version}" /></ItemGroup></Project>""";

    private ProyectoLeido Leer(string xml) => _lector.Leer("a.csproj", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));

    private string CodigoDe(string xml) => Assert.Throws<ArchivoInvalidoException>(() => Leer(xml)).Codigo;

    [Fact]
    public void Nombre_de_archivo_de_260_caracteres_se_acepta_y_de_261_se_rechaza()
    {
        Assert.Equal(260, _validador.Validar(new string('a', 253) + ".csproj", 10).Length);

        var ex = Assert.Throws<ArchivoInvalidoException>(() => _validador.Validar(new string('a', 254) + ".csproj", 10));

        Assert.Equal(CodigosLectura.NombreArchivoDemasiadoLargo, ex.Codigo);
        Assert.Contains("261", ex.Message);
        Assert.Contains("260", ex.Message);
    }

    [Fact]
    public void El_limite_del_nombre_se_mide_sin_la_ruta_y_en_caracteres_Unicode()
    {
        var ruta = @"C:\" + string.Join('\\', Enumerable.Repeat(new string('d', 50), 10)) + @"\Mi.csproj"; // ruta de >500 caracteres
        Assert.Equal("Mi.csproj", _validador.Validar(ruta, 10));

        Assert.Equal(260, LimitesDatos.Longitud(Emojis(253) + ".csproj"));
        _validador.Validar(Emojis(253) + ".csproj", 10);
        Assert.Throws<ArchivoInvalidoException>(() => _validador.Validar(Emojis(254) + ".csproj", 10));
    }

    [Fact]
    public void Un_nombre_de_paquete_de_200_caracteres_se_lee_y_uno_de_201_se_rechaza_en_la_lectura()
    {
        var ok = Leer(Xml(new string('p', 200)));
        Assert.Equal(200, ok.Dependencias.Single().Nombre.Length);

        var largo = new string('p', 201);
        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer(Xml(largo)));

        Assert.Equal(CodigosLectura.NombrePaqueteDemasiadoLargo, ex.Codigo);
        Assert.Contains("201", ex.Message);
        Assert.Contains("200", ex.Message);
        Assert.DoesNotContain(largo, ex.Message); // solo un extracto del identificador
    }

    [Fact]
    public void El_limite_del_paquete_se_mide_en_caracteres_Unicode()
    {
        Assert.Equal(200, Leer(Xml(Emojis(200))).Dependencias.Single().Nombre.EnumerateRunes().Count());
        Assert.Equal(CodigosLectura.NombrePaqueteDemasiadoLargo, CodigoDe(Xml(Emojis(201))));
    }

    [Fact]
    public void Una_version_de_100_caracteres_se_lee_y_una_de_101_se_rechaza_nombrando_el_paquete()
    {
        var v100 = "1.0.0-" + new string('b', 94);
        Assert.Equal(v100, Leer(Xml("Ejemplo.Uno", v100)).Dependencias.Single().Version);

        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer(Xml("Ejemplo.Uno", v100 + "b")));

        Assert.Equal(CodigosLectura.VersionDemasiadoLarga, ex.Codigo);
        Assert.Contains("Ejemplo.Uno", ex.Message);
        Assert.Contains("101", ex.Message);
    }

    [Fact]
    public void Un_valor_demasiado_largo_en_una_dependencia_rechaza_toda_la_lectura_sin_resultados_parciales()
    {
        var xml = $"""<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Bueno" Version="1.0.0" /><PackageReference Include="{new string('x', 201)}" Version="1.0.0" /></ItemGroup></Project>""";

        Assert.Equal(CodigosLectura.NombrePaqueteDemasiadoLargo, CodigoDe(xml));
    }

    [Fact]
    public void Una_condicion_larga_no_tiene_limite_porque_la_columna_es_text()
    {
        var condicion = new string('c', 5000);
        var xml = $"""<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="A" Version="1.0.0" Condition="{condicion}" /></ItemGroup></Project>""";

        Assert.Equal(5000, Leer(xml).Dependencias.Single().Condicion!.Length);
    }

}
