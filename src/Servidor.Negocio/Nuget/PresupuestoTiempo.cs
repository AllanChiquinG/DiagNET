namespace Servidor.Negocio.Nuget;

/// <summary>
/// Presupuesto global de tiempo de un análisis (120 s por defecto en el plan). Incluye esperas y reintentos: el gateway no inicia una espera
/// que ya no cabe y, al agotarse, cancela las solicitudes en curso. Un tiempo por intento no sustituye a este presupuesto.
/// </summary>
public sealed class PresupuestoTiempo : IDisposable
{
    private readonly TimeProvider _tiempo;
    private readonly DateTimeOffset _limite;
    private readonly CancellationTokenSource _fuente;

    public PresupuestoTiempo(TimeSpan total, TimeProvider? tiempo = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(total, TimeSpan.Zero);
        _tiempo = tiempo ?? TimeProvider.System;
        _limite = _tiempo.GetUtcNow() + total;
        _fuente = new CancellationTokenSource(total, _tiempo);
    }

    /// <summary>Se cancela cuando el presupuesto se agota.</summary>
    public CancellationToken Token => _fuente.Token;

    public TimeSpan Restante
    {
        get
        {
            var resto = _limite - _tiempo.GetUtcNow();
            return resto > TimeSpan.Zero ? resto : TimeSpan.Zero;
        }
    }

    public bool Agotado => Restante == TimeSpan.Zero || _fuente.IsCancellationRequested;

    public void Dispose() => _fuente.Dispose();
}
