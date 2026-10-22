using System.Net;
using System.Net.Http.Json;
using System.Text;
using Compartido.Contratos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Servidor.Negocio.Reportes;
using UglyToad.PdfPig;
using static Servidor.Api.Tests.Analisis.Ayuda;

namespace Servidor.Api.Tests.Analisis;

/// <summary>
/// GET /api/analisis/{id}/pdf (E10) contra la aplicación REAL: Program + motores reales + PostgreSQL real (base temporal) + generador PDF real.
/// El PDF sale SOLO del snapshot guardado: no consulta NuGet, no recalcula y no se guarda.
/// </summary>
public sealed class PdfHttpTests : IClassFixture<BaseDeDatosApi>, IDisposable
{
    private readonly BaseDeDatosApi _bd;
    private readonly NuGetSimulado _nuget = new();
    private readonly FabricaAnalisis _fabrica;
    private readonly HttpClient _http;

    public PdfHttpTests(BaseDeDatosApi bd)
    {
        _bd = bd;
        _fabrica = new FabricaAnalisis(bd.CadenaConexion, _nuget);
        _http = _fabrica.CreateClient();
    }

    public void Dispose()
    {
        _http.Dispose();
        _fabrica.Dispose();
    }

    private async Task<ResultadoAnalisisDto> AnalizarOk(string contenido, string nombre = "Mi.csproj", HttpClient? cliente = null)
    {
        var r = await (cliente ?? _http).PostAsync("/api/analisis", Formulario(contenido, nombre));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<ResultadoAnalisisDto>())!;
    }

    private static string Texto(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join(" ", doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text))).Replace("​", "");
    }

    private static async Task VerificarProblema(HttpResponseMessage respuesta, HttpStatusCode estado, string codigo)
    {
        Assert.Equal(estado, respuesta.StatusCode);
        Assert.Equal("application/problem+json", respuesta.Content.Headers.ContentType?.MediaType);
        var problema = (await respuesta.Content.ReadFromJsonAsync<ProblemaDto>())!;
        Assert.Equal(codigo, problema.Codigo);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
    }

    [Fact]
    public async Task Descarga_un_PDF_real_con_tipo_nombre_y_las_cabeceras_de_E8_y_sin_consultar_NuGet()
    {
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.A"), "2.0.0"), ("Ejemplo.Obsoleto", "1.0.0")), "Mi Tienda.Api.csproj");
        var solicitudes = _nuget.Solicitudes.Count;
        var filas = await _bd.ContarAsync("analisis");

        var r = await _http.GetAsync($"/api/analisis/{dto.Id}/pdf");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/pdf", r.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", r.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches(@"^DiagNET_Mi_Tienda\.Api_\d{8}\.pdf$", r.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await r.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.Equal(bytes.Length, r.Content.Headers.ContentLength);
        // Política de cabeceras de E8 aplicada también aquí.
        Assert.Equal("no-store", r.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        // Ni consultas a NuGet, ni nuevo análisis, ni nada guardado.
        Assert.Equal(solicitudes, _nuget.Solicitudes.Count);
        Assert.Equal(filas, await _bd.ContarAsync("analisis"));
    }

    [Fact]
    public async Task El_contenido_del_PDF_coincide_con_el_snapshot_que_devuelve_el_detalle()
    {
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.B"), "2.0.0"), ("Ejemplo.Obsoleto", "1.0.0"), ("Ejemplo.NoExiste", "1.0.0")));
        var detalle = await (await _http.GetAsync($"/api/analisis/{dto.Id}")).Content.ReadFromJsonAsync<ResultadoAnalisisDto>();

        var texto = Texto(await (await _http.GetAsync($"/api/analisis/{dto.Id}/pdf")).Content.ReadAsByteArrayAsync());

        Assert.Contains(dto.Id.ToString("D"), texto);
        Assert.Contains("Parcial", texto);
        Assert.Contains("Diagnóstico parcial", texto);
        foreach (var d in detalle!.Dependencias)
        {
            Assert.Contains(d.Nombre, texto);
            Assert.Contains(d.Diagnostico.Estado switch
            {
                EstadoDiagnostico.Actualizada => "Actualizada",
                EstadoDiagnostico.Desactualizada => "Desactualizada",
                EstadoDiagnostico.Incompatible => "Incompatible",
                EstadoDiagnostico.SinSoporteActivo => "Sin soporte activo",
                _ => "No determinada",
            }, texto);
            foreach (var r in d.Recomendaciones) Assert.Contains(r.Etiqueta!, texto);
        }

        Assert.Contains("Alternativa sugerida por el autor", texto);
        Assert.Contains("Sin datos de NuGet", texto);          // la dependencia que NuGet no conoce no inventa datos
        Assert.Contains("inferida", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AOT", texto);
    }

    [Fact]
    public async Task Un_analisis_sin_dependencias_genera_un_PDF_valido()
    {
        var dto = await AnalizarOk(Csproj("net8.0"));

        var r = await _http.GetAsync($"/api/analisis/{dto.Id}/pdf");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("no declara paquetes NuGet", Texto(await r.Content.ReadAsByteArrayAsync()));
    }

    [Fact]
    public async Task Generar_el_PDF_no_cambia_el_resultado_guardado_y_se_puede_repetir()
    {
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.C"), "1.0.0")));
        var antes = await (await _http.GetAsync($"/api/analisis/{dto.Id}")).Content.ReadAsStringAsync();

        var a = await _http.GetAsync($"/api/analisis/{dto.Id}/pdf");
        var b = await _http.GetAsync($"/api/analisis/{dto.Id}/pdf");

        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal(antes, await (await _http.GetAsync($"/api/analisis/{dto.Id}")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Un_analisis_inexistente_devuelve_404_y_un_identificador_invalido_400_sin_PDF()
    {
        var inexistente = await _http.GetAsync($"/api/analisis/{Guid.NewGuid()}/pdf");
        await VerificarProblema(inexistente, HttpStatusCode.NotFound, CodigosError.AnalisisNoEncontrado);
        Assert.NotEqual("application/pdf", inexistente.Content.Headers.ContentType?.MediaType);

        await VerificarProblema(await _http.GetAsync("/api/analisis/no-es-un-uuid/pdf"), HttpStatusCode.BadRequest, CodigosError.IdentificadorInvalido);
        Assert.Empty(_nuget.Solicitudes);
    }

    [Fact]
    public async Task Si_falla_el_generador_responde_500_pdf_no_disponible_y_el_resultado_sigue_guardado_y_consultable()
    {
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, nuget, personalizar: s => s.Replace(ServiceDescriptor.Singleton<IGeneradorPdf, GeneradorQueFalla>()));
        using var http = fabrica.CreateClient();
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.D"), "1.0.0")), cliente: http);

        var r = await http.GetAsync($"/api/analisis/{dto.Id}/pdf");

        await VerificarProblema(r, HttpStatusCode.InternalServerError, CodigosError.PdfNoDisponible);
        var cuerpo = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("GeneradorQueFalla", cuerpo);          // sin trazas ni detalles internos
        Assert.DoesNotContain("System.", cuerpo);
        // El análisis sigue ahí, intacto: la interfaz lo conserva en pantalla y se puede reintentar.
        var detalle = await http.GetAsync($"/api/analisis/{dto.Id}");
        Assert.Equal(HttpStatusCode.OK, detalle.StatusCode);
    }

    [Fact]
    public async Task Con_la_base_caida_el_PDF_responde_503_uniforme_y_no_consulta_NuGet()
    {
        var rota = new Npgsql.NpgsqlConnectionStringBuilder(_bd.CadenaConexion) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 3 }.ConnectionString;
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(rota, nuget);
        using var http = fabrica.CreateClient();

        var r = await http.GetAsync($"/api/analisis/{Guid.NewGuid()}/pdf");

        await VerificarProblema(r, HttpStatusCode.ServiceUnavailable, CodigosError.AlmacenamientoNoDisponible);
        Assert.Empty(nuget.Solicitudes);
    }

    [Fact]
    public async Task Los_registros_del_PDF_no_contienen_identificadores_ni_URL_ni_nombre_de_archivo_pero_si_la_referencia_del_error()
    {
        var registros = new CapturadorDeRegistros();
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, nuget, registros: registros, personalizar: s => s.Replace(ServiceDescriptor.Singleton<IGeneradorPdf, GeneradorQueFalla>()));
        using var http = fabrica.CreateClient();
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.E"), "1.0.0")), "Reservado.csproj", http);

        await http.GetAsync($"/api/analisis/{dto.Id}/pdf");                      // 500 con generador roto
        await http.GetAsync($"/api/analisis/{Guid.NewGuid()}/pdf");              // 404
        await http.GetAsync("/api/analisis/x/pdf");                              // 400

        var entradas = registros.Entradas;
        Assert.NotEmpty(entradas);
        foreach (var e in entradas)
            foreach (var prohibido in new[] { dto.Id.ToString(), dto.Id.ToString("N"), "/pdf", "/api/analisis", "Reservado", ".csproj", "Ejemplo." })
                Assert.False(e.Texto.Contains(prohibido, StringComparison.OrdinalIgnoreCase), $"El registro [{e.Categoria}] contiene «{prohibido}»: {e.Texto}");
        Assert.Contains(entradas, e => e.Nivel == LogLevel.Error && e.Texto.Contains("No se pudo generar un PDF. Referencia"));
        Assert.DoesNotContain(entradas, e => (e.Categoria.StartsWith("Microsoft.AspNetCore") || e.Categoria.StartsWith("System.Net.Http")
                                              || e.Categoria.StartsWith("Microsoft.EntityFrameworkCore") || e.Categoria.StartsWith("Npgsql")) && e.Nivel < LogLevel.Warning);
    }

    [Fact]
    public async Task El_excedente_de_PDF_simultaneos_recibe_429_y_no_se_encola()
    {
        var bloqueo = new GeneradorBloqueable();
        var nuget = new NuGetSimulado();
        using var fabrica = new FabricaAnalisis(_bd.CadenaConexion, nuget, new Dictionary<string, string?> { ["Analisis:MaxReportesSimultaneos"] = "1" },
            personalizar: s => s.Replace(ServiceDescriptor.Singleton<IGeneradorPdf>(bloqueo)));
        using var http = fabrica.CreateClient();
        var dto = await AnalizarOk(Csproj("net8.0", (Unico("Ejemplo.F"), "1.0.0")), cliente: http);

        var primera = http.GetAsync($"/api/analisis/{dto.Id}/pdf");
        await bloqueo.Entro.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var segunda = await http.GetAsync($"/api/analisis/{dto.Id}/pdf");

        await VerificarProblema(segunda, HttpStatusCode.TooManyRequests, CodigosError.DemasiadasSolicitudes);
        bloqueo.Liberar.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await primera).StatusCode);
    }

    private sealed class GeneradorQueFalla : IGeneradorPdf
    {
        public byte[] Generar(ModeloReporte modelo) => throw new InvalidOperationException("fallo simulado del generador");
    }

    private sealed class GeneradorBloqueable : IGeneradorPdf
    {
        public TaskCompletionSource Entro { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Liberar { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public byte[] Generar(ModeloReporte modelo)
        {
            Entro.TrySetResult();
            Liberar.Task.Wait(TimeSpan.FromSeconds(30));
            return Encoding.ASCII.GetBytes("%PDF-1.4\n%simulado\n");
        }
    }
}
