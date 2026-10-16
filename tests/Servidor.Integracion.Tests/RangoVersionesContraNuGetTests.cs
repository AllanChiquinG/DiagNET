using NuGet.Versioning;
using Servidor.Negocio.Recomendaciones;

namespace Servidor.Integracion.Tests;

/// <summary>El intérprete propio de rangos (Negocio, sin dependencias) debe coincidir con NuGet.Versioning.VersionRange.</summary>
public class RangoVersionesContraNuGetTests
{
    private static readonly string[] Rangos =
    [
        "1.0.0", "[1.0.0]", "[1.0.0,2.0.0)", "[1.0.0,2.0.0]", "(1.0.0,2.0.0)", "(1.0.0,2.0.0]", "(,2.0.0]", "(,2.0.0)", "[2.0.0, )", "(2.0.0,)", "[1.5.0,)",
    ];

    private static readonly string[] Versiones = ["0.1.0", "1.0.0", "1.0.1", "1.5.0", "1.9.9", "2.0.0-beta", "2.0.0", "2.0.1", "3.0.0"];

    [Fact]
    public void Coincide_con_VersionRange_de_NuGet_para_todas_las_combinaciones()
    {
        foreach (var rango in Rangos)
            foreach (var version in Versiones)
            {
                var esperado = VersionRange.Parse(rango).Satisfies(NuGetVersion.Parse(version));
                var propio = RangoVersiones.Analizar(rango)!.Contiene(version);
                Assert.True(esperado == propio, $"{rango} contiene {version}: NuGet={esperado}, propio={propio}");
            }
    }
}
