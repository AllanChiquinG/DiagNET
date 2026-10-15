namespace Servidor.Integracion.NuGet;

/// <summary>
/// Disyuntor con semántica propia (no la predeterminada por porcentaje): se abre tras N operaciones CONSECUTIVAS que agotaron sus reintentos
/// y descansa un tiempo fijo. Pasado ese tiempo deja pasar una sola operación de prueba; si tiene éxito se cierra, si falla vuelve a abrirse.
/// Un éxito (incluido un 404, que es una respuesta válida de NuGet) reinicia la cuenta.
/// </summary>
public sealed class Disyuntor(int umbral, TimeSpan pausa, TimeProvider tiempo)
{
    private readonly object _candado = new();
    private int _fallosSeguidos;
    private DateTimeOffset? _abiertoHasta;
    private bool _pruebaEnCurso;

    public bool Abierto
    {
        get
        {
            lock (_candado)
                return _abiertoHasta is not null && tiempo.GetUtcNow() < _abiertoHasta;
        }
    }

    /// <summary>Indica si se puede iniciar una operación. Con el disyuntor abierto y la pausa cumplida, autoriza una única prueba.</summary>
    public bool TryPasar()
    {
        lock (_candado)
        {
            if (_abiertoHasta is null) return true;
            if (tiempo.GetUtcNow() < _abiertoHasta) return false;
            if (_pruebaEnCurso) return false;
            _pruebaEnCurso = true;
            return true;
        }
    }

    public void RegistrarExito()
    {
        lock (_candado)
        {
            _fallosSeguidos = 0;
            _abiertoHasta = null;
            _pruebaEnCurso = false;
        }
    }

    /// <summary>Registra una operación que agotó sus reintentos.</summary>
    public void RegistrarFallo()
    {
        lock (_candado)
        {
            _fallosSeguidos++;
            if (_pruebaEnCurso || _fallosSeguidos >= umbral)
                _abiertoHasta = tiempo.GetUtcNow() + pausa;
            _pruebaEnCurso = false;
        }
    }

    /// <summary>Libera una prueba autorizada que terminó sin información (p. ej. cancelada) para no dejar el disyuntor bloqueado.</summary>
    public void LiberarPrueba()
    {
        lock (_candado) _pruebaEnCurso = false;
    }
}
