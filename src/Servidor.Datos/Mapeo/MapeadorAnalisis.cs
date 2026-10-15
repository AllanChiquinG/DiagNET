using Servidor.Datos.Entidades;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using static Servidor.Datos.Mapeo.Serializacion;

namespace Servidor.Datos.Mapeo;

/// <summary>Convierte entre el resultado de dominio y las filas. Las fechas se guardan en UTC (timestamptz).</summary>
internal static class MapeadorAnalisis
{
    public static AnalisisFila AFila(Analisis a)
    {
        // framework_objetivo es solo un resumen legible (varchar(100)); la lista exacta se guarda completa en frameworks_declarados (jsonb).
        var resumenFrameworks = LimitesDatos.Recortar(string.Join(';', a.FrameworksDeclarados), LimitesDatos.FrameworkObjetivo);
        return new AnalisisFila
        {
            Id = a.Id,
            NombreArchivo = a.NombreArchivo,
            FrameworkObjetivo = resumenFrameworks,
            FechaAnalisis = a.FechaAnalisis.ToUniversalTime(),
            Estado = Texto(a.Estado),
            TotalDependencias = a.Dependencias.Count,
            FrameworksDeclarados = AJson(a.FrameworksDeclarados),
            FrameworksEvaluados = AJson(a.FrameworksEvaluados),
            Advertencias = AJson(a.Advertencias),
            VersionReglas = a.VersionReglas,
            Dependencias = a.Dependencias.Select(AFila).ToList(),
        };
    }

    private static DependenciaFila AFila(DependenciaAnalizada d) => new()
    {
        NombrePaquete = d.Nombre,
        VersionDeclarada = d.VersionDeclarada,
        TipoVersion = Texto(d.TipoVersion),
        Condicion = d.Condicion,
        Advertencias = AJson(d.Advertencias),
        Diagnostico = AFila(d.Diagnostico, d.Recomendaciones),
    };

    private static DiagnosticoFila AFila(Diagnostico g, IReadOnlyList<Recomendacion> recomendaciones) => new()
    {
        Estado = Texto(g.Estado),
        UltimaVersion = g.UltimaVersion,
        Indicadores = g.Indicadores is null ? "{}" : AJson(g.Indicadores),
        Motivos = g.Motivos,
        FechaConsulta = g.FechaConsulta?.ToUniversalTime(),
        Origen = Texto(g.Origen),
        Hallazgos = AJson(g.Hallazgos),
        EvaluacionPorTfm = AJson(g.EvaluacionPorTfm),
        Recomendaciones = recomendaciones.Select(AFila).ToList(),
    };

    private static RecomendacionFila AFila(Recomendacion r) => new()
    {
        Tipo = Texto(r.Tipo),
        PaqueteAlternativo = r.Paquete,
        VersionSugerida = r.VersionSugerida,
        Justificacion = r.Justificacion,
        Evidencia = AJson(r.Evidencia),
        Limitaciones = AJson(r.Limitaciones),
        PosiblesCambiosDeCodigo = r.PosiblesCambiosDeCodigo,
    };

    public static Analisis ADominio(AnalisisFila f) => new(
        f.Id,
        f.NombreArchivo,
        DeJson<List<string>>(f.FrameworksDeclarados),
        DeJson<List<string>>(f.FrameworksEvaluados),
        f.FechaAnalisis,
        Enumeracion<EstadoAnalisis>(f.Estado),
        f.Dependencias.OrderBy(d => d.Id).Select(ADominio).ToList(),
        DeJson<List<Advertencia>>(f.Advertencias),
        f.VersionReglas);

    private static DependenciaAnalizada ADominio(DependenciaFila d)
    {
        var g = d.Diagnostico ?? throw new InvalidOperationException($"La dependencia {d.Id} no tiene diagnóstico: el resultado guardado está incompleto.");
        return new DependenciaAnalizada(
            d.NombrePaquete,
            d.VersionDeclarada,
            Enumeracion<TipoVersion>(d.TipoVersion),
            d.Condicion,
            DeJson<List<Advertencia>>(d.Advertencias),
            new Diagnostico(
                Enumeracion<EstadoDiagnostico>(g.Estado),
                g.Motivos,
                g.UltimaVersion,
                DeJson<List<Hallazgo>>(g.Hallazgos),
                DeJson<List<EvaluacionTfm>>(g.EvaluacionPorTfm),
                Enumeracion<OrigenDato>(g.Origen),
                g.FechaConsulta,
                IndicadoresDe(g.Indicadores)),
            g.Recomendaciones.OrderBy(r => r.Id).Select(ADominio).ToList());
    }

    private static IndicadoresLibreria? IndicadoresDe(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && !doc.RootElement.EnumerateObject().Any()
            ? null
            : DeJson<IndicadoresLibreria>(json);
    }

    private static Recomendacion ADominio(RecomendacionFila r) => new(
        Enumeracion<TipoRecomendacion>(r.Tipo),
        r.PaqueteAlternativo,
        r.VersionSugerida,
        r.Justificacion,
        DeJson<List<Evidencia>>(r.Evidencia),
        DeJson<List<string>>(r.Limitaciones),
        r.PosiblesCambiosDeCodigo);

    public static ResumenAnalisis AResumen(AnalisisFila f) => new(
        f.Id,
        f.NombreArchivo,
        DeJson<List<string>>(f.FrameworksDeclarados),
        f.FechaAnalisis,
        f.TotalDependencias,
        Enumeracion<EstadoAnalisis>(f.Estado));
}
