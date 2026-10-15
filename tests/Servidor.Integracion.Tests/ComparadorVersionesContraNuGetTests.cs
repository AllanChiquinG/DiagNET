using NuGet.Versioning;
using Servidor.Negocio.Reglas;

namespace Servidor.Integracion.Tests;

/// <summary>El comparador propio de Negocio (sin dependencias) debe coincidir con NuGet.Versioning, que es la referencia oficial.</summary>
public class ComparadorVersionesContraNuGetTests
{
    private static readonly string[] Versiones =
    [
        "1.0", "1.0.0", "1.0.0.0", "1.0.0.1", "1.0.1", "1.9.0", "1.10.0", "2.0.0-alpha", "2.0.0-alpha.1", "2.0.0-alpha.beta", "2.0.0-beta", "2.0.0-beta.2",
        "2.0.0-beta.11", "2.0.0-rc.1", "2.0.0-RC.1", "2.0.0-1", "2.0.0-10", "2.0.0", "2.0.0+build5", "10.0.0", "0.0.1", "00001.0.0",
    ];

    [Fact]
    public void Coincide_con_NuGet_Versioning_para_todos_los_pares()
    {
        foreach (var a in Versiones)
            foreach (var b in Versiones)
            {
                var esperado = Math.Sign(NuGetVersion.Parse(a).CompareTo(NuGetVersion.Parse(b)));
                Assert.True(esperado == ComparadorVersiones.Comparar(a, b), $"{a} vs {b}: NuGet={esperado}, propio={ComparadorVersiones.Comparar(a, b)}");
            }
    }
}
