using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Tests.Dominio;

public class NormalizadorPaquetesTests
{
    [Theory]
    [InlineData("Newtonsoft.Json", "newtonsoft.json")]
    [InlineData("  Ejemplo.UNO ", "ejemplo.uno")]
    [InlineData("ABC", "abc")]
    public void El_identificador_se_normaliza_sin_distinguir_mayusculas(string entrada, string esperado) =>
        Assert.Equal(esperado, NormalizadorPaquetes.Identificador(entrada));

    [Theory]
    [InlineData("1", "1.0.0")]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.2.3.4", "1.2.3.4")]
    [InlineData("01.002.0003", "1.2.3")]
    [InlineData("1.0.0+meta", "1.0.0")]
    [InlineData("1.0.0-Beta.1", "1.0.0-beta.1")]
    [InlineData("1.0.0-BETA.1+meta", "1.0.0-beta.1")]
    [InlineData("  2.1.0  ", "2.1.0")]
    [InlineData("10.0.0", "10.0.0")]
    public void La_version_se_lleva_a_su_forma_normal(string entrada, string esperado) =>
        Assert.Equal(esperado, NormalizadorPaquetes.VersionNormalizada(entrada));

    [Theory]
    [InlineData("[1.0,2.0)", "[1.0,2.0)")]
    [InlineData("1.*", "1.*")]
    [InlineData("$(V)", "$(v)")]
    [InlineData("Abc", "abc")]
    public void Lo_que_no_es_una_version_reconocible_se_devuelve_recortado_y_en_minusculas_sin_fallar(string entrada, string esperado) =>
        Assert.Equal(esperado, NormalizadorPaquetes.VersionNormalizada(entrada));

    [Theory]
    [InlineData("99999999999999999999999999.1", "99999999999999999999999999.1.0")]
    [InlineData("0000000000000000000000000001.0.0", "1.0.0")]
    [InlineData("1.2.3.99999999999999999999", "1.2.3.99999999999999999999")]
    public void Los_numeros_enormes_no_provocan_desbordamiento(string entrada, string esperado) =>
        Assert.Equal(esperado, NormalizadorPaquetes.VersionNormalizada(entrada));

    [Fact]
    public void Versiones_distintas_siguen_siendo_distintas()
    {
        Assert.NotEqual(NormalizadorPaquetes.VersionNormalizada("1.0.1"), NormalizadorPaquetes.VersionNormalizada("1.0.0"));
        Assert.NotEqual(NormalizadorPaquetes.VersionNormalizada("1.0.0-beta"), NormalizadorPaquetes.VersionNormalizada("1.0.0"));
        Assert.NotEqual(NormalizadorPaquetes.VersionNormalizada("1.10.0"), NormalizadorPaquetes.VersionNormalizada("1.1.0"));
    }
}
