using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cliente.Web.Servicios;
using Compartido.Contratos;
using Microsoft.AspNetCore.Components;

namespace Cliente.Web.Tests;

// ============================================================================================================================
// DOBLES Y FIXTURES DE PRUEBA. Todo lo de este archivo vive SOLO en las pruebas: la interfaz real nunca usa datos fijos ni simuladores.
// Los datos de paquetes son FIXTURES IDENTIFICADOS (paquetes «Ejemplo.*» inventados), no datos de NuGet.
// ============================================================================================================================

/// <summary>Navegador en memoria: almacenamiento local simulable (disponible, bloqueado o lleno), portapapeles y panel.</summary>
internal sealed class FakeNavegador : IServicioNavegador
{
    public bool Disponible { get; set; } = true;
    public bool EscrituraFalla { get; set; }
    public Dictionary<string, string> Datos { get; } = new();
    public bool CopiaFunciona { get; set; } = true;
    public List<string> Copiados { get; } = [];
    public string Direccion { get; set; } = "http://localhost/resultado/00000000-0000-4000-8000-000000000001";
    public int PanelesAbiertos { get; private set; }
    public int PanelesCerrados { get; private set; }
    public Func<Task>? AlCerrarRegistrado { get; private set; }
    public string? DisparadorRegistrado { get; private set; }

    public Task<bool> AlmacenamientoDisponibleAsync() => Task.FromResult(Disponible);

    public Task<string?> LeerAsync(string clave)
    {
        if (!Disponible) throw new InvalidOperationException("almacenamiento no disponible");
        return Task.FromResult(Datos.TryGetValue(clave, out var v) ? v : null);
    }

    public Task EscribirAsync(string clave, string valor)
    {
        if (!Disponible || EscrituraFalla) throw new InvalidOperationException("almacenamiento no disponible o lleno");
        Datos[clave] = valor;
        return Task.CompletedTask;
    }

    public Task<bool> CopiarAsync(string texto)
    {
        if (CopiaFunciona) Copiados.Add(texto);
        return Task.FromResult(CopiaFunciona);
    }

    public Task<string> DireccionActualAsync() => Task.FromResult(Direccion);

    public bool DescargaFunciona { get; set; } = true;
    public List<(string Nombre, byte[] Contenido, string Tipo)> Descargas { get; } = [];

    public Task<bool> DescargarArchivoAsync(string nombre, byte[] contenido, string tipo)
    {
        if (DescargaFunciona) Descargas.Add((nombre, contenido, tipo));
        return Task.FromResult(DescargaFunciona);
    }

    public Task EnfocarAsync(ElementReference elemento) => Task.CompletedTask;

    public Task AbrirPanelAsync(ElementReference panel, string? selectorDisparador, Func<Task> alCerrar)
    {
        PanelesAbiertos++;
        DisparadorRegistrado = selectorDisparador;
        AlCerrarRegistrado = alCerrar;
        return Task.CompletedTask;
    }

    public Task CerrarPanelAsync()
    {
        PanelesCerrados++;
        return Task.CompletedTask;
    }
}

/// <summary>Servidor HTTP controlado: cada prueba define las respuestas que quiere comprobar (incluidos los errores difíciles de reproducir).</summary>
internal sealed class ManejadorHttp(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<(string Metodo, string Url)> Solicitudes { get; } = [];
    public Exception? Lanzar { get; set; }
    public TaskCompletionSource? Bloqueo { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Solicitudes.Add((request.Method.Method, request.RequestUri!.PathAndQuery));
        if (Bloqueo is not null) await Bloqueo.Task.WaitAsync(ct);
        if (Lanzar is not null) throw Lanzar;
        return responder(request);
    }

    public HttpClient Cliente() => new(this) { BaseAddress = new Uri("http://localhost/") };
}

internal static class Respuestas
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static HttpResponseMessage Json<T>(T valor, HttpStatusCode estado = HttpStatusCode.OK) => new(estado)
    {
        Content = new StringContent(JsonSerializer.Serialize(valor, Web), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Problema(string codigo, HttpStatusCode estado, string titulo, string? detalle = null, string? referencia = "0HNREF1234") => new(estado)
    {
        Content = new StringContent(JsonSerializer.Serialize(new ProblemaDto(null, titulo, (int)estado, detalle, null, codigo, referencia), Web), Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>Respuesta de descarga del PDF (bytes identificados de prueba, no un PDF real: la interfaz solo los entrega al navegador).</summary>
    public static HttpResponseMessage Pdf(byte[] bytes, string nombre = "DiagNET_Mi_20261005.pdf")
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
        r.Content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = nombre };
        return r;
    }

    public static HttpResponseMessage Html(HttpStatusCode estado) => new(estado) { Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html") };

    public static ConfiguracionPublicaDto Configuracion { get; } = new(90, 50, 1024 * 1024, 100, 120);
}

/// <summary>Constructores de resultados de ejemplo (fixtures identificados).</summary>
internal static class Muestras
{
    public static readonly DateTimeOffset Fecha = new(2026, 10, 5, 15, 42, 0, TimeSpan.Zero);

    public static IndicadoresDto Indicadores(
        string? ultima = "2.0.0", long? descargas = 1000, bool deprecado = false, DeprecacionDto? deprecacion = null, bool seleccionCompleta = true,
        LicenciaDto? licencia = null, string? licenciaNota = null) =>
        new(
            new IndicadorDto<DateTimeOffset?>(Disponibilidad.Conocido, Fecha),
            ultima is null ? new IndicadorDto<string?>(Disponibilidad.NoDeterminado, null, "No se recorrió todo el registro.") : new IndicadorDto<string?>(Disponibilidad.Conocido, ultima),
            new IndicadorDto<string?>(Disponibilidad.Conocido, ultima),
            seleccionCompleta,
            new IndicadorDto<int?>(Disponibilidad.Conocido, 8),
            descargas is null ? new IndicadorDto<long?>(Disponibilidad.NoDeterminado, null, "NuGet no informó descargas.") : new IndicadorDto<long?>(Disponibilidad.Conocido, descargas),
            licencia is null && licenciaNota is not null
                ? new IndicadorDto<LicenciaDto?>(Disponibilidad.NoDeterminado, null, licenciaNota)
                : new IndicadorDto<LicenciaDto?>(Disponibilidad.Conocido, licencia ?? new LicenciaDto(TipoLicencia.Expresion, "MIT")),
            new IndicadorDto<bool?>(Disponibilidad.Conocido, deprecado),
            deprecacion,
            [new CompatibilidadTfmDto("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups", "Declara un grupo para net8.0.")],
            new IndicadorDto<bool?>(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));

    public static DiagnosticoDto Diagnostico(
        EstadoDiagnostico estado, string resumen = "Resumen del estado.", IReadOnlyList<HallazgoDto>? hallazgos = null, OrigenDato origen = OrigenDato.Nuget,
        DateTimeOffset? fecha = null, IndicadoresDto? indicadores = null, bool sinIndicadores = false) =>
        new(estado, resumen, hallazgos ?? [], [new EvaluacionTfmDto("net8.0", estado)], origen, origen == OrigenDato.NoDeterminado ? null : fecha ?? Fecha,
            sinIndicadores ? null : indicadores ?? Indicadores());

    public static HallazgoDto Hallazgo(string codigo, string mensaje, GravedadHallazgo gravedad = GravedadHallazgo.Aviso, bool faltante = false, params (string, string)[] evidencia) =>
        new("regla", codigo, gravedad, mensaje, evidencia.Select(e => new EvidenciaDto(e.Item1, e.Item2)).ToList(), faltante);

    public static RecomendacionDto Recomendacion(
        TipoRecomendacion tipo = TipoRecomendacion.ActualizarVersion, string paquete = "Ejemplo.Nueva", string? version = "2.0.0", bool cambios = true) =>
        new(tipo, paquete, version, "Justificación redactada por el servidor.", [new EvidenciaDto("version_comprobada", version ?? "(sin dato)")],
            ["La compatibilidad se infiere de los grupos de dependencias que publica NuGet; no se compiló ni se ejecutó nada."], cambios,
            tipo switch
            {
                TipoRecomendacion.ActualizarVersion => "Actualización del mismo paquete",
                TipoRecomendacion.Alternativa => "Alternativa sugerida por el autor",
                _ => "Candidato para revisión (relación funcional no demostrada)",
            },
            cambios ? "Puede requerir cambios de código." : "Cambios de código no determinados.");

    public static DependenciaAnalizadaDto Dependencia(
        string nombre, EstadoDiagnostico estado = EstadoDiagnostico.Actualizada, string? version = "1.0.0", TipoVersionDeclarada tipo = TipoVersionDeclarada.Exacta,
        DiagnosticoDto? diagnostico = null, IReadOnlyList<RecomendacionDto>? recomendaciones = null, IReadOnlyList<AdvertenciaDto>? advertencias = null) =>
        new(nombre, version, tipo, null, advertencias ?? [], diagnostico ?? Diagnostico(estado), recomendaciones ?? []);

    public static ResultadoAnalisisDto Resultado(
        IReadOnlyList<DependenciaAnalizadaDto> dependencias, EstadoAnalisis estado = EstadoAnalisis.Completo, string nombre = "Mi.csproj",
        IReadOnlyList<AdvertenciaDto>? advertencias = null, Guid? id = null, int retencion = 90) =>
        new(id ?? Guid.Parse("11111111-1111-4111-8111-111111111111"), nombre, ["net8.0"], ["net8.0"], Fecha, estado, ResumenEstadosDto.Calcular(dependencias),
            dependencias, advertencias ?? [], "e6-1.1|ant=24m|agr=pes+e7-1.0", retencion);

    public static ResumenAnalisisDto Resumen(Guid id, string nombre = "Mi.csproj", EstadoAnalisis estado = EstadoAnalisis.Completo, DateTimeOffset? fecha = null, int total = 3) =>
        new(id, nombre, ["net8.0"], fecha ?? Fecha, total, estado);
}
