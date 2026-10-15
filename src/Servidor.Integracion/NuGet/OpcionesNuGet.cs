namespace Servidor.Integracion.NuGet;

/// <summary>
/// Valores configurables de la integración (sección «NuGet» de la configuración). Los de resiliencia son los supuestos documentales
/// iniciales (10 s, 3 reintentos, 4 simultáneas, disyuntor de 5 fallos y 30 s, respuesta de 5 MB): se ajustan con pruebas reales.
/// </summary>
public sealed class OpcionesNuGet
{
    public const string Seccion = "NuGet";

    /// <summary>Único dato fijo: todo lo demás se descubre desde el índice de servicios.</summary>
    public string UrlIndice { get; set; } = "https://api.nuget.org/v3/index.json";

    public string UserAgent { get; set; } = "DiagNET/1.0 (proyecto de graduacion UMG Coban)";

    /// <summary>Dominios permitidos: el host debe ser igual o un subdominio (límite de punto) y el esquema HTTPS.</summary>
    public string[] DominiosPermitidos { get; set; } = ["nuget.org"];

    // ---- Resiliencia ----
    public int TiempoEsperoSeg { get; set; } = 10;
    public int MaxReintentos { get; set; } = 3;
    public int[] EsperasReintentoSeg { get; set; } = [1, 2, 4];

    /// <summary>Variación aleatoria máxima sobre cada espera (0.2 = hasta +20 %).</summary>
    public double VariacionEspera { get; set; } = 0.2;

    public int MaxConcurrentes { get; set; } = 4;

    /// <summary>Si <c>Retry-After</c> supera este valor no se espera: la operación falla con LimiteSolicitudes (modo degradado).</summary>
    public int MaxRetryAfterSeg { get; set; } = 10;

    /// <summary>
    /// Operaciones consecutivas que agotan sus reintentos para abrir el disyuntor (DEC-15, confirmada por el propietario): cuenta OPERACIONES fallidas
    /// (una solicitud lógica que agotó sus reintentos cuenta 1), no cada intento. Un éxito o un 404 reinicia la cuenta.
    /// </summary>
    public int UmbralDisyuntor { get; set; } = 5;

    public int PausaDisyuntorSeg { get; set; } = 30;
    public long MaxRespuestaBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxRedirecciones { get; set; } = 3;

    // ---- Caché en memoria ----
    public int VigenciaIndiceHoras { get; set; } = 24;
    public int CacheRegistroMinutos { get; set; } = 10;
    public int MaxDocumentosEnCache { get; set; } = 500;

    // ---- Lectura del registro ----
    /// <summary>Páginas del registro SIN hojas incluidas que se pueden descargar por paquete. Al agotarse, la selección se marca incompleta.</summary>
    public int MaxPaginas { get; set; } = 8;

    /// <summary>
    /// 100: la referencia oficial del .nuspec (learn.microsoft.com/nuget/reference/nuspec, consultada el 15/10/2026) dice «When uploading a package to nuget.org,
    /// the id field is limited to 100 characters». El máximo de 128 de esa página aplica al atributo id de una DEPENDENCIA (puede apuntar a otras fuentes),
    /// no a un paquete de nuget.org. Un identificador mayor no puede existir en nuget.org y se informa como no encontrado.
    /// </summary>
    public int MaxLongitudIdentificador { get; set; } = 100;

    /// <summary>NuGet limita licenseUrl a 4000 caracteres al publicar; una licencia mayor es anormal y se descarta CON advertencia.</summary>
    public int MaxLongitudLicencia { get; set; } = 4000;

    /// <summary>Consulta las descargas de cada paquete (DEC-05, confirmada): una búsqueda por identificador exacto, con caché y dentro del presupuesto global.</summary>
    public bool ConsultarDescargas { get; set; } = true;

    public int MaxTextoMensaje { get; set; } = 1000;

    /// <summary>Presupuesto global de un análisis (lo aplica el orquestador de E5; se documenta aquí con el resto).</summary>
    public int PresupuestoAnalisisSeg { get; set; } = 120;
}
