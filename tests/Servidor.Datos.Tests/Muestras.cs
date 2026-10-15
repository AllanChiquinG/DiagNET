using System.Text.Json;
using System.Text.Json.Serialization;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

namespace Servidor.Datos.Tests;

/// <summary>Resultados de ejemplo para las pruebas (paquetes inventados «Ejemplo.*»; no son datos de paquetes reales).</summary>
internal static class Muestras
{
    public static readonly DateTimeOffset Base = new(2026, 10, 14, 18, 30, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Opciones = Crear();

    private static JsonSerializerOptions Crear()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }

    /// <summary>Texto JSON canónico para comparar resultados completos (incluye nulos, listas y enumeraciones).</summary>
    public static string Json<T>(T valor) => JsonSerializer.Serialize(valor, Opciones);

    public static IndicadoresLibreria Indicadores(string ultima = "2.1.0", bool deprecado = false, long? descargas = 480000) => new(
        new Indicador<DateTimeOffset?>(Disponibilidad.Conocido, Base.AddYears(-1)),
        new Indicador<string?>(Disponibilidad.Conocido, ultima),
        new Indicador<string?>(Disponibilidad.Conocido, ultima),
        true,
        new Indicador<int?>(Disponibilidad.Conocido, 1),
        descargas is null ? new Indicador<long?>(Disponibilidad.NoDeterminado, null, "NuGet no devolvió el dato.") : new Indicador<long?>(Disponibilidad.Conocido, descargas),
        new Indicador<Licencia?>(Disponibilidad.Conocido, new Licencia(TipoLicencia.Expresion, "MIT")),
        new Indicador<bool?>(Disponibilidad.Conocido, deprecado),
        deprecado ? new Deprecacion(["Legacy"], "Reemplazado.", "Ejemplo.Moderno", "[1.0.0, )") : null,
        [new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups", "Grupo para net8.0.")],
        new Indicador<bool?>(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));

    public static DependenciaAnalizada Actualizada(string nombre = "Ejemplo.Registro", string version = "3.2.1") => new(
        nombre, version, TipoVersion.Exacta, null, [],
        new Diagnostico(EstadoDiagnostico.Actualizada, "Usas la última versión estable compatible.", version,
            [new Hallazgo("actualizacion", "version_al_dia", GravedadHallazgo.Informativo, "Sin versión posterior.", [new Evidencia("versionDeclarada", version)])],
            [new EvaluacionTfm("net8.0", EstadoDiagnostico.Actualizada)],
            OrigenDato.Nuget, Base.AddSeconds(2), Indicadores(version)),
        []);

    public static DependenciaAnalizada Desactualizada(string nombre = "Ejemplo.Cliente") => new(
        nombre, "1.0.0", TipoVersion.Exacta, null, [],
        new Diagnostico(EstadoDiagnostico.Desactualizada, "Existe una versión posterior compatible (2.1.0).", "2.1.0",
            [new Hallazgo("actualizacion", "version_posterior_compatible", GravedadHallazgo.Aviso, "Hay una versión más reciente.", [new Evidencia("ultimaEstableCompatible", "2.1.0")])],
            [new EvaluacionTfm("net8.0", EstadoDiagnostico.Desactualizada)],
            OrigenDato.CacheVigente, Base.AddHours(-9), Indicadores()),
        [
            new Recomendacion(TipoRecomendacion.ActualizarVersion, nombre, "2.1.0", "Es la última estable compatible.",
                [new Evidencia("ultimaEstableCompatible", "2.1.0")], ["Cambio de versión mayor: puede requerir cambios de código."], true),
        ]);

    public static DependenciaAnalizada Deprecada(string nombre = "Ejemplo.Antiguo") => new(
        nombre, "0.9.0", TipoVersion.Exacta, null, [],
        new Diagnostico(EstadoDiagnostico.SinSoporteActivo, "NuGet lo marca como obsoleto; propone Ejemplo.Moderno.", "0.9.0",
            [new Hallazgo("soporte_activo", "paquete_deprecado", GravedadHallazgo.Grave, "Marcado como obsoleto.", [new Evidencia("paqueteAlterno", "Ejemplo.Moderno")])],
            [new EvaluacionTfm("net8.0", EstadoDiagnostico.SinSoporteActivo)],
            OrigenDato.CacheExpirada, Base.AddDays(-2), Indicadores("0.9.0", deprecado: true)),
        [
            new Recomendacion(TipoRecomendacion.Alternativa, "Ejemplo.Moderno", "1.4.0", "El propio autor lo indica como reemplazo.",
                [new Evidencia("paqueteAlterno", "Ejemplo.Moderno")], [], true),
            new Recomendacion(TipoRecomendacion.CandidatoParaRevision, "Ejemplo.Parecido", null, "Hallado en la búsqueda; relación funcional no demostrada.", [], ["Revísalo manualmente."], false),
        ]);

    /// <summary>Dependencia sin ningún dato: fecha nula, indicadores nulos, versión no resuelta.</summary>
    public static DependenciaAnalizada SinDatos(string nombre = "Ejemplo.Desconocido") => new(
        nombre, null, TipoVersion.Propiedad, "'$(Configuration)' == 'Debug'",
        [new Advertencia("version_no_resuelta", "Depende de una propiedad.", nombre)],
        new Diagnostico(EstadoDiagnostico.NoDeterminada, "No se pudo consultar NuGet y no hay copia guardada.", null,
            [new Hallazgo("actualizacion", "sin_evidencia", GravedadHallazgo.Aviso, "Faltan datos.", [])],
            [new EvaluacionTfm("net8.0", EstadoDiagnostico.NoDeterminada)],
            OrigenDato.NoDeterminado, null, null),
        []);

    public static Analisis Completo(Guid? id = null, DateTimeOffset? fecha = null) => new(
        id ?? Guid.NewGuid(),
        "MiProyecto.csproj",
        ["net8.0"],
        ["net8.0"],
        fecha ?? Base,
        EstadoAnalisis.Completo,
        [Actualizada(), Desactualizada(), Deprecada()],
        [],
        "0.1-propuesta");

    public static Analisis Parcial(Guid? id = null, DateTimeOffset? fecha = null) => new(
        id ?? Guid.NewGuid(),
        "Servicio.Api.csproj",
        ["net8.0", "net10.0", "net48"],
        ["net8.0", "net10.0"],
        fecha ?? Base,
        EstadoAnalisis.Parcial,
        [Actualizada(), SinDatos()],
        [new Advertencia("analisis_parcial", "1 de 2 dependencias no pudo determinarse.")],
        "0.1-propuesta");

    public static Analisis SinPaquetes(Guid? id = null, DateTimeOffset? fecha = null) => new(
        id ?? Guid.NewGuid(), "Vacio.csproj", ["net8.0"], ["net8.0"], fecha ?? Base, EstadoAnalisis.Completo, [],
        [new Advertencia("sin_paquetes", "El proyecto no declara ningún PackageReference.")], "0.1-propuesta");

    public static MetadatoLibreria Metadato(string nombre = "Ejemplo.Registro", string version = "3.2.1", DateTimeOffset? consulta = null,
        long? descargas = 1250000, bool? deprecado = false) => new(
        nombre, version, Base.AddMonths(-2), descargas, "MIT", ["net8.0", "net10.0"], null, deprecado, consulta ?? Base);
}
