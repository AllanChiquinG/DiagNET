using Npgsql;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Tests;

/// <summary>Los límites que valida la aplicación deben coincidir con las columnas reales de PostgreSQL, y los valores en el límite deben guardarse y recuperarse.</summary>
public class LimitesDeColumnasTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private static string Emojis(int cantidad) => string.Concat(Enumerable.Repeat("😀", cantidad));

    [Theory]
    [InlineData("analisis", "nombre_archivo", LimitesDatos.NombreArchivo)]
    [InlineData("analisis", "framework_objetivo", LimitesDatos.FrameworkObjetivo)]
    [InlineData("analisis", "version_reglas", LimitesDatos.VersionReglas)]
    [InlineData("dependencia", "nombre_paquete", LimitesDatos.NombrePaquete)]
    [InlineData("dependencia", "version_declarada", LimitesDatos.Version)]
    [InlineData("diagnostico", "ultima_version", LimitesDatos.Version)]
    [InlineData("metadato_libreria", "nombre_paquete", LimitesDatos.NombrePaquete)]
    [InlineData("metadato_libreria", "version", LimitesDatos.Version)]
    [InlineData("metadato_libreria", "paquete_alterno", LimitesDatos.NombrePaquete)]
    [InlineData("metadato_libreria", "rango_alterno", LimitesDatos.RangoAlterno)]
    [InlineData("recomendacion", "paquete_alternativo", LimitesDatos.NombrePaquete)]
    [InlineData("recomendacion", "version_sugerida", LimitesDatos.Version)]
    [InlineData("seleccion_paquete", "nombre_paquete", LimitesDatos.NombrePaquete)]
    [InlineData("seleccion_paquete", "ultima_version_estable", LimitesDatos.Version)]
    public async Task El_limite_de_la_aplicacion_coincide_con_la_columna_de_PostgreSQL(string tabla, string columna, int maximo)
    {
        var real = await bd.EscalarAsync<int>(
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t AND column_name = @c",
            ("t", tabla), ("c", columna));

        Assert.Equal(maximo, real);
    }

    [Fact]
    public async Task Todas_las_columnas_varchar_de_entrada_tienen_su_limite_registrado()
    {
        // Si alguien añade o cambia una columna varchar de texto libre, esta prueba obliga a registrar su límite en LimitesDatos.
        var controladas = new HashSet<string>
        {
            "analisis.nombre_archivo", "analisis.framework_objetivo", "analisis.version_reglas", "dependencia.nombre_paquete", "dependencia.version_declarada",
            "diagnostico.ultima_version", "metadato_libreria.nombre_paquete", "metadato_libreria.version", "metadato_libreria.paquete_alterno", "metadato_libreria.rango_alterno",
            "recomendacion.paquete_alternativo", "recomendacion.version_sugerida", "seleccion_paquete.nombre_paquete", "seleccion_paquete.ultima_version_estable",
            // Valores cerrados (enumeraciones con CHECK), no son texto libre:
            "analisis.estado", "dependencia.tipo_version", "diagnostico.estado", "diagnostico.origen", "recomendacion.tipo",
        };
        await using var conexion = new NpgsqlConnection(bd.CadenaConexion);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT table_name || '.' || column_name FROM information_schema.columns WHERE table_schema = 'public' AND data_type = 'character varying' AND table_name <> '__EFMigrationsHistory'", conexion);
        var columnas = new List<string>();
        await using (var lector = await cmd.ExecuteReaderAsync())
            while (await lector.ReadAsync()) columnas.Add(lector.GetString(0));

        Assert.All(columnas, c => Assert.Contains(c, controladas));
    }

    [Fact]
    public async Task Un_analisis_con_todos_los_campos_en_su_limite_exacto_se_guarda_y_se_recupera_igual()
    {
        var dependencia = new DependenciaAnalizada(
            new string('p', 200), new string('1', 100), Servidor.Negocio.Lectura.TipoVersion.Exacta, null, [],
            Muestras.Desactualizada().Diagnostico with { UltimaVersion = new string('u', 100) },
            [new Recomendacion(TipoRecomendacion.Alternativa, new string('q', 200), new string('s', 100), "j", [], [], false)]);
        var analisis = Muestras.SinPaquetes() with
        {
            NombreArchivo = new string('a', 253) + ".csproj",
            VersionReglas = new string('r', 30),
            Dependencias = [dependencia],
            Advertencias = [],
        };

        await bd.Analisis.GuardarAsync(analisis);

        Assert.Equal(Muestras.Json(analisis), Muestras.Json(await bd.Analisis.ObtenerAsync(analisis.Id)));
    }

    [Fact]
    public async Task Los_nombres_con_emojis_en_el_limite_se_guardan_porque_PostgreSQL_cuenta_caracteres_y_no_unidades_UTF16()
    {
        var analisis = Muestras.SinPaquetes() with
        {
            NombreArchivo = Emojis(253) + ".csproj",
            Dependencias = [Muestras.Actualizada(Emojis(200), "1.0.0")],
            Advertencias = [],
        };

        await bd.Analisis.GuardarAsync(analisis);

        var leido = (await bd.Analisis.ObtenerAsync(analisis.Id))!;
        Assert.Equal(analisis.NombreArchivo, leido.NombreArchivo);
        Assert.Equal(Emojis(200), leido.Dependencias.Single().Nombre);
    }

    [Fact]
    public async Task Los_frameworks_declarados_se_conservan_exactos_aunque_el_resumen_supere_100_caracteres()
    {
        var muchos = Enumerable.Range(1, 30).Select(i => $"net{i}.0-windows10.0.19041").ToList(); // el resumen unido pasa de 100 caracteres
        var analisis = Muestras.SinPaquetes() with { FrameworksDeclarados = muchos };
        Assert.True(string.Join(';', muchos).Length > 100);

        await bd.Analisis.GuardarAsync(analisis);

        Assert.Equal(muchos, (await bd.Analisis.ObtenerAsync(analisis.Id))!.FrameworksDeclarados); // lista exacta (jsonb)
        var resumen = await bd.EscalarAsync<string>("SELECT framework_objetivo FROM analisis WHERE id = @id", ("id", analisis.Id));
        Assert.Equal(100, resumen.Length);
        Assert.StartsWith("net1.0-windows10.0.19041;net2.0", resumen);
    }

    [Theory]
    [InlineData("nombre", 201)]
    [InlineData("version", 101)]
    [InlineData("alterno", 201)]
    [InlineData("rango", 101)]
    public async Task Un_metadato_que_excede_un_limite_se_rechaza_con_error_claro_y_no_se_guarda(string campo, int longitud)
    {
        var nombre = $"Ejemplo.Largo.{Guid.NewGuid():N}";
        var valor = new string('x', longitud);
        var m = campo switch
        {
            "nombre" => Muestras.Metadato(valor),
            "version" => Muestras.Metadato(nombre, valor),
            "alterno" => Muestras.Metadato(nombre) with { PaqueteAlterno = valor },
            _ => Muestras.Metadato(nombre) with { PaqueteAlterno = "Ejemplo.Nuevo", RangoAlterno = valor },
        };
        var antes = await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria");

        await Assert.ThrowsAsync<ValorExcedeLimiteException>(() => bd.Metadatos.GuardarAsync(m));

        Assert.Equal(antes, await bd.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria"));
    }

    [Fact]
    public async Task Un_metadato_en_los_limites_exactos_se_guarda()
    {
        var nombre = new string('m', 200);
        var version = "1.0.0-" + new string('b', 94); // 100 caracteres y ya normalizada
        await bd.Metadatos.GuardarAsync(Muestras.Metadato(nombre, version) with
        {
            Licencia = "https://example.org/" + new string('l', 3000), // una URL larga: la columna es text
            PaqueteAlterno = new string('a', 200),
            RangoAlterno = new string('r', 100),
        });

        var leido = await bd.Metadatos.BuscarAsync(nombre, version);

        Assert.Equal(3020, leido!.Licencia!.Length);
        Assert.Equal(200, leido.PaqueteAlterno!.Length);
        Assert.Equal(100, leido.RangoAlterno!.Length);
    }

    [Fact]
    public async Task Si_la_normalizacion_alarga_la_version_mas_alla_del_limite_se_rechaza_con_error_claro()
    {
        var version = new string('1', 99); // 99 caracteres, pero normalizada es «111…1.0.0» (103)

        var ex = await Assert.ThrowsAsync<ValorExcedeLimiteException>(() => bd.Metadatos.GuardarAsync(Muestras.Metadato($"Ejemplo.Norm.{Guid.NewGuid():N}", version)));

        Assert.Contains("normalizada", ex.Campo);
        Assert.Equal(103, ex.Longitud);
    }

    [Fact]
    public async Task Una_seleccion_que_excede_un_limite_se_rechaza_con_error_claro()
    {
        var s = new SeleccionPaquete(new string('n', 201), "1.0.0", new Dictionary<string, string>(), true, Muestras.Base);

        var ex = await Assert.ThrowsAsync<ValorExcedeLimiteException>(() => bd.Metadatos.GuardarSeleccionAsync(s));

        Assert.Equal("seleccion_paquete.nombre_paquete", ex.Campo);
    }
}
