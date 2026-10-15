using Microsoft.Extensions.DependencyInjection;
using Servidor.Integracion;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.ContratoNuGet.Tests;

/// <summary>
/// COMPROBACIONES REALES contra nuget.org (necesitan internet). NO forman parte de DiagNET.slnx ni de «dotnet test DiagNET.slnx»:
/// se ejecutan a mano con  dotnet test tests/Servidor.Integracion.ContratoNuGet.Tests  para confirmar que la API real sigue entregando los campos
/// que el módulo espera. No afirman versiones concretas actuales (cambian): comprueban estructura y propiedades estables.
/// Usan el registro de dependencias real (HttpClient con descompresión y sin redirecciones automáticas).
/// </summary>
[Trait("Categoria", "NuGetReal")]
public class ContratoNuGetRealTests : IDisposable
{
    private static readonly string[] Tfms = ["net8.0", "net10.0"];
    private readonly ServiceProvider _servicios;
    private readonly IClienteNuGet _nuget;

    public ContratoNuGetRealTests()
    {
        _servicios = new ServiceCollection().AddLogging().AddNuGet(new OpcionesNuGet()).BuildServiceProvider();
        _nuget = _servicios.GetRequiredService<IClienteNuGet>();
    }

    public void Dispose() => _servicios.Dispose();

    private async Task<ResultadoNuGet<InformacionPaquete>> Leer(string paquete, string? version)
    {
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(120));
        return await _nuget.ObtenerPaqueteAsync(new ConsultaPaquete(paquete, version, Tfms), presupuesto);
    }

    [Fact]
    public async Task Paquete_conocido_con_muchas_versiones_se_lee_con_los_campos_esperados()
    {
        var r = await Leer("Newtonsoft.Json", "13.0.3");

        Assert.True(r.EsCorrecto, r.Mensaje);
        var info = r.Valor!;
        Assert.Equal(EstadoVersionDeclarada.Publicada, info.EstadoDeclarada);
        Assert.Equal("13.0.3", info.Declarada!.Version);
        Assert.NotNull(info.Declarada.Publicada);
        Assert.False(string.IsNullOrWhiteSpace(info.Declarada.Licencia));
        Assert.NotEmpty(info.Declarada.GruposFramework);
        Assert.NotNull(info.UltimaEstable);
        Assert.False(info.UltimaEstable.EsPrerelease);
        Assert.All(Tfms, t => Assert.NotNull(info.UltimaEstableCompatiblePorTfm[t])); // Newtonsoft.Json soporta .NET 8 y .NET 10
        Assert.Contains(info.CompatibilidadDeclarada, c => c.Tfm == "net8.0" && c.Resultado == ResultadoCompatibilidad.Compatible);
        Assert.True(info.SeleccionCompleta);
        Assert.True(info.Descargas > 0, info.DescargasNota);
        Assert.True(r.SolicitudesHttp <= 6, $"solicitudes: {r.SolicitudesHttp}"); // índice + registro + páginas necesarias + descargas
    }

    [Fact]
    public async Task Paquete_con_paginas_sin_hojas_incluidas_se_pagina_correctamente()
    {
        var r = await Leer("Microsoft.Extensions.Logging.Abstractions", "8.0.0");

        Assert.True(r.EsCorrecto, r.Mensaje);
        var info = r.Valor!;
        Assert.True(info.PaginasTotales >= 2, $"páginas totales: {info.PaginasTotales}");
        Assert.Equal(EstadoVersionDeclarada.Publicada, info.EstadoDeclarada);
        Assert.NotNull(info.UltimaEstable);
        Assert.True(info.SeleccionCompleta);
        Assert.True(info.VersionesLeidas > 0);
    }

    [Fact]
    public async Task Paquete_deprecado_expone_la_deprecacion_y_su_reemplazo_con_identificador_valido()
    {
        var r = await Leer("WindowsAzure.Storage", "9.3.3");

        Assert.True(r.EsCorrecto, r.Mensaje);
        var dep = r.Valor!.Declarada!.Deprecacion;
        Assert.NotNull(dep);
        Assert.NotEmpty(dep.Razones);
        Assert.False(string.IsNullOrWhiteSpace(dep.Mensaje)); // el mensaje real de la deprecación llega y se conserva
        if (dep.PaqueteAlterno is not null)
            Assert.True(new ValidadorDirecciones(new OpcionesNuGet()).EsIdentificadorValido(dep.PaqueteAlterno), dep.PaqueteAlterno);
    }

    [Fact]
    public async Task Una_licencia_real_que_es_una_URL_se_distingue_de_una_expresion()
    {
        var r = await Leer("WindowsAzure.Storage", "9.3.3");

        var d = r.Valor!.Declarada!;
        Assert.NotNull(d.Licencia);
        Assert.NotNull(d.TipoLicencia); // el tipo lo entrega NuGet (licenseExpression o licenseUrl); no se deduce del texto
        Assert.Equal(TipoLicencia.Url, d.TipoLicencia); // este paquete declara licenseUrl
    }

    [Fact]
    public async Task Un_paquete_inexistente_es_NoEncontrado()
    {
        var r = await Leer("DiagNET.Paquete.Que.No.Existe." + Guid.NewGuid().ToString("N")[..8], "1.0.0");

        Assert.Equal(EstadoConsulta.NoEncontrado, r.Estado);
    }

    [Fact]
    public async Task Una_version_inexistente_de_un_paquete_real_es_NoPublicada()
    {
        var r = await Leer("Newtonsoft.Json", "0.0.0-diagnet-no-existe");

        Assert.True(r.EsCorrecto, r.Mensaje);
        Assert.Equal(EstadoVersionDeclarada.NoPublicada, r.Valor!.EstadoDeclarada);
        Assert.Null(r.Valor.Declarada);
        Assert.NotNull(r.Valor.UltimaEstable);
    }

    [Fact]
    public async Task La_busqueda_real_devuelve_candidatos_con_identificadores_validos_y_descargas_no_negativas()
    {
        var r = await _nuget.BuscarCandidatosAsync("json", "Newtonsoft.Json", 5);

        Assert.True(r.EsCorrecto, r.Mensaje);
        var candidatos = r.Valor!;
        Assert.NotEmpty(candidatos);
        Assert.True(candidatos.Count <= 5);
        var validador = new ValidadorDirecciones(new OpcionesNuGet());
        Assert.All(candidatos, c =>
        {
            Assert.True(validador.EsIdentificadorValido(c.Id), c.Id);
            Assert.NotEqual("Newtonsoft.Json", c.Id, StringComparer.OrdinalIgnoreCase);
            Assert.True(c.Descargas is null or >= 0);
        });
    }

    [Fact]
    public async Task Las_descargas_de_un_paquete_conocido_son_un_numero_positivo()
    {
        var r = await _nuget.ObtenerDescargasAsync("Newtonsoft.Json");

        Assert.True(r.EsCorrecto, r.Mensaje);
        Assert.True(r.Valor > 0);
    }

    [Fact]
    public async Task Ningun_paquete_leido_supera_el_limite_oficial_de_100_caracteres_en_el_identificador_ni_64_en_la_version()
    {
        // Contraste con la documentación oficial (nuspec: id ≤ 100 y version ≤ 64 al publicar en nuget.org) sobre datos reales.
        var r = await Leer("Newtonsoft.Json", "13.0.3");

        Assert.True(r.Valor!.Paquete.Length <= 100);
        Assert.True(r.Valor.Declarada!.VersionOriginal.Length <= 64);
        Assert.True(r.Valor.UltimaEstable!.VersionOriginal.Length <= 64);
    }

    [Fact]
    public async Task El_disyuntor_y_los_limites_no_interfieren_con_varias_consultas_reales_seguidas()
    {
        var paquetes = new[] { "Newtonsoft.Json", "Serilog", "Dapper" };

        var resultados = await Task.WhenAll(paquetes.Select(p => Leer(p, null)));

        Assert.All(resultados, r => Assert.True(r.EsCorrecto, r.Mensaje));
        Assert.All(resultados, r => Assert.NotNull(r.Valor!.UltimaEstable));
    }
}
