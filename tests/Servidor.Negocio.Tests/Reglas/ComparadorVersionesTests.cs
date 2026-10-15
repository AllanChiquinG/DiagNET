using Servidor.Negocio.Reglas;

namespace Servidor.Negocio.Tests.Reglas;

public class ComparadorVersionesTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0.0", "1.0.0", 0)]
    [InlineData("1.0.0+abc", "1.0.0", 0)]
    [InlineData("2.0.0-beta", "2.0.0", -1)]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.10", -1)]
    [InlineData("2.0.0-alpha", "2.0.0-beta", -1)]
    [InlineData("2.0.0-1", "2.0.0-alpha", -1)]
    [InlineData("2.0.0-RC1", "2.0.0-rc1", 0)]
    [InlineData("2.0.0-beta", "2.0.0-beta.1", -1)]
    [InlineData("1.0.0.1", "1.0.0", 1)]
    [InlineData("00010.0.0", "9.0.0", 1)]
    [InlineData("99999999999999999999.0.0", "99999999999999999998.0.0", 1)]
    public void Compara_con_reglas_de_NuGet_y_no_como_texto(string a, string b, int esperado)
    {
        Assert.Equal(esperado, ComparadorVersiones.Comparar(a, b));
        Assert.Equal(-esperado, ComparadorVersiones.Comparar(b, a));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.0.0-")]
    [InlineData("[1.0.0,2.0.0)")]
    [InlineData("$(V)")]
    public void Una_version_no_interpretable_no_se_compara(string? mala)
    {
        Assert.Null(ComparadorVersiones.Comparar(mala, "1.0.0"));
        Assert.Null(ComparadorVersiones.Comparar("1.0.0", mala));
    }
}
