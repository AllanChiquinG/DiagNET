using System.Net;
using Microsoft.Extensions.Logging;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

internal enum EstadoHttp { Ok, NoEncontrado, Fallo }

internal sealed record RespuestaHttp(EstadoHttp Estado, byte[]? Cuerpo, CausaFallo? Causa, string? Detalle, int Solicitudes)
{
    public bool EsOk => Estado == EstadoHttp.Ok;
}

/// <summary>
/// Única puerta de salida hacia NuGet. Aplica, en este orden: validación de dirección (HTTPS y nuget.org) → disyuntor → cupo de concurrencia →
/// tiempo máximo por intento → clasificación de la respuesta → reintentos con espera creciente (Retry-After incluido) → redirecciones manuales
/// revisadas salto a salto → lectura limitada de la respuesta. Todo respeta el CancellationToken y el presupuesto global de tiempo.
/// </summary>
internal sealed class EjecutorHttp
{
    private enum TipoIntento { Ok, NoEncontrado, Reintentable, Terminal }

    private sealed record Intento(TipoIntento Tipo, byte[]? Cuerpo, CausaFallo? Causa, string? Detalle, TimeSpan? EsperaSugerida, int Solicitudes);

    private readonly HttpClient _http;
    private readonly OpcionesNuGet _opciones;
    private readonly ValidadorDirecciones _validador;
    private readonly Disyuntor _disyuntor;
    private readonly TimeProvider _tiempo;
    private readonly ILogger _log;
    private readonly Func<double> _azar;
    private readonly SemaphoreSlim _cupo;

    public EjecutorHttp(HttpClient http, OpcionesNuGet opciones, ValidadorDirecciones validador, Disyuntor disyuntor,
        TimeProvider tiempo, ILogger log, Func<double>? azar = null)
    {
        _http = http;
        _opciones = opciones;
        _validador = validador;
        _disyuntor = disyuntor;
        _tiempo = tiempo;
        _log = log;
        _azar = azar ?? Random.Shared.NextDouble;
        _cupo = new SemaphoreSlim(Math.Max(1, opciones.MaxConcurrentes));
    }

    public Disyuntor Disyuntor => _disyuntor;

    public async Task<RespuestaHttp> ObtenerAsync(Uri uri, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        if (!_validador.EsPermitida(uri))
            return Fallo(CausaFallo.DireccionNoPermitida, "La dirección no es HTTPS de nuget.org y no se consulta.", 0);
        if (!_disyuntor.TryPasar())
            return Fallo(CausaFallo.CircuitoAbierto, "NuGet no responde (disyuntor abierto); se reintentará más tarde.", 0);

        var solicitudes = 0;
        var reintentos = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (presupuesto?.Agotado == true)
                {
                    _disyuntor.LiberarPrueba();
                    return Fallo(CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.", solicitudes);
                }

                var intento = await UnIntentoAsync(uri, presupuesto, ct);
                solicitudes += intento.Solicitudes;

                switch (intento.Tipo)
                {
                    case TipoIntento.Ok:
                        _disyuntor.RegistrarExito();
                        return new RespuestaHttp(EstadoHttp.Ok, intento.Cuerpo, null, null, solicitudes);

                    case TipoIntento.NoEncontrado:
                        _disyuntor.RegistrarExito(); // un 404 es una respuesta válida: NuGet está vivo
                        return new RespuestaHttp(EstadoHttp.NoEncontrado, null, null, "404", solicitudes);

                    case TipoIntento.Terminal:
                        _disyuntor.LiberarPrueba(); // no es una señal de salud de NuGet
                        return Fallo(intento.Causa!.Value, intento.Detalle ?? "", solicitudes);
                }

                // Reintentable
                var causa = intento.Causa!.Value;
                if (intento.EsperaSugerida is { } sugerida && sugerida > TimeSpan.FromSeconds(_opciones.MaxRetryAfterSeg))
                {
                    _disyuntor.RegistrarFallo();
                    return Fallo(CausaFallo.LimiteSolicitudes, $"NuGet pidió esperar {sugerida.TotalSeconds:0} s; se pasa al modo degradado.", solicitudes);
                }

                if (reintentos >= _opciones.MaxReintentos)
                {
                    _disyuntor.RegistrarFallo();
                    return Fallo(causa, intento.Detalle ?? "NuGet no respondió tras los reintentos.", solicitudes);
                }

                var espera = intento.EsperaSugerida ?? Espera(reintentos);
                if (presupuesto is not null && presupuesto.Restante <= espera)
                {
                    _disyuntor.RegistrarFallo();
                    return Fallo(CausaFallo.PresupuestoAgotado, "No queda tiempo en el análisis para reintentar.", solicitudes);
                }

                try
                {
                    using var union = CancellationTokenSource.CreateLinkedTokenSource(ct, presupuesto?.Token ?? CancellationToken.None);
                    await Task.Delay(espera, _tiempo, union.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _disyuntor.RegistrarFallo();
                    return Fallo(CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis durante una espera.", solicitudes);
                }

                reintentos++;
            }
        }
        catch (OperationCanceledException)
        {
            _disyuntor.LiberarPrueba();
            throw;
        }
    }

    private TimeSpan Espera(int numeroReintento)
    {
        var esperas = _opciones.EsperasReintentoSeg;
        var baseSeg = esperas.Length == 0 ? 1 : esperas[Math.Min(numeroReintento, esperas.Length - 1)];
        return TimeSpan.FromSeconds(baseSeg * (1 + _azar() * _opciones.VariacionEspera));
    }

    private async Task<Intento> UnIntentoAsync(Uri inicio, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        var tokenPresupuesto = presupuesto?.Token ?? CancellationToken.None;

        // El cupo de concurrencia se espera sin gastar el tiempo del intento, pero sí respetando cancelación y presupuesto.
        try
        {
            using var espera = CancellationTokenSource.CreateLinkedTokenSource(ct, tokenPresupuesto);
            await _cupo.WaitAsync(espera.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Intento(TipoIntento.Terminal, null, CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.", null, 0);
        }

        var solicitudes = 0;
        try
        {
            using var limite = new CancellationTokenSource(TimeSpan.FromSeconds(_opciones.TiempoEsperoSeg), _tiempo);
            using var union = CancellationTokenSource.CreateLinkedTokenSource(ct, tokenPresupuesto, limite.Token);
            var actual = inicio;
            var saltos = 0;

            try
            {
                while (true)
                {
                    if (!_validador.EsPermitida(actual))
                        return new Intento(TipoIntento.Terminal, null, CausaFallo.DireccionNoPermitida,
                            "Una redirección apunta fuera de HTTPS/nuget.org y se rechazó.", null, solicitudes);

                    using var peticion = new HttpRequestMessage(HttpMethod.Get, actual);
                    solicitudes++;
                    using var respuesta = await _http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, union.Token);
                    var codigo = (int)respuesta.StatusCode;

                    if (codigo is >= 200 and < 300)
                        return await LeerCuerpoAsync(respuesta, union.Token, solicitudes);

                    if (respuesta.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                    {
                        if (++saltos > _opciones.MaxRedirecciones || respuesta.Headers.Location is null)
                            return new Intento(TipoIntento.Terminal, null, CausaFallo.RespuestaNoEsperada, "Demasiadas redirecciones o redirección sin destino.", null, solicitudes);
                        actual = new Uri(actual, respuesta.Headers.Location);
                        continue;
                    }

                    if (respuesta.StatusCode == HttpStatusCode.NotFound)
                        return new Intento(TipoIntento.NoEncontrado, null, null, null, null, solicitudes);

                    if (codigo == 429)
                        return new Intento(TipoIntento.Reintentable, null, CausaFallo.LimiteSolicitudes, "NuGet respondió 429.", EsperaSolicitada(respuesta), solicitudes);
                    if (codigo == 408)
                        return new Intento(TipoIntento.Reintentable, null, CausaFallo.TiempoAgotado, "NuGet respondió 408.", null, solicitudes);
                    if (codigo >= 500)
                        return new Intento(TipoIntento.Reintentable, null, CausaFallo.ErrorServidor, $"NuGet respondió {codigo}.", EsperaSolicitada(respuesta), solicitudes);

                    return new Intento(TipoIntento.Terminal, null, CausaFallo.RespuestaNoEsperada, $"NuGet respondió {codigo}.", null, solicitudes);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cancelación pedida por el llamador: se propaga, sin reintentos
            }
            catch (OperationCanceledException) when (tokenPresupuesto.IsCancellationRequested)
            {
                return new Intento(TipoIntento.Terminal, null, CausaFallo.PresupuestoAgotado, "Se agotó el tiempo máximo del análisis.", null, solicitudes);
            }
            catch (OperationCanceledException)
            {
                return new Intento(TipoIntento.Reintentable, null, CausaFallo.TiempoAgotado,
                    $"NuGet no respondió en {_opciones.TiempoEsperoSeg} s.", null, solicitudes);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                _log.LogWarning(ex, "Fallo de red hacia NuGet ({Host})", inicio.Host);
                return new Intento(TipoIntento.Reintentable, null, CausaFallo.Red, "No se pudo conectar con NuGet.", null, solicitudes);
            }
        }
        finally
        {
            _cupo.Release();
        }
    }

    private async Task<Intento> LeerCuerpoAsync(HttpResponseMessage respuesta, CancellationToken ct, int solicitudes)
    {
        var maximo = _opciones.MaxRespuestaBytes;
        if (respuesta.Content.Headers.ContentLength > maximo)
            return Excesiva(maximo, solicitudes);

        await using var flujo = await respuesta.Content.ReadAsStreamAsync(ct);
        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int leidos;
        while ((leidos = await flujo.ReadAsync(buffer, ct)) > 0)
        {
            memoria.Write(buffer, 0, leidos);
            if (memoria.Length > maximo) return Excesiva(maximo, solicitudes); // se aborta la lectura (ya descomprimida)
        }

        return new Intento(TipoIntento.Ok, memoria.ToArray(), null, null, null, solicitudes);
    }

    private static Intento Excesiva(long maximo, int solicitudes) =>
        new(TipoIntento.Terminal, null, CausaFallo.RespuestaExcesiva, $"La respuesta supera {maximo / (1024 * 1024)} MB y se descartó.", null, solicitudes);

    private TimeSpan? EsperaSolicitada(HttpResponseMessage respuesta)
    {
        var retry = respuesta.Headers.RetryAfter;
        if (retry?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry?.Date is { } fecha)
        {
            var resto = fecha - _tiempo.GetUtcNow();
            return resto < TimeSpan.Zero ? TimeSpan.Zero : resto;
        }

        return null;
    }

    private static RespuestaHttp Fallo(CausaFallo causa, string detalle, int solicitudes) =>
        new(EstadoHttp.Fallo, null, causa, detalle, solicitudes);
}
