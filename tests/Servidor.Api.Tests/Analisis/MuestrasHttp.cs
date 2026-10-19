using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Compartido.Contratos;
using static Servidor.Api.Tests.Analisis.Ayuda;

namespace Servidor.Api.Tests.Analisis;

/// <summary>
/// Genera, a petición, muestras reales de solicitud/respuesta HTTP (aplicación real + PostgreSQL real + NuGet simulado) para la documentación de seguimiento.
/// Solo escribe archivos si existe la variable de entorno DIAGNET_MUESTRAS_DIR; sin ella, solo comprueba que el flujo responde y no escribe nada.
/// </summary>
public sealed class MuestrasHttp(BaseDeDatosApi bd) : IClassFixture<BaseDeDatosApi>
{
    private static readonly JsonSerializerOptions Indentado = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public async Task Genera_muestras_http_si_se_pide()
    {
        var directorio = Environment.GetEnvironmentVariable("DIAGNET_MUESTRAS_DIR");
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(bd.CadenaConexion, nuget);
        using var http = fabrica.CreateClient();
        var muestras = new List<(string Nombre, string Peticion, HttpResponseMessage Respuesta, string Cuerpo)>();

        async Task Registrar(string nombre, string peticion, HttpResponseMessage respuesta) =>
            muestras.Add((nombre, peticion, respuesta, await respuesta.Content.ReadAsStringAsync()));

        var csproj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n  <ItemGroup>\n"
                     + "    <PackageReference Include=\"Ejemplo.Antigua\" Version=\"1.0.0\" />\n    <PackageReference Include=\"Ejemplo.Obsoleto\" Version=\"1.0.0\" />\n"
                     + "    <PackageReference Include=\"Ejemplo.NoExiste\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>";

        var post = await http.PostAsync("/api/analisis", Formulario(csproj));
        await Registrar("01_post_analisis_parcial_201", "curl -i -X POST http://localhost:5000/api/analisis -F \"archivo=@Mi.csproj\"", post);
        var id = Json(muestras[0].Cuerpo).GetProperty("id").GetGuid();
        var solicitudes = nuget.Solicitudes.Count;

        await Registrar("02_get_detalle_200", $"curl -i http://localhost:5000/api/analisis/{id}", await http.GetAsync($"/api/analisis/{id}"));
        await Registrar("03_get_historial_200", $"curl -i \"http://localhost:5000/api/analisis?ids={id}&pagina=1&tamano=20\"", await http.GetAsync($"/api/analisis?ids={id}&pagina=1&tamano=20"));
        Assert.Equal(solicitudes, nuget.Solicitudes.Count);   // el detalle y el historial no consultaron NuGet

        await Registrar("04_post_archivo_vacio_400", "curl -i -X POST http://localhost:5000/api/analisis -F \"archivo=@Vacio.csproj\"", await http.PostAsync("/api/analisis", Formulario("", "Vacio.csproj")));
        await Registrar("05_post_framework_fuera_de_alcance_422", "curl -i -X POST http://localhost:5000/api/analisis -F \"archivo=@Viejo.csproj\"",
            await http.PostAsync("/api/analisis", Formulario("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>", "Viejo.csproj")));
        await Registrar("06_get_inexistente_404", $"curl -i http://localhost:5000/api/analisis/{Guid.Empty}", await http.GetAsync($"/api/analisis/{Guid.Empty}"));
        await Registrar("07_get_id_invalido_400", "curl -i http://localhost:5000/api/analisis/no-es-un-uuid", await http.GetAsync("/api/analisis/no-es-un-uuid"));
        await Registrar("08_get_historial_sin_ids_400", "curl -i http://localhost:5000/api/analisis", await http.GetAsync("/api/analisis"));
        await Registrar("09_get_ruta_api_desconocida_404", "curl -i http://localhost:5000/api/no-existe", await http.GetAsync("/api/no-existe"));
        await Registrar("10_get_pdf_no_disponible_en_E8_404", $"curl -i http://localhost:5000/api/analisis/{id}/pdf", await http.GetAsync($"/api/analisis/{id}/pdf"));

        Assert.Equal(10, muestras.Count);
        if (string.IsNullOrWhiteSpace(directorio)) return;

        Directory.CreateDirectory(directorio);
        foreach (var (nombre, peticion, r, cuerpo) in muestras)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Solicitud reproducible (contra la API en ejecución)").AppendLine(peticion).AppendLine();
            sb.AppendLine($"# Respuesta real de las pruebas HTTP: HTTP {(int)r.StatusCode} {r.ReasonPhrase}");
            sb.AppendLine($"Content-Type: {r.Content.Headers.ContentType}");
            if (r.Headers.Location is not null) sb.AppendLine($"Location: {r.Headers.Location}");
            if (r.Headers.CacheControl is not null) sb.AppendLine($"Cache-Control: {r.Headers.CacheControl}");
            sb.AppendLine().AppendLine(cuerpo.Length == 0 ? "(sin cuerpo)" : Pretty(cuerpo));
            await File.WriteAllTextAsync(Path.Combine(directorio, nombre + ".txt"), sb.ToString(), new UTF8Encoding(false));
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            return JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement, Indentado);
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
