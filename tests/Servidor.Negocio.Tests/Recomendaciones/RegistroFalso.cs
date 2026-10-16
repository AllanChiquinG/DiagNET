using System.Collections.Concurrent;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Reglas;
using Servidor.Negocio.Tests.Orquestacion;

namespace Servidor.Negocio.Tests.Recomendaciones;

/// <summary>
/// DOBLE DE PRUEBAS: un «nuget.org» en memoria con las versiones de cada paquete. Calcula la información como lo haría el gateway real
/// (declarada, última estable, última estable compatible por TFM) usando <see cref="InferidorFalso"/>. No es parte de producción.
/// </summary>
internal sealed class RegistroFalso : IClienteNuGet
{
    private readonly Dictionary<string, List<VersionPublicada>> _paquetes = new(StringComparer.OrdinalIgnoreCase);
    private readonly InferidorFalso _inferidor = new();
    private int _llamadas;

    public HashSet<string> Caidos { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Lanzar { get; } = new(StringComparer.OrdinalIgnoreCase);   // lanzan una excepción inesperada
    public ConcurrentQueue<ConsultaPaquete> Consultas { get; } = new();
    public int Llamadas => _llamadas;
    public int Busquedas { get; private set; }
    public string? UltimoTextoBuscado { get; private set; }
    public Func<string, ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> Busqueda { get; set; } = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto([]);

    public RegistroFalso Paquete(string id, params VersionPublicada[] versiones)
    {
        _paquetes[id] = versiones.ToList();
        return this;
    }

    public Task<ResultadoNuGet<InformacionPaquete>> ObtenerPaqueteAsync(ConsultaPaquete c, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _llamadas);
        Consultas.Enqueue(c);
        if (Lanzar.Contains(c.Paquete)) throw new InvalidOperationException("fallo inesperado del registro simulado");
        if (Caidos.Contains(c.Paquete))
            return Task.FromResult(ResultadoNuGet<InformacionPaquete>.NoDisponible(CausaFallo.ErrorServidor, "NuGet no respondió (503)"));
        if (!_paquetes.TryGetValue(c.Paquete, out var versiones))
            return Task.FromResult(ResultadoNuGet<InformacionPaquete>.NoEncontrado("No existe en nuget.org."));

        var estables = versiones.Where(v => v.Listada && !v.EsPrerelease).ToList();
        VersionPublicada? Mayor(IEnumerable<VersionPublicada> vs) =>
            vs.OrderByDescending(v => v.Version, Comparer<string>.Create((a, b) => ComparadorVersiones.Comparar(a, b) ?? 0)).FirstOrDefault();

        var declarada = c.VersionDeclarada is null ? null : versiones.FirstOrDefault(v => ComparadorVersiones.Comparar(v.Version, c.VersionDeclarada) == 0);
        var estado = c.VersionDeclarada is null ? EstadoVersionDeclarada.NoSolicitada : declarada is null ? EstadoVersionDeclarada.NoPublicada : EstadoVersionDeclarada.Publicada;
        var compat = declarada is null ? [] : c.FrameworksEvaluados.Select(t => _inferidor.Inferir(t, declarada.GruposFramework)).ToList();
        var ultima = Mayor(estables);
        var porTfm = c.FrameworksEvaluados.ToDictionary(t => t, t => Mayor(estables.Where(v => _inferidor.Inferir(t, v.GruposFramework).Resultado == ResultadoCompatibilidad.Compatible)),
            StringComparer.OrdinalIgnoreCase);

        var info = new InformacionPaquete(c.Paquete, estado, declarada, compat, ultima, null, porTfm, ultima?.Publicada, true, 1, 1, versiones.Count, Paquetes.Ahora, 1000, null);
        return Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(info, [], 2));
    }

    public Task<ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> BuscarCandidatosAsync(
        string texto, string? excluirPaquete, int maximo, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        Busquedas++;
        UltimoTextoBuscado = texto;
        return Task.FromResult(Busqueda(texto));
    }

    public Task<ResultadoNuGet<long?>> ObtenerDescargasAsync(string paquete, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
