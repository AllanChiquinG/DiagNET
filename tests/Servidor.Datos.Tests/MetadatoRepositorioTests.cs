using Npgsql;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Tests;

public class MetadatoRepositorioTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private IMetadatoRepositorio Repo => bd.Metadatos;

    private static string Nuevo(string prefijo = "Ejemplo.Meta") => $"{prefijo}.{Guid.NewGuid():N}";

    // ---------- unicidad y normalización ----------

    [Fact]
    public async Task El_identificador_no_distingue_mayusculas_y_hay_una_sola_fila()
    {
        var nombre = Nuevo();
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base, descargas: 1));
        await Repo.GuardarAsync(Muestras.Metadato(nombre.ToUpperInvariant(), "1.0.0", Muestras.Base.AddMinutes(1), descargas: 2));

        Assert.Equal(1, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE lower(nombre_paquete) = @n", ("n", nombre.ToLowerInvariant())));
        var leido = await Repo.BuscarAsync(nombre.ToLowerInvariant(), "1.0.0");
        Assert.Equal(2L, leido!.Descargas);
        Assert.Equal(nombre, leido.NombrePaquete); // conserva las mayúsculas de la primera inserción
    }

    [Theory]
    [InlineData("1.0", "1.0.0")]
    [InlineData("1.0.0.0", "1.0.0")]
    [InlineData("1.0.0+build7", "1.0.0")]
    [InlineData("01.00.00", "1.0.0")]
    [InlineData("2.0.0-BETA.1", "2.0.0-beta.1")]
    public async Task Las_versiones_equivalentes_comparten_fila(string escrita, string normal)
    {
        var nombre = Nuevo();
        await Repo.GuardarAsync(Muestras.Metadato(nombre, escrita, Muestras.Base));

        var leido = await Repo.BuscarAsync(nombre, normal);

        Assert.NotNull(leido);
        Assert.Equal(normal, leido.Version);
        await Repo.GuardarAsync(Muestras.Metadato(nombre, normal, Muestras.Base.AddMinutes(1)));
        Assert.Equal(1, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE lower(nombre_paquete) = @n", ("n", nombre.ToLowerInvariant())));
    }

    [Fact]
    public async Task Versiones_distintas_son_filas_distintas()
    {
        var nombre = Nuevo();
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0"));
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.1"));

        Assert.Equal(2, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE lower(nombre_paquete) = @n", ("n", nombre.ToLowerInvariant())));
    }

    [Fact]
    public async Task Buscar_algo_que_no_existe_devuelve_nulo() =>
        Assert.Null(await Repo.BuscarAsync(Nuevo(), "1.0.0"));

    // ---------- nulos ----------

    [Fact]
    public async Task Conserva_los_nulos_distintos_de_cero_y_de_falso()
    {
        var conNulos = Nuevo("Ejemplo.Nulos");
        var conValores = Nuevo("Ejemplo.Valores");
        await Repo.GuardarAsync(new MetadatoLibreria(conNulos, "1.0.0", null, null, null, null, null, null, Muestras.Base));
        await Repo.GuardarAsync(new MetadatoLibreria(conValores, "1.0.0", Muestras.Base, 0, "MIT", [], false, false, Muestras.Base));

        var a = (await Repo.BuscarAsync(conNulos, "1.0.0"))!;
        var b = (await Repo.BuscarAsync(conValores, "1.0.0"))!;

        Assert.Null(a.Descargas);
        Assert.Null(a.Deprecado);
        Assert.Null(a.CompatibleAot);
        Assert.Null(a.FrameworksObjetivo);
        Assert.Null(a.FechaPublicacion);
        Assert.Equal(0L, b.Descargas);
        Assert.False(b.Deprecado);
        Assert.False(b.CompatibleAot);
        Assert.Empty(b.FrameworksObjetivo!);
    }

    [Fact]
    public async Task La_base_rechaza_descargas_negativas() =>
        await Assert.ThrowsAsync<PostgresException>(() => Repo.GuardarAsync(Muestras.Metadato(Nuevo(), "1.0.0", descargas: -5)));

    // ---------- reemplazo declarado y licencia (E4) ----------

    [Fact]
    public async Task El_paquete_alterno_su_rango_y_la_licencia_completa_se_recuperan_desde_la_cache()
    {
        var nombre = Nuevo("Ejemplo.Deprecado");
        var licencia = "https://example.org/licencias/" + new string('x', 900);
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", deprecado: true) with
        {
            Licencia = licencia,
            PaqueteAlterno = "Ejemplo.Moderno",
            RangoAlterno = "[1.0.0, )",
        });

        var leido = (await Repo.BuscarAsync(nombre, "1.0.0"))!;

        Assert.True(leido.Deprecado);
        Assert.Equal("Ejemplo.Moderno", leido.PaqueteAlterno);
        Assert.Equal("[1.0.0, )", leido.RangoAlterno);
        Assert.Equal(licencia, leido.Licencia);
    }

    [Fact]
    public async Task Sin_reemplazo_declarado_alterno_y_rango_quedan_nulos_y_una_renovacion_los_actualiza()
    {
        var nombre = Nuevo("Ejemplo.Renovado");
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base));
        var inicial = (await Repo.BuscarAsync(nombre, "1.0.0"))!;
        Assert.Null(inicial.PaqueteAlterno);
        Assert.Null(inicial.RangoAlterno);

        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base.AddHours(1), deprecado: true) with { PaqueteAlterno = "Ejemplo.Nuevo", RangoAlterno = "*" });

        var renovado = (await Repo.BuscarAsync(nombre, "1.0.0"))!;
        Assert.Equal("Ejemplo.Nuevo", renovado.PaqueteAlterno);
        Assert.Equal("*", renovado.RangoAlterno);
    }

    [Fact]
    public async Task Un_dato_antiguo_no_borra_ni_cambia_el_reemplazo_declarado_de_uno_mas_reciente()
    {
        var nombre = Nuevo("Ejemplo.Orden");
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base.AddHours(1), deprecado: true) with { PaqueteAlterno = "Ejemplo.Nuevo", RangoAlterno = "*" });

        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base, deprecado: false));

        Assert.Equal("Ejemplo.Nuevo", (await Repo.BuscarAsync(nombre, "1.0.0"))!.PaqueteAlterno);
    }

    // ---------- upsert concurrente ----------

    [Fact]
    public async Task Upsert_concurrente_del_mismo_paquete_no_duplica_ni_falla_y_gana_el_dato_mas_reciente()
    {
        var nombre = Nuevo("Ejemplo.Concurrente");
        var tareas = Enumerable.Range(0, 60).Select(i => Task.Run(() =>
            Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base.AddSeconds(i), descargas: i)))).ToArray();

        await Task.WhenAll(tareas);

        Assert.Equal(1, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE lower(nombre_paquete) = @n", ("n", nombre.ToLowerInvariant())));
        var final = (await Repo.BuscarAsync(nombre, "1.0.0"))!;
        Assert.Equal(59L, final.Descargas);
        Assert.Equal(Muestras.Base.AddSeconds(59), final.FechaConsulta);
    }

    [Fact]
    public async Task Un_dato_mas_antiguo_no_pisa_a_uno_mas_reciente()
    {
        var nombre = Nuevo();
        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base.AddHours(1), descargas: 200));

        await Repo.GuardarAsync(Muestras.Metadato(nombre, "1.0.0", Muestras.Base, descargas: 100));

        Assert.Equal(200L, (await Repo.BuscarAsync(nombre, "1.0.0"))!.Descargas);
    }

    [Fact]
    public async Task Upsert_de_varios_paquetes_a_la_vez_guarda_cada_uno_una_vez()
    {
        var nombres = Enumerable.Range(0, 20).Select(_ => Nuevo("Ejemplo.Lote")).ToList();
        var tareas = nombres.SelectMany(n => Enumerable.Range(0, 5).Select(i =>
            Task.Run(() => Repo.GuardarAsync(Muestras.Metadato(n, "1.0.0", Muestras.Base.AddSeconds(i)))))).ToArray();

        await Task.WhenAll(tareas);

        foreach (var n in nombres)
            Assert.Equal(1, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE lower(nombre_paquete) = @n", ("n", n.ToLowerInvariant())));
    }

    // ---------- purga ----------

    [Fact]
    public async Task Purgar_respeta_el_limite_exacto_y_solo_borra_lo_anterior()
    {
        await using var propio = new BaseDeDatosFixture();
        await propio.InitializeAsync();
        try
        {
            var limite = Muestras.Base;
            await propio.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Vieja", "1.0.0", limite.AddSeconds(-1)));
            await propio.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Justa", "1.0.0", limite));
            await propio.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Nueva", "1.0.0", limite.AddSeconds(1)));

            var borradas = await propio.Metadatos.PurgarAnterioresAsync(limite);

            Assert.Equal(1, borradas);
            Assert.Null(await propio.Metadatos.BuscarAsync("Ejemplo.Vieja", "1.0.0"));
            Assert.NotNull(await propio.Metadatos.BuscarAsync("Ejemplo.Justa", "1.0.0"));
            Assert.NotNull(await propio.Metadatos.BuscarAsync("Ejemplo.Nueva", "1.0.0"));
        }
        finally
        {
            await propio.DisposeAsync();
        }
    }

    // ---------- selección de versiones por paquete (DEC-11) ----------

    [Fact]
    public async Task Guarda_y_recupera_la_seleccion_de_versiones_sin_distinguir_mayusculas()
    {
        var nombre = Nuevo("Ejemplo.Seleccion");
        var seleccion = new SeleccionPaquete(nombre, "5.0.0", new Dictionary<string, string> { ["net8.0"] = "4.2.0", ["net10.0"] = "5.0.0" }, true, Muestras.Base);
        await Repo.GuardarSeleccionAsync(seleccion);

        var leida = (await Repo.BuscarSeleccionAsync(nombre.ToUpperInvariant()))!;

        Assert.Equal("5.0.0", leida.UltimaVersionEstable);
        Assert.Equal("4.2.0", leida.UltimaCompatiblePorTfm["net8.0"]);
        Assert.True(leida.SeleccionCompleta);
        Assert.Equal(Muestras.Base, leida.FechaConsulta);
    }

    [Fact]
    public async Task Una_seleccion_incompleta_o_sin_ultima_version_se_conserva_tal_cual()
    {
        var nombre = Nuevo("Ejemplo.Incompleta");
        await Repo.GuardarSeleccionAsync(new SeleccionPaquete(nombre, null, new Dictionary<string, string>(), false, Muestras.Base));

        var leida = (await Repo.BuscarSeleccionAsync(nombre))!;

        Assert.Null(leida.UltimaVersionEstable);
        Assert.False(leida.SeleccionCompleta);
        Assert.Empty(leida.UltimaCompatiblePorTfm);
    }

    [Fact]
    public async Task Upsert_concurrente_de_la_seleccion_deja_una_fila_con_el_dato_mas_reciente()
    {
        var nombre = Nuevo("Ejemplo.SelConcurrente");
        var tareas = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            Repo.GuardarSeleccionAsync(new SeleccionPaquete(nombre, $"1.{i}.0", new Dictionary<string, string>(), true, Muestras.Base.AddSeconds(i))))).ToArray();

        await Task.WhenAll(tareas);

        Assert.Equal(1, await bd.EscalarAsync<int>("SELECT count(*) FROM seleccion_paquete WHERE lower(nombre_paquete) = @n", ("n", nombre.ToLowerInvariant())));
        Assert.Equal("1.39.0", (await Repo.BuscarSeleccionAsync(nombre))!.UltimaVersionEstable);
    }

    [Fact]
    public async Task Purgar_selecciones_antiguas_solo_borra_las_vencidas()
    {
        var vieja = Nuevo("Ejemplo.SelVieja");
        var nueva = Nuevo("Ejemplo.SelNueva");
        var limite = Muestras.Base.AddYears(5);
        await Repo.GuardarSeleccionAsync(new SeleccionPaquete(vieja, "1.0.0", new Dictionary<string, string>(), true, limite.AddDays(-1)));
        await Repo.GuardarSeleccionAsync(new SeleccionPaquete(nueva, "1.0.0", new Dictionary<string, string>(), true, limite.AddDays(1)));

        await Repo.PurgarSeleccionesAnterioresAsync(limite);

        Assert.Null(await Repo.BuscarSeleccionAsync(vieja));
        Assert.NotNull(await Repo.BuscarSeleccionAsync(nueva));
    }
}
