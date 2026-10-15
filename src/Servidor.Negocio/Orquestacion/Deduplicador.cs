namespace Servidor.Negocio.Orquestacion;

/// <summary>
/// Comparte UNA ejecución entre todas las solicitudes simultáneas equivalentes (misma clave). Reglas:
/// <list type="bullet">
/// <item>Cada llamador espera con su PROPIO token: si cancela, deja de esperar sin cancelar la operación para los demás.</item>
/// <item>La operación compartida solo se cancela cuando ya no queda ningún llamador esperando; en ese mismo instante se retira del diccionario,
/// de modo que nadie que llegue después herede una operación cancelada (empieza una nueva).</item>
/// <item>Al terminar (con éxito, error o cancelación) la operación se retira SOLO si sigue siendo la registrada para esa clave: una operación vieja
/// que termina tarde no puede retirar a la nueva.</item>
/// </list>
/// La clave decide qué es «equivalente»: quien la construya debe incluir todo lo que cambia el resultado (versión, TFM…).
/// </summary>
public sealed class Deduplicador<TClave, TResultado> where TClave : notnull
{
    private sealed class Operacion
    {
        public Task<TResultado> Tarea { get; set; } = null!;
        public CancellationTokenSource Cancelacion { get; } = new();
        public int Esperando;
    }

    private readonly object _candado = new();
    private readonly Dictionary<TClave, Operacion> _enVuelo = new();

    public int EnVuelo
    {
        get { lock (_candado) return _enVuelo.Count; }
    }

    /// <param name="cancelacionAdicional">Otro motivo para dejar de esperar (p. ej. el token del presupuesto del llamador).</param>
    public async Task<TResultado> EjecutarAsync(
        TClave clave, Func<CancellationToken, Task<TResultado>> fabrica, CancellationToken ct, CancellationToken cancelacionAdicional = default)
    {
        Operacion operacion;
        lock (_candado)
        {
            if (!_enVuelo.TryGetValue(clave, out operacion!))
            {
                var nueva = new Operacion();
                nueva.Tarea = Task.Run(async () =>
                {
                    try
                    {
                        return await fabrica(nueva.Cancelacion.Token);
                    }
                    finally
                    {
                        lock (_candado)
                        {
                            if (_enVuelo.TryGetValue(clave, out var actual) && ReferenceEquals(actual, nueva)) _enVuelo.Remove(clave);
                        }
                    }
                }, CancellationToken.None);
                operacion = nueva;
                _enVuelo[clave] = operacion;
            }

            operacion.Esperando++;
        }

        try
        {
            using var union = CancellationTokenSource.CreateLinkedTokenSource(ct, cancelacionAdicional);
            return await operacion.Tarea.WaitAsync(union.Token);
        }
        finally
        {
            bool cancelarOperacion;
            lock (_candado)
            {
                operacion.Esperando--;
                cancelarOperacion = operacion.Esperando == 0 && !operacion.Tarea.IsCompleted;
                if (cancelarOperacion && _enVuelo.TryGetValue(clave, out var actual) && ReferenceEquals(actual, operacion))
                    _enVuelo.Remove(clave);   // ya no se puede compartir: quien llegue ahora inicia una operación nueva
            }

            if (cancelarOperacion)
            {
                try { operacion.Cancelacion.Cancel(); }
                catch (ObjectDisposedException) { /* ya terminó */ }
            }
        }
    }
}
