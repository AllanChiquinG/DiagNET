using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Datos.Tests;

/// <summary>
/// Un paquete leído de NuGet (simulado), convertido a filas de caché, guardado en PostgreSQL REAL, leído tras «reiniciar» y reconstruido,
/// debe conservar todo lo que usan el diagnóstico y las recomendaciones: licencia completa, reemplazo declarado (y su rango), deprecación,
/// grupos de framework, compatibilidad por TFM, última estable, última compatible, descargas y completitud de la selección.
/// </summary>
public class CacheEquivalenciaTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private static readonly string UrlLicenciaLarga = "https://example.org/licencias/" + new string('x', 2500);

    private sealed class NuGetFalso : HttpMessageHandler
    {
        private const string Base = "https://api.nuget.org/v3/registration5-gz-semver2/";

        private static string Hoja(string version, string[] grupos, string? licenciaExpr, string? licenciaUrl, string? deprecacion, string publicada) =>
            "{\"catalogEntry\":{\"version\":\"" + version + "\",\"listed\":true,\"published\":\"" + publicada + "\""
            + (licenciaExpr is null ? "" : ",\"licenseExpression\":\"" + licenciaExpr + "\"")
            + (licenciaUrl is null ? "" : ",\"licenseUrl\":\"" + licenciaUrl + "\"")
            + ",\"dependencyGroups\":[" + string.Join(',', grupos.Select(g => g.Length == 0 ? "{}" : "{\"targetFramework\":\"" + g + "\"}")) + "]"
            + (deprecacion is null ? "" : ",\"deprecation\":" + deprecacion) + "}}";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            string? json = url switch
            {
                "https://api.nuget.org/v3/index.json" =>
                    "{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + Base + "\",\"@type\":\"RegistrationsBaseUrl/3.6.0\"},{\"@id\":\"https://azuresearch-usnc.nuget.org/query\",\"@type\":\"SearchQueryService/3.5.0\"}]}",
                var u when u == Base + "ejemplo.deprecado/index.json" =>
                    "{\"items\":[{\"lower\":\"1.0.0\",\"upper\":\"3.0.0\",\"items\":[" + string.Join(',',
                        Hoja("1.0.0", ["", "net6.0"], null, UrlLicenciaLarga,
                            "{\"reasons\":[\"Legacy\"],\"message\":\"Obsoleto\",\"alternatePackage\":{\"id\":\"Ejemplo.Moderno\",\"range\":\"[2.0.0, )\"}}", "2024-01-10T00:00:00+00:00"),
                        Hoja("2.0.0", ["net6.0"], "MIT", null, null, "2025-02-20T00:00:00+00:00"),
                        Hoja("3.0.0", ["net10.0"], "MIT", null, null, "2026-03-30T00:00:00+00:00")) + "]}]}",
                var u when u.StartsWith("https://azuresearch-usnc.nuget.org/query") =>
                    "{\"data\":[{\"id\":\"Ejemplo.Deprecado\",\"version\":\"3.0.0\",\"totalDownloads\":4321,\"verified\":false}]}",
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static readonly JsonSerializerOptions Opciones = Crear();

    private static JsonSerializerOptions Crear()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return o;
    }

    /// <summary>Todo lo que el motor de diagnóstico y el de recomendaciones consultan de un paquete.</summary>
    private static string Proyeccion(InformacionPaquete i) => JsonSerializer.Serialize(new
    {
        i.EstadoDeclarada,
        Declarada = i.Declarada is null ? null : new
        {
            i.Declarada.Version, i.Declarada.Publicada, i.Declarada.Licencia, i.Declarada.TipoLicencia,
            Deprecado = i.Declarada.Deprecacion is not null,
            Alterno = i.Declarada.Deprecacion?.PaqueteAlterno,
            Rango = i.Declarada.Deprecacion?.RangoVersionAlterno,
            i.Declarada.GruposFramework,
        },
        Compatibilidad = i.CompatibilidadDeclarada,
        UltimaEstable = i.UltimaEstable?.Version,
        UltimaEstablePublicada = i.UltimaEstable?.Publicada,
        UltimaCompatible = i.UltimaEstableCompatiblePorTfm.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value?.Version),
        i.SeleccionCompleta,
        i.Descargas,
        i.UltimaPublicacion,
    }, Opciones);

    private static NuGetGateway Gateway()
    {
        var opciones = new OpcionesNuGet();
        return new NuGetGateway(new HttpClient(new NuGetFalso()), opciones);
    }

    [Fact]
    public async Task Servir_desde_la_cache_conserva_lo_necesario_para_el_mismo_diagnostico_y_las_mismas_recomendaciones()
    {
        var consulta = new ConsultaPaquete("Ejemplo.Deprecado", "1.0.0", ["net8.0", "net10.0"]);
        var original = (await Gateway().ObtenerPaqueteAsync(consulta)).Valor!;
        Assert.Equal("Ejemplo.Moderno", original.Declarada!.Deprecacion!.PaqueteAlterno); // la lectura real trajo el reemplazo declarado
        Assert.Equal(UrlLicenciaLarga, original.Declarada.Licencia);
        Assert.Equal(4321L, original.Descargas);

        // Se guarda en PostgreSQL, se «reinicia» la aplicación y se vuelve a leer de la base.
        var paraCache = ConversorCache.Convertir(original);
        Assert.Empty(paraCache.Advertencias);
        foreach (var fila in paraCache.Metadatos) await bd.Metadatos.GuardarAsync(fila);
        await bd.Metadatos.GuardarSeleccionAsync(paraCache.Seleccion!);

        await using var reinicio = bd.Reiniciar();
        var metadatos = reinicio.GetRequiredService<IMetadatoRepositorio>();
        var declarada = await metadatos.BuscarAsync("Ejemplo.Deprecado", "1.0.0");
        var estable = await metadatos.BuscarAsync("Ejemplo.Deprecado", "3.0.0");
        var seleccion = await metadatos.BuscarSeleccionAsync("ejemplo.deprecado");
        var reconstruido = ConversorCache.Reconstruir("Ejemplo.Deprecado", declarada, estable, seleccion, consulta.FrameworksEvaluados, true, new InferidorCompatibilidad());

        Assert.Equal(Proyeccion(original), Proyeccion(reconstruido));
        // Lo que más importa a las recomendaciones, de forma explícita:
        Assert.Equal(UrlLicenciaLarga, reconstruido.Declarada!.Licencia);
        Assert.Equal("Ejemplo.Moderno", reconstruido.Declarada.Deprecacion!.PaqueteAlterno);
        Assert.Equal("[2.0.0, )", reconstruido.Declarada.Deprecacion.RangoVersionAlterno);
        Assert.Equal("3.0.0", reconstruido.UltimaEstable!.Version);
        Assert.Equal("2.0.0", reconstruido.UltimaEstableCompatiblePorTfm["net8.0"]!.Version);
        Assert.Equal("3.0.0", reconstruido.UltimaEstableCompatiblePorTfm["net10.0"]!.Version);
        Assert.Equal([ResultadoCompatibilidad.Compatible, ResultadoCompatibilidad.Compatible], reconstruido.CompatibilidadDeclarada.Select(c => c.Resultado)); // el grupo «para todos» sobrevive
    }

    [Fact]
    public async Task Lo_que_la_cache_no_conserva_esta_documentado_y_no_cambia_el_resultado()
    {
        var consulta = new ConsultaPaquete("Ejemplo.Deprecado", "1.0.0", ["net8.0"]);
        var original = (await Gateway().ObtenerPaqueteAsync(consulta)).Valor!;
        var paraCache = ConversorCache.Convertir(original);
        foreach (var fila in paraCache.Metadatos) await bd.Metadatos.GuardarAsync(fila);
        await bd.Metadatos.GuardarSeleccionAsync(paraCache.Seleccion!);

        var reconstruido = ConversorCache.Reconstruir("Ejemplo.Deprecado",
            await bd.Metadatos.BuscarAsync("Ejemplo.Deprecado", "1.0.0"), await bd.Metadatos.BuscarAsync("Ejemplo.Deprecado", "3.0.0"),
            await bd.Metadatos.BuscarSeleccionAsync("Ejemplo.Deprecado"), consulta.FrameworksEvaluados, true, new InferidorCompatibilidad());

        // Razones y mensaje de la deprecación no están en la caché (el estado y el reemplazo sí).
        Assert.Equal("Legacy", Assert.Single(original.Declarada!.Deprecacion!.Razones));
        Assert.Empty(reconstruido.Declarada!.Deprecacion!.Razones);
        Assert.Null(reconstruido.Declarada.Deprecacion.Mensaje);
        Assert.Equal(original.Declarada.Deprecacion.PaqueteAlterno, reconstruido.Declarada.Deprecacion.PaqueteAlterno);
    }

    [Fact]
    public async Task Un_paquete_sin_licencia_ni_reemplazo_se_reconstruye_sin_inventarlos()
    {
        var info = new InformacionPaquete("Ejemplo.Simple", EstadoVersionDeclarada.Publicada,
            new VersionPublicada("1.0.0", "1.0.0", false, true, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null, ["net8.0"], []),
            [], new VersionPublicada("1.0.0", "1.0.0", false, true, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), null, null, null, ["net8.0"], []), null,
            new Dictionary<string, VersionPublicada?> { ["net8.0"] = null }, null, true, 1, 1, 1, Muestras.Base, null, "sin dato");
        var paraCache = ConversorCache.Convertir(info);
        foreach (var fila in paraCache.Metadatos) await bd.Metadatos.GuardarAsync(fila);

        var leido = (await bd.Metadatos.BuscarAsync("Ejemplo.Simple", "1.0.0"))!;
        var rec = ConversorCache.Reconstruir("Ejemplo.Simple", leido, leido, null, ["net8.0"], true, new InferidorCompatibilidad());

        Assert.Null(leido.Licencia);
        Assert.Null(leido.PaqueteAlterno);
        Assert.Null(leido.Descargas);
        Assert.Null(rec.Declarada!.Licencia);
        Assert.Null(rec.Declarada.Deprecacion);
        Assert.Null(rec.Descargas);
    }
}
