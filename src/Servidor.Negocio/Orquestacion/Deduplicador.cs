namespace Servidor.Negocio.Orquestacion;

/// <summary>
/// Comparte UNA ejecución entre todas las solicitudes simultáneas equivalentes (misma clave). Reglas:
/// <list type="bullet">
/// <item>Cada llamador espera con su PROPIO token: si cancela, deja de esperar sin cancelar la operación para los demás.</item>
/// <item>La operación compartida solo se cancela cuando ya no queda ningún llamador esperando.</item>
/// <item>Al terminar (con éxito, error o cancelación) la operación se retira: una llamada posterior empieza una nueva (normalmente servida por la caché).</item>
/// </list>
/// La clave decide qué es «equivalente»: quien la construya debe incluir todo lo que cambia el resultado (versión, TFM…).
/// </summary>
public sealed class Deduplicador<TClave, TResultado> where TClave : notnull
{
    private sealed class Operacion
    {
        public required Task<TResultado> Tarea { get; init; }
        public required CancellationTokenSource Cancelacion { get; init; }
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
                var fuente = new CancellationTokenSource();
                var tarea = Task.Run(async () =>
                {
                    try
                    {
                        return await fabrica(fuente.Token);
                    }
                    finally
                    {
                        lock (_candado) _enVuelo.Remove(clave);
                        fuente.Dispose();
                    }
                }, CancellationToken.None);
                operacion = new Operacion { Tarea = tarea, Cancelacion = fuente };
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
            }

            if (cancelarOperacion)
            {
                try { operacion.Cancelacion.Cancel(); }
                catch (ObjectDisposedException) { /* ya terminó */ }
            }
        }
    }
}
