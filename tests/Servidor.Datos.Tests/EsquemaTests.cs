using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Servidor.Datos.Tests;

/// <summary>Comprueba en PostgreSQL real que las migraciones crean el esquema de referencia (schema.sql) más las decisiones DEC-14 y DEC-11.</summary>
public class EsquemaTests(BaseDeDatosFixture bd) : IClassFixture<BaseDeDatosFixture>
{
    private async Task<List<string>> ColumnasAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(bd.CadenaConexion);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT column_name || ':' || CASE WHEN data_type = 'character varying' THEN 'varchar(' || character_maximum_length || ')' ELSE data_type END
                   || ':' || CASE WHEN is_nullable = 'YES' THEN 'null' ELSE 'notnull' END
            FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position
            """, conexion);
        cmd.Parameters.AddWithValue("t", tabla);
        var lista = new List<string>();
        await using var lector = await cmd.ExecuteReaderAsync();
        while (await lector.ReadAsync()) lista.Add(lector.GetString(0));
        return lista;
    }

    private async Task<List<string>> ConsultaAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(bd.CadenaConexion);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        var lista = new List<string>();
        await using var lector = await cmd.ExecuteReaderAsync();
        while (await lector.ReadAsync()) lista.Add(lector.GetString(0));
        return lista;
    }

    [Fact]
    public async Task Existen_las_cinco_tablas_del_modelo_mas_la_cache_de_seleccion()
    {
        var tablas = await ConsultaAsync("SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY 1");

        Assert.Equal(["analisis", "dependencia", "diagnostico", "metadato_libreria", "recomendacion", "seleccion_paquete"], tablas);
    }

    [Theory]
    [InlineData("analisis", new[]
    {
        "id:uuid:notnull", "nombre_archivo:varchar(260):notnull", "framework_objetivo:varchar(100):notnull", "fecha_analisis:timestamp with time zone:notnull",
        "estado:varchar(20):notnull", "total_dependencias:integer:notnull",
        // DEC-14
        "frameworks_declarados:jsonb:notnull", "frameworks_evaluados:jsonb:notnull", "advertencias:jsonb:notnull", "version_reglas:varchar(30):notnull",
    })]
    [InlineData("dependencia", new[]
    {
        "id:bigint:notnull", "analisis_id:uuid:notnull", "nombre_paquete:varchar(200):notnull", "version_declarada:varchar(100):null",
        "tipo_version:varchar(20):notnull", "condicion:text:null", "advertencias:jsonb:notnull",
    })]
    [InlineData("metadato_libreria", new[]
    {
        "id:bigint:notnull", "nombre_paquete:varchar(200):notnull", "version:varchar(100):notnull", "fecha_publicacion:timestamp with time zone:null",
        "descargas:bigint:null", "licencia:text:null", "frameworks_objetivo:jsonb:null", "compatible_aot:boolean:null", "deprecado:boolean:null",
        "fecha_consulta:timestamp with time zone:notnull",
        // E4: reemplazo declarado por el autor al deprecar (sobrevive a la caché)
        "paquete_alterno:varchar(200):null", "rango_alterno:varchar(100):null",
    })]
    [InlineData("diagnostico", new[]
    {
        "id:bigint:notnull", "dependencia_id:bigint:notnull", "metadato_id:bigint:null", "estado:varchar(30):notnull", "ultima_version:varchar(100):null",
        "indicadores:jsonb:notnull", "motivos:text:notnull",
        "fecha_consulta:timestamp with time zone:null", // DEC-14: nunca se inventa una fecha
        "origen:varchar(20):notnull", "hallazgos:jsonb:notnull", "evaluacion_por_tfm:jsonb:notnull",
    })]
    [InlineData("recomendacion", new[]
    {
        "id:bigint:notnull", "diagnostico_id:bigint:notnull", "tipo:varchar(30):notnull", "paquete_alternativo:varchar(200):notnull",
        "version_sugerida:varchar(100):null", "justificacion:text:notnull",
        "evidencia:jsonb:notnull", "limitaciones:jsonb:notnull", "posibles_cambios_codigo:boolean:notnull",
    })]
    [InlineData("seleccion_paquete", new[]
    {
        "id:bigint:notnull", "nombre_paquete:varchar(200):notnull", "ultima_version_estable:varchar(100):null", "ultima_compatible_por_tfm:jsonb:notnull",
        "seleccion_completa:boolean:notnull", "fecha_consulta:timestamp with time zone:notnull",
    })]
    public async Task Cada_tabla_tiene_las_columnas_tipos_y_nulabilidad_esperados(string tabla, string[] esperadas) =>
        Assert.Equal(esperadas.Order(), (await ColumnasAsync(tabla)).Order()); // el orden físico lo decide la migración; se compara el conjunto

    [Fact]
    public async Task Los_CHECK_las_llaves_foraneas_y_las_cascadas_coinciden_con_el_modelo()
    {
        var checks = await ConsultaAsync("SELECT conname FROM pg_constraint WHERE contype = 'c' AND connamespace = 'public'::regnamespace ORDER BY 1");
        Assert.Equal(
            ["ck_analisis_estado", "ck_analisis_total", "ck_dependencia_tipo_version", "ck_diagnostico_estado", "ck_diagnostico_origen", "ck_metadato_descargas", "ck_recomendacion_tipo"],
            checks);

        var foraneas = await ConsultaAsync(
            "SELECT conname || ':' || confdeltype::text FROM pg_constraint WHERE contype = 'f' AND connamespace = 'public'::regnamespace ORDER BY 1");
        Assert.Equal(
            ["fk_dependencia_analisis:c", "fk_diagnostico_dependencia:c", "fk_diagnostico_metadato:n", "fk_recomendacion_diagnostico:c"], // c = CASCADE, n = SET NULL
            foraneas);
    }

    [Fact]
    public async Task Los_indices_incluyen_las_unicidades_por_identificador_sin_distinguir_mayusculas()
    {
        var indices = await ConsultaAsync("SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND indexname NOT ILIKE 'pk%' ORDER BY 1");

        Assert.Equal(
            [
                "ix_analisis_fecha", "ix_dependencia_analisis", "ix_diagnostico_metadato", "ix_metadato_fecha_consulta", "ix_recomendacion_diagnostico",
                "ix_seleccion_fecha_consulta", "ux_diagnostico_dependencia", "ux_metadato_paquete_version", "ux_recomendacion", "ux_seleccion_paquete_nombre",
            ],
            indices);
        var definicion = await ConsultaAsync("SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_metadato_paquete_version'");
        Assert.Contains("UNIQUE", definicion.Single());
        Assert.Contains("lower((nombre_paquete)::text)", definicion.Single());
    }

    [Fact]
    public async Task Las_tablas_y_columnas_llevan_comentarios_para_documentar_la_base_en_pgAdmin()
    {
        var sinComentario = await ConsultaAsync(
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory' AND obj_description(c.oid, 'pg_class') IS NULL
            """);
        Assert.Empty(sinComentario);
    }

    [Fact]
    public async Task Los_valores_por_defecto_son_los_del_modelo()
    {
        await bd.EjecutarAsync("INSERT INTO analisis (nombre_archivo, framework_objetivo, estado) VALUES ('d.csproj','net8.0','completo')");

        Assert.Equal(1, await bd.EscalarAsync<int>(
            "SELECT count(*) FROM analisis WHERE nombre_archivo = 'd.csproj' AND id IS NOT NULL AND fecha_analisis > now() - interval '1 minute' AND total_dependencias = 0"));
    }

    [Fact]
    public async Task El_modelo_de_EF_Core_no_tiene_cambios_sin_migracion()
    {
        await using var db = await bd.Fabrica.CreateDbContextAsync();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Las_migraciones_se_pueden_revertir_y_volver_a_aplicar()
    {
        await using var propio = new BaseDeDatosFixture();
        await propio.InitializeAsync();
        try
        {
            await using var db = await propio.Fabrica.CreateDbContextAsync();
            var migraciones = db.Database.GetMigrations().ToList();
            Assert.Equal(3, migraciones.Count);
            var migrador = db.GetService<IMigrator>();

            await migrador.MigrateAsync(migraciones[0]); // vuelve al esquema inicial (equivalente a schema.sql)
            Assert.Equal(["analisis", "dependencia", "diagnostico", "metadato_libreria", "recomendacion"],
                await ConsultaEnAsync(propio, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY 1"));
            Assert.DoesNotContain("seleccion_paquete", await ConsultaEnAsync(propio, "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"));
            Assert.Equal(["analisis", "dependencia", "diagnostico", "metadato_libreria", "recomendacion"],
                (await ConsultaEnAsync(propio, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' ORDER BY 1")));

            await migrador.MigrateAsync(); // y vuelve a aplicarse sin errores
            Assert.Contains("seleccion_paquete", await ConsultaEnAsync(propio, "SELECT tablename FROM pg_tables WHERE schemaname = 'public'"));
        }
        finally
        {
            await propio.DisposeAsync();
        }
    }

    [Fact]
    public async Task La_migracion_de_E4_conserva_los_datos_existentes_y_agrega_las_columnas_nuevas()
    {
        await using var propio = new BaseDeDatosFixture();
        await propio.InitializeAsync();
        try
        {
            await using var db = await propio.Fabrica.CreateDbContextAsync();
            var migraciones = db.Database.GetMigrations().ToList();
            var migrador = db.GetService<IMigrator>();

            await migrador.MigrateAsync(migraciones[1]); // esquema anterior: licencia varchar(200), sin columnas de alterno
            await propio.EjecutarAsync(
                "INSERT INTO metadato_libreria (nombre_paquete, version, descargas, licencia, frameworks_objetivo, deprecado, fecha_consulta) VALUES " +
                "('Ejemplo.Antiguo', '1.0.0', 42, 'MIT', '[\"net8.0\"]', true, '2026-10-01T00:00:00Z')," +
                "('Ejemplo.SinDatos', '2.0.0', NULL, NULL, NULL, NULL, '2026-10-02T00:00:00Z')");
            await propio.EjecutarAsync("INSERT INTO seleccion_paquete (nombre_paquete, ultima_version_estable, seleccion_completa) VALUES ('Ejemplo.Antiguo', '1.0.0', true)");

            await migrador.MigrateAsync(); // aplica AmpliarMetadatoLibreria sobre datos reales

            Assert.Equal(2, await propio.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria"));
            Assert.Equal("MIT", await propio.EscalarAsync<string>("SELECT licencia FROM metadato_libreria WHERE nombre_paquete = 'Ejemplo.Antiguo'"));
            Assert.Equal(42L, await propio.EscalarAsync<long>("SELECT descargas FROM metadato_libreria WHERE nombre_paquete = 'Ejemplo.Antiguo'"));
            Assert.True(await propio.EscalarAsync<bool>("SELECT deprecado FROM metadato_libreria WHERE nombre_paquete = 'Ejemplo.Antiguo'"));
            Assert.Equal(1, await propio.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE nombre_paquete = 'Ejemplo.SinDatos' AND licencia IS NULL AND deprecado IS NULL AND descargas IS NULL"));
            Assert.Equal(2, await propio.EscalarAsync<int>("SELECT count(*) FROM metadato_libreria WHERE paquete_alterno IS NULL AND rango_alterno IS NULL"));
            Assert.Equal(1, await propio.EscalarAsync<int>("SELECT count(*) FROM seleccion_paquete"));
            // ya admite una licencia larga (una URL de 1000 caracteres)
            await propio.EjecutarAsync($"UPDATE metadato_libreria SET licencia = '{new string('u', 1000)}' WHERE nombre_paquete = 'Ejemplo.Antiguo'");
            Assert.Equal(1000, await propio.EscalarAsync<int>("SELECT char_length(licencia) FROM metadato_libreria WHERE nombre_paquete = 'Ejemplo.Antiguo'"));
        }
        finally
        {
            await propio.DisposeAsync();
        }
    }

    private static async Task<List<string>> ConsultaEnAsync(BaseDeDatosFixture fixture, string sql)
    {
        await using var conexion = new NpgsqlConnection(fixture.CadenaConexion);
        await conexion.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexion);
        var lista = new List<string>();
        await using var lector = await cmd.ExecuteReaderAsync();
        while (await lector.ReadAsync()) lista.Add(lector.GetString(0));
        return lista;
    }
}
