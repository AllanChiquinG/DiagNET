using System.Text.Json.Serialization;

namespace Compartido.Contratos;

// Todas las enumeraciones viajan como texto en snake_case (valores alineados con los CHECK de la base de datos).

/// <summary>Estado principal de una dependencia (RF-09). Precedencia propuesta: ver docs/criterios_diagnostico.md.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EstadoDiagnostico
{
    [JsonStringEnumMemberName("actualizada")] Actualizada,
    [JsonStringEnumMemberName("desactualizada")] Desactualizada,
    [JsonStringEnumMemberName("incompatible")] Incompatible,
    [JsonStringEnumMemberName("sin_soporte_activo")] SinSoporteActivo,
    [JsonStringEnumMemberName("no_determinada")] NoDeterminada,
}

/// <summary>Un análisis es parcial cuando algún resultado decisivo no pudo determinarse.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EstadoAnalisis
{
    [JsonStringEnumMemberName("completo")] Completo,
    [JsonStringEnumMemberName("parcial")] Parcial,
}

/// <summary>De dónde proviene el dato mostrado (ResultadoMetadato del diseño UML).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OrigenDato
{
    [JsonStringEnumMemberName("nuget")] Nuget,
    [JsonStringEnumMemberName("cache_vigente")] CacheVigente,
    [JsonStringEnumMemberName("cache_expirada")] CacheExpirada,
    [JsonStringEnumMemberName("no_determinado")] NoDeterminado,
}

/// <summary>
/// Distingue tres situaciones que nunca deben confundirse: valor conocido (incluido <c>false</c> o <c>0</c>),
/// dato que no pudo obtenerse y dato que esta versión del producto no evalúa (AOT).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Disponibilidad
{
    [JsonStringEnumMemberName("conocido")] Conocido,
    [JsonStringEnumMemberName("no_determinado")] NoDeterminado,
    [JsonStringEnumMemberName("no_evaluado")] NoEvaluado,
}

/// <summary>Forma en que el .csproj declara la versión. Ninguna forma distinta de «exacta» se resuelve sin evidencia.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TipoVersionDeclarada
{
    [JsonStringEnumMemberName("exacta")] Exacta,
    [JsonStringEnumMemberName("rango")] Rango,
    [JsonStringEnumMemberName("flotante")] Flotante,
    [JsonStringEnumMemberName("propiedad")] Propiedad,
    [JsonStringEnumMemberName("ausente")] Ausente,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResultadoCompatibilidad
{
    [JsonStringEnumMemberName("compatible")] Compatible,
    [JsonStringEnumMemberName("incompatible")] Incompatible,
    [JsonStringEnumMemberName("no_determinada")] NoDeterminada,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GravedadHallazgo
{
    [JsonStringEnumMemberName("informativo")] Informativo,
    [JsonStringEnumMemberName("aviso")] Aviso,
    [JsonStringEnumMemberName("grave")] Grave,
}

/// <summary><c>candidato_para_revision</c> no es una recomendación: es un paquete hallado sin relación funcional demostrada.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TipoRecomendacion
{
    [JsonStringEnumMemberName("actualizar_version")] ActualizarVersion,
    [JsonStringEnumMemberName("alternativa")] Alternativa,
    [JsonStringEnumMemberName("candidato_para_revision")] CandidatoParaRevision,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TipoLicencia
{
    [JsonStringEnumMemberName("expresion")] Expresion,
    [JsonStringEnumMemberName("url")] Url,
    [JsonStringEnumMemberName("archivo")] Archivo,
}
