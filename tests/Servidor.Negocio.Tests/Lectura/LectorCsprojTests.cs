using System.Text;
using Servidor.Negocio.Lectura;

namespace Servidor.Negocio.Tests.Lectura;

public class LectorCsprojTests
{
    private readonly LectorCsproj _lector = new(new OpcionesLectura());

    private ProyectoLeido Leer(string xml, string nombre = "Prueba.csproj") =>
        _lector.Leer(nombre, new MemoryStream(new UTF8Encoding(false).GetBytes(xml)));

    private static string Proyecto(string tfm, string items) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>{tfm}</PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    private static string Paquete(string nombre, string? version) =>
        version is null ? $"""<PackageReference Include="{nombre}" />""" : $"""<PackageReference Include="{nombre}" Version="{version}" />""";

    private string ErrorDe(string xml) => Assert.Throws<ArchivoInvalidoException>(() => Leer(xml)).Codigo;

    private static IEnumerable<string> Codigos(ProyectoLeido p) => p.Advertencias.Select(a => a.Codigo);

    // ---------- lectura válida ----------

    [Fact]
    public void Lee_framework_y_dependencias_con_su_version_literal()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>",
            Paquete("Ejemplo.Uno", "1.2.3") + Paquete("Ejemplo.Dos", "2.0.0-beta.1")));

        Assert.Equal("Prueba.csproj", p.NombreArchivo);
        Assert.Equal(new FrameworkDeclarado("net8.0", true), Assert.Single(p.Frameworks));
        Assert.Collection(p.Dependencias,
            d => Assert.Equal(("Ejemplo.Uno", "1.2.3", TipoVersion.Exacta), (d.Nombre, d.Version, d.TipoVersion)),
            d => Assert.Equal(("Ejemplo.Dos", "2.0.0-beta.1", TipoVersion.Exacta), (d.Nombre, d.Version, d.TipoVersion)));
        Assert.Empty(p.Advertencias);
    }

    [Fact]
    public void Lee_proyectos_con_namespace_xml_antiguo()
    {
        var p = Leer("""
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Ejemplo.Uno" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        Assert.Equal("Ejemplo.Uno", Assert.Single(p.Dependencias).Nombre);
        Assert.True(Assert.Single(p.Frameworks).EnAlcance);
    }

    [Fact]
    public void Acepta_la_version_como_elemento_hijo()
    {
        var p = Leer(Proyecto("<TargetFramework>net10.0</TargetFramework>",
            """<PackageReference Include="Ejemplo.Uno"><Version>3.1.0</Version></PackageReference>"""));

        var d = Assert.Single(p.Dependencias);
        Assert.Equal("3.1.0", d.Version);
        Assert.Equal(TipoVersion.Exacta, d.TipoVersion);
    }

    [Fact]
    public void Acepta_contenido_con_BOM_utf8()
    {
        var xml = Proyecto("<TargetFramework>net8.0</TargetFramework>", Paquete("Ejemplo.Uno", "1.0.0"));
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray();

        var p = _lector.Leer("a.csproj", new MemoryStream(bytes));

        Assert.Single(p.Dependencias);
    }

    [Fact]
    public void Sin_paquetes_es_una_lectura_correcta_con_advertencia()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>", ""));

        Assert.Empty(p.Dependencias);
        Assert.Contains(AdvertenciasLectura.SinPaquetes, Codigos(p));
    }

    [Fact]
    public void No_ejecuta_ni_interpreta_destinos_de_MSBuild()
    {
        var p = Leer("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <Target Name="X" BeforeTargets="Build"><Exec Command="calc.exe" /></Target>
              <ItemGroup><PackageReference Include="Ejemplo.Uno" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        Assert.Single(p.Dependencias);
    }

    [Fact]
    public void Avisa_de_las_importaciones_que_no_lee()
    {
        var p = Leer("""
            <Project Sdk="Microsoft.NET.Sdk">
              <Import Project="otro.props" />
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Contains(AdvertenciasLectura.ImportacionNoLeida, Codigos(p));
    }

    [Fact]
    public void Avisa_de_PackageReference_Update_o_sin_Include()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>",
            """<PackageReference Update="Ejemplo.Uno" Version="2.0.0" /><PackageReference Remove="Ejemplo.Dos" />"""));

        Assert.Empty(p.Dependencias);
        Assert.Contains(AdvertenciasLectura.ReferenciaNoInterpretada, Codigos(p));
    }

    // ---------- frameworks (DEC-08) ----------

    [Fact]
    public void Separa_TargetFrameworks_normaliza_espacios_y_elimina_repetidos()
    {
        var p = Leer(Proyecto("<TargetFrameworks> net8.0 ; net10.0;NET8.0;; </TargetFrameworks>", Paquete("A", "1.0.0")));

        Assert.Equal(["net8.0", "net10.0"], p.Frameworks.Select(f => f.Valor));
        Assert.All(p.Frameworks, f => Assert.True(f.EnAlcance));
        Assert.Empty(p.Advertencias);
    }

    [Fact]
    public void Todos_los_frameworks_fuera_de_alcance_se_rechazan_con_mensaje_claro()
    {
        var ex = Assert.Throws<ArchivoInvalidoException>(() =>
            Leer(Proyecto("<TargetFrameworks>net6.0;netstandard2.0</TargetFrameworks>", Paquete("A", "1.0.0"))));

        Assert.Equal(CodigosLectura.FrameworkFueraDeAlcance, ex.Codigo);
        Assert.Contains(".NET 8 y .NET 10", ex.Message);
        Assert.Contains("net6.0", ex.Message);
        Assert.Contains("netstandard2.0", ex.Message);
    }

    [Theory]
    [InlineData("net9.0")]
    [InlineData("net48")]
    [InlineData("netcoreapp3.1")]
    [InlineData("net8")]
    [InlineData("net80.0")]
    [InlineData("net8.0x")]
    public void Un_framework_que_no_es_net8_ni_net10_esta_fuera_de_alcance(string tfm) =>
        Assert.Equal(CodigosLectura.FrameworkFueraDeAlcance, ErrorDe(Proyecto($"<TargetFramework>{tfm}</TargetFramework>", "")));

    [Fact]
    public void Mezcla_acepta_con_advertencia_y_marca_solo_los_admitidos()
    {
        var p = Leer(Proyecto("<TargetFrameworks>net8.0;net48</TargetFrameworks>", Paquete("A", "1.0.0")));

        Assert.Equal(new bool?[] { true, false }, p.Frameworks.Select(f => f.EnAlcance));
        Assert.Contains(AdvertenciasLectura.FrameworkFueraDeAlcanceOmitido, Codigos(p));
        Assert.DoesNotContain(AdvertenciasLectura.FrameworkNoResuelto, Codigos(p));
    }

    [Fact]
    public void Framework_ausente_permite_la_lectura_sin_afirmar_compatibilidad()
    {
        var p = Leer("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="A" Version="1.0.0" /></ItemGroup></Project>""");

        Assert.Empty(p.Frameworks);
        Assert.Contains(AdvertenciasLectura.FrameworkNoResuelto, Codigos(p));
        Assert.Single(p.Dependencias);
    }

    [Fact]
    public void Framework_con_variable_permite_la_lectura_y_no_se_considera_en_ni_fuera_de_alcance()
    {
        var p = Leer(Proyecto("<TargetFramework>$(Objetivo)</TargetFramework>", Paquete("A", "1.0.0")));

        var f = Assert.Single(p.Frameworks);
        Assert.Equal("$(Objetivo)", f.Valor);
        Assert.Null(f.EnAlcance);
        Assert.Contains(AdvertenciasLectura.FrameworkNoResuelto, Codigos(p));
    }

    [Fact]
    public void Variable_junto_a_un_framework_fuera_de_alcance_no_se_rechaza_porque_no_todos_estan_fuera()
    {
        var p = Leer(Proyecto("<TargetFrameworks>net6.0;$(Otro)</TargetFrameworks>", Paquete("A", "1.0.0")));

        Assert.Equal(new bool?[] { false, null }, p.Frameworks.Select(f => f.EnAlcance));
        Assert.Contains(AdvertenciasLectura.FrameworkNoResuelto, Codigos(p));
        Assert.Contains(AdvertenciasLectura.FrameworkFueraDeAlcanceOmitido, Codigos(p));
    }

    [Fact]
    public void Variable_junto_a_un_framework_admitido_conserva_ambos_y_avisa()
    {
        var p = Leer(Proyecto("<TargetFrameworks>net8.0;$(Otro)</TargetFrameworks>", Paquete("A", "1.0.0")));

        Assert.Equal(new bool?[] { true, null }, p.Frameworks.Select(f => f.EnAlcance));
        Assert.Contains(AdvertenciasLectura.FrameworkNoResuelto, Codigos(p));
    }

    [Theory]
    [InlineData("net8.0-windows")]
    [InlineData("net10.0-android")]
    [InlineData("NET8.0-Windows10.0.19041")]
    public void Conserva_el_sufijo_de_plataforma_tal_cual_y_no_supone_compatibilidad(string tfm)
    {
        var p = Leer(Proyecto($"<TargetFramework>{tfm}</TargetFramework>", Paquete("A", "1.0.0")));

        var f = Assert.Single(p.Frameworks);
        Assert.Equal(tfm, f.Valor);
        Assert.True(f.EnAlcance);
        Assert.Contains("no determina la compatibilidad", f.Nota);
        Assert.Contains(AdvertenciasLectura.SufijoPlataforma, Codigos(p));
    }

    [Fact]
    public void Framework_con_condicion_se_avisa_y_no_se_evalua()
    {
        var p = Leer("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(Configuration)'=='Debug'"><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        Assert.Contains(AdvertenciasLectura.CondicionNoEvaluada, Codigos(p));
        Assert.Equal("net8.0", Assert.Single(p.Frameworks).Valor);
    }

    // ---------- versiones ----------

    [Theory]
    [InlineData("1.2.3", TipoVersion.Exacta, "1.2.3")]
    [InlineData("1.0", TipoVersion.Exacta, "1.0")]
    [InlineData("2.0.0-beta.1", TipoVersion.Exacta, "2.0.0-beta.1")]
    [InlineData("1.0.0+build5", TipoVersion.Exacta, "1.0.0+build5")]
    [InlineData("  3.1.4  ", TipoVersion.Exacta, "3.1.4")]
    [InlineData("[1.0.0,2.0.0)", TipoVersion.Rango, "[1.0.0,2.0.0)")]
    [InlineData("(1.0,)", TipoVersion.Rango, "(1.0,)")]
    [InlineData("1.0,2.0", TipoVersion.Rango, "1.0,2.0")]
    [InlineData("1.*", TipoVersion.Flotante, "1.*")]
    [InlineData("*", TipoVersion.Flotante, "*")]
    [InlineData("1.0.0-*", TipoVersion.Flotante, "1.0.0-*")]
    [InlineData("$(VersionEjemplo)", TipoVersion.Propiedad, "$(VersionEjemplo)")]
    [InlineData("abc", TipoVersion.Invalida, "abc")]
    [InlineData("1.2.3.4.5", TipoVersion.Invalida, "1.2.3.4.5")]
    public void Clasifica_la_version_sin_resolverla_y_conserva_el_valor_declarado(string declarada, TipoVersion tipo, string conservada)
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>", Paquete("A", declarada)));

        var d = Assert.Single(p.Dependencias);
        Assert.Equal(tipo, d.TipoVersion);
        Assert.Equal(conservada, d.Version);
        if (tipo == TipoVersion.Exacta) Assert.Empty(d.Advertencias);
        else Assert.Contains(d.Advertencias, a => a.Codigo == AdvertenciasLectura.VersionNoResuelta && a.Dependencia == "A");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Version_ausente_o_en_blanco_queda_nula_y_nunca_se_sustituye_por_la_ultima(string vacia)
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>", Paquete("A", vacia)));

        var d = Assert.Single(p.Dependencias);
        Assert.Equal(TipoVersion.Ausente, d.TipoVersion);
        Assert.Null(d.Version);
    }

    [Fact]
    public void Sin_atributo_de_version_tambien_es_ausente()
    {
        var d = Assert.Single(Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>", Paquete("A", null))).Dependencias);

        Assert.Equal(TipoVersion.Ausente, d.TipoVersion);
        Assert.Null(d.Version);
        Assert.Contains("Directory.Packages.props", d.Advertencias.Single().Mensaje);
    }

    [Fact]
    public void VersionOverride_se_lee_como_la_version_declarada()
    {
        var d = Assert.Single(Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>",
            """<PackageReference Include="A" VersionOverride="4.5.6" />""")).Dependencias);

        Assert.Equal("4.5.6", d.Version);
    }

    // ---------- duplicados, condiciones y límite (DEC-09) ----------

    [Fact]
    public void Duplicados_equivalentes_se_unifican_sin_distinguir_mayusculas_del_identificador()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>",
            Paquete("Ejemplo.Uno", "1.0.0") + Paquete("EJEMPLO.UNO", "1.0.0")));

        var d = Assert.Single(p.Dependencias);
        Assert.DoesNotContain(d.Advertencias, a => a.Codigo == AdvertenciasLectura.DependenciaDuplicadaConflictiva);
    }

    [Fact]
    public void Versiones_conflictivas_se_conservan_todas_con_advertencia()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>",
            Paquete("Ejemplo.Uno", "1.0.0") + Paquete("Ejemplo.Uno", "2.0.0")));

        Assert.Equal(["1.0.0", "2.0.0"], p.Dependencias.Select(d => d.Version));
        Assert.All(p.Dependencias, d =>
            Assert.Contains(d.Advertencias, a => a.Codigo == AdvertenciasLectura.DependenciaDuplicadaConflictiva));
    }

    [Fact]
    public void Misma_version_con_condiciones_distintas_se_conserva_y_se_advierte()
    {
        var p = Leer("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup Condition="'$(A)'=='1'"><PackageReference Include="X" Version="1.0.0" /></ItemGroup>
              <ItemGroup Condition="'$(A)'=='2'"><PackageReference Include="X" Version="1.0.0" /></ItemGroup>
            </Project>
            """);

        Assert.Equal(2, p.Dependencias.Count);
        Assert.All(p.Dependencias, d =>
        {
            Assert.Contains(d.Advertencias, a => a.Codigo == AdvertenciasLectura.DependenciaDuplicadaConflictiva);
            Assert.Contains(d.Advertencias, a => a.Codigo == AdvertenciasLectura.CondicionNoEvaluada);
        });
    }

    [Fact]
    public void Conserva_la_condicion_de_la_referencia_y_la_del_grupo_sin_evaluarlas()
    {
        var p = Leer("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup Condition="'$(G)'=='1'"><PackageReference Include="X" Version="1.0.0" Condition="'$(R)'=='2'" /></ItemGroup>
            </Project>
            """);

        Assert.Equal("('$(G)'=='1') AND ('$(R)'=='2')", Assert.Single(p.Dependencias).Condicion);
    }

    private static string ConDependencias(int cantidad, string extra = "") =>
        Proyecto("<TargetFramework>net8.0</TargetFramework>",
            string.Concat(Enumerable.Range(1, cantidad).Select(i => Paquete($"Ejemplo.P{i}", "1.0.0"))) + extra);

    [Fact]
    public void Acepta_exactamente_100_dependencias_distintas() =>
        Assert.Equal(100, Leer(ConDependencias(100)).Dependencias.Count);

    [Fact]
    public void Rechaza_101_dependencias_distintas_con_mensaje_claro()
    {
        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer(ConDependencias(101)));

        Assert.Equal(CodigosLectura.LimiteDependenciasExcedido, ex.Codigo);
        Assert.Contains("101", ex.Message);
        Assert.Contains("100", ex.Message);
    }

    [Fact]
    public void Los_duplicados_no_cuentan_como_dependencias_distintas_ni_se_eliminan_para_cumplir_el_limite()
    {
        // 100 distintas + una segunda declaración conflictiva de la primera: 100 distintas (válido), 101 declaraciones conservadas.
        var p = Leer(ConDependencias(100, Paquete("Ejemplo.P1", "9.9.9")));

        Assert.Equal(101, p.Dependencias.Count);
        Assert.Equal(2, p.Dependencias.Count(d => d.Nombre == "Ejemplo.P1"));
    }

    [Fact]
    public void Con_101_distintas_mas_conflictos_se_rechaza_en_lugar_de_recortar_declaraciones()
    {
        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer(ConDependencias(101, Paquete("Ejemplo.P1", "9.9.9"))));

        Assert.Equal(CodigosLectura.LimiteDependenciasExcedido, ex.Codigo);
    }

    // ---------- entrada insegura o inválida ----------

    [Theory]
    [InlineData("""<!DOCTYPE Project [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><Project>&x;</Project>""")]
    [InlineData("""<!DOCTYPE Project [<!ENTITY x SYSTEM "http://127.0.0.1:9/xxe">]><Project>&x;</Project>""")]
    [InlineData("""<!DOCTYPE Project SYSTEM "http://127.0.0.1:9/evil.dtd"><Project />""")]
    [InlineData("""<!DOCTYPE Project [<!ENTITY a "aaaa"><!ENTITY b "&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;">]><Project>&c;</Project>""")]
    public void Un_DOCTYPE_se_rechaza_sin_resolver_entidades_ni_recursos_externos(string xml) =>
        Assert.Equal(CodigosLectura.XmlInvalido, ErrorDe(xml));

    [Theory]
    [InlineData("esto no es xml")]
    [InlineData("<Project><PropertyGroup></Project>")]
    [InlineData("   \r\n  ")]
    [InlineData("<Project>")]
    public void Xml_mal_formado_es_xml_invalido(string xml) =>
        Assert.Equal(CodigosLectura.XmlInvalido, ErrorDe(xml));

    [Theory]
    [InlineData("<html><body/></html>")]
    [InlineData("<Solution />")]
    [InlineData("<project />")]
    public void Xml_sin_elemento_raiz_Project_es_proyecto_invalido(string xml) =>
        Assert.Equal(CodigosLectura.ProyectoInvalido, ErrorDe(xml));

    [Fact]
    public void El_mensaje_de_error_no_expone_detalles_internos()
    {
        var ex = Assert.Throws<ArchivoInvalidoException>(() => Leer("<a><b></a>"));

        Assert.DoesNotContain("XmlException", ex.Message);
        Assert.DoesNotContain("Line", ex.Message);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Anidamiento_profundo_no_provoca_un_fallo_no_controlado()
    {
        var xml = "<Project>" + string.Concat(Enumerable.Repeat("<a>", 40_000)) + string.Concat(Enumerable.Repeat("</a>", 40_000)) + "</Project>";

        var resultado = Record.Exception(() => Leer(xml));

        Assert.True(resultado is null or ArchivoInvalidoException, resultado?.ToString());
    }

    [Fact]
    public void El_tamano_real_se_verifica_aunque_el_flujo_no_declare_longitud()
    {
        using var flujo = new SinLongitud(new MemoryStream(new byte[1024 * 1024 + 1]));

        var ex = Assert.Throws<ArchivoInvalidoException>(() => _lector.Leer("a.csproj", flujo));

        Assert.Equal(CodigosLectura.ArchivoExcedeTamano, ex.Codigo);
    }

    [Fact]
    public void Un_flujo_sin_longitud_y_sin_contenido_es_archivo_vacio()
    {
        using var flujo = new SinLongitud(new MemoryStream());

        Assert.Equal(CodigosLectura.ArchivoVacio, Assert.Throws<ArchivoInvalidoException>(() => _lector.Leer("a.csproj", flujo)).Codigo);
    }

    [Fact]
    public void Contenido_de_exactamente_1_MB_se_acepta()
    {
        var xml = Proyecto("<TargetFramework>net8.0</TargetFramework>", Paquete("A", "1.0.0"));
        var relleno = new string(' ', 1024 * 1024 - Encoding.UTF8.GetByteCount(xml));
        var bytes = Encoding.UTF8.GetBytes(xml + relleno);
        Assert.Equal(1024 * 1024, bytes.Length);

        Assert.Single(_lector.Leer("a.csproj", new MemoryStream(bytes)).Dependencias);
    }

    [Fact]
    public void Un_nombre_con_ruta_se_devuelve_sin_ella()
    {
        var p = Leer(Proyecto("<TargetFramework>net8.0</TargetFramework>", ""), @"C:\Users\ana\proyecto\Mi.csproj");

        Assert.Equal("Mi.csproj", p.NombreArchivo);
    }

    private sealed class SinLongitud(Stream interno) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => interno.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) interno.Dispose(); base.Dispose(disposing); }
    }
}
