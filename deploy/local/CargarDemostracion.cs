#:project ../../src/Servidor.Datos/Servidor.Datos.csproj
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true

// Carga DOS análisis de DEMOSTRACIÓN en diagnet_nueva usando los repositorios reales (el mismo camino que usará la API).
// Los datos son EJEMPLOS inventados (paquetes «Ejemplo.*»; archivos «DEMO-*.csproj»), no resultados de paquetes reales.
// Uso (desde la raíz del repositorio):
//   dotnet run deploy/local/CargarDemostracion.cs            carga los dos análisis
//   dotnet run deploy/local/CargarDemostracion.cs -- --limpiar   los elimina (solo los DEMO-*)
// La cadena de conexión se lee de src/Servidor.Api/appsettings.Local.json (ignorado por Git).

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Servidor.Datos;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

var archivo = Path.Combine(Directory.GetCurrentDirectory(), "src", "Servidor.Api", "appsettings.Local.json");
if (!File.Exists(archivo)) { Console.Error.WriteLine("Ejecuta este script desde la raíz del repositorio (falta src/Servidor.Api/appsettings.Local.json)."); return 1; }
var cadena = JsonDocument.Parse(File.ReadAllText(archivo)).RootElement.GetProperty("ConnectionStrings").GetProperty("DiagNet").GetString()!;

var servicios = new ServiceCollection().AddDatos(cadena).BuildServiceProvider();
var analisis = servicios.GetRequiredService<IAnalisisRepositorio>();
var metadatos = servicios.GetRequiredService<IMetadatoRepositorio>();
var fabrica = servicios.GetRequiredService<IDbContextFactory<AppDbContext>>();

if (args.Contains("--limpiar"))
{
    await using var db = await fabrica.CreateDbContextAsync();
    var n = await db.Database.ExecuteSqlRawAsync("DELETE FROM analisis WHERE nombre_archivo LIKE 'DEMO-%'");
    Console.WriteLine($"Análisis de demostración eliminados: {n} (sus dependencias, diagnósticos y recomendaciones se eliminan en cascada).");
    return 0;
}

var ahora = DateTimeOffset.UtcNow;
Indicador<T> Conocido<T>(T valor) => new(Disponibilidad.Conocido, valor);
IndicadoresLibreria Indicadores(string ultima, bool deprecado, long? descargas, int meses) => new(
    Conocido<DateTimeOffset?>(ahora.AddMonths(-meses)),
    Conocido<string?>(ultima),
    Conocido<string?>(ultima),
    true,
    Conocido<int?>(meses),
    descargas is null ? new(Disponibilidad.NoDeterminado, null, "NuGet no devolvió el dato.") : Conocido<long?>(descargas),
    Conocido<Licencia?>(new Licencia(TipoLicencia.Expresion, "MIT")),
    Conocido<bool?>(deprecado),
    deprecado ? new Deprecacion(["Legacy"], "Reemplazado por Ejemplo.Moderno.", "Ejemplo.Moderno", "[1.0.0, )") : null,
    [new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups", "Declara un grupo de dependencias para net8.0.")],
    new(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));

DependenciaAnalizada Dep(string nombre, string version, EstadoDiagnostico estado, string motivo, OrigenDato origen, string ultima, bool deprecado, int meses, Hallazgo hallazgo, params Recomendacion[] recs) =>
    new(nombre, version, TipoVersion.Exacta, null, [],
        new Diagnostico(estado, motivo, ultima, [hallazgo], [new EvaluacionTfm("net8.0", estado)], origen, ahora.AddSeconds(-5), Indicadores(ultima, deprecado, 480000, meses)),
        recs);

var completo = new Analisis(
    Guid.NewGuid(), "DEMO-MiProyecto.csproj", ["net8.0"], ["net8.0"], ahora.AddMinutes(-3), EstadoAnalisis.Completo,
    [
        Dep("Ejemplo.Registro", "3.2.1", EstadoDiagnostico.Actualizada, "Usas la última versión estable compatible con net8.0.", OrigenDato.Nuget, "3.2.1", false, 2,
            new Hallazgo("actualizacion", "version_al_dia", GravedadHallazgo.Informativo, "No existe una versión estable posterior compatible.", [new Evidencia("versionDeclarada", "3.2.1")])),
        Dep("Ejemplo.Cliente", "1.0.0", EstadoDiagnostico.Desactualizada, "Existe una versión estable posterior compatible con net8.0 (2.1.0).", OrigenDato.CacheVigente, "2.1.0", false, 20,
            new Hallazgo("actualizacion", "version_posterior_compatible", GravedadHallazgo.Aviso, "Hay una versión más reciente (2.1.0).", [new Evidencia("ultimaEstableCompatible", "2.1.0")]),
            new Recomendacion(TipoRecomendacion.ActualizarVersion, "Ejemplo.Cliente", "2.1.0", "Es la última versión estable que declara compatibilidad con net8.0.",
                [new Evidencia("ultimaEstableCompatible", "2.1.0")], ["Es un cambio de versión mayor: puede incluir cambios incompatibles."], true)),
        Dep("Ejemplo.Antiguo", "0.9.0", EstadoDiagnostico.SinSoporteActivo, "NuGet lo marca como obsoleto y propone Ejemplo.Moderno. La deprecación es una señal publicada; no prueba por sí sola falta de correcciones.", OrigenDato.Nuget, "0.9.0", true, 65,
            new Hallazgo("soporte_activo", "paquete_deprecado", GravedadHallazgo.Grave, "El autor marcó el paquete como obsoleto.", [new Evidencia("paqueteAlterno", "Ejemplo.Moderno")]),
            new Recomendacion(TipoRecomendacion.Alternativa, "Ejemplo.Moderno", "1.4.0", "El propio autor del paquete lo indica como reemplazo.",
                [new Evidencia("paqueteAlterno", "Ejemplo.Moderno")], ["La migración puede requerir cambios de código; DiagNET no los verificó."], true)),
    ],
    [], "0.1-propuesta");

var parcial = new Analisis(
    Guid.NewGuid(), "DEMO-Servicio.Api.csproj", ["net8.0", "net10.0", "net48"], ["net8.0", "net10.0"], ahora.AddMinutes(-1), EstadoAnalisis.Parcial,
    [
        Dep("Ejemplo.Registro", "3.2.1", EstadoDiagnostico.Actualizada, "Según la copia guardada, no existía una versión posterior compatible.", OrigenDato.CacheExpirada, "3.2.1", false, 2,
            new Hallazgo("actualizacion", "version_al_dia", GravedadHallazgo.Informativo, "Sin versión posterior según los datos disponibles.", [new Evidencia("ultimaEstableCompatible", "3.2.1")])),
        new("Ejemplo.Desconocido", "2.0.0", TipoVersion.Exacta, null, [new Advertencia("sin_datos", "NuGet no respondió y no hay copia guardada.", "Ejemplo.Desconocido")],
            new Diagnostico(EstadoDiagnostico.NoDeterminada, "No se pudo consultar NuGet y no existe una copia guardada. No es un error de tu proyecto.", null,
                [new Hallazgo("actualizacion", "sin_evidencia", GravedadHallazgo.Aviso, "Faltan los datos de NuGet.", [])],
                [new EvaluacionTfm("net8.0", EstadoDiagnostico.NoDeterminada), new EvaluacionTfm("net10.0", EstadoDiagnostico.NoDeterminada)],
                OrigenDato.NoDeterminado, null, null), []),
    ],
    [new Advertencia("analisis_parcial", "1 de 2 dependencias no pudo determinarse."), new Advertencia("framework_fuera_de_alcance_omitido", "net48 está fuera del alcance de DiagNET.")],
    "0.1-propuesta");

await metadatos.GuardarAsync(new MetadatoLibreria("Ejemplo.Registro", "3.2.1", ahora.AddMonths(-2), 1250000, "MIT", ["net8.0", "net10.0"], null, false, ahora.AddSeconds(-10)));
await metadatos.GuardarAsync(new MetadatoLibreria("Ejemplo.Antiguo", "0.9.0", ahora.AddMonths(-65), 92000, "MIT", ["net8.0"], null, true, ahora.AddSeconds(-10)));
await metadatos.GuardarSeleccionAsync(new SeleccionPaquete("Ejemplo.Registro", "3.2.1", new Dictionary<string, string> { ["net8.0"] = "3.2.1", ["net10.0"] = "3.2.1" }, true, ahora.AddSeconds(-10)));
await analisis.GuardarAsync(completo);
await analisis.GuardarAsync(parcial);

Console.WriteLine("Análisis de demostración guardados:");
Console.WriteLine($"  {completo.Id}  DEMO-MiProyecto.csproj       (completo, 3 dependencias)");
Console.WriteLine($"  {parcial.Id}  DEMO-Servicio.Api.csproj     (parcial, 2 dependencias)");
var recuperado = await analisis.ObtenerAsync(completo.Id);
Console.WriteLine($"Comprobación: se recuperó '{recuperado?.NombreArchivo}' con {recuperado?.Dependencias.Count} dependencias.");
return 0;
