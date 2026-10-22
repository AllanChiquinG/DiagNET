using System.Globalization;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

namespace Servidor.Negocio.Reportes;

/// <summary>
/// Textos y formatos del reporte. Reproducen los de la interfaz (una prueba compara ambos para que no diverjan). Las etiquetas de recomendación y los textos de
/// «cambios de código» NO están aquí: se toman de <see cref="Recomendaciones.TextosRecomendacion"/> (E7), igual que la API.
/// Las fechas del reporte van en UTC y lo dicen: un archivo no conoce la zona horaria de quien lo lea.
/// </summary>
public static class TextosReporte
{
    private static readonly string[] Meses = ["ene", "feb", "mar", "abr", "may", "jun", "jul", "ago", "sep", "oct", "nov", "dic"];

    public static string Texto(EstadoDiagnostico estado) => estado switch
    {
        EstadoDiagnostico.Actualizada => "Actualizada",
        EstadoDiagnostico.Desactualizada => "Desactualizada",
        EstadoDiagnostico.Incompatible => "Incompatible",
        EstadoDiagnostico.SinSoporteActivo => "Sin soporte activo",
        _ => "No determinada",
    };

    public static int Orden(EstadoDiagnostico estado) => estado switch
    {
        EstadoDiagnostico.Incompatible => 0,
        EstadoDiagnostico.SinSoporteActivo => 1,
        EstadoDiagnostico.Desactualizada => 2,
        EstadoDiagnostico.NoDeterminada => 3,
        _ => 4,
    };

    public static string Texto(EstadoAnalisis estado) => estado == EstadoAnalisis.Parcial ? "Parcial" : "Completo";

    public static string Texto(TipoVersion tipo) => tipo switch
    {
        TipoVersion.Exacta => "exacta",
        TipoVersion.Rango => "rango",
        TipoVersion.Flotante => "flotante",
        TipoVersion.Propiedad => "definida por una variable",
        TipoVersion.Ausente => "sin versión",
        _ => "no reconocida",
    };

    public static string Texto(GravedadHallazgo gravedad) => gravedad switch
    {
        GravedadHallazgo.Grave => "Grave",
        GravedadHallazgo.Aviso => "Aviso",
        _ => "Informativo",
    };

    public static string Texto(OrigenDato origen) => origen switch
    {
        OrigenDato.Nuget => "NuGet (consulta actual)",
        OrigenDato.CacheVigente => "Copia guardada vigente",
        OrigenDato.CacheExpirada => "Copia guardada vencida",
        _ => "Sin datos",
    };

    public static string Texto(ResultadoCompatibilidad r) => r switch
    {
        ResultadoCompatibilidad.Compatible => "compatible (inferida)",
        ResultadoCompatibilidad.Incompatible => "incompatible",
        _ => "no determinada",
    };

    public static string Texto(TipoLicencia tipo) => tipo switch
    {
        TipoLicencia.Expresion => "expresión SPDX",
        TipoLicencia.Url => "dirección web",
        _ => "archivo",
    };

    /// <summary>«5 oct 2026, 15:42 UTC».</summary>
    public static string Fecha(DateTimeOffset fecha)
    {
        var utc = fecha.ToUniversalTime();
        return string.Create(CultureInfo.InvariantCulture, $"{utc.Day} {Meses[utc.Month - 1]} {utc.Year}, {utc.Hour:00}:{utc.Minute:00} UTC");
    }

    /// <summary>«1 234 567»: separador de miles con espacio.</summary>
    public static string Entero(long valor) => valor.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    public static string OrigenYFecha(Diagnostico d) =>
        d.Origen == OrigenDato.NoDeterminado || d.FechaConsulta is null ? "Sin datos de NuGet" : $"{Texto(d.Origen)} · {Fecha(d.FechaConsulta.Value)}";

    /// <summary>Un indicador nunca confunde «sin dato» con falso o cero: conocido ⇒ su valor; no determinado / no evaluado ⇒ así se dice.</summary>
    public static (string Texto, bool Conocido) Indicador<T>(Indicador<T> indicador, Func<T, string> formato, string ausenteConocido = "Ninguna")
    {
        switch (indicador.Disponibilidad)
        {
            case Disponibilidad.Conocido:
                return indicador.Valor is null ? (ausenteConocido, true) : (formato(indicador.Valor), true);
            case Disponibilidad.NoEvaluado:
                return ("No evaluado", false);
            default:
                return ("No determinado", false);
        }
    }

    /// <summary>Nombre de archivo seguro para la descarga: solo ASCII, sin rutas ni caracteres reservados, acotado.</summary>
    public static string NombreArchivoPdf(string nombreAnalisis, DateTimeOffset fecha)
    {
        var sinExtension = Path.GetFileNameWithoutExtension(nombreAnalisis.Replace('\\', '/').Split('/')[^1]);
        var normalizado = sinExtension.Normalize(System.Text.NormalizationForm.FormD);
        var limpio = new System.Text.StringBuilder();
        foreach (var c in normalizado)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            limpio.Append(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_' ? c : '_');
        }

        var baseNombre = limpio.ToString().Trim('_', '.');
        if (baseNombre.Length == 0) baseNombre = "analisis";
        if (baseNombre.Length > 60) baseNombre = baseNombre[..60];
        return string.Create(CultureInfo.InvariantCulture, $"DiagNET_{baseNombre}_{fecha.ToUniversalTime():yyyyMMdd}.pdf");
    }
}
