using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Recomendaciones;

namespace Servidor.Negocio.Reportes;

/// <summary>
/// Convierte el snapshot guardado en el modelo del reporte. Es una conversión PURA: no consulta NuGet, no calcula diagnósticos ni recomendaciones, no lee la base.
/// Todo texto sale del snapshot o de los textos acordados en E6/E7; aquí solo se ordena, se agrupa y se rotula.
/// </summary>
public static class ConsolidadorReporte
{
    /// <summary>Códigos de las notas del motor de recomendaciones por dependencia (distinguen «no hay» de «no se pudo verificar»).</summary>
    private static readonly string[] CodigosNotaRecomendacion =
        ["sin_recomendaciones", "recomendaciones_no_verificadas", "recomendaciones_evaluacion_incompleta", "actualizacion_no_verificada"];

    /// <summary>Aviso del análisis cuando alguna dependencia no pudo evaluarse por completo para recomendar (distinto de un diagnóstico parcial).</summary>
    public const string CodigoRecomendacionesIncompletas = "recomendaciones_incompletas";

    /// <summary>Limitaciones generales del producto, siempre presentes (no dependen de los datos).</summary>
    public static readonly IReadOnlyList<string> LimitacionesGenerales =
    [
        "Este reporte reproduce el resultado guardado del análisis. No consulta NuGet ni recalcula nada al generarse: si NuGet publicó versiones nuevas después de la fecha del análisis, no aparecen aquí.",
        "La compatibilidad con cada framework es INFERIDA de los grupos de dependencias que publica NuGet. No certifica ensamblados, compilación ni ejecución; la ausencia de un grupo compatible no demuestra incompatibilidad.",
        "La compatibilidad con AOT no se evalúa en esta versión de DiagNET: no se afirma ni se descarta.",
        "«Alternativa sugerida por el autor» es lo que el autor del paquete publicó al deprecarlo; no se ha comprobado que sea equivalente. «Candidato para revisión» es un paquete hallado sin relación funcional demostrada: no es un reemplazo recomendado.",
        "Comparar versiones no prueba la ausencia de cambios de código: «Cambios de código no determinados» significa que no se evaluaron, no que no existan.",
        "La licencia se muestra solo como información publicada por NuGet; no es asesoría legal. Las fechas del reporte están en UTC.",
    ];

    public static ModeloReporte Consolidar(Analisis a, int retencionDias, DateTimeOffset generado)
    {
        var resumen = new ResumenReporte(
            a.Dependencias.Count,
            Contar(a, EstadoDiagnostico.Actualizada), Contar(a, EstadoDiagnostico.Desactualizada), Contar(a, EstadoDiagnostico.Incompatible),
            Contar(a, EstadoDiagnostico.SinSoporteActivo), Contar(a, EstadoDiagnostico.NoDeterminada));

        // Mismo orden que la tabla en pantalla: lo que exige acción primero; a igualdad, el orden del archivo.
        var ordenadas = a.Dependencias.Select((d, i) => (d, i)).OrderBy(x => TextosReporte.Orden(x.d.Diagnostico.Estado)).ThenBy(x => x.i).Select(x => x.d).ToList();

        return new ModeloReporte(
            new EncabezadoReporte(
                a.NombreArchivo, a.Id, a.FrameworksDeclarados, a.FrameworksEvaluados, TextosReporte.Fecha(a.FechaAnalisis), TextosReporte.Fecha(generado),
                TextosReporte.Texto(a.Estado), a.VersionReglas, retencionDias),
            resumen,
            a.Estado == EstadoAnalisis.Parcial,
            a.Advertencias.Where(w => w.Codigo != CodigoRecomendacionesIncompletas).Select(w => w.Mensaje).ToList(),
            a.Advertencias.Where(w => w.Codigo == CodigoRecomendacionesIncompletas).Select(w => w.Mensaje).ToList(),
            ordenadas.Select(Fila).ToList(),
            ordenadas.Select(Detalle).ToList(),
            LimitacionesGenerales);
    }

    private static int Contar(Analisis a, EstadoDiagnostico estado) => a.Dependencias.Count(d => d.Diagnostico.Estado == estado);

    private static FilaReporte Fila(DependenciaAnalizada d)
    {
        var etiquetas = new List<string>();
        if (d.Diagnostico.TieneEvidenciaIncompleta()) etiquetas.Add("Evidencia incompleta");

        var version = d.VersionDeclarada ?? "—";
        if (d.TipoVersion != TipoVersion.Exacta) version += $" ({TextosReporte.Texto(d.TipoVersion)})";

        return new FilaReporte(d.Nombre, version, UltimaVersion(d), TextosReporte.Texto(d.Diagnostico.Estado), TextosReporte.OrigenYFecha(d.Diagnostico), ResumenRecomendacion(d), etiquetas);
    }

    private static string UltimaVersion(DependenciaAnalizada d)
    {
        var i = d.Diagnostico.Indicadores;
        if (i is null) return "—";
        var (texto, conocido) = TextosReporte.Indicador(i.UltimaVersionEstable, v => v ?? "—", "—");
        return conocido ? texto : "No determinada";
    }

    private static string ResumenRecomendacion(DependenciaAnalizada d)
    {
        if (d.Recomendaciones.Count > 0)
        {
            var r = d.Recomendaciones[0];
            var etiqueta = TextosRecomendacion.Etiqueta(r.Tipo);
            var texto = r.VersionSugerida is null ? $"{etiqueta}: {r.Paquete}" : $"{etiqueta}: {r.Paquete} {r.VersionSugerida}";
            return d.Recomendaciones.Count > 1 ? $"{texto} (+{d.Recomendaciones.Count - 1})" : texto;
        }

        return d.Diagnostico.Estado == EstadoDiagnostico.Actualizada ? "—" : "Sin recomendación";
    }

    private static DetalleReporte Detalle(DependenciaAnalizada d)
    {
        var dg = d.Diagnostico;
        var motivos = dg.Hallazgos.Where(h => !h.EvidenciaDecisivaFaltante && h.Gravedad is GravedadHallazgo.Grave or GravedadHallazgo.Aviso).Select(Motivo).ToList();
        var faltantes = dg.Hallazgos.Where(h => h.EvidenciaDecisivaFaltante).Select(Motivo).ToList();
        var informativos = dg.Hallazgos.Where(h => !h.EvidenciaDecisivaFaltante && h.Gravedad == GravedadHallazgo.Informativo).Select(Motivo).ToList();
        var notas = d.Advertencias.Where(w => CodigosNotaRecomendacion.Contains(w.Codigo)).Select(w => w.Mensaje).ToList();
        var avisos = d.Advertencias.Where(w => !CodigosNotaRecomendacion.Contains(w.Codigo)).Select(w => w.Mensaje).ToList();

        var version = (d.VersionDeclarada ?? "—") + $" ({TextosReporte.Texto(d.TipoVersion)})";
        var (indicadores, deprecacion, compatibilidad) = Indicadores(dg);

        return new DetalleReporte(
            d.Nombre, version, TextosReporte.Texto(dg.Estado), Titulo(dg.Motivos), TextosReporte.OrigenYFecha(dg),
            motivos, faltantes, informativos,
            dg.EvaluacionPorTfm.Select(t => new ParReporte(t.Tfm, TextosReporte.Texto(t.Estado))).ToList(),
            dg.Indicadores is not null, indicadores, deprecacion, compatibilidad, avisos, notas,
            d.Recomendaciones.Select(Recomendacion).ToList());
    }

    /// <summary>Primera parte del resumen guardado (el estado y su razón principal), antes de la lista de motivos que se detalla aparte.</summary>
    private static string Titulo(string texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "";
        var cortes = new[] { " Motivos:", " Limitaciones:", " Evidencia incompleta:" }.Select(c => texto.IndexOf(c, StringComparison.Ordinal)).Where(p => p > 0).ToList();
        return cortes.Count == 0 ? texto : texto[..cortes.Min()];
    }

    private static MotivoReporte Motivo(Hallazgo h) =>
        new(TextosReporte.Texto(h.Gravedad), h.Mensaje, h.Evidencia.Select(e => new ParReporte(e.Clave.Replace('_', ' '), e.Valor)).ToList());

    private static (IReadOnlyList<ParReporte> Indicadores, IReadOnlyList<string> Deprecacion, IReadOnlyList<string> Compatibilidad) Indicadores(Diagnostico dg)
    {
        var origen = new ParReporte("Origen de los datos", TextosReporte.OrigenYFecha(dg),
            dg.Origen == OrigenDato.CacheExpirada ? "La fecha es la de la copia original: NuGet no respondió y se usó una copia guardada vencida." : null,
            dg.Origen != OrigenDato.NoDeterminado);

        if (dg.Indicadores is not { } i)
            return ([origen, new ParReporte("Compatibilidad con AOT", "No evaluado", "DiagNET no evalúa AOT en esta versión.", false)], [], []);

        var fecha = TextosReporte.Indicador(i.FechaPublicacionDeclarada, v => v is { } f ? TextosReporte.Fecha(f) : "");
        var estable = TextosReporte.Indicador(i.UltimaVersionEstable, v => v ?? "", "No hay versión estable publicada");
        var compatible = TextosReporte.Indicador(i.UltimaVersionEstableCompatible, v => v ?? "", "Ninguna comprobada");
        var meses = TextosReporte.Indicador(i.MesesDesdeUltimaPublicacion, m => $"{m} meses");
        var descargas = TextosReporte.Indicador(i.Descargas, n => TextosReporte.Entero(n ?? 0));
        var licencia = TextosReporte.Indicador(i.Licencia, l => l is null ? "" : l.Tipo is { } t ? $"{l.Valor} ({TextosReporte.Texto(t)})" : $"{l.Valor} (tipo no informado por NuGet)");
        var deprecado = TextosReporte.Indicador(i.Deprecado, b => b == true ? "Sí" : "No");

        var indicadores = new List<ParReporte>
        {
            new("Publicación de la versión declarada", fecha.Texto, i.FechaPublicacionDeclarada.Nota, fecha.Conocido),
            new("Última versión estable", estable.Texto, i.UltimaVersionEstable.Nota, estable.Conocido),
            new("Última estable compatible", compatible.Texto, i.UltimaVersionEstableCompatible.Nota, compatible.Conocido),
            new("Selección de versiones", i.SeleccionVersionesCompleta ? "Completa" : "Incompleta",
                i.SeleccionVersionesCompleta ? null : "No se revisó todo el registro del paquete: puede haber versiones más recientes sin revisar.", i.SeleccionVersionesCompleta),
            new("Meses desde la última publicación", meses.Texto, i.MesesDesdeUltimaPublicacion.Nota, meses.Conocido),
            new("Descargas", descargas.Texto, i.Descargas.Nota, descargas.Conocido),
            new("Licencia (informativa)", licencia.Texto, i.Licencia.Nota, licencia.Conocido),
            new("Deprecada en NuGet", deprecado.Texto, i.Deprecado.Nota, deprecado.Conocido),
            new("Compatibilidad con AOT", "No evaluado", i.Aot.Nota ?? "DiagNET no evalúa AOT en esta versión: no se afirma ni se descarta.", false),
            origen,
        };

        var deprecacion = new List<string>();
        if (i.Deprecacion is { } dep)
        {
            deprecacion.Add($"Razones: {(dep.Razones.Count == 0 ? "sin razón indicada" : string.Join(", ", dep.Razones))}");
            if (!string.IsNullOrWhiteSpace(dep.Mensaje)) deprecacion.Add($"Mensaje del autor: {dep.Mensaje}");
            if (dep.PaqueteAlterno is not null)
                deprecacion.Add($"Paquete alterno que indica el autor: {dep.PaqueteAlterno}{(dep.RangoVersionAlterno is null ? "" : $" (rango {dep.RangoVersionAlterno})")}");
        }

        var compat = i.CompatibilidadPorTfm.Select(c => $"{c.Tfm}: {TextosReporte.Texto(c.Resultado)}{(string.IsNullOrWhiteSpace(c.Detalle) ? "" : " — " + c.Detalle)}").ToList();
        if (compat.Count == 0) compat.Add("No se determinó compatibilidad con ningún framework.");
        return (indicadores, deprecacion, compat);
    }

    private static RecomendacionReporte Recomendacion(Recomendacion r) => new(
        TextosRecomendacion.Etiqueta(r.Tipo), r.Paquete, r.VersionSugerida is null ? "sin versión comprobada" : $"versión comprobada {r.VersionSugerida}", r.Justificacion,
        TextosRecomendacion.CambiosDeCodigo(r.PosiblesCambiosDeCodigo),
        r.Evidencia.Select(e => new ParReporte(e.Clave.Replace('_', ' '), e.Valor)).ToList(), r.Limitaciones);
}
