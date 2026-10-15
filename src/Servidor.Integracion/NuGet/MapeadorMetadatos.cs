using System.Globalization;
using NuGet.Versioning;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

/// <summary>
/// Convierte una hoja del registro en <see cref="VersionPublicada"/>. Tratamiento de lo que NuGet entrega:
/// una versión no interpretable se descarta con advertencia (no se inventa); la fecha 1900 de las no listadas pasa a nula; textos largos
/// (mensaje de deprecación, razones) se acotan CON advertencia; un paquete alterno con identificador inválido se ignora con advertencia.
/// Los textos de NuGet se tratan como datos (Blazor los escapa al mostrarlos).
/// </summary>
internal sealed class MapeadorMetadatos(OpcionesNuGet opciones, ValidadorDirecciones validador)
{
    private const int MaxRazones = 10;
    private const int MaxLongitudRazon = 100;
    private const int MaxLongitudRango = 100;
    private const int MaxLongitudGrupo = 200;
    private const int MaxLongitudVersion = 256;

    public HojaVersion? Mapear(HojaRegistro hoja, string paquete, List<Advertencia> avisos)
    {
        var original = hoja.Version?.Trim();
        if (string.IsNullOrEmpty(original) || original.Length > MaxLongitudVersion || !NuGetVersion.TryParse(original, out var version))
        {
            avisos.Add(new("version_no_interpretable",
                $"Se omitió una versión de {paquete} que no se pudo interpretar ({LimitesDatos.Extracto(original ?? "vacía")}).", paquete));
            return null;
        }

        var (publicada, esCentinela) = Fecha(hoja.Publicada);
        // «listed» manda; si falta, NuGet marca las no listadas con la fecha 1900 (una fecha ilegible no hace que sea no listada).
        var listada = hoja.Listada ?? !esCentinela;

        var (licencia, tipo) = Licencia(hoja);
        if (licencia is not null && LimitesDatos.Excede(licencia, opciones.MaxLongitudLicencia))
        {
            avisos.Add(new("licencia_descartada",
                $"La licencia de {paquete} {version.ToNormalizedString()} tiene {LimitesDatos.Longitud(licencia)} caracteres (NuGet limita licenseUrl a {opciones.MaxLongitudLicencia} al publicar); " +
                "se descartó y queda sin determinar.", paquete));
            (licencia, tipo) = (null, null);
        }

        var deprecacion = hoja.Deprecacion is null ? null : Deprecacion(hoja.Deprecacion, paquete, version.ToNormalizedString(), avisos);

        var grupos = new List<string>();
        foreach (var g in hoja.Grupos)
        {
            if (g.Length > MaxLongitudGrupo)
                avisos.Add(new("grupo_framework_descartado", $"Un grupo de dependencias de {paquete} tiene un nombre demasiado largo y se descartó.", paquete));
            else
                grupos.Add(g);
        }

        var vulns = hoja.Vulnerabilidades.Select(v => new AvisoVulnerabilidad(Acotar(v.Severidad, 30), Acotar(v.Url, 500))).ToList();

        var datos = new VersionPublicada(
            version.ToNormalizedString(), original, version.IsPrerelease, listada, publicada, licencia, tipo, deprecacion, grupos, vulns);
        return new HojaVersion(version, datos);
    }

    /// <summary>La fecha 1900 que NuGet usa para los paquetes no listados (o una fecha ilegible) se guarda como nula.</summary>
    private static (DateTimeOffset? Fecha, bool EsCentinela) Fecha(string? texto)
    {
        if (!DateTimeOffset.TryParse(texto, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var fecha)) return (null, false);
        return fecha.Year <= 1900 ? (null, true) : (fecha, false);
    }

    private static (string? Licencia, TipoLicencia? Tipo) Licencia(HojaRegistro hoja)
    {
        if (!string.IsNullOrWhiteSpace(hoja.LicenciaExpresion)) return (hoja.LicenciaExpresion.Trim(), TipoLicencia.Expresion);
        if (!string.IsNullOrWhiteSpace(hoja.LicenciaUrl)) return (hoja.LicenciaUrl.Trim(), TipoLicencia.Url); // una URL no equivale a una expresión SPDX
        return (null, null);
    }

    private Deprecacion Deprecacion(DeprecacionRegistro d, string paquete, string version, List<Advertencia> avisos)
    {
        var razones = d.Razones.Take(MaxRazones).Select(r => Acotar(r, MaxLongitudRazon)!).ToList();

        var mensaje = d.Mensaje;
        if (mensaje is not null && LimitesDatos.Excede(mensaje, opciones.MaxTextoMensaje))
        {
            avisos.Add(new("texto_externo_acotado",
                $"El mensaje de deprecación de {paquete} {version} tiene {LimitesDatos.Longitud(mensaje)} caracteres; se acotó a {opciones.MaxTextoMensaje}.", paquete));
            mensaje = LimitesDatos.Recortar(mensaje, opciones.MaxTextoMensaje) + "…";
        }

        var alterno = d.PaqueteAlterno;
        if (alterno is not null && !validador.EsIdentificadorValido(alterno))
        {
            avisos.Add(new("paquete_alterno_invalido",
                $"NuGet indica un paquete alterno para {paquete} con un identificador no válido ({LimitesDatos.Extracto(alterno)}); se ignoró.", paquete));
            alterno = null;
        }

        var rango = d.RangoAlterno;
        if (rango is not null && LimitesDatos.Excede(rango, MaxLongitudRango))
        {
            avisos.Add(new("rango_alterno_descartado", $"El rango de versiones del paquete alterno de {paquete} es demasiado largo y se descartó.", paquete));
            rango = null;
        }

        return new Deprecacion(razones, mensaje, alterno, alterno is null ? null : rango);
    }

    private static string? Acotar(string? texto, int maximo) =>
        texto is null ? null : LimitesDatos.Excede(texto, maximo) ? LimitesDatos.Recortar(texto, maximo) : texto;
}
