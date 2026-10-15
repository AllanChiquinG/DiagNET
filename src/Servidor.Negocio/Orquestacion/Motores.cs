using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Orquestacion;

/// <summary>Todo lo que un motor necesita para evaluar UNA dependencia. El orquestador no interpreta reglas: solo entrega este contexto.</summary>
/// <param name="FrameworksEvaluados">TFM del proyecto en alcance (vacío si no se resolvió ninguno: la compatibilidad queda no determinada).</param>
public sealed record ContextoDiagnostico(
    DependenciaDeclarada Dependencia,
    ResultadoMetadato Metadato,
    IReadOnlyList<string> FrameworksEvaluados,
    DateTimeOffset Ahora);

/// <summary>
/// Puerto del motor de diagnóstico. La implementación definitiva es de E6 (reglas, precedencia, indicadores). El orquestador SOBRESCRIBE
/// <c>Origen</c> y <c>FechaConsulta</c> del diagnóstico con los del metadato (son hechos del dato, no reglas): la fecha original de una copia vencida
/// nunca se pierde, sea cual sea el motor.
/// </summary>
public interface IMotorDiagnostico
{
    /// <summary>Versión de las reglas; se guarda con el análisis para no reinterpretar resultados antiguos.</summary>
    string VersionReglas { get; }

    Diagnostico Diagnosticar(ContextoDiagnostico contexto);
}

/// <summary>Puerto del motor de recomendaciones. La implementación definitiva es de E7. Se invoca solo para dependencias deficientes.</summary>
public interface IMotorRecomendacion
{
    Task<IReadOnlyList<Recomendacion>> RecomendarAsync(
        ContextoDiagnostico contexto, Diagnostico diagnostico, PresupuestoTiempo? presupuesto, CancellationToken ct);
}
