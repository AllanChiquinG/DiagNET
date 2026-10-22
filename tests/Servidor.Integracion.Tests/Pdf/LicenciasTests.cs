namespace Servidor.Integracion.Tests.Pdf;

/// <summary>Guardas de la decisión de licencias de E10: versión fijada y avisos conservados. Si fallan, hay que revisar la licencia antes de seguir.</summary>
public class LicenciasTests
{
    private static string Raiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagNET.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }

    [Fact]
    public void La_biblioteca_PDF_esta_fijada_a_la_version_MIT_6_2_4_sin_rangos()
    {
        var csproj = File.ReadAllText(Path.Combine(Raiz(), "src", "Servidor.Integracion", "Servidor.Integracion.csproj"));

        Assert.Contains("<PackageReference Include=\"PDFsharp-MigraDoc\" Version=\"[6.2.4]\" />", csproj);
        Assert.DoesNotContain("Include=\"PDFsharp\" ", csproj.Replace("PDFsharp-MigraDoc", ""));   // ninguna otra referencia flotante a PDFsharp
    }

    [Fact]
    public void Los_avisos_de_licencia_de_la_biblioteca_y_de_las_fuentes_estan_conservados()
    {
        var avisos = File.ReadAllText(Path.Combine(Raiz(), "THIRD_PARTY_NOTICES.md"));
        Assert.Contains("MIT License", avisos);
        Assert.Contains("empira", avisos);
        Assert.Contains("SIL Open Font License 1.1", avisos);
        Assert.Contains("Verificado el 21 de octubre de 2026", avisos);
        Assert.Contains("revisar de nuevo su licencia antes de cambiar de versión", avisos);

        var fuentes = Path.Combine(Raiz(), "src", "Servidor.Integracion", "Pdf", "Fuentes");
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", File.ReadAllText(Path.Combine(fuentes, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(fuentes, "AUTHORS")));
        foreach (var f in new[] { "Regular", "Bold", "Italic", "BoldItalic" })
            Assert.True(File.Exists(Path.Combine(fuentes, $"LiberationSans-{f}.ttf")));
    }
}
