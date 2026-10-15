using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Servidor.Negocio.Tests;

/// <summary>
/// RNF-12: el núcleo de negocio no depende de EF, HTTP, API ni Contratos.
/// Se leen los metadatos de la DLL sin cargarla ni ejecutarla, para no depender de que el sistema permita cargarla.
/// </summary>
public class DependenciasDeCapasTests
{
    private static IReadOnlyList<string> ReferenciasDeNegocio()
    {
        var ruta = Path.Combine(AppContext.BaseDirectory, "Servidor.Negocio.dll");
        using var flujo = File.OpenRead(ruta);
        using var pe = new PEReader(flujo);
        var lector = pe.GetMetadataReader();
        return lector.AssemblyReferences
            .Select(h => lector.GetString(lector.GetAssemblyReference(h).Name))
            .ToList();
    }

    [Theory]
    [InlineData("Compartido.Contratos")]
    [InlineData("Servidor.Api")]
    [InlineData("Servidor.Datos")]
    [InlineData("Servidor.Integracion")]
    [InlineData("Cliente.Web")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore.App")]
    public void Negocio_no_referencia_capas_externas(string prohibido)
    {
        Assert.DoesNotContain(prohibido, ReferenciasDeNegocio());
    }

    [Fact]
    public void Proyecto_de_negocio_no_declara_referencias_de_proyecto_ni_paquetes()
    {
        // El ensamblado solo lista referencias realmente usadas; el .csproj garantiza que ni siquiera se declaren.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagNET.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var csproj = File.ReadAllText(Path.Combine(dir.FullName, "src", "Servidor.Negocio", "Servidor.Negocio.csproj"));

        Assert.DoesNotContain("ProjectReference", csproj);
        Assert.DoesNotContain("PackageReference", csproj);
    }
}
