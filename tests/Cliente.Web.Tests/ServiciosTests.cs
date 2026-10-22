using System.Net;
using Cliente.Web.Servicios;
using Compartido.Contratos;

namespace Cliente.Web.Tests;

public class PresentacionTests
{
    [Theory]
    [InlineData(EstadoDiagnostico.Actualizada, "Actualizada", "ok")]
    [InlineData(EstadoDiagnostico.Desactualizada, "Desactualizada", "old")]
    [InlineData(EstadoDiagnostico.Incompatible, "Incompatible", "inc")]
    [InlineData(EstadoDiagnostico.SinSoporteActivo, "Sin soporte activo", "eol")]
    [InlineData(EstadoDiagnostico.NoDeterminada, "No determinada", "nd")]
    public void Cada_estado_tiene_texto_y_clase_propios(EstadoDiagnostico estado, string texto, string clase)
    {
        Assert.Equal(texto, Presentacion.Texto(estado));
        Assert.Equal(clase, Presentacion.Clase(estado));
    }

    [Fact]
    public void El_orden_de_gravedad_pone_lo_que_exige_accion_primero()
    {
        var orden = new[] { EstadoDiagnostico.Actualizada, EstadoDiagnostico.NoDeterminada, EstadoDiagnostico.Desactualizada, EstadoDiagnostico.SinSoporteActivo, EstadoDiagnostico.Incompatible }
            .OrderBy(Presentacion.Orden).ToList();

        Assert.Equal([EstadoDiagnostico.Incompatible, EstadoDiagnostico.SinSoporteActivo, EstadoDiagnostico.Desactualizada, EstadoDiagnostico.NoDeterminada, EstadoDiagnostico.Actualizada], orden);
    }

    [Fact]
    public void Un_indicador_no_determinado_o_no_evaluado_nunca_se_muestra_como_cero_ni_falso()
    {
        var descargasCero = Presentacion.Indicador(new IndicadorDto<long?>(Disponibilidad.Conocido, 0), n => Presentacion.Entero(n ?? -1));
        var descargasAusentes = Presentacion.Indicador(new IndicadorDto<long?>(Disponibilidad.NoDeterminado, null, "nota"), n => Presentacion.Entero(n ?? -1));
        var deprecadoFalso = Presentacion.Indicador(new IndicadorDto<bool?>(Disponibilidad.Conocido, false), b => b == true ? "Sí" : "No");
        var deprecadoAusente = Presentacion.Indicador(new IndicadorDto<bool?>(Disponibilidad.NoDeterminado), b => b == true ? "Sí" : "No");
        var aot = Presentacion.Indicador(new IndicadorDto<bool?>(Disponibilidad.NoEvaluado), _ => "Sí");

        Assert.Equal(("0", true), descargasCero);
        Assert.Equal(("No determinado", false), descargasAusentes);
        Assert.Equal(("No", true), deprecadoFalso);
        Assert.Equal(("No determinado", false), deprecadoAusente);
        Assert.Equal(("No evaluado", false), aot);
    }

    [Fact]
    public void Un_valor_conocido_nulo_se_muestra_con_su_texto_de_ausencia_conocida()
    {
        var r = Presentacion.Indicador(new IndicadorDto<string?>(Disponibilidad.Conocido, null), v => v ?? "", "No hay versión estable publicada");

        Assert.Equal(("No hay versión estable publicada", true), r);
    }

    [Theory]
    [InlineData(500, "500 bytes")]
    [InlineData(3481, "3,4 KB")]
    [InlineData(1048576, "1,0 MB")]
    public void El_tamano_se_formatea_sin_depender_de_la_cultura_del_navegador(long bytes, string esperado) => Assert.Equal(esperado, Presentacion.Tamano(bytes));

    [Fact]
    public void Los_enteros_grandes_usan_espacio_como_separador_de_miles() => Assert.Equal("1 259 869 554", Presentacion.Entero(1259869554));

    [Fact]
    public void La_fecha_se_muestra_en_espanol_y_la_ausencia_como_raya()
    {
        var texto = Presentacion.Fecha(new DateTimeOffset(2026, 10, 5, 15, 42, 0, TimeSpan.Zero));

        Assert.Matches(@"^\d{1,2} (oct|sep|nov) 2026, \d{2}:\d{2}$", texto);   // el día puede variar con la zona horaria local
        Assert.Equal("—", Presentacion.Fecha((DateTimeOffset?)null));
    }

    [Fact]
    public void El_origen_de_una_copia_vencida_conserva_la_fecha_original()
    {
        var d = Muestras.Diagnostico(EstadoDiagnostico.Actualizada, origen: OrigenDato.CacheExpirada, fecha: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        var texto = Presentacion.OrigenYFecha(d);

        Assert.StartsWith("Copia guardada vencida · ", texto);
        Assert.Contains("2026", texto);
        Assert.Equal("Sin datos de NuGet", Presentacion.OrigenYFecha(Muestras.Diagnostico(EstadoDiagnostico.NoDeterminada, origen: OrigenDato.NoDeterminado, sinIndicadores: true)));
    }
}

public class ClienteApiTests
{
    private static ClienteApi Api(ManejadorHttp m) => new(m.Cliente());

    [Fact]
    public async Task Un_error_con_problem_details_conserva_codigo_estado_y_referencia()
    {
        var m = new ManejadorHttp(_ => Respuestas.Problema("analisis_no_encontrado", HttpStatusCode.NotFound, "No existe", "Detalle", "REF-1"));

        var r = await Api(m).ObtenerAnalisisAsync(Guid.NewGuid());

        Assert.False(r.Ok);
        Assert.Equal(("analisis_no_encontrado", 404, "REF-1"), (r.Error!.Codigo, r.Error.Status, r.Error.Referencia));
        Assert.True(r.Error.EsNoEncontrado);
    }

    [Fact]
    public async Task Sin_conexion_se_distingue_de_un_error_del_servidor()
    {
        var m = new ManejadorHttp(_ => Respuestas.Json("x")) { Lanzar = new HttpRequestException("sin red") };

        var r = await Api(m).ObtenerAnalisisAsync(Guid.NewGuid());

        Assert.Equal((ErrorApi.SinConexion, 0), (r.Error!.Codigo, r.Error.Status));
    }

    [Fact]
    public async Task Una_respuesta_de_error_que_no_es_json_no_rompe_y_conserva_el_estado_http()
    {
        var m = new ManejadorHttp(_ => Respuestas.Html(HttpStatusCode.BadGateway));

        var r = await Api(m).ObtenerAnalisisAsync(Guid.NewGuid());

        Assert.Equal(("http_502", 502), (r.Error!.Codigo, r.Error.Status));
        Assert.Null(r.Error.Referencia);
    }

    [Fact]
    public async Task Un_exito_con_cuerpo_ilegible_es_una_respuesta_inesperada()
    {
        var m = new ManejadorHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{no es json", System.Text.Encoding.UTF8, "application/json") });

        var r = await Api(m).ObtenerAnalisisAsync(Guid.NewGuid());

        Assert.Equal(ErrorApi.RespuestaInesperada, r.Error!.Codigo);
    }

    [Fact]
    public async Task El_analisis_se_envia_como_multipart_en_el_campo_archivo_por_post()
    {
        string? contenidoTipo = null;
        var m = new ManejadorHttp(solicitud =>
        {
            contenidoTipo = solicitud.Content!.Headers.ContentType!.MediaType;
            return Respuestas.Json(Muestras.Resultado([]), HttpStatusCode.Created);
        });

        var r = await Api(m).AnalizarAsync("Mi.csproj", [1, 2, 3]);

        Assert.True(r.Ok);
        Assert.Equal(("POST", "/api/analisis"), m.Solicitudes.Single());
        Assert.Equal("multipart/form-data", contenidoTipo);
    }

    [Fact]
    public async Task El_historial_se_consulta_en_lotes_de_hasta_50_y_ninguna_entrada_se_descarta()
    {
        var ids = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
        var m = new ManejadorHttp(s =>
        {
            var pedidos = System.Web.HttpUtility.ParseQueryString(s.RequestUri!.Query)["ids"]!.Split(',').Select(Guid.Parse).ToList();
            // el servidor devuelve solo los que «existen»: los pares
            var existentes = pedidos.Where(i => ids.IndexOf(i) % 2 == 0).Select(i => Muestras.Resumen(i)).ToList();
            return Respuestas.Json(new PaginaDto<ResumenAnalisisDto>(existentes, 1, 50, existentes.Count));
        });

        var r = await Api(m).ListarAsync(ids);

        Assert.Equal(3, m.Solicitudes.Count);                                              // 50 + 50 + 20
        Assert.All(m.Solicitudes, s => Assert.True(s.Url.Split("ids=")[1].Split('&')[0].Split(',').Length <= 50));
        Assert.Equal(60, r.Encontrados.Count);
        Assert.Equal(60, r.NoDevueltos.Count);
        Assert.Equal(120, r.Encontrados.Count + r.NoDevueltos.Count + r.SinConsultar.Count);   // todas las entradas están contabilizadas
        Assert.Empty(r.SinConsultar);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Si_un_lote_falla_sus_identificadores_se_conservan_como_sin_consultar_y_los_demas_lotes_siguen()
    {
        var ids = Enumerable.Range(0, 120).Select(_ => Guid.NewGuid()).ToList();
        var llamada = 0;
        var m = new ManejadorHttp(s =>
        {
            llamada++;
            if (llamada == 2) return Respuestas.Problema("almacenamiento_no_disponible", HttpStatusCode.ServiceUnavailable, "No se pudo leer", null, "REF-LOTE");
            var pedidos = System.Web.HttpUtility.ParseQueryString(s.RequestUri!.Query)["ids"]!.Split(',').Select(Guid.Parse).ToList();
            return Respuestas.Json(new PaginaDto<ResumenAnalisisDto>(pedidos.Select(i => Muestras.Resumen(i)).ToList(), 1, 50, pedidos.Count));
        });

        var r = await Api(m).ListarAsync(ids);

        Assert.Equal(3, m.Solicitudes.Count);
        Assert.Equal(70, r.Encontrados.Count);
        Assert.Equal(50, r.SinConsultar.Count);                                            // se conservan: se desconoce su estado
        Assert.Empty(r.NoDevueltos);
        Assert.Equal("REF-LOTE", r.Error!.Referencia);
    }

    [Fact]
    public async Task Los_identificadores_repetidos_se_consultan_una_sola_vez()
    {
        var id = Guid.NewGuid();
        var m = new ManejadorHttp(s => Respuestas.Json(new PaginaDto<ResumenAnalisisDto>([Muestras.Resumen(id)], 1, 50, 1)));

        var r = await Api(m).ListarAsync([id, id, id]);

        Assert.Single(r.Encontrados);
        Assert.Single(System.Web.HttpUtility.ParseQueryString(m.Solicitudes.Single().Url.Split('?')[1])["ids"]!.Split(','));
    }
}

public class AlmacenHistorialTests
{
    private static EntradaLocal Entrada(string nombre = "A.csproj") => new(Guid.NewGuid(), nombre, Muestras.Fecha);

    [Fact]
    public async Task Con_almacenamiento_vacio_el_historial_esta_disponible_y_vacio()
    {
        var estado = await new AlmacenHistorial(new FakeNavegador()).CargarAsync();

        Assert.True(estado.Disponible);
        Assert.Empty(estado.Entradas);
        Assert.False(estado.Danado);
    }

    [Fact]
    public async Task Agregar_guarda_la_entrada_mas_reciente_primero_y_no_duplica()
    {
        var navegador = new FakeNavegador();
        var almacen = new AlmacenHistorial(navegador);
        var a = Entrada("A");
        var b = Entrada("B");

        Assert.True(await almacen.AgregarAsync(a));
        Assert.True(await almacen.AgregarAsync(b));
        Assert.True(await almacen.AgregarAsync(a));

        Assert.Equal([a.Id, b.Id], (await almacen.CargarAsync()).Entradas.Select(e => e.Id));
    }

    [Fact]
    public async Task Quitar_solo_borra_esa_entrada()
    {
        var almacen = new AlmacenHistorial(new FakeNavegador());
        var a = Entrada();
        var b = Entrada();
        await almacen.AgregarAsync(a);
        await almacen.AgregarAsync(b);

        Assert.True(await almacen.QuitarAsync(a.Id));

        Assert.Equal([b.Id], (await almacen.CargarAsync()).Entradas.Select(e => e.Id));
    }

    [Fact]
    public async Task Con_el_almacenamiento_bloqueado_se_informa_no_disponible_y_no_se_lanza()
    {
        var almacen = new AlmacenHistorial(new FakeNavegador { Disponible = false });

        var estado = await almacen.CargarAsync();

        Assert.False(estado.Disponible);
        Assert.False(await almacen.AgregarAsync(Entrada()));
        Assert.False(await almacen.QuitarAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Si_el_almacenamiento_esta_lleno_agregar_devuelve_falso_y_no_pierde_lo_guardado()
    {
        var navegador = new FakeNavegador();
        var almacen = new AlmacenHistorial(navegador);
        var existente = Entrada();
        await almacen.AgregarAsync(existente);
        navegador.EscrituraFalla = true;

        Assert.False(await almacen.AgregarAsync(Entrada()));

        navegador.EscrituraFalla = false;
        Assert.Equal([existente.Id], (await almacen.CargarAsync()).Entradas.Select(e => e.Id));
    }

    [Fact]
    public async Task Un_contenido_danado_se_detecta_y_no_rompe_la_pantalla()
    {
        var navegador = new FakeNavegador();
        navegador.Datos[AlmacenHistorial.Clave] = "{esto no es una lista";

        var estado = await new AlmacenHistorial(navegador).CargarAsync();

        Assert.True(estado.Disponible);
        Assert.True(estado.Danado);
        Assert.Empty(estado.Entradas);
    }

    [Fact]
    public async Task Al_llegar_al_limite_de_entradas_no_se_guarda_en_silencio_se_devuelve_falso()
    {
        var navegador = new FakeNavegador();
        var almacen = new AlmacenHistorial(navegador);
        for (var i = 0; i < AlmacenHistorial.MaximoEntradas; i++) Assert.True(await almacen.AgregarAsync(Entrada($"{i}.csproj")));

        Assert.False(await almacen.AgregarAsync(Entrada("una más.csproj")));
        Assert.Equal(AlmacenHistorial.MaximoEntradas, (await almacen.CargarAsync()).Entradas.Count);
    }
}
