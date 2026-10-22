using Servidor.Negocio.Operacion;

namespace Servidor.Api.Operacion;

/// <summary>
/// Ejecuta la purga cada <see cref="OpcionesPurga.IntervaloHoras"/> horas SOLO si <see cref="OpcionesPurga.Habilitada"/>. Por defecto está desactivada y no hace nada
/// (ni siquiera consulta la base). Un fallo de una pasada se registra (sin identificadores) y la siguiente lo reintenta; nunca detiene la aplicación.
/// </summary>
public sealed class PurgaProgramada(ServicioPurga purga, OpcionesPurga opciones, TimeProvider reloj, ILogger<PurgaProgramada> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken detener)
    {
        if (!opciones.Habilitada)
        {
            log.LogInformation("Purga programada desactivada (Purga:Habilitada=false): no se elimina ningún dato automáticamente.");
            return;
        }

        try
        {
            opciones.Validar();
        }
        catch (InvalidOperationException ex)
        {
            log.LogError("Purga programada NO iniciada por configuración inválida: {Motivo}", ex.Message);
            return;
        }

        log.LogWarning("Purga programada ACTIVA: análisis > {Analisis} días, caché > {Cache} días, cada {Horas} h.", opciones.RetencionAnalisisDias, opciones.RetencionCacheDias, opciones.IntervaloHoras);
        using var temporizador = new PeriodicTimer(TimeSpan.FromHours(opciones.IntervaloHoras), reloj);
        do
        {
            try
            {
                var r = await purga.EjecutarAsync(detener);
                log.LogInformation("Purga ejecutada: {Analisis} análisis, {Metadatos} metadatos de caché y {Selecciones} selecciones eliminados.",
                    r.AnalisisEliminados, r.MetadatosEliminados, r.SeleccionesEliminadas);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "La purga programada falló; se reintentará en la próxima pasada.");
            }
        }
        while (await SiguienteAsync(temporizador, detener));
    }

    private static async Task<bool> SiguienteAsync(PeriodicTimer t, CancellationToken ct)
    {
        try
        {
            return await t.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
