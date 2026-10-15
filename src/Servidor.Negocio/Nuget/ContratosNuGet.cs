using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Nuget;

// Tipos y puerto de la integración con NuGet. Negocio solo conoce esta interfaz: nunca HttpClient ni el formato JSON de NuGet.

public enum EstadoConsulta
{
    /// <summary>NuGet respondió y los datos son utilizables.</summary>
    Correcto,

    /// <summary>NuGet respondió que el recurso no existe (404 o identificador imposible). No se reintenta.</summary>
    NoEncontrado,

    /// <summary>No se pudo obtener una respuesta utilizable. Nunca se inventa un valor: el llamador usa la caché o declara «no determinado».</summary>
    NoDisponible,
}

public enum CausaFallo
{
    Red,
    TiempoAgotado,
    LimiteSolicitudes,
    ErrorServidor,
    RespuestaNoEsperada,
    RespuestaInvalida,
    RespuestaExcesiva,
    DireccionNoPermitida,
    CircuitoAbierto,
    PresupuestoAgotado,
    IndiceNoDisponible,
    ProtocoloNoSoportado,
}

public sealed record ResultadoNuGet<T>(
    EstadoConsulta Estado,
    T? Valor,
    CausaFallo? Causa,
    string? Mensaje,
    IReadOnlyList<Advertencia> Advertencias,
    int SolicitudesHttp)
{
    public bool EsCorrecto => Estado == EstadoConsulta.Correcto;

    public static ResultadoNuGet<T> Correcto(T valor, IReadOnlyList<Advertencia>? advertencias = null, int solicitudes = 0) =>
        new(EstadoConsulta.Correcto, valor, null, null, advertencias ?? [], solicitudes);

    public static ResultadoNuGet<T> NoEncontrado(string mensaje, IReadOnlyList<Advertencia>? advertencias = null, int solicitudes = 0) =>
        new(EstadoConsulta.NoEncontrado, default, null, mensaje, advertencias ?? [], solicitudes);

    public static ResultadoNuGet<T> NoDisponible(CausaFallo causa, string mensaje, IReadOnlyList<Advertencia>? advertencias = null, int solicitudes = 0) =>
        new(EstadoConsulta.NoDisponible, default, causa, mensaje, advertencias ?? [], solicitudes);
}

/// <param name="Paquete">Identificador tal como lo declara el proyecto.</param>
/// <param name="VersionDeclarada">Versión exacta declarada, o <c>null</c> si no se pudo resolver (rango, flotante, propiedad): entonces solo se consulta la última.</param>
/// <param name="FrameworksEvaluados">TFM del proyecto (p. ej. <c>net8.0</c>, <c>net10.0</c>) para los que se pide compatibilidad.</param>
public sealed record ConsultaPaquete(string Paquete, string? VersionDeclarada, IReadOnlyList<string> FrameworksEvaluados);

public sealed record AvisoVulnerabilidad(string? Severidad, string? Url);

/// <summary>Una versión de un paquete tal como la publica el registro de NuGet. Todo valor ausente es <c>null</c>; nada se rellena.</summary>
/// <param name="Version">Versión normalizada (SemVer / reglas de NuGet).</param>
/// <param name="Publicada"><c>null</c> si NuGet no informa fecha (las versiones no listadas usan 1900: se descarta).</param>
/// <param name="Licencia">Expresión de licencia si existe; si no, la URL de licencia. Puede ser larga: la caché la limita, el resultado guardado no.</param>
/// <param name="GruposFramework">Un TFM por grupo de dependencias; la cadena vacía representa un grupo sin framework (aplica a todos).</param>
public sealed record VersionPublicada(
    string Version,
    string VersionOriginal,
    bool EsPrerelease,
    bool Listada,
    DateTimeOffset? Publicada,
    string? Licencia,
    TipoLicencia? TipoLicencia,
    Deprecacion? Deprecacion,
    IReadOnlyList<string> GruposFramework,
    IReadOnlyList<AvisoVulnerabilidad> Vulnerabilidades);

public enum EstadoVersionDeclarada
{
    /// <summary>El proyecto no declaró una versión exacta: solo se consultó la última.</summary>
    NoSolicitada,

    /// <summary>La versión existe en el registro.</summary>
    Publicada,

    /// <summary>Se recorrió el registro completo y esa versión no existe.</summary>
    NoPublicada,

    /// <summary>No se pudo determinar (versión no interpretable o registro no leído por completo).</summary>
    NoDeterminada,
}

/// <summary>
/// Lo que se sabe de un paquete en nuget.org. Distingue la última versión ESTABLE de la última estable COMPATIBLE con cada TFM,
/// y declara si la selección es completa. La compatibilidad es inferida de los grupos de dependencias; AOT no existe aquí (no se evalúa).
/// </summary>
/// <param name="UltimaEstableCompatiblePorTfm">Un valor por TFM evaluado; <c>null</c> si ninguna versión estable listada es compatible (o no se determinó).</param>
/// <param name="Descargas">Total de descargas informado por la búsqueda de NuGet; <c>null</c> (nunca 0) si no se obtuvo, con la explicación en <c>DescargasNota</c>.</param>
/// <param name="SeleccionCompleta">
/// <c>true</c> cuando la última estable y la última compatible por TFM son definitivas (se encontraron, o se agotaron las páginas).
/// <c>false</c> si el presupuesto de páginas se agotó antes: ningún máximo parcial debe presentarse como global.
/// </param>
public sealed record InformacionPaquete(
    string Paquete,
    EstadoVersionDeclarada EstadoDeclarada,
    VersionPublicada? Declarada,
    IReadOnlyList<CompatibilidadTfm> CompatibilidadDeclarada,
    VersionPublicada? UltimaEstable,
    VersionPublicada? UltimaPrerelease,
    IReadOnlyDictionary<string, VersionPublicada?> UltimaEstableCompatiblePorTfm,
    DateTimeOffset? UltimaPublicacion,
    bool SeleccionCompleta,
    int PaginasLeidas,
    int PaginasTotales,
    int VersionesLeidas,
    DateTimeOffset FechaConsulta,
    long? Descargas = null,
    string? DescargasNota = null);

/// <summary>Regla de inferencia de compatibilidad (la implementa Integracion con NuGet.Frameworks). Negocio la necesita para reconstruir resultados desde la caché.</summary>
public interface IInferidorCompatibilidad
{
    CompatibilidadTfm Inferir(string tfmProyecto, IReadOnlyList<string> gruposPaquete);
}

/// <summary>Resultado de una búsqueda. Un candidato NO es una alternativa recomendada: la relación funcional no está demostrada.</summary>
public sealed record CandidatoBusqueda(string Id, string? UltimaVersion, long? Descargas, bool? Verificado, string? Descripcion);

public interface IClienteNuGet
{
    /// <summary>Lee el registro del paquete (con paginación acotada) y selecciona versiones. 404 → <see cref="EstadoConsulta.NoEncontrado"/>.</summary>
    Task<ResultadoNuGet<InformacionPaquete>> ObtenerPaqueteAsync(ConsultaPaquete consulta, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default);

    /// <summary>Busca candidatos por texto: excluye el propio texto si coincide con un identificador, prioriza verificados y más descargados.</summary>
    Task<ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> BuscarCandidatosAsync(
        string texto, string? excluirPaquete, int maximo, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default);

    /// <summary>Descargas totales de un paquete (búsqueda por identificador exacto). <c>null</c> = NuGet no lo informó; nunca se rellena con 0.</summary>
    Task<ResultadoNuGet<long?>> ObtenerDescargasAsync(string paquete, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default);
}
