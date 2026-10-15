namespace Servidor.Negocio.Dominio;

/// <summary>Persistencia de análisis. Los análisis solo se insertan; nunca se editan (RNF-07).</summary>
public interface IAnalisisRepositorio
{
    /// <summary>Guarda el resultado completo en una única transacción: o se guarda todo o no se guarda nada.</summary>
    Task GuardarAsync(Analisis analisis, CancellationToken ct = default);

    /// <summary>Devuelve el análisis tal como se guardó, o <c>null</c> si no existe o fue purgado.</summary>
    Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default);

    /// <param name="ids">Si se indica, solo esos análisis (historial por navegador, DEC-06 propuesta); si es <c>null</c>, todos.</param>
    Task<Pagina<ResumenAnalisis>> ListarAsync(int numeroPagina, int tamanoPagina, IReadOnlyCollection<Guid>? ids = null, CancellationToken ct = default);

    /// <summary>Elimina los análisis anteriores al límite (con sus dependencias, diagnósticos y recomendaciones). Devuelve cuántos.</summary>
    Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);
}

/// <summary>Caché de metadatos y de selección de versiones. Nunca comparte un DbContext entre tareas paralelas.</summary>
public interface IMetadatoRepositorio
{
    Task<MetadatoLibreria?> BuscarAsync(string nombrePaquete, string version, CancellationToken ct = default);

    /// <summary>Inserta o actualiza (upsert atómico y seguro ante concurrencia). Un dato más antiguo no pisa a uno más reciente.</summary>
    Task GuardarAsync(MetadatoLibreria metadato, CancellationToken ct = default);

    /// <summary>Elimina filas de caché consultadas antes del límite. Los diagnósticos conservan su copia de indicadores.</summary>
    Task<int> PurgarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);

    Task<SeleccionPaquete?> BuscarSeleccionAsync(string nombrePaquete, CancellationToken ct = default);

    Task GuardarSeleccionAsync(SeleccionPaquete seleccion, CancellationToken ct = default);

    Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default);
}
