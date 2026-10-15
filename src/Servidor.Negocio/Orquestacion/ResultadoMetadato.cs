using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Orquestacion;

/// <summary>Por qué no se obtuvo ningún dato de un paquete. Nunca se inventa un valor: el diagnóstico lo declara «no determinado» con este motivo.</summary>
public enum MotivoSinDatos
{
    NoExisteEnNuGet,
    NuGetNoDisponible,
    PresupuestoAgotado,
    CircuitoAbierto,
    ErrorInterno,
}

/// <summary>
/// Lo que se sabe de un paquete y de dónde salió (RN-06). <see cref="Origen"/> es NuGet (consultado ahora), caché vigente, caché expirada
/// (NuGet falló: se usa la copia vencida con su fecha ORIGINAL y una advertencia) o no determinado (no hay respuesta ni copia).
/// </summary>
/// <param name="FechaDatos">Fecha de los datos usados. En caché expirada es la fecha ORIGINAL de la copia (la más antigua de las filas usadas), nunca la actual. <c>null</c> si no hay datos.</param>
/// <param name="Informacion"><c>null</c> cuando el origen es <see cref="OrigenDato.NoDeterminado"/>.</param>
/// <param name="DegradadoPorPresupuesto"><c>true</c> si el presupuesto global se agotó y por eso no se consultó NuGet (se usó la caché o no hay datos).</param>
public sealed record ResultadoMetadato(
    OrigenDato Origen,
    InformacionPaquete? Informacion,
    DateTimeOffset? FechaDatos,
    MotivoSinDatos? Motivo,
    string? Mensaje,
    IReadOnlyList<Advertencia> Advertencias,
    int SolicitudesHttp,
    bool DegradadoPorPresupuesto = false)
{
    public bool TieneDatos => Informacion is not null;
}
