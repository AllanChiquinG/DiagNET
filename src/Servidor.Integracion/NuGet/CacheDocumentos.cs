namespace Servidor.Integracion.NuGet;

/// <summary>Caché en memoria de documentos ya interpretados (registro y páginas), con vigencia corta y tamaño acotado. Evita leer dos veces el mismo paquete en un análisis.</summary>
internal sealed class CacheDocumentos(TimeSpan vigencia, int maximo, TimeProvider tiempo)
{
    private readonly object _candado = new();
    private readonly Dictionary<string, (DateTimeOffset Expira, object Valor)> _datos = new(StringComparer.Ordinal);

    public bool TryGet<T>(Uri uri, out T valor) where T : class
    {
        lock (_candado)
        {
            if (_datos.TryGetValue(uri.AbsoluteUri, out var e) && tiempo.GetUtcNow() < e.Expira && e.Valor is T t)
            {
                valor = t;
                return true;
            }
        }

        valor = null!;
        return false;
    }

    public void Set(Uri uri, object valor)
    {
        lock (_candado)
        {
            var ahora = tiempo.GetUtcNow();
            if (_datos.Count >= maximo)
            {
                foreach (var vencida in _datos.Where(p => p.Value.Expira <= ahora).Select(p => p.Key).ToList()) _datos.Remove(vencida);
                if (_datos.Count >= maximo)
                    _datos.Remove(_datos.OrderBy(p => p.Value.Expira).First().Key);
            }

            _datos[uri.AbsoluteUri] = (ahora + vigencia, valor);
        }
    }

    public int Cantidad
    {
        get { lock (_candado) return _datos.Count; }
    }
}
