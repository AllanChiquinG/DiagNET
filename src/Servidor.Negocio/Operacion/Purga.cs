using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Operacion;

/// <summary>
/// Opciones de la purga (E11). <b>Desactivada por defecto</b>: la purga elimina datos y solo se activa con una decisión expresa del propietario sobre los plazos.
/// Los plazos son PROPUESTAS configurables (retención de análisis 90 días, caché 30 días), pendientes de validación.
/// </summary>
public sealed class OpcionesPurga
{
    public const int MinimoRetencionAnalisisDias = 7;
    public const int MinimoRetencionCacheDias = 1;

    /// <summary>Si es <c>false</c> (por defecto) no se purga nada automáticamente. La purga manual por línea de comandos exige además <c>--ejecutar</c>.</summary>
    public bool Habilitada { get; set; }

    /// <summary>Días que se conserva un análisis. En la aplicación se toma de <c>Historial:RetencionDias</c> (la misma cifra que la interfaz muestra al usuario).</summary>
    public int RetencionAnalisisDias { get; set; } = 90;

    /// <summary>Días que se conserva una copia de metadatos o de selección de versiones en la caché. Los diagnósticos conservan su propia copia de indicadores.</summary>
    public int RetencionCacheDias { get; set; } = 30;

    /// <summary>Cada cuántas horas corre la purga programada cuando está habilitada.</summary>
    public int IntervaloHoras { get; set; } = 24;

    /// <summary>Lanza si un plazo es absurdo: evita que un valor 0 o negativo borre todo por error de configuración.</summary>
    public void Validar()
    {
        if (RetencionAnalisisDias < MinimoRetencionAnalisisDias)
            throw new InvalidOperationException($"La retención de análisis debe ser de al menos {MinimoRetencionAnalisisDias} días (valor recibido: {RetencionAnalisisDias}).");
        if (RetencionCacheDias < MinimoRetencionCacheDias)
            throw new InvalidOperationException($"La retención de caché debe ser de al menos {MinimoRetencionCacheDias} día (valor recibido: {RetencionCacheDias}).");
        if (IntervaloHoras < 1)
            throw new InvalidOperationException("El intervalo de purga debe ser de al menos 1 hora.");
    }
}

/// <summary>Cuántas filas tocaría una purga con los plazos actuales (sin borrar nada).</summary>
public sealed record PlanPurga(
    DateTimeOffset LimiteAnalisis, DateTimeOffset LimiteCache, int Analisis, int MetadatosCache, int SeleccionesCache)
{
    public int Total => Analisis + MetadatosCache + SeleccionesCache;
}

/// <summary>Cuántas filas eliminó una purga realmente ejecutada (los análisis arrastran sus dependencias, diagnósticos y recomendaciones por cascada).</summary>
public sealed record ResultadoPurga(PlanPurga Plan, int AnalisisEliminados, int MetadatosEliminados, int SeleccionesEliminadas);

/// <summary>Cuenta lo que se purgaría. Solo lee: lo usa la simulación (sin <c>--ejecutar</c>) y el plan previo a eliminar.</summary>
public interface IContadorPurga
{
    Task<int> ContarAnalisisAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);

    Task<int> ContarMetadatosAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);

    Task<int> ContarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);
}

/// <summary>
/// Purga de análisis y caché antiguos (RNF-07/RNF-11). Los límites son ESTRICTOS: se elimina lo que es anterior al límite, nunca lo que está exactamente en él.
/// <see cref="PlanearAsync"/> nunca borra; <see cref="EjecutarAsync"/> es el único método que elimina y valida los plazos antes de tocar nada.
/// Nunca registra identificadores ni contenido: solo cantidades.
/// </summary>
public sealed class ServicioPurga(
    IAnalisisRepositorio analisis, IMetadatoRepositorio metadatos, IContadorPurga contador, TimeProvider reloj, OpcionesPurga opciones)
{
    public async Task<PlanPurga> PlanearAsync(CancellationToken ct = default)
    {
        opciones.Validar();
        var ahora = reloj.GetUtcNow();
        var limiteAnalisis = ahora.AddDays(-opciones.RetencionAnalisisDias);
        var limiteCache = ahora.AddDays(-opciones.RetencionCacheDias);
        return new PlanPurga(
            limiteAnalisis, limiteCache,
            await contador.ContarAnalisisAnterioresAsync(limiteAnalisis, ct),
            await contador.ContarMetadatosAnterioresAsync(limiteCache, ct),
            await contador.ContarSeleccionesAnterioresAsync(limiteCache, ct));
    }

    /// <summary>Elimina lo anterior a los límites. Los análisis se eliminan en cascada; la caché no destruye diagnósticos (conservan su copia de indicadores).</summary>
    public async Task<ResultadoPurga> EjecutarAsync(CancellationToken ct = default)
    {
        var plan = await PlanearAsync(ct);       // valida los plazos y fija los límites con un único «ahora»
        var a = await analisis.EliminarAnterioresAsync(plan.LimiteAnalisis, ct);
        var m = await metadatos.PurgarAnterioresAsync(plan.LimiteCache, ct);
        var s = await metadatos.PurgarSeleccionesAnterioresAsync(plan.LimiteCache, ct);
        return new ResultadoPurga(plan, a, m, s);
    }
}

/// <summary>Estado de la base para la comprobación de disponibilidad (readiness).</summary>
public sealed record EstadoBaseDatos(bool Disponible, bool MigracionesAlDia, int MigracionesPendientes);

public interface IVerificadorBase
{
    /// <summary>Comprueba conexión y migraciones pendientes con un tiempo máximo corto. Nunca lanza: una base caída es un resultado, no un error.</summary>
    Task<EstadoBaseDatos> ComprobarAsync(CancellationToken ct = default);
}
