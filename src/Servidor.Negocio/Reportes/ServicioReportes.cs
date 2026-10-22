using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Reportes;

/// <summary>Puerto del generador de PDF: la biblioteca concreta queda detrás de esta interfaz (se puede sustituir sin tocar el negocio).</summary>
public interface IGeneradorPdf
{
    /// <summary>Dibuja el modelo y devuelve los bytes del PDF. No guarda nada ni accede a red o base de datos.</summary>
    byte[] Generar(ModeloReporte modelo);
}

public sealed record ReportePdf(byte[] Contenido, string NombreArchivo);

/// <summary>
/// Reporte PDF bajo demanda (CU-07): lee el snapshot guardado, lo consolida y lo dibuja. NO depende del cliente de NuGet ni de los motores (no puede consultar ni
/// recalcular) y NO guarda el PDF: cada petición lo genera de nuevo desde el mismo snapshot.
/// </summary>
public sealed class ServicioReportes(IAnalisisRepositorio repositorio, IGeneradorPdf generador, TimeProvider reloj)
{
    /// <summary>Devuelve <c>null</c> si el análisis no existe o ya fue eliminado.</summary>
    public async Task<ReportePdf?> GenerarAsync(Guid id, int retencionDias, bool purgaAutomaticaActiva = false, CancellationToken ct = default)
    {
        var analisis = await repositorio.ObtenerAsync(id, ct);
        if (analisis is null) return null;

        byte[] contenido;
        try
        {
            var modelo = ConsolidadorReporte.Consolidar(analisis, retencionDias, reloj.GetUtcNow(), purgaAutomaticaActiva);
            contenido = generador.Generar(modelo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El análisis existe y se leyó bien: lo que falló es el dibujo del PDF. La interfaz conserva el resultado en pantalla.
            throw new ReporteNoGeneradoException(ex);
        }

        return new ReportePdf(contenido, TextosReporte.NombreArchivoPdf(analisis.NombreArchivo, analisis.FechaAnalisis));
    }
}

/// <summary>No se pudo construir el PDF de un análisis que sí existe (se distingue de un fallo de almacenamiento).</summary>
public sealed class ReporteNoGeneradoException(Exception interna) : Exception("No se pudo generar el PDF del análisis.", interna);
