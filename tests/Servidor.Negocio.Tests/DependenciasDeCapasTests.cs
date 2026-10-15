using System.Reflection;

namespace Servidor.Negocio.Tests;

/// <summary>RNF-12: el núcleo de negocio no depende de EF, HTTP, API ni Contratos.</summary>
public class DependenciasDeCapasTests
{
    private static readonly Assembly Negocio = typeof(Servidor.Negocio.MarcadorAssembly).Assembly;

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
        var referencias = Negocio.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(prohibido, referencias);
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
