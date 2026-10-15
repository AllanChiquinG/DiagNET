using System.Text.RegularExpressions;

namespace Servidor.Integracion.NuGet;

/// <summary>Valida cada dirección ANTES de enviarla (también en cada salto de redirección) y los identificadores antes de formar una URL.</summary>
public sealed partial class ValidadorDirecciones(OpcionesNuGet opciones)
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PatronIdentificador();

    /// <summary>HTTPS, sin credenciales en la URL, puerto estándar y host igual a un dominio permitido o subdominio con límite de punto.</summary>
    public bool EsPermitida(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        if (!uri.IsDefaultPort) return false;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (var dominio in opciones.DominiosPermitidos)
        {
            var d = dominio.Trim().TrimStart('.').ToLowerInvariant();
            if (d.Length == 0) continue;
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Letras, números, punto, guion y guion bajo; sin barras ni caracteres que alteren la dirección.</summary>
    public bool EsIdentificadorValido(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= opciones.MaxLongitudIdentificador && PatronIdentificador().IsMatch(id);
}
