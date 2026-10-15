using Servidor.Negocio.Lectura;

namespace Servidor.Negocio.Tests.Lectura;

public class ValidadorArchivoCsprojTests
{
    private const long UnMb = 1024 * 1024;
    private readonly ValidadorArchivoCsproj _validador = new(new OpcionesLectura());

    private static void EsperaError(string codigo, Action accion) =>
        Assert.Equal(codigo, Assert.Throws<ArchivoInvalidoException>(accion).Codigo);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_nombre_es_archivo_ausente(string? nombre) =>
        EsperaError(CodigosLectura.ArchivoAusente, () => _validador.Validar(nombre, 10));

    [Fact]
    public void Sin_longitud_es_archivo_ausente() =>
        EsperaError(CodigosLectura.ArchivoAusente, () => _validador.Validar("a.csproj", null));

    [Theory]
    [InlineData("a.txt")]
    [InlineData("a.csproj.txt")]
    [InlineData("a.sln")]
    [InlineData("csproj")]
    [InlineData(".csproj")]
    [InlineData("a.csproj ")]
    public void Extension_distinta_o_sin_nombre_es_rechazada(string nombre)
    {
        // "a.csproj " se recorta (los navegadores no lo envían así), por eso se comprueba aparte.
        if (nombre.Trim() == "a.csproj") Assert.Equal("a.csproj", _validador.Validar(nombre, 10));
        else EsperaError(CodigosLectura.ExtensionInvalida, () => _validador.Validar(nombre, 10));
    }

    [Theory]
    [InlineData("a.csproj")]
    [InlineData("A.CSPROJ")]
    [InlineData("Mi.Proyecto.Web.csproj")]
    public void Acepta_la_extension_sin_distinguir_mayusculas(string nombre) =>
        Assert.Equal(nombre, _validador.Validar(nombre, 10));

    [Theory]
    [InlineData(@"C:\Users\ana\src\Mi.csproj", "Mi.csproj")]
    [InlineData("/home/ana/src/Mi.csproj", "Mi.csproj")]
    [InlineData(@"..\..\Mi.csproj", "Mi.csproj")]
    public void El_nombre_nunca_conserva_la_ruta(string recibido, string esperado) =>
        Assert.Equal(esperado, _validador.Validar(recibido, 10));

    [Fact]
    public void Archivo_vacio_es_rechazado() =>
        EsperaError(CodigosLectura.ArchivoVacio, () => _validador.Validar("a.csproj", 0));

    [Fact]
    public void Limite_exacto_de_1_MB_se_acepta_y_un_byte_mas_se_rechaza()
    {
        Assert.Equal("a.csproj", _validador.Validar("a.csproj", UnMb));
        EsperaError(CodigosLectura.ArchivoExcedeTamano, () => _validador.Validar("a.csproj", UnMb + 1));
    }

    [Fact]
    public void El_mensaje_de_tamano_indica_el_maximo() =>
        Assert.Contains("1 MB", Assert.Throws<ArchivoInvalidoException>(() => _validador.Validar("a.csproj", UnMb + 1)).Message);
}
