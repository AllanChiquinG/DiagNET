namespace Servidor.Integracion.Pdf;

/// <summary>
/// Dice si la fuente incrustada tiene glifo para un carácter, leyendo la tabla <c>cmap</c> del archivo TrueType (formatos 4 y 12). Así se sabe CON EXACTITUD qué
/// caracteres no se podrían dibujar (los de escrituras que Liberation Sans no cubre, emoji, controles) en lugar de dejar que salgan como un cuadro vacío o se pierdan.
/// </summary>
internal sealed class CoberturaFuente
{
    private readonly byte[] _datos;
    private readonly (int Inicio, int Fin, int Delta, int OffsetRango, int PosOffsetRango)[] _bmp;
    private readonly (uint Inicio, uint Fin, uint GlifoInicial)[] _plano;

    public CoberturaFuente(byte[] ttf)
    {
        _datos = ttf;
        var tablas = U16(4);
        var cmap = -1;
        for (var i = 0; i < tablas; i++)
        {
            var reg = 12 + i * 16;
            if (Etiqueta(reg) == "cmap") cmap = (int)U32(reg + 8);
        }

        if (cmap < 0) throw new InvalidOperationException("La fuente no tiene tabla cmap.");

        var subtablas = U16(cmap + 2);
        var f4 = -1;
        var f12 = -1;
        for (var i = 0; i < subtablas; i++)
        {
            var reg = cmap + 4 + i * 8;
            int plataforma = U16(reg), codificacion = U16(reg + 2);
            var pos = cmap + (int)U32(reg + 4);
            var formato = U16(pos);
            if (formato == 4 && (plataforma == 3 && codificacion == 1 || plataforma == 0) && f4 < 0) f4 = pos;
            if (formato == 12 && (plataforma == 3 && codificacion == 10 || plataforma == 0) && f12 < 0) f12 = pos;
        }

        _bmp = f4 < 0 ? [] : LeerFormato4(f4);
        _plano = f12 < 0 ? [] : LeerFormato12(f12);
    }

    public bool Tiene(int puntoDeCodigo)
    {
        if (puntoDeCodigo is < 0 or > 0x10FFFF) return false;
        if (puntoDeCodigo <= 0xFFFF)
        {
            foreach (var s in _bmp)
            {
                if (puntoDeCodigo > s.Fin) continue;
                if (puntoDeCodigo < s.Inicio) return false;
                int glifo;
                if (s.OffsetRango == 0) glifo = (puntoDeCodigo + s.Delta) & 0xFFFF;
                else
                {
                    var pos = s.PosOffsetRango + s.OffsetRango + (puntoDeCodigo - s.Inicio) * 2;
                    if (pos + 1 >= _datos.Length) return false;
                    var g = U16(pos);
                    glifo = g == 0 ? 0 : (g + s.Delta) & 0xFFFF;
                }

                return glifo != 0;
            }

            return false;
        }

        foreach (var g in _plano)
            if (puntoDeCodigo >= g.Inicio && puntoDeCodigo <= g.Fin) return g.GlifoInicial + (uint)(puntoDeCodigo - g.Inicio) != 0;
        return false;
    }

    private (int, int, int, int, int)[] LeerFormato4(int pos)
    {
        var segmentos = U16(pos + 6) / 2;
        var fin = pos + 14;
        var inicio = fin + segmentos * 2 + 2;
        var delta = inicio + segmentos * 2;
        var rango = delta + segmentos * 2;
        var r = new (int, int, int, int, int)[segmentos];
        for (var i = 0; i < segmentos; i++)
        {
            var d = U16(delta + i * 2);
            r[i] = (U16(inicio + i * 2), U16(fin + i * 2), d >= 0x8000 ? d - 0x10000 : d, U16(rango + i * 2), rango + i * 2);
        }

        return r;
    }

    private (uint, uint, uint)[] LeerFormato12(int pos)
    {
        var n = (int)U32(pos + 12);
        var r = new (uint, uint, uint)[n];
        for (var i = 0; i < n; i++)
        {
            var g = pos + 16 + i * 12;
            r[i] = (U32(g), U32(g + 4), U32(g + 8));
        }

        return r;
    }

    private string Etiqueta(int pos) => System.Text.Encoding.ASCII.GetString(_datos, pos, 4);

    private int U16(int pos) => _datos[pos] << 8 | _datos[pos + 1];

    private uint U32(int pos) => (uint)(_datos[pos] << 24 | _datos[pos + 1] << 16 | _datos[pos + 2] << 8 | _datos[pos + 3]);
}
