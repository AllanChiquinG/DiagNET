using Servidor.Negocio.Reglas;

namespace Servidor.Negocio.Recomendaciones;

/// <summary>
/// Rango de versiones de NuGet en notación de intervalos (el que publica <c>alternatePackage.range</c>): <c>1.0</c> (mínimo inclusivo), <c>[1.0]</c> (exacta),
/// <c>[1.0,2.0)</c>, <c>(1.0,]</c>, <c>(,2.0]</c>. No admite versiones flotantes (<c>*</c>): devuelve <c>null</c> y quien lo use debe declarar que no pudo respetar el rango.
/// </summary>
public sealed class RangoVersiones
{
    public string? Minima { get; }
    public bool MinimaInclusiva { get; }
    public string? Maxima { get; }
    public bool MaximaInclusiva { get; }
    public string Texto { get; }

    private RangoVersiones(string texto, string? min, bool minInc, string? max, bool maxInc)
    {
        Texto = texto;
        Minima = min;
        MinimaInclusiva = minInc;
        Maxima = max;
        MaximaInclusiva = maxInc;
    }

    public static RangoVersiones? Analizar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var t = texto.Trim();

        if (t[0] is not ('[' or '('))
            return ComparadorVersiones.Comparar(t, "0") is null || t.Contains('*') ? null : new RangoVersiones(t, t, true, null, false);

        if (t.Length < 3 || t[^1] is not (']' or ')')) return null;
        var minInc = t[0] == '[';
        var maxInc = t[^1] == ']';
        var cuerpo = t[1..^1];

        var coma = cuerpo.IndexOf(',');
        if (coma < 0)
        {
            // [1.0] = exacta; (1.0) o [1.0) no son válidos.
            var unica = cuerpo.Trim();
            return minInc && maxInc && ComparadorVersiones.Comparar(unica, "0") is not null ? new RangoVersiones(t, unica, true, unica, true) : null;
        }

        var min = cuerpo[..coma].Trim();
        var max = cuerpo[(coma + 1)..].Trim();
        if (max.Contains(',')) return null;
        if (min.Length > 0 && ComparadorVersiones.Comparar(min, "0") is null) return null;
        if (max.Length > 0 && ComparadorVersiones.Comparar(max, "0") is null) return null;
        if (min.Length > 0 && max.Length > 0 && ComparadorVersiones.Comparar(min, max) > 0) return null;
        return new RangoVersiones(t, min.Length == 0 ? null : min, minInc, max.Length == 0 ? null : max, maxInc);
    }

    public bool Contiene(string version)
    {
        if (Minima is not null)
        {
            var c = ComparadorVersiones.Comparar(version, Minima);
            if (c is null || c < 0 || c == 0 && !MinimaInclusiva) return false;
        }

        if (Maxima is not null)
        {
            var c = ComparadorVersiones.Comparar(version, Maxima);
            if (c is null || c > 0 || c == 0 && !MaximaInclusiva) return false;
        }

        return true;
    }
}
