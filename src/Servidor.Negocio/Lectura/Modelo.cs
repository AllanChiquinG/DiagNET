namespace Servidor.Negocio.Lectura;

/// <summary>Forma en que el .csproj declara una versión. Solo <see cref="Exacta"/> se considera concreta; nada se resuelve por suposición.</summary>
public enum TipoVersion
{
    Exacta,
    Rango,
    Flotante,
    Propiedad,
    Ausente,
    Invalida,
}

public sealed record AdvertenciaLectura(string Codigo, string Mensaje, string? Dependencia = null);

/// <summary><see cref="EnAlcance"/> es <c>null</c> cuando no puede determinarse (el valor depende de una variable).</summary>
public sealed record FrameworkDeclarado(string Valor, bool? EnAlcance, string? Nota = null);

public sealed record DependenciaDeclarada(
    string Nombre,
    string? Version,
    TipoVersion TipoVersion,
    string? Condicion,
    IReadOnlyList<AdvertenciaLectura> Advertencias);

public sealed record ProyectoLeido(
    string NombreArchivo,
    IReadOnlyList<FrameworkDeclarado> Frameworks,
    IReadOnlyList<DependenciaDeclarada> Dependencias,
    IReadOnlyList<AdvertenciaLectura> Advertencias);

/// <summary>Códigos de error de lectura. Deben coincidir con el catálogo del contrato público (lo verifica una prueba).</summary>
public static class CodigosLectura
{
    public const string ArchivoAusente = "archivo_ausente";
    public const string ExtensionInvalida = "extension_invalida";
    public const string ArchivoVacio = "archivo_vacio";
    public const string ArchivoExcedeTamano = "archivo_excede_tamano";
    public const string XmlInvalido = "xml_invalido";
    public const string ProyectoInvalido = "proyecto_invalido";
    public const string FrameworkFueraDeAlcance = "framework_fuera_de_alcance";
    public const string LimiteDependenciasExcedido = "limite_dependencias_excedido";
}

/// <summary>Códigos de advertencia de lectura.</summary>
public static class AdvertenciasLectura
{
    public const string SinPaquetes = "sin_paquetes";
    public const string VersionNoResuelta = "version_no_resuelta";
    public const string CondicionNoEvaluada = "condicion_no_evaluada";
    public const string DependenciaDuplicadaConflictiva = "dependencia_duplicada_conflictiva";
    public const string FrameworkNoResuelto = "framework_no_resuelto";
    public const string FrameworkFueraDeAlcanceOmitido = "framework_fuera_de_alcance_omitido";
    public const string SufijoPlataforma = "sufijo_plataforma";
    public const string NombreNoResuelto = "nombre_no_resuelto";
    public const string ReferenciaNoInterpretada = "referencia_no_interpretada";
    public const string ImportacionNoLeida = "importacion_no_leida";
}

/// <summary>Error de entrada con mensaje escrito para el usuario. No lleva datos internos.</summary>
public sealed class ArchivoInvalidoException(string codigo, string mensaje) : Exception(mensaje)
{
    public string Codigo { get; } = codigo;
}

public sealed class OpcionesLectura
{
    public long TamanoMaximoArchivoBytes { get; set; } = 1024 * 1024;

    public int MaximoDependencias { get; set; } = 100;
}

public interface ILectorProyecto
{
    /// <summary>Lee un .csproj sin ejecutarlo, sin consultar la red y sin guardar nada. Lanza <see cref="ArchivoInvalidoException"/> si la entrada no es válida.</summary>
    ProyectoLeido Leer(string? nombreArchivo, Stream contenido);
}
