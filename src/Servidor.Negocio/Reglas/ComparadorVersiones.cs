namespace Servidor.Negocio.Reglas;

/// <summary>
/// Compara versiones con las reglas de NuGet/SemVer: componentes numéricos (hasta 4; los ausentes valen 0), una versión preliminar es menor que la
/// estable de igual número, las etiquetas preliminares se comparan por campo (numéricos como números, menores que los alfanuméricos) y los metadatos
/// de compilación (<c>+x</c>) se ignoran. NUNCA se compara como texto. Devuelve <c>null</c> si alguna versión no es interpretable.
/// </summary>
public static class ComparadorVersiones
{
    public static int? Comparar(string? a, string? b)
    {
        if (!TryParse(a, out var va) || !TryParse(b, out var vb)) return null;

        for (var i = 0; i < 4; i++)
        {
            var c = CompararNumeros(va.Numeros[i], vb.Numeros[i]);
            if (c != 0) return c;
        }

        return CompararPreliminar(va.Preliminar, vb.Preliminar);
    }

    public static bool EsPreliminar(string? version) => TryParse(version, out var v) && v.Preliminar.Length > 0;

    private readonly record struct Analizada(string[] Numeros, string[] Preliminar);

    private static bool TryParse(string? texto, out Analizada resultado)
    {
        resultado = default;
        if (string.IsNullOrWhiteSpace(texto)) return false;

        var t = texto.Trim();
        var mas = t.IndexOf('+');
        if (mas >= 0) t = t[..mas];

        var guion = t.IndexOf('-');
        var nucleo = guion >= 0 ? t[..guion] : t;
        var pre = guion >= 0 ? t[(guion + 1)..] : "";

        var partes = nucleo.Split('.');
        if (partes.Length is < 1 or > 4 || partes.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) return false;
        if (guion >= 0 && (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))) return false;

        var numeros = new string[4];
        for (var i = 0; i < 4; i++)
        {
            var valor = i < partes.Length ? partes[i].TrimStart('0') : "";
            numeros[i] = valor.Length == 0 ? "0" : valor;
        }

        resultado = new Analizada(numeros, pre.Length == 0 ? [] : pre.Split('.'));
        return true;
    }

    // Sin conversión a número: soporta componentes arbitrariamente largos.
    private static int CompararNumeros(string a, string b) =>
        a.Length != b.Length ? Math.Sign(a.Length.CompareTo(b.Length)) : Math.Sign(string.CompareOrdinal(a, b));

    private static string SinCeros(string s)
    {
        var r = s.TrimStart('0');
        return r.Length == 0 ? "0" : r;
    }

    private static int CompararPreliminar(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;   // la estable es mayor que la preliminar
        if (b.Length == 0) return -1;

        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var numA = a[i].All(char.IsAsciiDigit);
            var numB = b[i].All(char.IsAsciiDigit);
            int c;
            if (numA && numB) c = CompararNumeros(SinCeros(a[i]), SinCeros(b[i]));
            else if (numA) c = -1;
            else if (numB) c = 1;
            else c = Math.Sign(string.Compare(a[i], b[i], StringComparison.OrdinalIgnoreCase));
            if (c != 0) return c;
        }

        return Math.Sign(a.Length.CompareTo(b.Length));
    }
}
