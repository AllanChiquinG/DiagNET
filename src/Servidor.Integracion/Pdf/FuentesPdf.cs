using System.Reflection;
using PdfSharp.Fonts;

namespace Servidor.Integracion.Pdf;

/// <summary>
/// Resuelve la tipografía del reporte desde fuentes INCRUSTADAS en el ensamblado (Liberation Sans, licencia SIL OFL 1.1): el PDF sale igual en Windows, Linux y Docker
/// y no depende de las fuentes instaladas. Cubre el español (tildes, ñ, signos de apertura) y el latín ampliado.
/// </summary>
internal sealed class FuentesPdf : IFontResolver
{
    public const string Familia = "Liberation Sans";

    private static readonly object Candado = new();
    private static bool registrada;

    public static void Registrar()
    {
        lock (Candado)
        {
            if (registrada) return;
            GlobalFontSettings.FontResolver = new FuentesPdf();
            registrada = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        // Cualquier familia pedida se resuelve a Liberation Sans: el reporte no usa otra, y así nunca falla por una fuente ausente.
        var cara = (isBold, isItalic) switch
        {
            (true, true) => "LiberationSans-BoldItalic",
            (true, false) => "LiberationSans-Bold",
            (false, true) => "LiberationSans-Italic",
            _ => "LiberationSans-Regular",
        };
        return new FontResolverInfo(cara);
    }

    public byte[]? GetFont(string faceName)
    {
        using var flujo = typeof(FuentesPdf).Assembly.GetManifestResourceStream($"Fuentes.{faceName}.ttf")
                          ?? throw new InvalidOperationException($"Falta la fuente incrustada «{faceName}».");
        using var memoria = new MemoryStream();
        flujo.CopyTo(memoria);
        return memoria.ToArray();
    }
}
