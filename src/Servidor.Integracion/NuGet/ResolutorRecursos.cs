using Microsoft.Extensions.Logging;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

internal sealed record IndiceServicios(DateTimeOffset ObtenidoEn, Uri Registro, Uri? Busqueda);

internal sealed record ResultadoRecursos(
    IndiceServicios? Indice, CausaFallo? Causa, string? Mensaje, IReadOnlyList<Advertencia> Advertencias, int Solicitudes)
{
    public bool EsCorrecto => Indice is not null;
}

/// <summary>
/// Descubre las direcciones de los recursos desde el índice de servicios (la única URL fija) y las conserva en memoria 24 horas.
/// Cada dirección se valida (HTTPS y nuget.org). Si el índice no se puede leer, se usa el último conocido aunque haya vencido; si no hay ninguno,
/// el resultado es «no disponible». Prefiere el registro con soporte SemVer 2.0.
/// </summary>
internal sealed class ResolutorRecursos(EjecutorHttp http, OpcionesNuGet opciones, ValidadorDirecciones validador, TimeProvider tiempo, ILogger log)
{
    private static readonly string[] TiposRegistro =
        ["RegistrationsBaseUrl/3.6.0", "RegistrationsBaseUrl/3.4.0", "RegistrationsBaseUrl/Versioned", "RegistrationsBaseUrl"];

    private static readonly string[] TiposBusqueda = ["SearchQueryService/3.5.0", "SearchQueryService/3.0.0-rc", "SearchQueryService"];

    private readonly SemaphoreSlim _candado = new(1, 1);
    private IndiceServicios? _indice;

    private bool Vigente(IndiceServicios i) => tiempo.GetUtcNow() - i.ObtenidoEn < TimeSpan.FromHours(opciones.VigenciaIndiceHoras);

    public async Task<ResultadoRecursos> ObtenerAsync(PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        if (_indice is { } actual && Vigente(actual)) return new(actual, null, null, [], 0);

        // Una sola lectura del índice aunque lleguen muchas consultas a la vez.
        await _candado.WaitAsync(ct);
        try
        {
            if (_indice is { } fresco && Vigente(fresco)) return new(fresco, null, null, [], 0);

            var respuesta = await http.ObtenerAsync(new Uri(opciones.UrlIndice), presupuesto, ct);
            if (respuesta.EsOk)
            {
                try
                {
                    var nuevo = Interpretar(respuesta.Cuerpo!, out var causa, out var mensaje);
                    if (nuevo is not null)
                    {
                        _indice = nuevo;
                        return new(nuevo, null, null, [], respuesta.Solicitudes);
                    }

                    return Degradar(causa!.Value, mensaje!, respuesta.Solicitudes);
                }
                catch (RespuestaInvalidaException ex)
                {
                    log.LogWarning(ex, "El índice de servicios de NuGet no se pudo interpretar");
                    return Degradar(CausaFallo.RespuestaInvalida, "El índice de servicios de NuGet tiene un formato inesperado.", respuesta.Solicitudes);
                }
            }

            return Degradar(respuesta.Causa ?? CausaFallo.IndiceNoDisponible, respuesta.Detalle ?? "No se pudo leer el índice de servicios de NuGet.", respuesta.Solicitudes);
        }
        finally
        {
            _candado.Release();
        }
    }

    private ResultadoRecursos Degradar(CausaFallo causa, string mensaje, int solicitudes)
    {
        if (_indice is { } antiguo)
            return new(antiguo, null, null,
                [new Advertencia("indice_servicios_antiguo", "No se pudo renovar el índice de servicios de NuGet; se usa el último conocido.")], solicitudes);
        var conservar = causa is CausaFallo.ProtocoloNoSoportado or CausaFallo.PresupuestoAgotado or CausaFallo.CircuitoAbierto or CausaFallo.DireccionNoPermitida;
        return new(null, conservar ? causa : CausaFallo.IndiceNoDisponible, mensaje, [], solicitudes);
    }

    private IndiceServicios? Interpretar(byte[] cuerpo, out CausaFallo? causa, out string? mensaje)
    {
        var servicios = ParserRegistro.ParseServicios(cuerpo, out var version);
        causa = null;
        mensaje = null;

        if (!int.TryParse(version?.Split('.')[0], out var mayor) || mayor != 3)
        {
            causa = CausaFallo.ProtocoloNoSoportado;
            mensaje = $"El índice de NuGet declara la versión «{version ?? "desconocida"}»; solo se admite la versión mayor 3.";
            return null;
        }

        var registro = Elegir(servicios, TiposRegistro);
        if (registro is null)
        {
            causa = CausaFallo.DireccionNoPermitida;
            mensaje = "El índice no ofrece un recurso de registro HTTPS de nuget.org.";
            return null;
        }

        var registroBase = new Uri(registro.EndsWith('/') ? registro : registro + "/");
        var busqueda = Elegir(servicios, TiposBusqueda);
        return new IndiceServicios(tiempo.GetUtcNow(), registroBase, busqueda is null ? null : new Uri(busqueda));
    }

    /// <summary>Primera dirección, en orden de preferencia de tipo, que sea válida (HTTPS de nuget.org). Las demás se descartan y se registran.</summary>
    private string? Elegir(IReadOnlyList<(string Tipo, string Url)> servicios, string[] preferencia)
    {
        foreach (var tipo in preferencia)
            foreach (var (t, url) in servicios.Where(s => string.Equals(s.Tipo, tipo, StringComparison.OrdinalIgnoreCase)))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && validador.EsPermitida(uri)) return url;
                log.LogWarning("Se rechazó una dirección del índice de NuGet ({Tipo}) por no ser HTTPS de un dominio permitido", t);
            }

        return null;
    }
}
