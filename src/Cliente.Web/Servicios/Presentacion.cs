using System.Globalization;
using Compartido.Contratos;

namespace Cliente.Web.Servicios;

/// <summary>
/// Textos y formatos de la interfaz en un solo lugar (español). Nada aquí inventa datos: solo traduce lo que devuelve la API. Los textos de recomendaciones
/// ("Alternativa sugerida por el autor", "Cambios de código no determinados"…) llegan ya redactados desde el servidor (E6/E7) y NO se reescriben aquí.
/// </summary>
public static class Presentacion
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

    /// <summary>Clase CSS del prototipo: ok, old, inc, eol, nd.</summary>
    public static string Clase(EstadoDiagnostico estado) => estado switch
    {
        EstadoDiagnostico.Actualizada => "ok",
        EstadoDiagnostico.Desactualizada => "old",
        EstadoDiagnostico.Incompatible => "inc",
        EstadoDiagnostico.SinSoporteActivo => "eol",
        _ => "nd",
    };

    /// <summary>Orden de gravedad en la tabla: lo que exige acción primero; «no determinada» antes que «actualizada» porque requiere atención.</summary>
    public static int Orden(EstadoDiagnostico estado) => estado switch
    {
        EstadoDiagnostico.Incompatible => 0,
        EstadoDiagnostico.SinSoporteActivo => 1,
        EstadoDiagnostico.Desactualizada => 2,
        EstadoDiagnostico.NoDeterminada => 3,
        _ => 4,
    };

    public static string Texto(EstadoAnalisis estado) => estado == EstadoAnalisis.Parcial ? "Parcial" : "Completo";

    public static string Texto(TipoVersionDeclarada tipo) => tipo switch
    {
        TipoVersionDeclarada.Exacta => "exacta",
        TipoVersionDeclarada.Rango => "rango",
        TipoVersionDeclarada.Flotante => "flotante",
        TipoVersionDeclarada.Propiedad => "definida por una variable",
        TipoVersionDeclarada.Ausente => "sin versión",
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

    /// <summary>
    /// Plazo de conservación SIN prometer lo que no ocurre: la eliminación automática solo se menciona si está activada en el servidor.
    /// </summary>
    public static string Conservacion(int dias, bool purgaActiva) => purgaActiva
        ? $"Se conserva {dias} días y luego se elimina automáticamente."
        : $"Plazo de conservación previsto: {dias} días. La eliminación automática todavía no está activada, por lo que el análisis puede seguir disponible después de ese plazo.";

    /// <summary>«5 oct 2026, 15:42» en la hora local del navegador. Formato propio para no depender de los datos de cultura del navegador.</summary>
    public static string Fecha(DateTimeOffset fecha)
    {
        var local = fecha.ToLocalTime();
        return string.Create(CultureInfo.InvariantCulture, $"{local.Day} {Meses[local.Month - 1]} {local.Year}, {local.Hour:00}:{local.Minute:00}");
    }

    public static string Fecha(DateTimeOffset? fecha) => fecha is { } f ? Fecha(f) : "—";

    /// <summary>Dónde y cuándo se obtuvieron los datos de una dependencia (la fecha de una copia vencida es la ORIGINAL).</summary>
    public static string OrigenYFecha(DiagnosticoDto d) =>
        d.Origen == OrigenDato.NoDeterminado || d.FechaConsulta is null ? "Sin datos de NuGet" : $"{Texto(d.Origen)} · {Fecha(d.FechaConsulta)}";

    /// <summary>«3,4 KB». Sin depender de datos de cultura del navegador.</summary>
    public static string Tamano(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => Decimal(bytes / 1024.0) + " KB",
        _ => Decimal(bytes / (1024.0 * 1024.0)) + " MB",
    };

    private static string Decimal(double valor) => valor.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>«1 234 567»: separador de miles con espacio, sin depender de la cultura del navegador.</summary>
    public static string Entero(long valor) => valor.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    /// <summary>
    /// Muestra un indicador sin confundir «no hay dato» con falso o cero: conocido ⇒ su valor; no determinado ⇒ «No determinado»; no evaluado ⇒ «No evaluado».
    /// </summary>
    public static (string Texto, bool Conocido) Indicador<T>(IndicadorDto<T> indicador, Func<T, string> formato, string ausenteConocido = "Ninguna")
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
}
