using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Servidor.Api.Configuracion;

/// <summary>
/// Seguridad de transporte y proxy (E11). Todo configurable desde variables de entorno (<c>Seguridad__ExigirHttps=true</c>, …); nada de esto lleva secretos.
/// </summary>
public sealed class OpcionesSeguridad
{
    /// <summary>
    /// Redirige HTTP a HTTPS y envía HSTS. <c>null</c> (por defecto) = sí fuera de Desarrollo. Para ejecutar la versión publicada SIN proxy (demo local en http://localhost)
    /// se fija en <c>false</c> de forma explícita; detrás de un proxy que termina TLS se deja en <c>true</c> junto con <see cref="ReenviarCabeceras"/>.
    /// </summary>
    public bool? ExigirHttps { get; set; }

    /// <summary>Confía en <c>X-Forwarded-For/Proto/Host</c> SOLO de las redes de <see cref="RedesConfiables"/> (y del propio equipo). Sin esto, tras un proxy el esquema sería siempre «http».</summary>
    public bool ReenviarCabeceras { get; set; }

    /// <summary>Redes (CIDR, p. ej. <c>172.16.0.0/12</c>) cuyo proxy puede indicar el esquema y la dirección del cliente. Vacío = solo loopback. Nunca «0.0.0.0/0».</summary>
    public string[] RedesConfiables { get; set; } = [];

    public bool ExigirHttpsEfectivo(bool esDesarrollo) => ExigirHttps ?? !esDesarrollo;

    /// <summary>Opciones del middleware de cabeceras reenviadas; rechaza redes que abrirían la confianza a todo Internet.</summary>
    public ForwardedHeadersOptions CrearCabecerasReenviadas()
    {
        var o = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            ForwardLimit = 1,
        };
        foreach (var red in RedesConfiables)
        {
            var ipRed = System.Net.IPNetwork.Parse(red.Trim());
            if (ipRed.PrefixLength == 0)
                throw new InvalidOperationException("Seguridad:RedesConfiables no puede incluir una red /0 (confiaría en cualquier origen).");
            o.KnownIPNetworks.Add(ipRed);
        }

        return o;
    }
}
