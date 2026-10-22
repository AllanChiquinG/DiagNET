using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Operacion;

namespace Servidor.Negocio.Tests.Operacion;

public class PurgaTests
{
    private sealed class Cuentas : IContadorPurga
    {
        public List<DateTimeOffset> Limites { get; } = [];
        public Task<int> ContarAnalisisAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Limites.Add(l); return Task.FromResult(4); }
        public Task<int> ContarMetadatosAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Limites.Add(l); return Task.FromResult(2); }
        public Task<int> ContarSeleccionesAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Limites.Add(l); return Task.FromResult(1); }
    }

    private sealed class Repos : IAnalisisRepositorio, IMetadatoRepositorio
    {
        public List<(string, DateTimeOffset)> Eliminaciones { get; } = [];
        public Task GuardarAsync(Analisis a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Pagina<ResumenAnalisis>> ListarAsync(int p, int t, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> EliminarAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Eliminaciones.Add(("analisis", l)); return Task.FromResult(4); }
        public Task<MetadatoLibreria?> BuscarAsync(string n, string v, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GuardarAsync(MetadatoLibreria m, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgarAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Eliminaciones.Add(("metadatos", l)); return Task.FromResult(2); }
        public Task<SeleccionPaquete?> BuscarSeleccionAsync(string n, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GuardarSeleccionAsync(SeleccionPaquete s, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset l, CancellationToken ct = default) { Eliminaciones.Add(("selecciones", l)); return Task.FromResult(1); }
    }

    private static readonly DateTimeOffset Ahora = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    private static ServicioPurga Servicio(Repos repos, Cuentas cuentas, OpcionesPurga? o = null) => new(repos, repos, cuentas, new FakeTimeProvider(Ahora), o ?? new OpcionesPurga());

    [Fact]
    public void Los_plazos_propuestos_son_90_dias_de_analisis_30_de_cache_y_la_purga_esta_desactivada()
    {
        var o = new OpcionesPurga();

        Assert.Equal((false, 90, 30, 24), (o.Habilitada, o.RetencionAnalisisDias, o.RetencionCacheDias, o.IntervaloHoras));
    }

    [Fact]
    public async Task Planear_calcula_los_limites_desde_un_unico_ahora_y_no_elimina()
    {
        var repos = new Repos();
        var cuentas = new Cuentas();

        var plan = await Servicio(repos, cuentas).PlanearAsync();

        Assert.Equal((Ahora.AddDays(-90), Ahora.AddDays(-30)), (plan.LimiteAnalisis, plan.LimiteCache));
        Assert.Equal((4, 2, 1, 7), (plan.Analisis, plan.MetadatosCache, plan.SeleccionesCache, plan.Total));
        Assert.Empty(repos.Eliminaciones);
    }

    [Fact]
    public async Task Ejecutar_elimina_con_los_mismos_limites_del_plan()
    {
        var repos = new Repos();

        var r = await Servicio(repos, new Cuentas(), new OpcionesPurga { RetencionAnalisisDias = 45, RetencionCacheDias = 10 }).EjecutarAsync();

        Assert.Equal([("analisis", Ahora.AddDays(-45)), ("metadatos", Ahora.AddDays(-10)), ("selecciones", Ahora.AddDays(-10))], repos.Eliminaciones);
        Assert.Equal((4, 2, 1), (r.AnalisisEliminados, r.MetadatosEliminados, r.SeleccionesEliminadas));
    }

    [Theory]
    [InlineData(0, 30, 24)]
    [InlineData(-1, 30, 24)]
    [InlineData(6, 30, 24)]       // menos de 7 días de análisis: demasiado fácil perder resultados por error
    [InlineData(90, 0, 24)]
    [InlineData(90, 30, 0)]
    public async Task Los_plazos_invalidos_se_rechazan_antes_de_consultar_o_eliminar(int analisis, int cache, int horas)
    {
        var repos = new Repos();
        var cuentas = new Cuentas();
        var servicio = Servicio(repos, cuentas, new OpcionesPurga { RetencionAnalisisDias = analisis, RetencionCacheDias = cache, IntervaloHoras = horas });

        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.PlanearAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => servicio.EjecutarAsync());

        Assert.Empty(repos.Eliminaciones);
        Assert.Empty(cuentas.Limites);
    }

    [Theory]
    [InlineData(7, 1, 1)]
    [InlineData(90, 30, 24)]
    [InlineData(3650, 365, 168)]
    public void Los_plazos_validos_se_aceptan(int analisis, int cache, int horas) =>
        new OpcionesPurga { RetencionAnalisisDias = analisis, RetencionCacheDias = cache, IntervaloHoras = horas }.Validar();
}
