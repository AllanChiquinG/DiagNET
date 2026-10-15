using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NuGet.Versioning;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

/// <summary>
/// Único punto de acceso a NuGet (implementa <see cref="IClienteNuGet"/>). Coordina las piezas: ResolutorRecursos (direcciones), LectorRegistro (páginas),
/// SelectorVersion (elección), InferidorCompatibilidad (inferencia) y MapeadorMetadatos (campos), y traduce los fallos técnicos a resultados del dominio.
/// Solo viajan identificadores y versiones (nunca el contenido del .csproj). No descarga paquetes (.nupkg) ni el catálogo.
/// </summary>
public sealed class NuGetGateway : IClienteNuGet
{
    private const int MaxTextoBusqueda = 100;
    private const int MaxDescripcion = 300;

    private readonly OpcionesNuGet _opciones;
    private readonly ValidadorDirecciones _validador;
    private readonly EjecutorHttp _http;
    private readonly ResolutorRecursos _recursos;
    private readonly LectorRegistro _lector;
    private readonly InferidorCompatibilidad _inferidor = new();
    private readonly ILogger _log;
    private readonly CacheDocumentos _cache;

    public NuGetGateway(HttpClient http, OpcionesNuGet opciones, TimeProvider? tiempo = null, ILogger? log = null)
        : this(http, opciones, tiempo ?? TimeProvider.System, log ?? NullLogger.Instance, null)
    {
    }

    /// <summary>Constructor para pruebas: permite fijar la variación aleatoria de las esperas.</summary>
    internal NuGetGateway(HttpClient http, OpcionesNuGet opciones, TimeProvider tiempo, ILogger log, Func<double>? azar)
    {
        _opciones = opciones;
        _log = log;
        _validador = new ValidadorDirecciones(opciones);
        var disyuntor = new Disyuntor(opciones.UmbralDisyuntor, TimeSpan.FromSeconds(opciones.PausaDisyuntorSeg), tiempo);
        _http = new EjecutorHttp(http, opciones, _validador, disyuntor, tiempo, log, azar);
        _recursos = new ResolutorRecursos(_http, opciones, _validador, tiempo, log);
        _cache = new CacheDocumentos(TimeSpan.FromMinutes(opciones.CacheRegistroMinutos), opciones.MaxDocumentosEnCache, tiempo);
        _lector = new LectorRegistro(_http, opciones, new MapeadorMetadatos(opciones, _validador), _inferidor, _cache, tiempo, log);
    }

    internal Disyuntor Disyuntor => _http.Disyuntor;

    public async Task<ResultadoNuGet<InformacionPaquete>> ObtenerPaqueteAsync(
        ConsultaPaquete consulta, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        // LÍMITE EXTERNO (DEC-16): nuget.org limita el id a 100 caracteres al publicar (referencia oficial del .nuspec). Es distinto de los límites de
        // lectura y almacenamiento de DiagNET (200, aprobados en E2/E3): un id de 101 a 200 caracteres SE LEE y SE GUARDA como declaración del
        // proyecto, pero no puede existir en nuget.org, así que no se consulta.
        if (LimitesDatos.Excede(consulta.Paquete, _opciones.MaxLongitudIdentificador))
            return ResultadoNuGet<InformacionPaquete>.NoEncontrado(
                $"El identificador tiene {LimitesDatos.Longitud(consulta.Paquete)} caracteres y nuget.org admite como máximo {_opciones.MaxLongitudIdentificador}; no puede existir allí (puede ser de otra fuente).",
                [new Advertencia("identificador_excede_limite_nuget",
                    $"«{LimitesDatos.Extracto(consulta.Paquete)}» supera el máximo de {_opciones.MaxLongitudIdentificador} caracteres de nuget.org; no se consultó.", consulta.Paquete)]);

        if (!_validador.EsIdentificadorValido(consulta.Paquete))
            return ResultadoNuGet<InformacionPaquete>.NoEncontrado(
                "El identificador no tiene un formato válido de NuGet (letras, números, punto, guion y guion bajo); no existe en nuget.org.",
                [new Advertencia("identificador_no_valido", $"«{LimitesDatos.Extracto(consulta.Paquete)}» no es un identificador válido de NuGet; no se consultó.", consulta.Paquete)]);

        var avisos = new List<Advertencia>();
        NuGetVersion? declarada = null;
        var noInterpretable = false;
        if (!string.IsNullOrWhiteSpace(consulta.VersionDeclarada))
        {
            if (NuGetVersion.TryParse(consulta.VersionDeclarada.Trim(), out declarada)) { }
            else
            {
                noInterpretable = true;
                avisos.Add(new("version_declarada_no_interpretable",
                    $"La versión declarada de {consulta.Paquete} no se pudo interpretar; solo se consultó la última.", consulta.Paquete));
            }
        }

        try
        {
            var recursos = await _recursos.ObtenerAsync(presupuesto, ct);
            var solicitudes = recursos.Solicitudes;
            avisos.AddRange(recursos.Advertencias);
            if (!recursos.EsCorrecto)
                return ResultadoNuGet<InformacionPaquete>.NoDisponible(recursos.Causa ?? CausaFallo.IndiceNoDisponible, recursos.Mensaje ?? "Índice de NuGet no disponible.", avisos, solicitudes);

            // DEC-05 (confirmada): las descargas se piden para cada paquete en paralelo con el registro, con caché y dentro del presupuesto global.
            // Si faltan, quedan en null con una explicación (nunca 0) y NO hacen fallar la consulta del paquete.
            var tareaDescargas = _opciones.ConsultarDescargas ? DescargasSeguraAsync(consulta.Paquete, presupuesto, ct) : null;

            var lectura = await _lector.LeerAsync(
                recursos.Indice!.Registro, consulta.Paquete, declarada, noInterpretable, consulta.FrameworksEvaluados, presupuesto, ct);
            solicitudes += lectura.Solicitudes;
            avisos.AddRange(lectura.Advertencias);

            var descargas = tareaDescargas is null ? null : await tareaDescargas;
            if (descargas is not null)
            {
                solicitudes += descargas.Solicitudes;
                if (lectura.Estado == EstadoLectura.Correcto && descargas.Valor is null)
                    avisos.Add(new("descargas_no_disponibles", descargas.Nota!, consulta.Paquete));
            }

            return lectura.Estado switch
            {
                EstadoLectura.Correcto => ResultadoNuGet<InformacionPaquete>.Correcto(
                    descargas is null ? lectura.Info! : lectura.Info! with { Descargas = descargas.Valor, DescargasNota = descargas.Nota }, avisos, solicitudes),
                EstadoLectura.NoEncontrado => ResultadoNuGet<InformacionPaquete>.NoEncontrado(lectura.Mensaje!, avisos, solicitudes),
                _ => ResultadoNuGet<InformacionPaquete>.NoDisponible(lectura.Causa ?? CausaFallo.RespuestaNoEsperada, lectura.Mensaje ?? "No se pudo leer el registro.", avisos, solicitudes),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && presupuesto?.Agotado == true)
        {
            return ResultadoNuGet<InformacionPaquete>.NoDisponible(CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.", avisos);
        }
    }

    private sealed record Descargas(long? Valor, string? Nota, int Solicitudes);

    private sealed record ListaBusqueda(IReadOnlyList<ResultadoBusqueda> Resultados);

    private async Task<Descargas> DescargasSeguraAsync(string paquete, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        try
        {
            var r = await ObtenerDescargasAsync(paquete, presupuesto, ct);
            return r.Estado switch
            {
                EstadoConsulta.Correcto when r.Valor is not null => new Descargas(r.Valor, null, r.SolicitudesHttp),
                EstadoConsulta.Correcto => new Descargas(null,
                    r.Advertencias.FirstOrDefault()?.Mensaje ?? "NuGet no informó el total de descargas; queda sin determinar (no se rellena con 0).", r.SolicitudesHttp),
                _ => new Descargas(null, $"No se pudieron consultar las descargas ({r.Causa?.ToString() ?? r.Estado.ToString()}): {r.Mensaje}", r.SolicitudesHttp),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Descargas(null, "No se consultaron las descargas: se agotó el tiempo máximo del análisis.", 0);
        }
    }

    public async Task<ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> BuscarCandidatosAsync(
        string texto, string? excluirPaquete, int maximo, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        var consulta = (texto ?? "").Trim();
        if (consulta.Length == 0 || LimitesDatos.Excede(consulta, MaxTextoBusqueda) || consulta.Any(char.IsControl))
            return ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto([],
                [new Advertencia("consulta_no_valida", "El texto de búsqueda está vacío, es demasiado largo o contiene caracteres de control; no se buscó.")]);
        if (maximo < 1) return ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto([]);

        var tomar = Math.Min(maximo + 5, 50); // margen para descartar el propio paquete
        var resultado = await BuscarAsync(consulta, tomar, false, presupuesto, ct);
        if (!resultado.EsCorrecto)
            return ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.NoDisponible(resultado.Causa!.Value, resultado.Mensaje!, resultado.Advertencias, resultado.SolicitudesHttp);

        var avisos = resultado.Advertencias.ToList();
        var candidatos = new List<CandidatoBusqueda>();
        foreach (var r in resultado.Valor!)
        {
            if (!_validador.EsIdentificadorValido(r.Id)) continue; // un identificador inesperado nunca se usa
            if (excluirPaquete is not null && string.Equals(r.Id, excluirPaquete, StringComparison.OrdinalIgnoreCase)) continue;
            var version = r.Version is not null && NuGetVersion.TryParse(r.Version, out var v) ? v.ToNormalizedString() : null;
            var descripcion = r.Descripcion is null ? null : LimitesDatos.Excede(r.Descripcion, MaxDescripcion) ? LimitesDatos.Recortar(r.Descripcion, MaxDescripcion) + "…" : r.Descripcion;
            candidatos.Add(new CandidatoBusqueda(r.Id!, version, r.Descargas, r.Verificado, descripcion));
        }

        // Prioriza verificados y más descargados. Esto ordena candidatos; NO demuestra que sean alternativas funcionales.
        var ordenados = candidatos
            .OrderByDescending(c => c.Verificado == true)
            .ThenByDescending(c => c.Descargas ?? -1)
            .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .Take(maximo)
            .ToList();
        return ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto(ordenados, avisos, resultado.SolicitudesHttp);
    }

    public async Task<ResultadoNuGet<long?>> ObtenerDescargasAsync(string paquete, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        if (!_validador.EsIdentificadorValido(paquete))
            return ResultadoNuGet<long?>.NoEncontrado("El identificador no tiene un formato válido de NuGet.");

        var resultado = await BuscarAsync("packageid:" + paquete.ToLowerInvariant(), 5, true, presupuesto, ct);
        if (!resultado.EsCorrecto)
            return ResultadoNuGet<long?>.NoDisponible(resultado.Causa!.Value, resultado.Mensaje!, resultado.Advertencias, resultado.SolicitudesHttp);

        var exacto = resultado.Valor!.FirstOrDefault(r => string.Equals(r.Id, paquete, StringComparison.OrdinalIgnoreCase));
        if (exacto is null)
            return ResultadoNuGet<long?>.Correcto(null,
                [new Advertencia("descargas_no_informadas", $"La búsqueda de NuGet no devolvió {paquete}; las descargas quedan sin determinar.", paquete)], resultado.SolicitudesHttp);

        // null = NuGet no lo informó; nunca se rellena con 0.
        return ResultadoNuGet<long?>.Correcto(exacto.Descargas, resultado.Advertencias, resultado.SolicitudesHttp);
    }

    private async Task<ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>> BuscarAsync(
        string consulta, int tomar, bool incluirPreliminares, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        try
        {
            var recursos = await _recursos.ObtenerAsync(presupuesto, ct);
            if (!recursos.EsCorrecto)
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.NoDisponible(
                    recursos.Causa ?? CausaFallo.IndiceNoDisponible, recursos.Mensaje ?? "Índice de NuGet no disponible.", recursos.Advertencias, recursos.Solicitudes);
            if (recursos.Indice!.Busqueda is null)
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.NoDisponible(
                    CausaFallo.IndiceNoDisponible, "El índice de NuGet no ofrece un servicio de búsqueda válido.", recursos.Advertencias, recursos.Solicitudes);

            var url = new Uri(string.Create(CultureInfo.InvariantCulture,
                $"{recursos.Indice.Busqueda}?q={Uri.EscapeDataString(consulta)}&take={tomar}&prerelease={(incluirPreliminares ? "true" : "false")}&semVerLevel=2.0.0"));
            if (_cache.TryGet<ListaBusqueda>(url, out var enCache))
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.Correcto(enCache.Resultados, recursos.Advertencias, recursos.Solicitudes);

            var respuesta = await _http.ObtenerAsync(url, presupuesto, ct);
            var solicitudes = recursos.Solicitudes + respuesta.Solicitudes;

            if (respuesta.Estado == EstadoHttp.NoEncontrado)
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.Correcto([], recursos.Advertencias, solicitudes);
            if (respuesta.Estado == EstadoHttp.Fallo)
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.NoDisponible(respuesta.Causa!.Value, respuesta.Detalle ?? "La búsqueda falló.", recursos.Advertencias, solicitudes);

            try
            {
                var resultados = ParserRegistro.ParseBusqueda(respuesta.Cuerpo!);
                _cache.Set(url, new ListaBusqueda(resultados));
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.Correcto(resultados, recursos.Advertencias, solicitudes);
            }
            catch (RespuestaInvalidaException ex)
            {
                var referencia = Guid.NewGuid().ToString("N")[..8];
                _log.LogError(ex, "Respuesta de búsqueda inesperada de NuGet. Referencia {Referencia}", referencia);
                return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.NoDisponible(
                    CausaFallo.RespuestaInvalida, $"La búsqueda de NuGet devolvió un formato inesperado (referencia {referencia}).", recursos.Advertencias, solicitudes);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && presupuesto?.Agotado == true)
        {
            return ResultadoNuGet<IReadOnlyList<ResultadoBusqueda>>.NoDisponible(CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.");
        }
    }
}
