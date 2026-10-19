using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

namespace Servidor.Datos.Tests;

public class AnalisisRepositorioTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private IAnalisisRepositorio Repo => bd.Analisis;

    private static void MismoResultado(Analisis esperado, Analisis? real)
    {
        Assert.NotNull(real);
        Assert.Equal(Muestras.Json(esperado), Muestras.Json(real));
    }

    // ---------- guardar y recuperar ----------

    [Theory]
    [InlineData("completo")]
    [InlineData("parcial")]
    [InlineData("sin_paquetes")]
    public async Task Guardar_y_recuperar_devuelve_el_mismo_resultado(string caso)
    {
        var analisis = caso switch { "completo" => Muestras.Completo(), "parcial" => Muestras.Parcial(), _ => Muestras.SinPaquetes() };

        await Repo.GuardarAsync(analisis);

        MismoResultado(analisis, await Repo.ObtenerAsync(analisis.Id));
    }

    [Fact]
    public async Task Tras_reiniciar_la_aplicacion_se_recupera_exactamente_el_mismo_resultado()
    {
        var analisis = Muestras.Completo();
        await Repo.GuardarAsync(analisis);

        await using var reinicio = bd.Reiniciar();
        var recuperado = await reinicio.GetRequiredService<IAnalisisRepositorio>().ObtenerAsync(analisis.Id);

        MismoResultado(analisis, recuperado);
    }

    [Fact]
    public async Task Lo_desconocido_sigue_siendo_nulo_y_no_se_convierte_en_cero_ni_falso()
    {
        var analisis = Muestras.Parcial();
        await Repo.GuardarAsync(analisis);

        var sinDatos = (await Repo.ObtenerAsync(analisis.Id))!.Dependencias.Single(d => d.Nombre == "Ejemplo.Desconocido");

        Assert.Null(sinDatos.Diagnostico.FechaConsulta);
        Assert.Null(sinDatos.Diagnostico.Indicadores);
        Assert.Null(sinDatos.VersionDeclarada);
        Assert.Equal(OrigenDato.NoDeterminado, sinDatos.Diagnostico.Origen);
        Assert.Equal(TipoVersion.Propiedad, sinDatos.TipoVersion);
        Assert.Equal("'$(Configuration)' == 'Debug'", sinDatos.Condicion);
        // El diagnóstico sin fecha se guardó como NULL real en la base.
        Assert.Equal(1, await bd.EscalarAsync<int>(
            "SELECT count(*) FROM diagnostico d JOIN dependencia p ON p.id = d.dependencia_id " +
            "WHERE p.analisis_id = @id AND d.fecha_consulta IS NULL AND d.origen = 'no_determinado'", ("id", analisis.Id)));
    }

    [Fact]
    public async Task Los_indicadores_distinguen_cero_conocido_de_dato_ausente_y_aot_no_evaluado()
    {
        var conCero = Muestras.Actualizada("Ejemplo.Cero") with { };
        var indicadores = Muestras.Indicadores(descargas: 0);
        var dep = conCero with { Diagnostico = conCero.Diagnostico with { Indicadores = indicadores } };
        var analisis = Muestras.SinPaquetes() with { Dependencias = [dep, Muestras.Actualizada("Ejemplo.Ausente") with { Diagnostico = Muestras.Actualizada().Diagnostico with { Indicadores = Muestras.Indicadores(descargas: null) } }] };
        await Repo.GuardarAsync(analisis);

        var leido = (await Repo.ObtenerAsync(analisis.Id))!;

        var cero = leido.Dependencias[0].Diagnostico.Indicadores!.Descargas;
        var ausente = leido.Dependencias[1].Diagnostico.Indicadores!.Descargas;
        Assert.Equal(Disponibilidad.Conocido, cero.Disponibilidad);
        Assert.Equal(0L, cero.Valor);
        Assert.Equal(Disponibilidad.NoDeterminado, ausente.Disponibilidad);
        Assert.Null(ausente.Valor);
        Assert.Equal(Disponibilidad.NoEvaluado, leido.Dependencias[0].Diagnostico.Indicadores!.Aot.Disponibilidad);
    }

    [Fact]
    public async Task Conserva_el_orden_y_las_declaraciones_duplicadas_conflictivas()
    {
        var dependencias = Enumerable.Range(1, 40).Select(i => Muestras.Actualizada($"Ejemplo.P{i:00}", "1.0.0")).ToList();
        dependencias.Add(Muestras.Actualizada("Ejemplo.P01", "9.9.9")); // segunda declaración conflictiva
        var analisis = Muestras.SinPaquetes() with { Dependencias = dependencias, Advertencias = [] };
        await Repo.GuardarAsync(analisis);

        var leido = (await Repo.ObtenerAsync(analisis.Id))!;

        Assert.Equal(dependencias.Select(d => (d.Nombre, d.VersionDeclarada)), leido.Dependencias.Select(d => (d.Nombre, d.VersionDeclarada)));
        Assert.Equal(41, await bd.EscalarAsync<int>("SELECT total_dependencias FROM analisis WHERE id = @id", ("id", analisis.Id)));
    }

    [Fact]
    public async Task Las_fechas_se_guardan_en_UTC_aunque_lleguen_con_otra_zona()
    {
        var local = new DateTimeOffset(2026, 10, 14, 12, 30, 0, TimeSpan.FromHours(-6));
        var analisis = Muestras.SinPaquetes(fecha: local);

        await Repo.GuardarAsync(analisis);

        var leido = (await Repo.ObtenerAsync(analisis.Id))!;
        Assert.Equal(local, leido.FechaAnalisis); // mismo instante
        Assert.Equal(TimeSpan.Zero, leido.FechaAnalisis.Offset);
    }

    [Fact]
    public async Task Obtener_un_analisis_inexistente_devuelve_nulo() =>
        Assert.Null(await Repo.ObtenerAsync(Guid.NewGuid()));

    // ---------- atomicidad e integridad ----------

    [Fact]
    public async Task Si_la_base_rechaza_algo_no_se_guarda_nada_en_ninguna_tabla()
    {
        // La 2.ª dependencia repite la misma recomendación (viola ux_recomendacion en la base): toda la transacción debe deshacerse,
        // incluidos el análisis, la 1.ª dependencia y sus diagnósticos, que ya se habían insertado en la misma operación.
        var duplicada = Muestras.Desactualizada("Ejemplo.Duplicada");
        var invalida = Muestras.Completo() with
        {
            Dependencias = [Muestras.Actualizada("Ejemplo.Valida"), duplicada with { Recomendaciones = [duplicada.Recomendaciones[0], duplicada.Recomendaciones[0]] }],
        };
        var antes = await ContarFilasAsync();

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Repo.GuardarAsync(invalida));

        Assert.Equal(antes, await ContarFilasAsync());
        Assert.Null(await Repo.ObtenerAsync(invalida.Id));
    }

    [Fact]
    public async Task Un_valor_que_excede_un_limite_se_rechaza_con_un_error_claro_sin_tocar_la_base()
    {
        var invalida = Muestras.Completo() with { Dependencias = [Muestras.Actualizada("Ejemplo.Valida"), Muestras.Desactualizada(new string('x', 201))] };
        var antes = await ContarFilasAsync();

        var ex = await Assert.ThrowsAsync<ValorExcedeLimiteException>(() => Repo.GuardarAsync(invalida));

        Assert.Equal("dependencia.nombre_paquete", ex.Campo);
        Assert.Equal(antes, await ContarFilasAsync());
    }

    [Fact]
    public async Task Guardar_dos_veces_el_mismo_id_falla_y_conserva_el_primero()
    {
        var primero = Muestras.Completo();
        await Repo.GuardarAsync(primero);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => Repo.GuardarAsync(Muestras.Parcial(primero.Id)));

        MismoResultado(primero, await Repo.ObtenerAsync(primero.Id));
    }

    [Theory]
    [InlineData("INSERT INTO analisis (nombre_archivo, framework_objetivo, estado, total_dependencias) VALUES ('a.csproj','net8.0','otro',0)")]
    [InlineData("INSERT INTO analisis (nombre_archivo, framework_objetivo, estado, total_dependencias) VALUES ('a.csproj','net8.0','completo',-1)")]
    public async Task La_base_rechaza_valores_invalidos_aunque_la_aplicacion_falle(string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => bd.EjecutarAsync(sql));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task La_base_rechaza_un_diagnostico_con_estado_u_origen_inventados()
    {
        var analisis = Muestras.SinPaquetes() with { Dependencias = [Muestras.Actualizada("Ejemplo.Uno")] };
        await Repo.GuardarAsync(analisis);

        var estado = await Assert.ThrowsAsync<PostgresException>(() => bd.EjecutarAsync("UPDATE diagnostico SET estado = 'quizas'"));
        var origen = await Assert.ThrowsAsync<PostgresException>(() => bd.EjecutarAsync("UPDATE diagnostico SET origen = 'inventado'"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, estado.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, origen.SqlState);
    }

    // ---------- historial ----------

    [Fact]
    public async Task Listar_ordena_del_mas_reciente_al_mas_antiguo_y_pagina()
    {
        await using var propio = new BaseDeDatosFixture();
        await propio.InitializeAsync();
        try
        {
            var fechas = Enumerable.Range(0, 5).Select(i => Muestras.Base.AddHours(i)).ToList();
            var ids = new List<Guid>();
            foreach (var f in fechas)
            {
                var analisis = Muestras.SinPaquetes(fecha: f);
                ids.Add(analisis.Id);
                await propio.Analisis.GuardarAsync(analisis);
            }

            var pagina1 = await propio.Analisis.ListarAsync(1, 2, ids);
            var pagina3 = await propio.Analisis.ListarAsync(3, 2, ids);

            Assert.Equal(5, pagina1.Total);
            Assert.Equal(fechas.OrderByDescending(f => f).Take(2), pagina1.Elementos.Select(e => e.FechaAnalisis));
            Assert.Single(pagina3.Elementos);
            Assert.Equal(fechas.Min(), pagina3.Elementos.Single().FechaAnalisis);
        }
        finally
        {
            await propio.DisposeAsync();
        }
    }

    [Fact]
    public async Task Listar_con_ids_devuelve_solo_esos_analisis_y_trae_el_resumen()
    {
        var mio = Muestras.Parcial();
        var otro = Muestras.Completo();
        await Repo.GuardarAsync(mio);
        await Repo.GuardarAsync(otro);

        var pagina = await Repo.ListarAsync(1, 10, [mio.Id]);

        var fila = Assert.Single(pagina.Elementos);
        Assert.Equal(1, pagina.Total);
        Assert.Equal(mio.Id, fila.Id);
        Assert.Equal(EstadoAnalisis.Parcial, fila.Estado);
        Assert.Equal(2, fila.TotalDependencias);
        Assert.Equal(["net8.0", "net10.0", "net48"], fila.FrameworksDeclarados);
    }

    [Fact]
    public async Task Listar_con_un_filtro_nulo_nunca_lista_todos_los_analisis_lanza_excepcion()
    {
        await Repo.GuardarAsync(Muestras.Completo());

        await Assert.ThrowsAsync<ArgumentNullException>(() => Repo.ListarAsync(1, 10, null!));
    }

    [Fact]
    public async Task Listar_con_un_filtro_vacio_devuelve_una_pagina_vacia_aunque_existan_analisis()
    {
        await Repo.GuardarAsync(Muestras.Completo());
        await Repo.GuardarAsync(Muestras.Parcial());

        var pagina = await Repo.ListarAsync(1, 10, []);

        Assert.Empty(pagina.Elementos);
        Assert.Equal(0, pagina.Total);
        Assert.Equal((1, 10), (pagina.NumeroPagina, pagina.TamanoPagina));
    }

    [Fact]
    public async Task Listar_solo_devuelve_los_pedidos_aunque_haya_otros_y_omite_los_desconocidos_sin_error()
    {
        var pedido = Muestras.Completo();
        var ajeno = Muestras.Parcial();
        await Repo.GuardarAsync(pedido);
        await Repo.GuardarAsync(ajeno);

        var pagina = await Repo.ListarAsync(1, 10, [pedido.Id, Guid.NewGuid(), pedido.Id]);

        Assert.Equal([pedido.Id], pagina.Elementos.Select(e => e.Id));
        Assert.Equal(1, pagina.Total);
    }

    [Fact]
    public void El_puerto_no_admite_un_filtro_opcional()
    {
        var parametro = typeof(IAnalisisRepositorio).GetMethod(nameof(IAnalisisRepositorio.ListarAsync))!.GetParameters().Single(p => p.Name == "ids");

        Assert.False(parametro.HasDefaultValue);                               // no hay forma de llamarlo «sin ids»
        Assert.Equal("IReadOnlyCollection`1", parametro.ParameterType.Name);
        Assert.False(new System.Reflection.NullabilityInfoContext().Create(parametro).WriteState == System.Reflection.NullabilityState.Nullable);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task Listar_valida_la_paginacion(int pagina, int tamano) =>
        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => Repo.ListarAsync(pagina, tamano, [Guid.NewGuid()]));

    // ---------- purga e inmutabilidad ----------

    [Fact]
    public async Task Purgar_analisis_antiguos_elimina_tambien_sus_dependencias_diagnosticos_y_recomendaciones()
    {
        await using var propio = new BaseDeDatosFixture();
        await propio.InitializeAsync();
        try
        {
            var antiguo = Muestras.Completo(fecha: Muestras.Base.AddDays(-100));
            var reciente = Muestras.Completo(fecha: Muestras.Base);
            await propio.Analisis.GuardarAsync(antiguo);
            await propio.Analisis.GuardarAsync(reciente);

            var eliminados = await propio.Analisis.EliminarAnterioresAsync(Muestras.Base.AddDays(-90));

            Assert.Equal(1, eliminados);
            Assert.Null(await propio.Analisis.ObtenerAsync(antiguo.Id));
            MismoResultado(reciente, await propio.Analisis.ObtenerAsync(reciente.Id));
            Assert.Equal(3, await propio.EscalarAsync<int>("SELECT count(*) FROM dependencia"));
            Assert.Equal(3, await propio.EscalarAsync<int>("SELECT count(*) FROM diagnostico"));
            Assert.Equal(3, await propio.EscalarAsync<int>("SELECT count(*) FROM recomendacion"));
        }
        finally
        {
            await propio.DisposeAsync();
        }
    }

    [Fact]
    public async Task Renovar_o_purgar_la_cache_no_cambia_un_resultado_guardado()
    {
        var dep = Muestras.Actualizada("Ejemplo.Cache", "3.2.1");
        var analisis = Muestras.SinPaquetes() with { Dependencias = [dep], Advertencias = [] };
        await bd.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Cache", "3.2.1", Muestras.Base, descargas: 100));
        await Repo.GuardarAsync(analisis);
        Assert.Equal(1, await bd.EscalarAsync<int>(
            "SELECT count(*) FROM diagnostico d JOIN dependencia p ON p.id = d.dependencia_id WHERE p.analisis_id = @id AND d.metadato_id IS NOT NULL", ("id", analisis.Id)));

        // La caché se renueva con valores distintos y luego se purga: el diagnóstico guardado no puede cambiar.
        await bd.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Cache", "3.2.1", Muestras.Base.AddDays(1), descargas: 999999, deprecado: true));
        MismoResultado(analisis, await Repo.ObtenerAsync(analisis.Id));

        await bd.Metadatos.PurgarAnterioresAsync(Muestras.Base.AddYears(1));

        MismoResultado(analisis, await Repo.ObtenerAsync(analisis.Id));
        Assert.Equal(1, await bd.EscalarAsync<int>(
            "SELECT count(*) FROM diagnostico d JOIN dependencia p ON p.id = d.dependencia_id WHERE p.analisis_id = @id AND d.metadato_id IS NULL", ("id", analisis.Id)));
    }

    private async Task<(int, int, int, int)> ContarFilasAsync() => (
        await bd.EscalarAsync<int>("SELECT count(*) FROM analisis"),
        await bd.EscalarAsync<int>("SELECT count(*) FROM dependencia"),
        await bd.EscalarAsync<int>("SELECT count(*) FROM diagnostico"),
        await bd.EscalarAsync<int>("SELECT count(*) FROM recomendacion"));
}
