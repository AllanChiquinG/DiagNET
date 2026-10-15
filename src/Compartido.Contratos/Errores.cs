using System.Text.Json.Serialization;

namespace Compartido.Contratos;

public sealed record ErrorCampoDto(string Campo, string Mensaje);

/// <summary>
/// Problem Details (RFC 9457) con extensiones de DiagNET: <c>codigo</c> estable y <c>referencia</c> para soporte.
/// El cliente decide su comportamiento por <see cref="Codigo"/>, nunca por el texto.
/// </summary>
public sealed record ProblemaDto(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Instance,
    string? Codigo,
    string? Referencia,
    [property: JsonPropertyName("errores")] IReadOnlyList<ErrorCampoDto>? Errores = null);

public sealed record DefinicionError(string Codigo, int Status, string Titulo);

/// <summary>Catálogo único de errores de la API. Los códigos de lectura de Negocio deben existir aquí (lo verifica una prueba).</summary>
public static class CodigosError
{
    public const string ArchivoAusente = "archivo_ausente";
    public const string ExtensionInvalida = "extension_invalida";
    public const string ArchivoVacio = "archivo_vacio";
    public const string ArchivoExcedeTamano = "archivo_excede_tamano";
    public const string CuerpoExcedeTamano = "cuerpo_excede_tamano";
    public const string XmlInvalido = "xml_invalido";
    public const string ProyectoInvalido = "proyecto_invalido";
    public const string FrameworkFueraDeAlcance = "framework_fuera_de_alcance";
    public const string LimiteDependenciasExcedido = "limite_dependencias_excedido";
    public const string IdentificadorInvalido = "identificador_invalido";
    public const string PaginacionInvalida = "paginacion_invalida";
    public const string AnalisisNoEncontrado = "analisis_no_encontrado";
    public const string RecursoNoEncontrado = "recurso_no_encontrado";
    public const string PdfNoDisponible = "pdf_no_disponible";
    public const string AlmacenamientoNoDisponible = "almacenamiento_no_disponible";
    public const string ErrorInterno = "error_interno";

    public static IReadOnlyList<DefinicionError> Todos { get; } =
    [
        new(ArchivoAusente, 400, "No se recibió ningún archivo"),
        new(ExtensionInvalida, 400, "El archivo no es un .csproj"),
        new(ArchivoVacio, 400, "El archivo está vacío"),
        new(ArchivoExcedeTamano, 400, "El archivo supera el tamaño máximo permitido"),
        new(CuerpoExcedeTamano, 413, "La solicitud supera el tamaño máximo permitido"),
        new(XmlInvalido, 400, "El archivo no es un XML válido"),
        new(ProyectoInvalido, 400, "El archivo no es un proyecto de .NET"),
        new(FrameworkFueraDeAlcance, 422, "El proyecto no usa .NET 8 ni .NET 10"),
        new(LimiteDependenciasExcedido, 422, "El proyecto declara más dependencias de las permitidas"),
        new(IdentificadorInvalido, 400, "El identificador no es válido"),
        new(PaginacionInvalida, 400, "Los parámetros de paginación no son válidos"),
        new(AnalisisNoEncontrado, 404, "El análisis no existe o ya no está disponible"),
        new(RecursoNoEncontrado, 404, "Recurso no encontrado"),
        new(PdfNoDisponible, 500, "No se pudo generar el PDF"),
        new(AlmacenamientoNoDisponible, 503, "No se pudo guardar o leer el análisis"),
        new(ErrorInterno, 500, "Ocurrió un error inesperado"),
    ];

    public static DefinicionError Definicion(string codigo) =>
        Todos.Single(d => d.Codigo == codigo);
}
