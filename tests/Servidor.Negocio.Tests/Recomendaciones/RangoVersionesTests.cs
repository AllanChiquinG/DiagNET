using Servidor.Negocio.Recomendaciones;

namespace Servidor.Negocio.Tests.Recomendaciones;

public class RangoVersionesTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.0", false)]
    [InlineData("1.0.0", "1.0.0", true)]
    [InlineData("1.0.0", "9.9.9", true)]
    [InlineData("[1.0.0]", "1.0.0", true)]
    [InlineData("[1.0.0]", "1.0.1", false)]
    [InlineData("[1.0.0,2.0.0)", "1.9.9", true)]
    [InlineData("[1.0.0,2.0.0)", "2.0.0", false)]
    [InlineData("[1.0.0,2.0.0]", "2.0.0", true)]
    [InlineData("(1.0.0,2.0.0)", "1.0.0", false)]
    [InlineData("(,2.0.0]", "0.1.0", true)]
    [InlineData("(,2.0.0]", "2.0.1", false)]
    [InlineData("[2.0.0, )", "2.0.0", true)]
    [InlineData("[2.0.0, )", "1.9.9", false)]
    [InlineData("[1.0.0,2.0.0)", "2.0.0-beta", true)]   // una preliminar de 2.0.0 es menor que 2.0.0 (como en NuGet)
    public void Contiene_respeta_los_limites_y_su_inclusion(string rango, string version, bool esperado)
    {
        Assert.Equal(esperado, RangoVersiones.Analizar(rango)!.Contiene(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[")]
    [InlineData("[2.0.0,1.0.0]")]
    [InlineData("(1.0.0)")]
    [InlineData("[1.0.0,2.0.0,3.0.0]")]
    [InlineData("1.*")]
    [InlineData("[1.*,2.0.0)")]
    [InlineData("texto")]
    public void Un_rango_no_interpretable_devuelve_null_para_no_fingir_que_se_respeta(string? rango)
    {
        Assert.Null(RangoVersiones.Analizar(rango));
    }

    [Fact]
    public void Expone_la_minima_inclusiva_para_poder_verificarla()
    {
        var r = RangoVersiones.Analizar("[1.5.0,2.0.0)")!;
        Assert.Equal(("1.5.0", true, "2.0.0", false), (r.Minima, r.MinimaInclusiva, r.Maxima, r.MaximaInclusiva));
    }
}
