using System.Globalization;
using System.Text;

namespace Servidor.Negocio.Dominio;

/// <summary>
/// Longitudes máximas de los campos de texto con límite en la base de datos (varchar). Son la única fuente: Datos las usa en el esquema
/// y las capas de entrada las usan para rechazar valores con un error claro ANTES de consultar NuGet o intentar persistir.
/// Una prueba comprueba que coinciden con las columnas reales de PostgreSQL.
/// </summary>
public static class LimitesDatos
{
    public const int NombreArchivo = 260;
    public const int FrameworkObjetivo = 100;
    public const int NombrePaquete = 200;
    public const int Version = 100;
    public const int RangoAlterno = 100;
    public const int VersionReglas = 30;

    /// <summary>Longitud tal como la cuenta PostgreSQL en varchar(n): caracteres Unicode (un emoji cuenta 1, no 2).</summary>
    public static int Longitud(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return 0;
        var cantidad = 0;
        foreach (var _ in texto.EnumerateRunes()) cantidad++;
        return cantidad;
    }

    public static bool Excede(string? texto, int maximo) => Longitud(texto) > maximo;

    /// <summary>Recorta a un máximo de caracteres Unicode sin partir un par sustituto.</summary>
    public static string Recortar(string texto, int maximo)
    {
        if (!Excede(texto, maximo)) return texto;
        var sb = new StringBuilder();
        var n = 0;
        foreach (var rune in texto.EnumerateRunes())
        {
            if (n++ == maximo) break;
            sb.Append(rune.ToString());
        }

        return sb.ToString();
    }

    /// <summary>Vista breve de un valor largo para mensajes de error (no se vuelca el valor completo).</summary>
    public static string Extracto(string texto, int maximo = 40) =>
        Excede(texto, maximo) ? string.Create(CultureInfo.InvariantCulture, $"{Recortar(texto, maximo)}…") : texto;
}

/// <summary>Un valor supera el máximo de su campo en la base de datos. Se lanza antes de abrir una transacción.</summary>
public sealed class ValorExcedeLimiteException(string campo, int maximo, int longitud)
    : Exception($"El campo «{campo}» tiene {longitud} caracteres; el máximo permitido es {maximo}.")
{
    public string Campo { get; } = campo;
    public int Maximo { get; } = maximo;
    public int Longitud { get; } = longitud;
}

/// <summary>Comprueba los límites de longitud de lo que se va a persistir, con mensajes que nombran el campo.</summary>
public static class ValidadorLimites
{
    public static void Exigir(string? valor, string campo, int maximo)
    {
        var longitud = LimitesDatos.Longitud(valor);
        if (longitud > maximo) throw new ValorExcedeLimiteException(campo, maximo, longitud);
    }

    public static void Validar(Analisis a)
    {
        Exigir(a.NombreArchivo, "analisis.nombre_archivo", LimitesDatos.NombreArchivo);
        Exigir(a.VersionReglas, "analisis.version_reglas", LimitesDatos.VersionReglas);
        foreach (var d in a.Dependencias)
        {
            Exigir(d.Nombre, "dependencia.nombre_paquete", LimitesDatos.NombrePaquete);
            Exigir(d.VersionDeclarada, "dependencia.version_declarada", LimitesDatos.Version);
            Exigir(d.Diagnostico.UltimaVersion, "diagnostico.ultima_version", LimitesDatos.Version);
            foreach (var r in d.Recomendaciones)
            {
                Exigir(r.Paquete, "recomendacion.paquete_alternativo", LimitesDatos.NombrePaquete);
                Exigir(r.VersionSugerida, "recomendacion.version_sugerida", LimitesDatos.Version);
            }
        }
    }

    public static void Validar(MetadatoLibreria m)
    {
        Exigir(m.NombrePaquete, "metadato_libreria.nombre_paquete", LimitesDatos.NombrePaquete);
        Exigir(m.Version, "metadato_libreria.version", LimitesDatos.Version);
        // licencia es text (sin límite en la base; NuGet limita licenseUrl a 4000 al publicar y el lector descarta lo anormal).
        Exigir(m.PaqueteAlterno, "metadato_libreria.paquete_alterno", LimitesDatos.NombrePaquete);
        Exigir(m.RangoAlterno, "metadato_libreria.rango_alterno", LimitesDatos.RangoAlterno);
    }

    public static void Validar(SeleccionPaquete s)
    {
        Exigir(s.NombrePaquete, "seleccion_paquete.nombre_paquete", LimitesDatos.NombrePaquete);
        Exigir(s.UltimaVersionEstable, "seleccion_paquete.ultima_version_estable", LimitesDatos.Version);
    }
}
