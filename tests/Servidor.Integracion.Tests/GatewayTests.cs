using System.Net;
using Servidor.Integracion.NuGet;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.Tests;

public class GatewayPaginacionTests
{
    private static readonly string[] Net8 = ["net8.0"];

    private static ConsultaPaquete Consulta(string? declarada = null, params string[] tfms) =>
        new("Ejemplo.Paquete", declarada, tfms.Length == 0 ? Net8 : tfms);

    private static (NuGetGateway Gw, NuGetSimulado Nuget, Microsoft.Extensions.Time.Testing.FakeTimeProvider Tiempo) Crear(
        NuGetSimulado nuget, Action<OpcionesNuGet>? ajustar = null)
    {
        var (gw, tiempo, _) = NuGetSimulado.Crear(nuget, o => { o.ConsultarDescargas = false; ajustar?.Invoke(o); });
        return (gw, nuget, tiempo);
    }

    private static int Registros(NuGetSimulado n) => n.Contar(u => u.EndsWith("/ejemplo.paquete/index.json"));

    private static int Paginas(NuGetSimulado n) => n.Contar(u => u.Contains("/ejemplo.paquete/page/"));

    /// <summary>Páginas ascendentes: la página p contiene las versiones (p+1).0.0 … (p+1).{porPagina-1}.0.</summary>
    private static (string[][] Hojas, string[][] Versiones) Generar(int paginas, int porPagina, Func<string, string>? hoja = null)
    {
        hoja ??= v => Registro.Hoja(v);
        var versiones = Enumerable.Range(0, paginas).Select(p => Enumerable.Range(0, porPagina).Select(i => $"{p + 1}.{i}.0").ToArray()).ToArray();
        return (versiones.Select(vs => vs.Select(hoja).ToArray()).ToArray(), versiones);
    }

    // ---------- paquete pequeño con hojas incluidas ----------

    [Fact]
    public async Task Paquete_de_12_versiones_con_hojas_incluidas_hace_una_consulta_al_registro_y_devuelve_ultima_y_declarada()
    {
        var hojas = Enumerable.Range(0, 12).Select(i => Registro.Hoja($"1.{i}.0")).ToArray();
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", hojas);
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta("1.3.0"));

        Assert.True(r.EsCorrecto);
        Assert.Equal(1, Registros(nuget));
        Assert.Equal(0, Paginas(nuget));
        Assert.Equal(2, r.SolicitudesHttp); // índice de servicios + registro
        var info = r.Valor!;
        Assert.Equal("1.11.0", info.UltimaEstable!.Version);
        Assert.Equal("1.11.0", info.UltimaEstableCompatiblePorTfm["net8.0"]!.Version);
        Assert.Equal(EstadoVersionDeclarada.Publicada, info.EstadoDeclarada);
        Assert.Equal("1.3.0", info.Declarada!.Version);
        Assert.True(info.SeleccionCompleta);
        Assert.Equal(12, info.VersionesLeidas);
    }

    [Fact]
    public async Task La_ultima_estable_y_la_ultima_compatible_se_distinguen()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete",
            Registro.Hoja("1.0.0", grupos: ["net8.0"]), Registro.Hoja("2.0.0", grupos: ["net8.0"]), Registro.Hoja("3.0.0", grupos: ["net10.0"]));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.0.0", "net8.0", "net10.0"))).Valor!;

        Assert.Equal("3.0.0", info.UltimaEstable!.Version);
        Assert.Equal("2.0.0", info.UltimaEstableCompatiblePorTfm["net8.0"]!.Version);
        Assert.Equal("3.0.0", info.UltimaEstableCompatiblePorTfm["net10.0"]!.Version);
    }

    [Fact]
    public async Task La_compatibilidad_de_la_declarada_se_infiere_por_cada_TFM()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0", grupos: ["net8.0"]));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.0.0", "net8.0", "net48"))).Valor!;

        Assert.Equal([ResultadoCompatibilidad.Compatible, ResultadoCompatibilidad.Incompatible], info.CompatibilidadDeclarada.Select(c => c.Resultado));
        Assert.All(info.CompatibilidadDeclarada, c => Assert.Equal("inferida_dependency_groups", c.Base));
    }

    [Fact]
    public async Task Un_paquete_sin_grupos_deja_la_compatibilidad_no_determinada_y_sin_frameworks()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0", incluirGrupos: false));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.0.0"))).Valor!;

        Assert.Equal(ResultadoCompatibilidad.NoDeterminada, info.CompatibilidadDeclarada.Single().Resultado);
        Assert.Empty(info.Declarada!.GruposFramework);
        Assert.Null(info.UltimaEstableCompatiblePorTfm["net8.0"]); // no se inventa compatibilidad
    }

    [Fact]
    public async Task Un_paquete_deprecado_con_paquete_alterno_lo_expone_con_su_rango()
    {
        var dep = Registro.Deprecacion("Reemplazado.", alterno: "Paquete.Nuevo", rango: "[2.0.0, )");
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0", deprecacion: dep), Registro.Hoja("1.1.0", deprecacion: dep));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.0.0"))).Valor!;

        Assert.Equal("Paquete.Nuevo", info.Declarada!.Deprecacion!.PaqueteAlterno);
        Assert.Equal("[2.0.0, )", info.Declarada.Deprecacion.RangoVersionAlterno);
    }

    [Fact]
    public async Task Sin_deprecacion_en_un_registro_leido_el_paquete_no_esta_deprecado_pero_sin_registro_no_se_afirma_nada()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.0.0"))).Valor!;

        Assert.Null(info.Declarada!.Deprecacion);
    }

    // ---------- paginación ----------

    [Fact]
    public async Task Paquete_de_200_versiones_con_paginas_aparte_hace_1_consulta_al_registro_y_como_maximo_2_paginas()
    {
        var (hojas, versiones) = Generar(4, 50); // 200 versiones
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("4.10.0"))).Valor!;

        Assert.Equal(1, Registros(nuget));
        Assert.Equal(1, Paginas(nuget)); // la declarada y la última están en la misma página (la más nueva)
        Assert.Equal("4.49.0", info.UltimaEstable!.Version);
        Assert.Equal("4.10.0", info.Declarada!.Version);
        Assert.True(info.SeleccionCompleta);
        Assert.Equal(1, info.PaginasLeidas);
        Assert.Equal(4, info.PaginasTotales);
    }

    [Fact]
    public async Task Si_la_declarada_esta_en_una_pagina_antigua_se_descargan_la_ultima_y_la_de_la_declarada_y_nada_mas()
    {
        var (hojas, versiones) = Generar(4, 50);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.5.0"))).Valor!;

        Assert.Equal(2, Paginas(nuget));
        Assert.Equal(1, nuget.Llamadas(Registro.UrlPagina(3)));
        Assert.Equal(1, nuget.Llamadas(Registro.UrlPagina(0)));
        Assert.Equal("1.5.0", info.Declarada!.Version);
        Assert.Equal("4.49.0", info.UltimaEstable!.Version);
    }

    [Fact]
    public async Task Una_ultima_pagina_con_solo_preliminares_no_detiene_la_busqueda_de_la_ultima_estable()
    {
        var (hojas, versiones) = Generar(3, 5, v => v.StartsWith("3.") ? Registro.Hoja(v + "-beta") : Registro.Hoja(v));
        // Las versiones de la página 3 pasan a ser preliminares: 3.0.0-beta … (el rango de la página usa las versiones sin etiqueta, que ordenan por encima)
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Equal("2.4.0", info.UltimaEstable!.Version);
        Assert.Equal("3.4.0-beta", info.UltimaPrerelease!.Version);
        Assert.Equal(2, Paginas(nuget)); // la última (solo preliminares) y la anterior
        Assert.True(info.SeleccionCompleta);
    }

    [Fact]
    public async Task Una_ultima_pagina_con_solo_versiones_no_listadas_no_detiene_la_busqueda()
    {
        var (hojas, versiones) = Generar(2, 5, v => v.StartsWith("2.") ? Registro.Hoja(v, listada: false) : Registro.Hoja(v));
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Equal("1.4.0", info.UltimaEstable!.Version);
        Assert.Equal(2, Paginas(nuget));
    }

    [Fact]
    public async Task Una_ultima_pagina_con_estables_incompatibles_sigue_buscando_la_ultima_compatible_en_paginas_anteriores()
    {
        var (hojas, versiones) = Generar(3, 5, v => v.StartsWith("3.") ? Registro.Hoja(v, grupos: [".NETFramework4.8"]) : Registro.Hoja(v, grupos: ["net8.0"]));
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Equal("3.4.0", info.UltimaEstable!.Version);                       // la más nueva...
        Assert.Equal("2.4.0", info.UltimaEstableCompatiblePorTfm["net8.0"]!.Version); // ...no es la última compatible
        Assert.Equal(2, Paginas(nuget));
        Assert.True(info.SeleccionCompleta);
    }

    [Fact]
    public async Task Sin_ninguna_estable_se_leen_todas_las_paginas_y_la_ultima_estable_es_nula()
    {
        var (hojas, versiones) = Generar(3, 4, v => Registro.Hoja(v + "-rc"));
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Null(info.UltimaEstable);
        Assert.Equal(3, Paginas(nuget));
        Assert.True(info.SeleccionCompleta); // se recorrió todo: «no hay estable» es definitivo
        Assert.NotNull(info.UltimaPrerelease);
    }

    [Fact]
    public async Task Si_el_presupuesto_de_paginas_se_agota_la_seleccion_es_incompleta_con_advertencia_y_no_se_afirma_un_maximo_global()
    {
        var (hojas, versiones) = Generar(6, 4, v => Registro.Hoja(v + "-rc")); // 6 páginas solo de preliminares
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget, o => o.MaxPaginas = 2);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        var info = r.Valor!;
        Assert.Equal(2, Paginas(nuget));
        Assert.False(info.SeleccionCompleta);
        Assert.Null(info.UltimaEstable); // no se inventa una estable
        Assert.Contains(r.Advertencias, a => a.Codigo == "seleccion_incompleta");
        Assert.Equal(2, info.PaginasLeidas);
        Assert.Equal(6, info.PaginasTotales);
    }

    [Fact]
    public async Task Con_presupuesto_agotado_y_estable_hallada_la_seleccion_sigue_siendo_definitiva_para_la_ultima_estable_pero_no_para_la_compatible()
    {
        var (hojas, versiones) = Generar(5, 4, v => Registro.Hoja(v, grupos: [".NETFramework4.8"]));
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget, o => o.MaxPaginas = 2);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        var info = r.Valor!;
        Assert.Equal("5.3.0", info.UltimaEstable!.Version);
        Assert.Null(info.UltimaEstableCompatiblePorTfm["net8.0"]); // no se encontró en las 2 páginas leídas
        Assert.False(info.SeleccionCompleta);                      // por eso la selección es incompleta
        Assert.Contains(r.Advertencias, a => a.Codigo == "seleccion_incompleta");
    }

    [Fact]
    public async Task Una_version_declarada_que_no_existe_en_ninguna_pagina_es_NoPublicada_sin_leer_paginas_de_mas()
    {
        var (hojas, versiones) = Generar(4, 50);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("9.9.9"))).Valor!; // mayor que el límite superior de todas las páginas

        Assert.Equal(EstadoVersionDeclarada.NoPublicada, info.EstadoDeclarada);
        Assert.Null(info.Declarada);
        Assert.Equal(1, Paginas(nuget)); // solo la más nueva (para la última estable)
    }

    [Fact]
    public async Task Una_declarada_que_cae_en_el_rango_de_una_pagina_pero_no_existe_es_NoPublicada_tras_leer_esa_pagina()
    {
        var (hojas, versiones) = Generar(3, 5);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta("1.2.5"))).Valor!; // dentro de 1.0.0–1.4.0 pero inexistente

        Assert.Equal(EstadoVersionDeclarada.NoPublicada, info.EstadoDeclarada);
        Assert.Equal("3.4.0", info.UltimaEstable!.Version);
    }

    [Fact]
    public async Task Una_declarada_con_pagina_que_no_se_pudo_leer_es_NoDeterminada_y_no_NoPublicada()
    {
        var (hojas, versiones) = Generar(3, 5);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones).Estado(Registro.UrlPagina(0), HttpStatusCode.NotFound);
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta("1.2.0"));

        Assert.Equal(EstadoVersionDeclarada.NoDeterminada, r.Valor!.EstadoDeclarada);
        Assert.Contains(r.Advertencias, a => a.Codigo == "version_declarada_no_determinada");
        Assert.Contains(r.Advertencias, a => a.Codigo == "pagina_registro_no_leida");
    }

    [Fact]
    public async Task Sin_version_declarada_solo_se_consulta_la_ultima()
    {
        var (hojas, versiones) = Generar(4, 50);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta(null))).Valor!;

        Assert.Equal(EstadoVersionDeclarada.NoSolicitada, info.EstadoDeclarada);
        Assert.Null(info.Declarada);
        Assert.Empty(info.CompatibilidadDeclarada);
        Assert.Equal(1, Paginas(nuget));
    }

    [Fact]
    public async Task Una_version_declarada_no_interpretable_se_informa_y_solo_se_consulta_la_ultima()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta("esto-no-es-version"));

        Assert.Equal(EstadoVersionDeclarada.NoDeterminada, r.Valor!.EstadoDeclarada);
        Assert.Contains(r.Advertencias, a => a.Codigo == "version_declarada_no_interpretable");
        Assert.Equal("1.0.0", r.Valor.UltimaEstable!.Version);
    }

    [Fact]
    public async Task Las_versiones_se_ordenan_con_SemVer_y_no_como_texto()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.9.0"), Registro.Hoja("1.10.0"), Registro.Hoja("1.2.0"), Registro.Hoja("2.0.0-beta.11"), Registro.Hoja("2.0.0-beta.2"));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Equal("1.10.0", info.UltimaEstable!.Version);
        Assert.Equal("2.0.0-beta.11", info.UltimaPrerelease!.Version);
    }

    [Fact]
    public async Task La_ultima_publicacion_es_la_fecha_mas_reciente_de_las_versiones_listadas()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete",
            Registro.Hoja("1.0.0", publicada: "2024-01-01T00:00:00+00:00"),
            Registro.Hoja("2.0.0", publicada: "2025-06-01T00:00:00+00:00"),
            Registro.Hoja("3.0.0", listada: false, publicada: "2026-01-01T00:00:00+00:00"));
        var (gw, _, _) = Crear(nuget);

        var info = (await gw.ObtenerPaqueteAsync(Consulta())).Valor!;

        Assert.Equal(new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero), info.UltimaPublicacion);
    }

    // ---------- errores del registro ----------

    [Fact]
    public async Task Un_404_del_registro_es_NoEncontrado_sin_reintentos()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Estado(Registro.UrlIndice(), HttpStatusCode.NotFound);
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(EstadoConsulta.NoEncontrado, r.Estado);
        Assert.Equal(1, Registros(nuget));
        Assert.Contains("nuget.org", r.Mensaje);
    }

    [Fact]
    public async Task Un_json_invalido_no_se_reintenta_y_es_RespuestaInvalida()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Json(Registro.UrlIndice(), "esto no es json");
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(CausaFallo.RespuestaInvalida, r.Causa);
        Assert.Equal(1, Registros(nuget));
        Assert.Contains("referencia", r.Mensaje);
    }

    [Fact]
    public async Task Una_estructura_inesperada_tampoco_se_reintenta()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar().Json(Registro.UrlIndice(), "{\"sin_items\":true}");
        var (gw, _, _) = Crear(nuget);

        Assert.Equal(CausaFallo.RespuestaInvalida, (await gw.ObtenerPaqueteAsync(Consulta())).Causa);
        Assert.Equal(1, Registros(nuget));
    }

    [Fact]
    public async Task Una_pagina_con_direccion_ajena_no_se_descarga_y_deja_la_seleccion_incompleta()
    {
        var entradas = new[] { Registro.Pagina("https://evil.com/robo/page0.json", "1.0.0", "1.4.0", null), Registro.Pagina("https://api.nuget.org/v3/registration5-gz-semver2/ejemplo.paquete/page/1.json", "2.0.0", "2.4.0", null) };
        var nuget = new NuGetSimulado().ServiciosEstandar().Json(Registro.UrlIndice(), Registro.Indice(entradas))
            .Json("https://evil.com/robo/page0.json", Registro.Pagina("https://evil.com/robo/page0.json", "1.0.0", "1.4.0", [Registro.Hoja("1.4.0")]))
            .Json("https://api.nuget.org/v3/registration5-gz-semver2/ejemplo.paquete/page/1.json", Registro.Pagina("x", "2.0.0", "2.4.0", [Registro.Hoja("2.4.0", grupos: [".NETFramework4.8"])]));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(0, nuget.Llamadas("https://evil.com/robo/page0.json"));
        Assert.False(r.Valor!.SeleccionCompleta);
        Assert.Contains(r.Advertencias, a => a.Codigo == "pagina_registro_no_leida");
    }

    [Fact]
    public async Task Una_hoja_con_version_no_interpretable_se_omite_con_advertencia_y_las_demas_se_conservan()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"), Registro.Hoja("no-es-version"), Registro.Hoja("2.0.0"));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal("2.0.0", r.Valor!.UltimaEstable!.Version);
        Assert.Equal(2, r.Valor.VersionesLeidas);
        Assert.Contains(r.Advertencias, a => a.Codigo == "version_no_interpretable");
    }

    [Fact]
    public async Task Un_identificador_invalido_no_se_consulta()
    {
        var nuget = new NuGetSimulado().ServiciosEstandar();
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete("../../etc/passwd", "1.0.0", Net8));

        Assert.Equal(EstadoConsulta.NoEncontrado, r.Estado);
        Assert.Equal(0, nuget.Total);
        Assert.Contains(r.Advertencias, a => a.Codigo == "identificador_no_valido");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task El_limite_oficial_de_100_caracteres_para_el_identificador_se_aplica_en_el_gateway(int longitud, bool seConsulta)
    {
        var id = new string('a', longitud);
        var nuget = new NuGetSimulado().ServiciosEstandar().AgregarInline(id, Registro.Hoja("1.0.0"));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(new ConsultaPaquete(id, "1.0.0", Net8));

        Assert.Equal(seConsulta, nuget.Total > 0);
        Assert.Equal(seConsulta ? EstadoConsulta.Correcto : EstadoConsulta.NoEncontrado, r.Estado);
    }

    // ---------- índice de servicios ----------

    [Fact]
    public async Task El_indice_de_servicios_se_lee_una_vez_y_se_conserva_24_horas()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"));
        var (gw, _, tiempo) = Crear(nuget, o => o.CacheRegistroMinutos = 0);

        await gw.ObtenerPaqueteAsync(Consulta());
        await gw.ObtenerPaqueteAsync(Consulta());
        Assert.Equal(1, nuget.Llamadas(NuGetSimulado.UrlIndice));

        tiempo.Advance(TimeSpan.FromHours(23));
        await gw.ObtenerPaqueteAsync(Consulta());
        Assert.Equal(1, nuget.Llamadas(NuGetSimulado.UrlIndice));

        tiempo.Advance(TimeSpan.FromHours(2)); // pasaron 25 h
        await gw.ObtenerPaqueteAsync(Consulta());
        Assert.Equal(2, nuget.Llamadas(NuGetSimulado.UrlIndice));
    }

    [Fact]
    public async Task Si_el_indice_no_se_puede_renovar_se_usa_el_ultimo_conocido_con_advertencia()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"));
        var (gw, _, tiempo) = Crear(nuget, o => o.CacheRegistroMinutos = 0);
        Assert.True((await gw.ObtenerPaqueteAsync(Consulta())).EsCorrecto);
        tiempo.Advance(TimeSpan.FromHours(25));
        nuget.Estado(NuGetSimulado.UrlIndice, HttpStatusCode.NotFound);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.True(r.EsCorrecto);
        Assert.Contains(r.Advertencias, a => a.Codigo == "indice_servicios_antiguo");
    }

    [Fact]
    public async Task Sin_indice_y_sin_copia_el_resultado_es_NoDisponible()
    {
        var nuget = new NuGetSimulado().Estado(NuGetSimulado.UrlIndice, HttpStatusCode.ServiceUnavailable);
        var (gw, _, tiempo) = Crear(nuget);

        var r = await NuGetSimulado.ConTiempo(gw.ObtenerPaqueteAsync(Consulta()), tiempo);

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(CausaFallo.IndiceNoDisponible, r.Causa);
    }

    [Fact]
    public async Task Un_indice_con_version_mayor_distinta_de_3_no_se_acepta()
    {
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, NuGetSimulado.Servicios(version: "4.0.0"));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(CausaFallo.ProtocoloNoSoportado, r.Causa);
    }

    [Fact]
    public async Task Una_version_menor_nueva_del_protocolo_se_acepta_y_las_propiedades_desconocidas_se_ignoran()
    {
        var servicios = NuGetSimulado.Servicios(version: "3.9.2").Replace("\"resources\"", "\"propiedadNueva\": {\"x\": 1}, \"resources\"");
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios).Json(Registro.UrlIndice(), Registro.Inline(Registro.Hoja("1.0.0")));
        var (gw, _, _) = Crear(nuget);

        Assert.True((await gw.ObtenerPaqueteAsync(Consulta())).EsCorrecto);
    }

    [Fact]
    public async Task Una_direccion_de_registro_fuera_de_nuget_org_se_rechaza_y_se_usa_la_siguiente_valida()
    {
        var servicios = """
            { "version": "3.0.0", "resources": [
                { "@id": "https://evil.com/registro/", "@type": "RegistrationsBaseUrl/3.6.0" },
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.4.0" } ] }
            """;
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios).Json(Registro.UrlIndice(), Registro.Inline(Registro.Hoja("1.0.0")));
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.True(r.EsCorrecto);
        Assert.Equal(0, nuget.Contar(u => u.Contains("evil.com")));
    }

    [Fact]
    public async Task Si_ninguna_direccion_de_registro_es_valida_el_resultado_es_NoDisponible_y_no_se_envia_a_dominios_ajenos()
    {
        var servicios = """{ "version": "3.0.0", "resources": [ { "@id": "http://api.nuget.org/registro/", "@type": "RegistrationsBaseUrl/3.6.0" }, { "@id": "https://nuget.org.evil.com/r/", "@type": "RegistrationsBaseUrl" } ] }""";
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios);
        var (gw, _, _) = Crear(nuget);

        var r = await gw.ObtenerPaqueteAsync(Consulta());

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(1, nuget.Total); // solo el índice
    }

    [Fact]
    public async Task Prefiere_el_registro_con_soporte_SemVer_2()
    {
        var servicios = """
            { "version": "3.0.0", "resources": [
                { "@id": "https://api.nuget.org/v3/registration5-semver1/", "@type": "RegistrationsBaseUrl" },
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver1/", "@type": "RegistrationsBaseUrl/3.4.0" },
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/", "@type": "RegistrationsBaseUrl/3.6.0" } ] }
            """;
        var nuget = new NuGetSimulado().Json(NuGetSimulado.UrlIndice, servicios).Json(Registro.UrlIndice(), Registro.Inline(Registro.Hoja("1.0.0")));
        var (gw, _, _) = Crear(nuget);

        Assert.True((await gw.ObtenerPaqueteAsync(Consulta())).EsCorrecto);
        Assert.Equal(1, Registros(nuget));
    }

    // ---------- caché en memoria del registro ----------

    [Fact]
    public async Task Leer_el_mismo_paquete_dentro_de_10_minutos_no_repite_las_consultas_y_despues_si()
    {
        var (hojas, versiones) = Generar(3, 5);
        var nuget = new NuGetSimulado().AgregarPaginado(hojas, versiones);
        var (gw, _, tiempo) = Crear(nuget);

        await gw.ObtenerPaqueteAsync(Consulta("1.2.0"));
        var primeras = nuget.Total;
        var r2 = await gw.ObtenerPaqueteAsync(Consulta("1.2.0"));

        Assert.Equal(primeras, nuget.Total);
        Assert.Equal(0, r2.SolicitudesHttp);

        tiempo.Advance(TimeSpan.FromMinutes(11));
        await gw.ObtenerPaqueteAsync(Consulta("1.2.0"));
        Assert.True(nuget.Total > primeras);
    }

    // ---------- cancelación y presupuesto en el gateway ----------

    [Fact]
    public async Task Un_token_cancelado_propaga_la_cancelacion()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0"));
        var (gw, _, _) = Crear(nuget);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gw.ObtenerPaqueteAsync(Consulta(), null, cts.Token));
    }

    [Fact]
    public async Task Si_el_presupuesto_se_agota_el_resultado_es_NoDisponible_PresupuestoAgotado_y_no_una_excepcion()
    {
        var nuget = new NuGetSimulado().Ruta(NuGetSimulado.UrlIndice, async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return NuGetSimulado.Respuesta(HttpStatusCode.OK, "{}"); });
        var (gw, _, tiempo) = Crear(nuget);
        using var presupuesto = new PresupuestoTiempo(TimeSpan.FromSeconds(4), tiempo);

        var r = await NuGetSimulado.ConTiempo(gw.ObtenerPaqueteAsync(Consulta(), presupuesto), tiempo, pasoMs: 500);

        Assert.Equal(EstadoConsulta.NoDisponible, r.Estado);
        Assert.Equal(CausaFallo.PresupuestoAgotado, r.Causa);
    }

    [Fact]
    public async Task Un_paquete_que_falla_no_impide_leer_los_demas_cuando_el_disyuntor_esta_cerrado()
    {
        var nuget = new NuGetSimulado().AgregarInline("ejemplo.paquete", Registro.Hoja("1.0.0")).Estado(Registro.UrlIndice("otro.paquete"), HttpStatusCode.NotFound);
        var (gw, _, _) = Crear(nuget);

        var malo = await gw.ObtenerPaqueteAsync(new ConsultaPaquete("Otro.Paquete", "1.0.0", Net8));
        var bueno = await gw.ObtenerPaqueteAsync(Consulta("1.0.0"));

        Assert.Equal(EstadoConsulta.NoEncontrado, malo.Estado);
        Assert.True(bueno.EsCorrecto);
    }

    [Fact]
    public async Task Diez_paquetes_en_paralelo_no_superan_4_solicitudes_simultaneas()
    {
        var puerta = new SemaphoreSlim(0);
        var nuget = new NuGetSimulado().ServiciosEstandar();
        for (var i = 0; i < 10; i++)
            nuget.Ruta(Registro.UrlIndice($"paquete{i}"), async (_, _, ct) => { await puerta.WaitAsync(ct); return NuGetSimulado.Respuesta(HttpStatusCode.OK, Registro.Inline(Registro.Hoja("1.0.0"))); });
        var (gw, _, _) = Crear(nuget);

        var tareas = Enumerable.Range(0, 10).Select(i => gw.ObtenerPaqueteAsync(new ConsultaPaquete($"Paquete{i}", "1.0.0", Net8))).ToList();
        await Task.Delay(300);
        puerta.Release(10);
        var resultados = await Task.WhenAll(tareas);

        Assert.All(resultados, r => Assert.True(r.EsCorrecto));
        Assert.True(nuget.MaximoConcurrente <= 4, $"máximo concurrente: {nuget.MaximoConcurrente}");
    }
}
