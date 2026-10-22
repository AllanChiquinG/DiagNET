using System.Globalization;
using System.Text;
using PdfSharp.Drawing;

namespace Servidor.Integracion.Pdf;

/// <summary>
/// Prepara TODO texto que llega de los datos antes de dibujarlo, con dos garantías:
/// 1) <b>Nada se pierde ni se sustituye en silencio.</b> Un carácter que la fuente incrustada no puede dibujar (escrituras no cubiertas, emoji, controles) se escribe
///    de forma visible como «‹U+XXXX›» (su punto de código), se cuenta y el reporte lo avisa. Los datos guardados y la pantalla no cambian.
/// 2) <b>Nada se sale de su celda.</b> MigraDoc parte las líneas por espacios pero no parte una palabra más ancha que el espacio disponible, y el texto se solaparía con
///    la columna vecina. Se MIDE con la fuente real y se inserta un espacio de ancho cero (U+200B) tras separadores habituales y, si aun así un tramo no cabe, justo
///    antes del carácter que desbordaría.
/// El estado es estático porque la generación está serializada (ver <see cref="GeneradorPdf"/>).
/// </summary>
internal static class AjusteTexto
{
    private const char SinAncho = '​';
    private const double PuntosPorMm = 72.0 / 25.4;

    /// <summary>Margen que se descuenta del ancho de una celda: relleno interior de MigraDoc (≈1,2 mm por lado) más holgura por el redondeo de la medición.</summary>
    public const double HolguraCeldaMm = 4;

    private static readonly Dictionary<(string, double, bool), double> Anchos = [];
    private static XGraphics? _medidor;
    private static CoberturaFuente? _cobertura;

    private static readonly SortedSet<int> _noSoportados = [];
    private static int _ocurrencias;

    /// <summary>Puntos de código sin glifo hallados desde el último <see cref="Reiniciar"/>, en orden.</summary>
    public static IReadOnlyCollection<int> NoSoportados => _noSoportados;

    /// <summary>Cuántos caracteres (con repeticiones) se mostraron como «‹U+XXXX›».</summary>
    public static int Ocurrencias => _ocurrencias;

    public static void Reiniciar()
    {
        _noSoportados.Clear();
        _ocurrencias = 0;
    }

    /// <summary>Debe llamarse con el candado de generación tomado (la caché y el contexto de medición no son seguros entre hilos).</summary>
    public static string Partir(string texto, double anchoMm, double puntos = 9, bool negrita = false)
    {
        if (string.IsNullOrEmpty(texto)) return texto;
        var limite = (anchoMm - HolguraCeldaMm) * PuntosPorMm;
        if (limite <= 10) limite = 10;

        var sb = new StringBuilder(texto.Length + texto.Length / 8);
        double tramo = 0;
        foreach (var rune in texto.EnumerateRunes())
        {
            // 1) Carácter sin glifo: se escribe su punto de código, visible. Saltos y tabuladores son separadores, no glifos.
            string grafema;
            if (rune.Value is '\n' or '\r' or '\t' || Cobertura.Tiene(rune.Value)) grafema = rune.ToString();
            else
            {
                _noSoportados.Add(rune.Value);
                _ocurrencias++;
                grafema = string.Create(CultureInfo.InvariantCulture, $"‹U+{rune.Value:X4}›");
            }

            // 2) Partir tramos sin espacios que no caben.
            foreach (var c in grafema)
            {
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                    tramo = 0;
                    continue;
                }

                var ancho = AnchoDe(c, puntos, negrita);
                if (tramo + ancho > limite)
                {
                    sb.Append(SinAncho);
                    tramo = 0;
                }

                sb.Append(c);
                tramo += ancho;
                if (c is '.' or '-' or '_' or '/' or '\\' or ',' or ';' or ':' or '+' or ')' or ']' or '›')
                {
                    sb.Append(SinAncho);   // oportunidad natural de corte (más legible que cortar a media palabra)
                    tramo = 0;
                }
            }
        }

        return sb.ToString();
    }

    private static CoberturaFuente Cobertura => _cobertura ??= new CoberturaFuente(new FuentesPdf().GetFont("LiberationSans-Regular")!);

    private static double AnchoDe(char c, double puntos, bool negrita)
    {
        var clave = (c.ToString(), puntos, negrita);
        if (Anchos.TryGetValue(clave, out var ya)) return ya;
        _medidor ??= XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var fuente = new XFont(FuentesPdf.Familia, puntos, negrita ? XFontStyleEx.Bold : XFontStyleEx.Regular);
        var ancho = _medidor.MeasureString(c.ToString(), fuente).Width;
        Anchos[clave] = ancho;
        return ancho;
    }
}
