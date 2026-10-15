using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Compartido.Contratos;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Servidor.Negocio.Lectura;

namespace Servidor.Api.Tests;

public class LecturaProyectoTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Ruta = "/api/proyectos/lectura";
    private const int UnMb = 1024 * 1024;

    private readonly WebApplicationFactory<Program> _fabrica;
    private readonly HttpClient _http;

    public LecturaProyectoTests(WebApplicationFactory<Program> fabrica)
    {
        _fabrica = fabrica;
        _http = fabrica.CreateClient();
    }

    private static string Proyecto(string tfm, int dependencias = 2) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFrameworks>{tfm}</TargetFrameworks></PropertyGroup>
          <ItemGroup>{string.Concat(Enumerable.Range(1, dependencias).Select(i => $"""<PackageReference Include="Ejemplo.P{i}" Version="1.{i}.0" />"""))}</ItemGroup>
        </Project>
        """;

    private static MultipartFormDataContent Formulario(byte[] contenido, string nombre, string campo = "archivo")
    {
        var archivo = new ByteArrayContent(contenido);
        archivo.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        return new MultipartFormDataContent { { archivo, campo, nombre } };
    }

    private Task<HttpResponseMessage> Enviar(string xml, string nombre = "Mi.csproj", string campo = "archivo") =>
        _http.PostAsync(Ruta, Formulario(Encoding.UTF8.GetBytes(xml), nombre, campo));

    private static async Task<ProblemaDto> Problema(HttpResponseMessage respuesta)
    {
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        var problema = await respuesta.Content.ReadFromJsonAsync<ProblemaDto>();
        Assert.NotNull(problema);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
        Assert.Equal(CodigosError.Definicion(problema.Codigo!).Status, (int)respuesta.StatusCode);
        return problema;
    }

    // ---------- éxito ----------

    [Fact]
    public async Task Archivo_valido_devuelve_200_con_frameworks_y_dependencias()
    {
        var respuesta = await Enviar(Proyecto("net8.0;net10.0"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var dto = await respuesta.Content.ReadFromJsonAsync<ProyectoLeidoDto>();
        Assert.NotNull(dto);
        Assert.Equal("Mi.csproj", dto.NombreArchivo);
        Assert.Equal(["net8.0", "net10.0"], dto.Frameworks.Select(f => f.Valor));
        Assert.Equal(["Ejemplo.P1", "Ejemplo.P2"], dto.Dependencias.Select(d => d.Nombre));
        Assert.All(dto.Dependencias, d => Assert.Equal(TipoVersionDeclarada.Exacta, d.TipoVersion));
        Assert.Empty(dto.Advertencias);
    }

    [Fact]
    public async Task La_respuesta_usa_los_nombres_de_enumeracion_del_contrato()
    {
        var texto = await (await Enviar("""<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="A" Version="$(V)" /></ItemGroup></Project>""")).Content.ReadAsStringAsync();

        Assert.Contains("\"tipoVersion\":\"propiedad\"", texto);
        Assert.Contains("\"codigo\":\"version_no_resuelta\"", texto);
    }

    [Fact]
    public async Task El_nombre_con_ruta_se_devuelve_sin_ella()
    {
        var dto = await (await Enviar(Proyecto("net8.0"), @"C:\Users\ana\src\Mi.csproj")).Content.ReadFromJsonAsync<ProyectoLeidoDto>();

        Assert.Equal("Mi.csproj", dto!.NombreArchivo);
    }

    [Fact]
    public async Task Mezcla_de_frameworks_acepta_con_advertencia()
    {
        var dto = await (await Enviar(Proyecto("net8.0;net48"))).Content.ReadFromJsonAsync<ProyectoLeidoDto>();

        Assert.Equal(new bool?[] { true, false }, dto!.Frameworks.Select(f => f.EnAlcance));
        Assert.Contains(dto.Advertencias, a => a.Codigo == "framework_fuera_de_alcance_omitido");
    }

    [Fact]
    public async Task Framework_con_variable_se_lee_con_advertencia_y_alcance_indeterminado()
    {
        var dto = await (await Enviar(Proyecto("$(Objetivo)"))).Content.ReadFromJsonAsync<ProyectoLeidoDto>();

        Assert.Null(dto!.Frameworks.Single().EnAlcance);
        Assert.Contains(dto.Advertencias, a => a.Codigo == "framework_no_resuelto");
    }

    [Fact]
    public async Task Acepta_100_dependencias_y_el_limite_de_1_MB_exacto()
    {
        var xml = Proyecto("net8.0", 100);
        var relleno = new string(' ', UnMb - Encoding.UTF8.GetByteCount(xml));

        var respuesta = await _http.PostAsync(Ruta, Formulario(Encoding.UTF8.GetBytes(xml + relleno), "Grande.csproj"));

        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        Assert.Equal(100, (await respuesta.Content.ReadFromJsonAsync<ProyectoLeidoDto>())!.Dependencias.Count);
    }

    // ---------- errores de entrada ----------

    [Fact]
    public async Task Extension_invalida_devuelve_400()
    {
        var respuesta = await Enviar(Proyecto("net8.0"), "Mi.txt");

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Equal(CodigosError.ExtensionInvalida, (await Problema(respuesta)).Codigo);
    }

    [Fact]
    public async Task Archivo_vacio_devuelve_400()
    {
        var respuesta = await _http.PostAsync(Ruta, Formulario([], "Mi.csproj"));

        Assert.Equal(CodigosError.ArchivoVacio, (await Problema(respuesta)).Codigo);
    }

    [Fact]
    public async Task Un_byte_sobre_1_MB_devuelve_400_archivo_excede_tamano()
    {
        var respuesta = await _http.PostAsync(Ruta, Formulario(new byte[UnMb + 1], "Mi.csproj"));

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await Problema(respuesta);
        Assert.Equal(CodigosError.ArchivoExcedeTamano, problema.Codigo);
        Assert.Contains("1 MB", problema.Detail);
    }

    [Fact]
    public async Task Cuerpo_sobre_2_MB_devuelve_413()
    {
        var respuesta = await _http.PostAsync(Ruta, Formulario(new byte[2 * UnMb + 10], "Mi.csproj"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, respuesta.StatusCode);
        Assert.Equal(CodigosError.CuerpoExcedeTamano, (await Problema(respuesta)).Codigo);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("")]
    public async Task Campo_con_otro_nombre_devuelve_archivo_ausente(string campo)
    {
        var respuesta = await _http.PostAsync(Ruta, Formulario(Encoding.UTF8.GetBytes(Proyecto("net8.0")), "Mi.csproj", campo.Length == 0 ? "otro" : campo));

        Assert.Equal(CodigosError.ArchivoAusente, (await Problema(respuesta)).Codigo);
    }

    [Fact]
    public async Task Cuerpo_que_no_es_multipart_devuelve_archivo_ausente()
    {
        var respuesta = await _http.PostAsJsonAsync(Ruta, new { archivo = "x" });

        Assert.Equal(CodigosError.ArchivoAusente, (await Problema(respuesta)).Codigo);
    }

    [Fact]
    public async Task Sin_cuerpo_devuelve_archivo_ausente()
    {
        var respuesta = await _http.PostAsync(Ruta, null);

        Assert.Equal(CodigosError.ArchivoAusente, (await Problema(respuesta)).Codigo);
    }

    [Theory]
    [InlineData("no es xml")]
    [InlineData("<Project><PropertyGroup></Project>")]
    [InlineData("""<!DOCTYPE Project [<!ENTITY x SYSTEM "file:///C:/Windows/win.ini">]><Project>&x;</Project>""")]
    public async Task Xml_invalido_o_con_DTD_devuelve_400_xml_invalido(string xml)
    {
        var respuesta = await Enviar(xml);

        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        var problema = await Problema(respuesta);
        Assert.Equal(CodigosError.XmlInvalido, problema.Codigo);
        Assert.DoesNotContain("win.ini", problema.Detail ?? "");
    }

    [Fact]
    public async Task Xml_que_no_es_un_proyecto_devuelve_400_proyecto_invalido()
    {
        var respuesta = await Enviar("<html><body/></html>");

        Assert.Equal(CodigosError.ProyectoInvalido, (await Problema(respuesta)).Codigo);
    }

    [Fact]
    public async Task Todos_los_frameworks_fuera_de_alcance_devuelve_422_con_mensaje_claro()
    {
        var respuesta = await Enviar(Proyecto("net6.0;netstandard2.0"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        var problema = await Problema(respuesta);
        Assert.Equal(CodigosError.FrameworkFueraDeAlcance, problema.Codigo);
        Assert.Contains(".NET 8 y .NET 10", problema.Detail);
        Assert.Contains("net6.0", problema.Detail);
    }

    [Fact]
    public async Task Mas_de_100_dependencias_distintas_devuelve_422()
    {
        var respuesta = await Enviar(Proyecto("net8.0", 101));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, respuesta.StatusCode);
        var problema = await Problema(respuesta);
        Assert.Equal(CodigosError.LimiteDependenciasExcedido, problema.Codigo);
        Assert.Contains("101", problema.Detail);
    }

    [Fact]
    public async Task Un_error_de_entrada_no_deja_rastro_del_contenido_en_la_respuesta()
    {
        var respuesta = await Enviar("<secreto-del-usuario>");

        var texto = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secreto-del-usuario", texto);
    }

    // ---------- coherencia de la configuración ----------

    [Fact]
    public void Los_limites_de_la_API_coinciden_con_los_del_contrato_compartido()
    {
        var lectura = _fabrica.Services.GetRequiredService<OpcionesLectura>();
        var api = _fabrica.Services.GetRequiredService<Servidor.Api.Endpoints.OpcionesApi>();

        Assert.Equal(LimitesEntrada.TamanoMaximoArchivoBytes, lectura.TamanoMaximoArchivoBytes);
        Assert.Equal(LimitesEntrada.MaximoDependencias, lectura.MaximoDependencias);
        Assert.Equal(LimitesEntrada.TamanoMaximoCuerpoBytes, api.TamanoMaximoCuerpoBytes);
        Assert.True(api.TamanoMaximoCuerpoBytes > lectura.TamanoMaximoArchivoBytes);
    }

    [Fact]
    public void El_formulario_se_mantiene_en_memoria_para_no_escribir_el_csproj_en_disco()
    {
        var formulario = _fabrica.Services.GetRequiredService<IOptions<FormOptions>>().Value;

        Assert.True(formulario.MemoryBufferThreshold >= LimitesEntrada.TamanoMaximoCuerpoBytes);
    }

    [Fact]
    public void Los_codigos_de_error_de_Negocio_existen_en_el_catalogo_publico()
    {
        var codigos = typeof(CodigosLectura).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!);
        var catalogo = CodigosError.Todos.Select(d => d.Codigo).ToHashSet();

        Assert.All(codigos, c => Assert.Contains(c, catalogo));
    }

    [Fact]
    public void Los_tipos_de_version_de_Negocio_y_del_contrato_tienen_los_mismos_valores()
    {
        Assert.Equal(
            Enum.GetNames<TipoVersion>().Order(),
            Enum.GetNames<TipoVersionDeclarada>().Order());
    }
}
