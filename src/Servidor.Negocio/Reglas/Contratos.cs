using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Negocio.Reglas;

/// <summary>Parámetros que influyen en la clasificación. Se resumen en <see cref="VersionReglas"/>, que se guarda con cada análisis (E6).</summary>
public sealed class ParametrosDiagnostico
{
    /// <summary>Revisión de las reglas. Cambia si cambia cualquier regla; así un análisis guardado nunca se reinterpreta con reglas nuevas.</summary>
    public const string Revision = "e6-1.1";

    /// <summary>Meses sin publicar a partir de los cuales se informa la antigüedad. Es solo informativo: no cambia el estado (DEC-01, propuesta).</summary>
    public int UmbralAntiguedadMeses { get; init; } = 24;

    /// <summary>
    /// Bases de compatibilidad (<see cref="CompatibilidadTfm.Base"/>) cuyo resultado «incompatible» se considera evidencia SUFICIENTE para afirmar incompatibilidad.
    /// Por defecto ninguna: la ausencia de un grupo de dependencias compatible NO basta (el registro no expone los ensamblados de <c>lib/</c>), así que con la
    /// única fuente actual (<c>inferida_dependency_groups</c>) el resultado es «no determinada». Se amplía solo cuando exista otra fuente de evidencia (decisión del propietario).
    /// </summary>
    public IReadOnlyCollection<string> BasesSuficientesParaIncompatible { get; init; } = [];

    /// <summary>Revisión + parámetros efectivos (≤ 30 caracteres, límite de la columna). «pes» = agregación pesimista por TFM (DEC-10, propuesta).</summary>
    public string VersionReglas => $"{Revision}|ant={UmbralAntiguedadMeses}m|agr=pes";
}

/// <param name="Info">Lo que se sabe del paquete; nunca <c>null</c> aquí (sin datos no se evalúan reglas).</param>
public sealed record EntradaReglas(ContextoDiagnostico Contexto, InformacionPaquete Info, ParametrosDiagnostico Parametros)
{
    public IReadOnlyList<string> Frameworks => Contexto.FrameworksEvaluados;

    public bool VersionResuelta =>
        Contexto.Dependencia.TipoVersion == TipoVersion.Exacta && Info.EstadoDeclarada == EstadoVersionDeclarada.Publicada && Info.Declarada is not null;

    public VersionPublicada? PorTfm(string tfm) =>
        Info.UltimaEstableCompatiblePorTfm.FirstOrDefault(p => string.Equals(p.Key, tfm, StringComparison.OrdinalIgnoreCase)).Value;
}

/// <summary>Un hallazgo y, si lo implica, el estado que propone. <paramref name="Tfm"/> <c>null</c> = afecta al paquete (todos los TFM).</summary>
public sealed record HallazgoDeRegla(Hallazgo Hallazgo, EstadoDiagnostico? Estado = null, string? Tfm = null);

/// <summary>Evidencia decisiva que falta. Impide declarar «actualizada» pero NO borra hallazgos negativos conocidos.</summary>
public sealed record Faltante(string Codigo, string Descripcion, string? Tfm = null);

public sealed record ResultadoRegla(IReadOnlyList<HallazgoDeRegla> Hallazgos, IReadOnlyList<Faltante> Faltantes)
{
    public static readonly ResultadoRegla Vacio = new([], []);
}

/// <summary>Una regla de diagnóstico: pura, determinista (el reloj llega en el contexto) y sin acceso a red ni base.</summary>
public interface IReglaDiagnostico
{
    string Nombre { get; }

    ResultadoRegla Evaluar(EntradaReglas entrada);
}
