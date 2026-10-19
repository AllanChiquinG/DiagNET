using Compartido.Contratos;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Recomendaciones;

namespace Servidor.Api.Mapeo;

/// <summary>
/// Convierte el resultado de Negocio (tal como se guardó) en el contrato público. Es una conversión pura: no consulta NuGet, no recalcula diagnósticos ni
/// recomendaciones. Todo lo que muestra sale del snapshot guardado, incluidas las advertencias y limitaciones de E6/E7.
/// </summary>
public static class MapeadorAnalisis
{
    public static ResultadoAnalisisDto ADto(Analisis a, int retencionDias)
    {
        var dependencias = a.Dependencias.Select(ADto).ToList();
        return new ResultadoAnalisisDto(
            a.Id, a.NombreArchivo, a.FrameworksDeclarados, a.FrameworksEvaluados, a.FechaAnalisis, E<Compartido.Contratos.EstadoAnalisis>(a.Estado),
            ResumenEstadosDto.Calcular(dependencias), dependencias, a.Advertencias.Select(ADto).ToList(), a.VersionReglas, retencionDias);
    }

    public static ResumenAnalisisDto ADto(ResumenAnalisis r) => new(
        r.Id, r.NombreArchivo, r.FrameworksDeclarados, r.FechaAnalisis, r.TotalDependencias, E<Compartido.Contratos.EstadoAnalisis>(r.Estado));

    private static DependenciaAnalizadaDto ADto(DependenciaAnalizada d) => new(
        d.Nombre, d.VersionDeclarada, E<TipoVersionDeclarada>(d.TipoVersion), d.Condicion, d.Advertencias.Select(ADto).ToList(),
        ADto(d.Diagnostico), d.Recomendaciones.Select(ADto).ToList());

    private static DiagnosticoDto ADto(Diagnostico d) => new(
        E<Compartido.Contratos.EstadoDiagnostico>(d.Estado), d.Motivos,
        d.Hallazgos.Select(h => new HallazgoDto(h.Regla, h.Codigo, E<Compartido.Contratos.GravedadHallazgo>(h.Gravedad), h.Mensaje,
            h.Evidencia.Select(ADto).ToList(), h.EvidenciaDecisivaFaltante)).ToList(),
        d.EvaluacionPorTfm.Select(t => new EvaluacionTfmDto(t.Tfm, E<Compartido.Contratos.EstadoDiagnostico>(t.Estado))).ToList(),
        E<Compartido.Contratos.OrigenDato>(d.Origen), d.FechaConsulta, d.Indicadores is null ? null : ADto(d.Indicadores));

    private static RecomendacionDto ADto(Recomendacion r) => new(
        E<Compartido.Contratos.TipoRecomendacion>(r.Tipo), r.Paquete, r.VersionSugerida, r.Justificacion, r.Evidencia.Select(ADto).ToList(), r.Limitaciones,
        r.PosiblesCambiosDeCodigo,
        TextosRecomendacion.Etiqueta(r.Tipo),                           // textos acordados (R3/R4): la interfaz y el PDF no los reescriben
        TextosRecomendacion.CambiosDeCodigo(r.PosiblesCambiosDeCodigo));

    private static IndicadoresDto ADto(IndicadoresLibreria i) => new(
        Ind(i.FechaPublicacionDeclarada), Ind(i.UltimaVersionEstable), Ind(i.UltimaVersionEstableCompatible), i.SeleccionVersionesCompleta,
        Ind(i.MesesDesdeUltimaPublicacion), Ind(i.Descargas),
        new IndicadorDto<LicenciaDto?>(E<Compartido.Contratos.Disponibilidad>(i.Licencia.Disponibilidad),
            i.Licencia.Valor is null ? null : new LicenciaDto(i.Licencia.Valor.Tipo is { } t ? E<Compartido.Contratos.TipoLicencia>(t) : null, i.Licencia.Valor.Valor),
            i.Licencia.Nota),
        Ind(i.Deprecado),
        i.Deprecacion is null ? null : new DeprecacionDto(i.Deprecacion.Razones, i.Deprecacion.Mensaje, i.Deprecacion.PaqueteAlterno, i.Deprecacion.RangoVersionAlterno),
        i.CompatibilidadPorTfm.Select(c => new CompatibilidadTfmDto(c.Tfm, E<Compartido.Contratos.ResultadoCompatibilidad>(c.Resultado), c.Base, c.Detalle)).ToList(),
        Ind(i.Aot));

    private static IndicadorDto<T?> Ind<T>(Indicador<T?> i) => new(E<Compartido.Contratos.Disponibilidad>(i.Disponibilidad), i.Valor, i.Nota);

    private static EvidenciaDto ADto(Evidencia e) => new(e.Clave, e.Valor);

    private static AdvertenciaDto ADto(Advertencia a) => new(a.Codigo, a.Mensaje, a.Dependencia);

    // Las enumeraciones del dominio y del contrato tienen los mismos nombres de miembro (lo comprueba una prueba).
    private static TDestino E<TDestino>(Enum valor) where TDestino : struct, Enum => Enum.Parse<TDestino>(valor.ToString());
}
