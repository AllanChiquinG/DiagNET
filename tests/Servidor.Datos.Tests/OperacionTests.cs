using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Operacion;

namespace Servidor.Datos.Tests;

/// <summary>
/// Operación (E11) sobre PostgreSQL REAL en bases temporales (diagnet_test_*): nunca sobre diagnet_nueva ni sobre datos reales.
/// </summary>
public sealed class PurgaTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private static readonly DateTimeOffset Ahora = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    // Reutiliza los servicios (y el grupo de conexiones) de la base de la clase: crear proveedores nuevos en cada llamada agotaría las conexiones de PostgreSQL al correr todo en paralelo.
    private ServicioPurga Servicio(OpcionesPurga? opciones = null) =>
        new(bd.Analisis, bd.Metadatos, bd.Contador, new FakeTimeProvider(Ahora), opciones ?? new OpcionesPurga());

    private Task Vaciar() => bd.EjecutarAsync("TRUNCATE analisis, metadato_libreria, seleccion_paquete RESTART IDENTITY CASCADE");

    [Fact]
    public async Task El_limite_es_estricto_se_elimina_lo_anterior_y_se_conserva_lo_que_esta_exactamente_en_el_limite_con_su_cascada()
    {
        await Vaciar();
        var limite = Ahora.AddDays(-90);
        var viejo = Muestras.Completo(fecha: limite.AddSeconds(-1));
        var justo = Muestras.Completo(fecha: limite);
        var nuevo = Muestras.Completo(fecha: limite.AddSeconds(1));
        foreach (var a in new[] { viejo, justo, nuevo }) await bd.Analisis.GuardarAsync(a);
        var hijosAntes = await bd.EscalarAsync<long>("SELECT (SELECT count(*) FROM dependencia) + (SELECT count(*) FROM diagnostico) + (SELECT count(*) FROM recomendacion)");

        var resultado = await Servicio().EjecutarAsync();

        Assert.Equal(1, resultado.AnalisisEliminados);
        Assert.Null(await bd.Analisis.ObtenerAsync(viejo.Id));
        Assert.NotNull(await bd.Analisis.ObtenerAsync(justo.Id));    // exactamente en el límite: se conserva
        Assert.NotNull(await bd.Analisis.ObtenerAsync(nuevo.Id));
        // La cascada eliminó todos los hijos del análisis viejo (y solo los suyos): quedan 2 de 3 juegos completos.
        var hijosDespues = await bd.EscalarAsync<long>("SELECT (SELECT count(*) FROM dependencia) + (SELECT count(*) FROM diagnostico) + (SELECT count(*) FROM recomendacion)");
        Assert.Equal(hijosAntes / 3 * 2, hijosDespues);
        Assert.Equal(0, await bd.EscalarAsync<long>("SELECT count(*) FROM dependencia d WHERE NOT EXISTS (SELECT 1 FROM analisis a WHERE a.id = d.analisis_id)"));
    }

    [Fact]
    public async Task Planear_solo_cuenta_y_no_elimina_nada()
    {
        await Vaciar();
        var viejo = Muestras.Completo(fecha: Ahora.AddDays(-200));
        await bd.Analisis.GuardarAsync(viejo);
        await bd.Metadatos.GuardarAsync(Muestras.Metadato("Ejemplo.Vieja", consulta: Ahora.AddDays(-45)));
        await bd.Metadatos.GuardarSeleccionAsync(new SeleccionPaquete("Ejemplo.Vieja", "1.0.0", new Dictionary<string, string>(), true, Ahora.AddDays(-45)));

        var plan = await Servicio().PlanearAsync();

        Assert.Equal((1, 1, 1, 3), (plan.Analisis, plan.MetadatosCache, plan.SeleccionesCache, plan.Total));
        Assert.Equal(Ahora.AddDays(-90), plan.LimiteAnalisis);
        Assert.Equal(Ahora.AddDays(-30), plan.LimiteCache);
        Assert.NotNull(await bd.Analisis.ObtenerAsync(viejo.Id));                                      // nada se eliminó
        Assert.Equal(1, await bd.EscalarAsync<long>("SELECT count(*) FROM metadato_libreria"));
        Assert.Equal(1, await bd.EscalarAsync<long>("SELECT count(*) FROM seleccion_paquete"));
    }

    [Fact]
    public async Task La_purga_de_cache_no_destruye_los_diagnosticos_que_conservan_su_copia_de_indicadores()
    {
        await Vaciar();
        var analisis = Muestras.Completo(fecha: Ahora.AddDays(-10));                                   // dentro del plazo de análisis
        await bd.Analisis.GuardarAsync(analisis);
        await bd.Metadatos.GuardarAsync(Muestras.Metadato(consulta: Ahora.AddDays(-45)));              // fuera del plazo de caché

        var r = await Servicio().EjecutarAsync();

        Assert.Equal((0, 1), (r.AnalisisEliminados, r.MetadatosEliminados));
        var recuperado = await bd.Analisis.ObtenerAsync(analisis.Id);
        Assert.Equal(Muestras.Json(analisis), Muestras.Json(recuperado));                              // el resultado guardado es idéntico
    }

    [Fact]
    public async Task Con_plazos_absurdos_no_se_elimina_nada_y_se_rechaza_antes_de_tocar_la_base()
    {
        await Vaciar();
        var a = Muestras.Completo(fecha: Ahora.AddDays(-400));
        await bd.Analisis.GuardarAsync(a);

        foreach (var opciones in new[] { new OpcionesPurga { RetencionAnalisisDias = 0 }, new OpcionesPurga { RetencionAnalisisDias = -5 }, new OpcionesPurga { RetencionCacheDias = 0 }, new OpcionesPurga { IntervaloHoras = 0 } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => Servicio(opciones).EjecutarAsync());

        Assert.NotNull(await bd.Analisis.ObtenerAsync(a.Id));
    }
}

public sealed class MigracionesTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    /// <summary>Base nueva y vacía (sin esquema) para probar la migración desde cero.</summary>
    private async Task<(string Cadena, Func<Task> Borrar)> BaseVacia()
    {
        var admin = new NpgsqlConnectionStringBuilder(bd.CadenaConexion) { Database = "postgres" };
        var nombre = "diagnet_test_mig_" + Guid.NewGuid().ToString("N");
        await using (var c = new NpgsqlConnection(admin.ConnectionString))
        {
            await c.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{nombre}\"", c).ExecuteNonQueryAsync();
        }

        var cadena = new NpgsqlConnectionStringBuilder(bd.CadenaConexion) { Database = nombre, Pooling = false }.ConnectionString;
        return (cadena, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using var c = new NpgsqlConnection(admin.ConnectionString);
            await c.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{nombre}\" WITH (FORCE)", c).ExecuteNonQueryAsync();
        });
    }

    private static ServiceProvider Proveedor(string cadena) => new ServiceCollection().AddDatos(cadena).BuildServiceProvider();

    [Fact]
    public async Task Migrar_una_base_vacia_crea_el_esquema_y_repetir_no_cambia_nada()
    {
        var (cadena, borrar) = await BaseVacia();
        try
        {
            await using var sp = Proveedor(cadena);
            var migrador = sp.GetRequiredService<MigradorBase>();
            var verificador = sp.GetRequiredService<IVerificadorBase>();
            var antes = await verificador.ComprobarAsync();

            var primera = await migrador.MigrarAsync();
            var segunda = await migrador.MigrarAsync();
            var despues = await verificador.ComprobarAsync();

            Assert.True(antes.Disponible);
            Assert.False(antes.MigracionesAlDia);
            Assert.True(antes.MigracionesPendientes > 0);
            Assert.NotEmpty(primera);
            Assert.Empty(segunda);                                                // idempotente
            Assert.Equal(new EstadoBaseDatos(true, true, 0), despues);
        }
        finally
        {
            await borrar();
        }
    }

    [Fact]
    public async Task Migrar_de_nuevo_una_base_con_datos_no_borra_ni_modifica_ninguno()
    {
        var (cadena, borrar) = await BaseVacia();
        try
        {
            await using var sp = Proveedor(cadena);
            await sp.GetRequiredService<MigradorBase>().MigrarAsync();
            var repo = sp.GetRequiredService<IAnalisisRepositorio>();
            var guardados = new[] { Muestras.Completo(), Muestras.Parcial(), Muestras.SinPaquetes() };
            foreach (var a in guardados) await repo.GuardarAsync(a);

            await sp.GetRequiredService<MigradorBase>().MigrarAsync();            // «actualizar» de nuevo sobre datos existentes

            foreach (var a in guardados) Assert.Equal(Muestras.Json(a), Muestras.Json(await repo.ObtenerAsync(a.Id)));
        }
        finally
        {
            await borrar();
        }
    }

    [Fact]
    public async Task Dos_migradores_simultaneos_no_se_pisan_y_dejan_el_esquema_completo()
    {
        var (cadena, borrar) = await BaseVacia();
        try
        {
            await using var uno = Proveedor(cadena);
            await using var dos = Proveedor(cadena);

            var resultados = await Task.WhenAll(uno.GetRequiredService<MigradorBase>().MigrarAsync(), dos.GetRequiredService<MigradorBase>().MigrarAsync());

            Assert.Equal(new EstadoBaseDatos(true, true, 0), await uno.GetRequiredService<IVerificadorBase>().ComprobarAsync());
            Assert.Contains(resultados, r => r.Count > 0);                        // al menos uno aplicó; el otro esperó el bloqueo o no halló pendientes
        }
        finally
        {
            await borrar();
        }
    }

    [Fact]
    public async Task La_disponibilidad_distingue_base_al_dia_pendientes_y_base_inexistente_sin_lanzar_ni_filtrar_la_cadena()
    {
        var (cadena, borrar) = await BaseVacia();
        try
        {
            await using var vacia = Proveedor(cadena);
            Assert.False((await vacia.GetRequiredService<IVerificadorBase>().ComprobarAsync()).MigracionesAlDia);

            var rota = new NpgsqlConnectionStringBuilder(bd.CadenaConexion) { Database = "diagnet_no_existe_" + Guid.NewGuid().ToString("N"), Timeout = 2, Pooling = false }.ConnectionString;
            await using var sinBase = Proveedor(rota);
            var estado = await sinBase.GetRequiredService<IVerificadorBase>().ComprobarAsync();

            Assert.Equal(new EstadoBaseDatos(false, false, 0), estado);
            Assert.DoesNotContain("Password", estado.ToString());
        }
        finally
        {
            await borrar();
        }
    }
}
