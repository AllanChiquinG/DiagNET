using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Servidor.Datos;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Tests;

/// <summary>
/// Crea una base PostgreSQL REAL y aislada (diagnet_test_xxx) para cada clase de prueba, aplica las migraciones de EF Core y la borra al terminar.
/// Nunca toca diagnet_nueva ni otras bases. Usa el rol diagnet_pruebas (solo puede crear sus propias bases).
/// Conexión: variable DIAGNET_TEST_CONNECTION o tests/Servidor.Datos.Tests/appsettings.Local.json (ConnectionStrings:Pruebas), ambos fuera de Git.
/// </summary>
public sealed class BaseDeDatosFixture : IAsyncLifetime
{
    private NpgsqlConnectionStringBuilder _administracion = null!;
    private ServiceProvider _servicios = null!;

    public string NombreBase { get; } = "diagnet_test_" + Guid.NewGuid().ToString("N");
    public string CadenaConexion { get; private set; } = "";
    public IDbContextFactory<AppDbContext> Fabrica => _servicios.GetRequiredService<IDbContextFactory<AppDbContext>>();
    public IAnalisisRepositorio Analisis => _servicios.GetRequiredService<IAnalisisRepositorio>();
    public IMetadatoRepositorio Metadatos => _servicios.GetRequiredService<IMetadatoRepositorio>();
    public Servidor.Negocio.Operacion.IContadorPurga Contador => _servicios.GetRequiredService<Servidor.Negocio.Operacion.IContadorPurga>();

    public async Task InitializeAsync()
    {
        _administracion = new NpgsqlConnectionStringBuilder(LeerCadenaDePruebas());
        await EjecutarAdminAsync($"CREATE DATABASE \"{NombreBase}\"");

        var cadena = new NpgsqlConnectionStringBuilder(_administracion.ConnectionString) { Database = NombreBase, Pooling = true, MaxPoolSize = 30 };
        CadenaConexion = cadena.ConnectionString;
        _servicios = Construir(CadenaConexion);

        await using var db = await Fabrica.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    /// <summary>Simula un reinicio de la aplicación: servicios y conexiones nuevos contra la misma base.</summary>
    public ServiceProvider Reiniciar()
    {
        NpgsqlConnection.ClearAllPools();
        return Construir(CadenaConexion);
    }

    private static ServiceProvider Construir(string cadena)
    {
        var servicios = new ServiceCollection();
        servicios.AddDatos(cadena);
        return servicios.BuildServiceProvider();
    }

    public async Task<T> EscalarAsync<T>(string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conexion = new NpgsqlConnection(CadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros) comando.Parameters.AddWithValue(nombre, valor);
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is null or DBNull ? default! : (T)Convert.ChangeType(resultado, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    public async Task EjecutarAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(CadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_servicios is not null) await _servicios.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (_administracion is not null) await EjecutarAdminAsync($"DROP DATABASE IF EXISTS \"{NombreBase}\" WITH (FORCE)");
    }

    private async Task EjecutarAdminAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_administracion.ConnectionString);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    private static string LeerCadenaDePruebas()
    {
        var entorno = Environment.GetEnvironmentVariable("DIAGNET_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(entorno)) return entorno;

        var archivo = Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        if (File.Exists(archivo))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(archivo));
            if (json.RootElement.TryGetProperty("ConnectionStrings", out var cs) && cs.TryGetProperty("Pruebas", out var c) && c.GetString() is { Length: > 0 } cadena)
                return cadena;
        }

        throw new InvalidOperationException(
            "Faltan las credenciales de las pruebas de PostgreSQL. Ejecuta deploy/local/crear_base_local.ps1 (crea tests/Servidor.Datos.Tests/appsettings.Local.json) " +
            "o define DIAGNET_TEST_CONNECTION. Estas pruebas NO se omiten: necesitan PostgreSQL real.");
    }
}
