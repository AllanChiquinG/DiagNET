using System.Net;
using Bunit;
using Cliente.Web.Pages;
using Cliente.Web.Servicios;
using Compartido.Contratos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Cliente.Web.Tests;

/// <summary>Base de las pruebas de páginas: servicios reales de la interfaz sobre un servidor HTTP controlado y un navegador en memoria.</summary>
public abstract class PaginaBase : BunitContext
{
    internal FakeNavegador Navegador { get; } = new();
    internal ManejadorHttp Http { get; private set; } = null!;
    internal AvisosSesion Avisos { get; } = new();

    internal void Servidor(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        Http = new ManejadorHttp(responder);
        Services.AddSingleton<IServicioNavegador>(Navegador);
        Services.AddSingleton(Http.Cliente());
        Services.AddSingleton<ClienteApi>();
        Services.AddSingleton<ConfiguracionCliente>();
        Services.AddSingleton<AlmacenHistorial>();
        Services.AddSingleton(Avisos);
    }

    internal string Uri => Services.GetRequiredService<NavigationManager>().Uri;

    internal static Guid Id(int n) => Guid.Parse($"{n:x8}-0000-4000-8000-000000000001");

    internal static HttpResponseMessage Configuracion() => Respuestas.Json(Respuestas.Configuracion);
}

public class CargaTests : PaginaBase
{
    private static InputFileContent Archivo(string nombre = "Mi.csproj", string texto = "<Project Sdk=\"Microsoft.NET.Sdk\" />") => InputFileContent.CreateFromText(texto, nombre);

    private IRenderedComponent<Carga> Pagina()
    {
        var cut = Render<Carga>();
        cut.WaitForState(() => cut.Instance is not null);
        return cut;
    }

    private static void Subir(IRenderedComponent<Carga> cut, InputFileContent archivo) => cut.FindComponent<InputFile>().UploadFiles(archivo);

    private void ServidorDeAnalisis(Func<HttpRequestMessage, HttpResponseMessage>? alAnalizar = null) =>
        Servidor(s => s.RequestUri!.AbsolutePath == "/api/configuracion" ? Configuracion()
            : alAnalizar is not null ? alAnalizar(s) : Respuestas.Json(Muestras.Resultado([], id: Id(7), nombre: "Mi.csproj"), HttpStatusCode.Created));

    [Fact]
    public void El_aviso_de_acceso_por_enlace_se_ve_antes_de_analizar_y_Analizar_esta_deshabilitado_sin_archivo()
    {
        ServidorDeAnalisis();

        var cut = Pagina();

        Assert.Contains("Quien tenga el enlace del resultado podrá consultarlo", cut.Find("#aviso-acceso").TextContent);
        Assert.Contains("No es una cuenta privada", cut.Find("#aviso-acceso").TextContent);
        Assert.Contains("solo en este navegador", cut.Find("#aviso-acceso").TextContent);
        Assert.True(cut.Find("button.analizar").HasAttribute("disabled"));
        Assert.Equal("aviso-acceso", cut.Find("button.analizar").GetAttribute("aria-describedby"));
    }

    [Fact]
    public void Muestra_los_limites_reales_del_servidor_y_no_afirma_que_el_archivo_no_se_guarde_si_se_guarda_el_resultado()
    {
        ServidorDeAnalisis();

        var cut = Pagina();

        cut.WaitForAssertion(() => Assert.Contains("Hasta 1,0 MB", cut.Markup));
        Assert.Contains("Se guarda el resultado, no el contenido del archivo", cut.Markup);
        Assert.DoesNotContain("%", cut.Markup);
    }

    [Fact]
    public void Elegir_un_csproj_valido_muestra_nombre_tamano_y_habilita_Analizar_con_Quitar_y_Reemplazar()
    {
        ServidorDeAnalisis();
        var cut = Pagina();

        Subir(cut, Archivo());

        cut.WaitForAssertion(() => Assert.Equal("Mi.csproj", cut.Find(".file .nombre-archivo").TextContent));
        Assert.Contains("listo para analizar", cut.Find(".file .meta").TextContent);
        Assert.False(cut.Find("button.analizar").HasAttribute("disabled"));
        Assert.Contains("Reemplazar", cut.Find(".reemplazar").TextContent);
        Assert.Contains(cut.FindAll("button"), b => b.TextContent.Trim() == "Quitar");
    }

    [Theory]
    [InlineData("notas.txt", "texto", "no tiene extensión .csproj")]
    [InlineData("Vacio.csproj", "", "está vacío")]
    public void Un_archivo_no_valido_se_explica_y_no_permite_analizar(string nombre, string texto, string esperado)
    {
        ServidorDeAnalisis();
        var cut = Pagina();

        Subir(cut, Archivo(nombre, texto));

        cut.WaitForAssertion(() => Assert.Contains(esperado, cut.Find(".alert.bad").TextContent));
        Assert.Contains("No pudimos leer el archivo", cut.Find(".alert.bad").TextContent);
        Assert.True(cut.Find("button.analizar").HasAttribute("disabled"));
        Assert.Contains("Seleccionar otro archivo", cut.Markup);
        Assert.DoesNotContain(Http.Solicitudes, s => s.Metodo == "POST");
    }

    [Fact]
    public void Un_archivo_mayor_al_limite_se_rechaza_en_el_navegador_con_el_limite_real()
    {
        ServidorDeAnalisis();
        var cut = Pagina();
        cut.WaitForAssertion(() => Assert.Contains("Hasta 1,0 MB", cut.Markup));

        Subir(cut, InputFileContent.CreateFromBinary(new byte[1024 * 1024 + 1], "Grande.csproj"));

        cut.WaitForAssertion(() => Assert.Contains("supera el tamaño máximo de 1,0 MB", cut.Find(".alert.bad").TextContent));
        Assert.DoesNotContain(Http.Solicitudes, s => s.Metodo == "POST");
    }

    [Fact]
    public void Quitar_vuelve_a_la_zona_de_carga_y_Reemplazar_cambia_el_archivo()
    {
        ServidorDeAnalisis();
        var cut = Pagina();
        Subir(cut, Archivo("Uno.csproj"));
        cut.WaitForAssertion(() => Assert.Equal("Uno.csproj", cut.Find(".file .nombre-archivo").TextContent));

        cut.FindComponent<InputFile>().UploadFiles(Archivo("Dos.csproj"));
        cut.WaitForAssertion(() => Assert.Equal("Dos.csproj", cut.Find(".file .nombre-archivo").TextContent));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Quitar").Click();
        Assert.NotNull(cut.Find(".drop"));
        Assert.True(cut.Find("button.analizar").HasAttribute("disabled"));
    }

    [Fact]
    public void Analizar_envia_un_solo_post_guarda_el_identificador_en_el_historial_y_abre_el_resultado()
    {
        ServidorDeAnalisis();
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/resultado/{Id(7)}", Uri));
        Assert.Single(Http.Solicitudes, s => s.Metodo == "POST" && s.Url == "/api/analisis");
        var guardado = Navegador.Datos[AlmacenHistorial.Clave];
        Assert.Contains(Id(7).ToString(), guardado);
        Assert.Contains("Mi.csproj", guardado);
        Assert.False(Avisos.HistorialNoGuardado);
    }

    [Fact]
    public void La_espera_es_indeterminada_sin_porcentajes_ni_etapas_inventadas_y_se_puede_cancelar_sin_guardar_nada()
    {
        var bloqueo = new TaskCompletionSource();
        ServidorDeAnalisis();
        Http.Bloqueo = bloqueo;
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=progressbar]")));
        var barra = cut.Find("[role=progressbar]");
        Assert.False(barra.HasAttribute("aria-valuenow"));                                   // sin valor: no hay porcentaje real
        Assert.Contains("no hay un porcentaje disponible", barra.GetAttribute("aria-valuetext"));
        Assert.Matches(@"^(?!.*\d\s?%)", cut.Find(".prog").TextContent);
        Assert.Contains("hasta 120 segundos", cut.Find(".prog").TextContent);
        Assert.Equal("true", cut.Find(".prog").GetAttribute("aria-busy"));

        cut.Find(".prog button").Click();                                                   // Cancelar

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("button.analizar")));
        Assert.Contains("Mi.csproj", cut.Find(".file .nombre-archivo").TextContent);        // el archivo sigue elegido
        Assert.False(Navegador.Datos.ContainsKey(AlmacenHistorial.Clave));                  // nada se guarda al cancelar
        Assert.DoesNotContain("/resultado/", Uri);
    }

    [Fact]
    public void Pulsar_Analizar_dos_veces_no_envia_dos_solicitudes()
    {
        var bloqueo = new TaskCompletionSource();
        ServidorDeAnalisis();
        Http.Bloqueo = bloqueo;
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[role=progressbar]")));

        Assert.Empty(cut.FindAll("button.analizar"));                                        // el botón ya no está: no hay segunda pulsación posible
        Assert.Single(Http.Solicitudes, s => s.Metodo == "POST");
        bloqueo.SetResult();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "almacenamiento_no_disponible", "Ocurrió un problema en el servidor. No se guardó ningún resultado.")]
    [InlineData(HttpStatusCode.InternalServerError, "error_interno", "Ocurrió un problema en el servidor. No se guardó ningún resultado.")]
    [InlineData(HttpStatusCode.TooManyRequests, "demasiadas_solicitudes", "Hay demasiados análisis en curso en este momento.")]
    public void Un_fallo_del_servidor_muestra_el_error_con_la_referencia_y_permite_reintentar_con_el_mismo_archivo(HttpStatusCode estado, string codigo, string texto)
    {
        ServidorDeAnalisis(_ => Respuestas.Problema(codigo, estado, "Error", null, "0HNREF1234"));
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.Contains("No pudimos completar el análisis", cut.Find("h1").TextContent));
        Assert.Contains(texto, cut.Markup);
        Assert.Equal("0HNREF1234", cut.Find(".ref").TextContent);
        Assert.Equal("alert", cut.Find(".empty").GetAttribute("role"));
        Assert.False(Navegador.Datos.ContainsKey(AlmacenHistorial.Clave));

        cut.FindAll("button").First(b => b.TextContent.Contains("Intentar de nuevo")).Click();
        Assert.Equal("Mi.csproj", cut.Find(".file .nombre-archivo").TextContent);            // no hace falta volver a elegirlo
    }

    [Fact]
    public void Sin_conexion_con_el_servidor_lo_dice_sin_inventar_una_referencia()
    {
        ServidorDeAnalisis();
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));
        Http.Lanzar = new HttpRequestException("sin red");

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.Contains("No pudimos contactar con el servidor", cut.Markup));
        Assert.Empty(cut.FindAll(".ref"));
    }

    [Fact]
    public void Un_error_de_entrada_del_servidor_se_muestra_junto_al_archivo_con_su_detalle_y_no_como_error_de_servidor()
    {
        ServidorDeAnalisis(_ => Respuestas.Problema("framework_fuera_de_alcance", HttpStatusCode.UnprocessableEntity, "El proyecto no usa .NET 8 ni .NET 10", "Ningún framework del proyecto es net8.0 ni net10.0."));
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.Contains("Ningún framework del proyecto es net8.0 ni net10.0.", cut.Find(".alert.bad").TextContent));
        Assert.True(cut.Find("button.analizar").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll(".ref"));
    }

    [Fact]
    public void Si_el_historial_del_navegador_no_esta_disponible_el_analisis_sigue_y_se_avisa_en_el_resultado()
    {
        Navegador.Disponible = false;
        ServidorDeAnalisis();
        var cut = Pagina();
        Subir(cut, Archivo());
        cut.WaitForAssertion(() => Assert.False(cut.Find("button.analizar").HasAttribute("disabled")));

        cut.Find("button.analizar").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/resultado/{Id(7)}", Uri));
        Assert.True(Avisos.HistorialNoGuardado);
        Assert.Equal(Id(7), Avisos.IdSinGuardar);
    }
}

public class ResultadoTests : PaginaBase
{
    private static ResultadoAnalisisDto Mixto(EstadoAnalisis estado = EstadoAnalisis.Completo, IReadOnlyList<AdvertenciaDto>? avisos = null) => Muestras.Resultado(
    [
        Muestras.Dependencia("Ejemplo.Ok"),
        Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada, recomendaciones: [Muestras.Recomendacion()]),
        Muestras.Dependencia("Ejemplo.Obsoleta", EstadoDiagnostico.SinSoporteActivo, recomendaciones: [Muestras.Recomendacion(TipoRecomendacion.Alternativa)]),
        Muestras.Dependencia("Ejemplo.NoSe", EstadoDiagnostico.NoDeterminada),
    ], estado, id: Id(1), advertencias: avisos);

    private IRenderedComponent<Resultado> Abrir(ResultadoAnalisisDto dto, string? id = null)
    {
        Servidor(s => s.RequestUri!.AbsolutePath == "/api/configuracion" ? Configuracion() : Respuestas.Json(dto));
        var cut = Render<Resultado>(p => p.Add(c => c.IdTexto, id ?? dto.Id.ToString()));
        cut.WaitForState(() => cut.FindAll("table").Count > 0, TimeSpan.FromSeconds(3));
        return cut;
    }

    [Fact]
    public void Muestra_los_seis_contadores_calculados_desde_las_dependencias_y_la_tabla_completa()
    {
        var cut = Abrir(Mixto());

        Assert.Equal(["4", "1", "1", "0", "1", "1"], cut.FindAll(".tile .v").Select(e => e.TextContent));
        Assert.Equal(4, cut.FindAll("tbody tr.fila").Count);
        Assert.Equal("Mi.csproj", cut.Find("h1").TextContent);
        Assert.Contains("Completo", cut.Find(".chips .badge").TextContent);
        Assert.Contains("4 dependencias", cut.Markup);
    }

    [Fact]
    public void Abrir_un_resultado_guardado_solo_hace_lecturas_nunca_un_analisis_nuevo()
    {
        var cut = Abrir(Mixto());

        Assert.NotEmpty(cut.FindAll("table"));
        Assert.All(Http.Solicitudes, s => Assert.Equal("GET", s.Metodo));
        Assert.Contains(Http.Solicitudes, s => s.Url == $"/api/analisis/{Id(1)}");
        Assert.DoesNotContain(Http.Solicitudes, s => s.Url.Contains("lectura"));
    }

    [Fact]
    public void Un_resultado_parcial_muestra_el_aviso_con_los_mensajes_del_servidor()
    {
        var cut = Abrir(Mixto(EstadoAnalisis.Parcial, [new AdvertenciaDto("analisis_parcial", "1 de 4 dependencia(s) no pudieron determinarse. Puedes volver a analizar el archivo más tarde.")]));

        Assert.Contains("Resultado parcial", cut.Find(".alert.warn").TextContent);
        Assert.Contains("1 de 4 dependencia(s) no pudieron determinarse", cut.Find(".alert.warn").TextContent);
        Assert.Contains("Parcial", cut.Find(".chips .badge").TextContent);
        Assert.Contains("part", cut.Find(".chips .badge").ClassList);
    }

    [Fact]
    public void Un_resultado_completo_con_recomendaciones_no_evaluadas_del_todo_conserva_el_aviso_sin_volverse_parcial()
    {
        var cut = Abrir(Mixto(EstadoAnalisis.Completo, [new AdvertenciaDto("recomendaciones_incompletas", "En 1 dependencia(s) las recomendaciones no se pudieron obtener o evaluar por completo.")]));

        Assert.Contains("Avisos del análisis", cut.Find(".alert.info").TextContent);
        Assert.Empty(cut.FindAll(".alert.warn"));
        Assert.Contains("Completo", cut.Find(".chips .badge").TextContent);
    }

    [Fact]
    public void Exportar_PDF_esta_inactivo_se_explica_y_no_simula_ninguna_descarga()
    {
        var cut = Abrir(Mixto());

        var boton = cut.FindAll("button").First(b => b.TextContent.Trim() == "Exportar PDF");
        Assert.Equal("true", boton.GetAttribute("aria-disabled"));
        var nota = cut.Find("#" + boton.GetAttribute("aria-describedby"));
        Assert.Contains("todavía no está disponible", nota.TextContent);

        Assert.Throws<MissingEventHandlerException>(() => boton.Click());   // no tiene ninguna acción asociada: pulsarlo no hace nada
        Assert.DoesNotContain(Http.Solicitudes, s => s.Url.Contains("pdf"));
        Assert.Empty(Navegador.Copiados);
        Assert.DoesNotContain("descarg", cut.Markup.Replace("descargas", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Elegir_un_estado_filtra_la_tabla_y_quitarlo_devuelve_todas_las_filas()
    {
        var cut = Abrir(Mixto());

        cut.Find("button.tile.old").Click();
        Assert.Equal(["Ejemplo.Vieja"], cut.FindAll("tbody tr.fila .nombre").Select(e => e.TextContent));

        cut.Find("button.tile.old").Click();
        Assert.Equal(4, cut.FindAll("tbody tr.fila").Count);
    }

    [Fact]
    public void Abrir_el_detalle_muestra_el_panel_modal_con_recomendaciones_y_cerrarlo_lo_quita()
    {
        var cut = Abrir(Mixto());

        cut.Find("tr.fila[data-nombre='Ejemplo.Obsoleta'] button.nombre").Click();

        var panel = cut.Find("[data-panel-detalle]");
        Assert.Equal("Ejemplo.Obsoleta", cut.Find(Navegador.DisparadorRegistrado!).TextContent);   // el foco volverá al botón de esa librería
        Assert.Contains("Ejemplo.Obsoleta", panel.TextContent);
        Assert.Contains("Alternativa sugerida por el autor", panel.TextContent);
        Assert.Equal(1, Navegador.PanelesAbiertos);

        cut.Find("button.x").Click();
        Assert.Empty(cut.FindAll("[data-panel-detalle]"));
    }

    [Fact]
    public void Un_analisis_que_ya_no_existe_dice_que_no_esta_disponible_con_la_retencion_real_y_sin_suponer_propietarios()
    {
        Servidor(s => s.RequestUri!.AbsolutePath == "/api/configuracion" ? Configuracion() : Respuestas.Problema("analisis_no_encontrado", HttpStatusCode.NotFound, "No existe"));

        var cut = Render<Resultado>(p => p.Add(c => c.IdTexto, Id(9).ToString()));

        cut.WaitForAssertion(() => Assert.Contains("Este análisis ya no está disponible", cut.Find("h1").TextContent));
        Assert.Contains("(90 días)", cut.Markup);
        Assert.Contains("no existe", cut.Markup);
        Assert.DoesNotContain("tuyo", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ajeno", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "historial");
    }

    [Fact]
    public void Un_identificador_mal_formado_no_llama_a_la_api()
    {
        Servidor(s => s.RequestUri!.AbsolutePath == "/api/configuracion" ? Configuracion() : Respuestas.Json("no debería pedirse"));

        var cut = Render<Resultado>(p => p.Add(c => c.IdTexto, "no-es-un-uuid"));

        cut.WaitForAssertion(() => Assert.Contains("Este análisis ya no está disponible", cut.Find("h1").TextContent));
        Assert.DoesNotContain(Http.Solicitudes, s => s.Url.StartsWith("/api/analisis"));
    }

    [Fact]
    public void Un_fallo_del_servidor_al_abrir_muestra_el_error_con_referencia_y_permite_reintentar()
    {
        var intento = 0;
        var dto = Mixto();
        Servidor(s => s.RequestUri!.AbsolutePath == "/api/configuracion" ? Configuracion()
            : ++intento == 1 ? Respuestas.Problema("almacenamiento_no_disponible", HttpStatusCode.ServiceUnavailable, "No se pudo leer", null, "REF-ABRIR") : Respuestas.Json(dto));

        var cut = Render<Resultado>(p => p.Add(c => c.IdTexto, Id(1).ToString()));

        cut.WaitForAssertion(() => Assert.Contains("No pudimos abrir el análisis", cut.Find("h1").TextContent));
        Assert.Equal("REF-ABRIR", cut.Find(".ref").TextContent);
        Assert.Contains("El análisis no se ha perdido", cut.Markup);

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Reintentar").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("table")));
    }

    [Fact]
    public void Copiar_enlace_usa_la_direccion_actual_y_avisa_si_no_se_pudo()
    {
        var cut = Abrir(Mixto());
        Navegador.Direccion = "http://localhost/resultado/x";

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Copiar enlace").Click();

        Assert.Equal(["http://localhost/resultado/x"], Navegador.Copiados);
        Assert.Contains("Enlace copiado", cut.Markup);

        Navegador.CopiaFunciona = false;
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Copiar enlace").Click();
        Assert.Contains("No se pudo copiar el enlace", cut.Markup);
    }

    [Fact]
    public void El_texto_de_conservacion_y_acceso_lee_la_retencion_del_resultado_y_no_promete_privacidad()
    {
        var cut = Abrir(Muestras.Resultado([Muestras.Dependencia("Ejemplo.Uno")], retencion: 45, id: Id(1)));

        var nota = cut.Find("p.note:not(.nota-pdf)").TextContent;

        Assert.Contains("Se conserva 45 días", nota);
        Assert.Contains("Cualquier persona con el enlace puede abrirlo; no es una cuenta privada.", nota);
        Assert.Contains("El resultado no cambia aunque NuGet publique nuevas versiones", nota);
        Assert.Contains("e6-1.1|ant=24m|agr=pes+e7-1.0", cut.Find(".reglas").TextContent);
    }

    [Fact]
    public void Si_el_analisis_no_esta_en_el_historial_de_este_navegador_se_ofrece_guardarlo_y_si_no_se_pudo_guardar_se_avisa()
    {
        var dto = Mixto();
        Avisos.HistorialNoGuardado = true;
        Avisos.IdSinGuardar = dto.Id;

        var cut = Abrir(dto);

        Assert.Contains("no se pudo guardar en el historial de este navegador", cut.Find(".alert.warn").TextContent);
        Assert.Contains("cualquiera que tenga el enlace podrá verlo", cut.Find(".alert.warn").TextContent);
        cut.FindAll("button").First(b => b.TextContent.Contains("Guardar en el historial")).Click();
        Assert.Contains(dto.Id.ToString(), Navegador.Datos[AlmacenHistorial.Clave]);
    }

    [Fact]
    public void Un_resultado_sin_dependencias_se_muestra_completo_y_explica_que_no_hay_paquetes()
    {
        var cut = Abrir(Muestras.Resultado([], id: Id(1), advertencias: [new AdvertenciaDto("sin_paquetes", "El proyecto no declara paquetes NuGet.")]));

        Assert.Contains("Este proyecto no declara paquetes NuGet.", cut.Find("tbody").TextContent);
        Assert.Equal(["0", "0", "0", "0", "0", "0"], cut.FindAll(".tile .v").Select(e => e.TextContent));
    }
}

public class HistorialTests : PaginaBase
{
    private static EntradaLocal Local(int n, string nombre) => new(Id(n), nombre, Muestras.Fecha.AddDays(-n));

    private void Guardar(params EntradaLocal[] entradas) =>
        Navegador.Datos[AlmacenHistorial.Clave] = System.Text.Json.JsonSerializer.Serialize(entradas, Respuestas.Web);

    private IRenderedComponent<Historial> Abrir()
    {
        var cut = Render<Historial>();
        cut.WaitForState(() => !cut.Markup.Contains("Consultando tus análisis guardados"), TimeSpan.FromSeconds(3));
        return cut;
    }

    private static HttpResponseMessage Devolver(HttpRequestMessage s, Func<Guid, bool> existe)
    {
        if (s.RequestUri!.AbsolutePath == "/api/configuracion") return Configuracion();
        var pedidos = System.Web.HttpUtility.ParseQueryString(s.RequestUri.Query)["ids"]!.Split(',').Select(Guid.Parse).ToList();
        var lista = pedidos.Where(existe).Select(i => Muestras.Resumen(i, "Servidor-" + i.ToString()[..8], fecha: Muestras.Fecha.AddDays(-i.ToByteArray()[0]), total: 5)).ToList();
        return Respuestas.Json(new PaginaDto<ResumenAnalisisDto>(lista, 1, 50, lista.Count));
    }

    [Fact]
    public void Explica_que_el_historial_es_de_este_navegador_que_no_es_una_cuenta_y_lee_la_retencion_real()
    {
        Servidor(s => Devolver(s, _ => true));

        var cut = Abrir();

        var texto = cut.Find(".head p").TextContent;
        Assert.Contains("pertenece a este navegador", texto);
        Assert.Contains("no es una cuenta", texto);
        Assert.Contains("no se comparte con otros dispositivos", texto);
        Assert.Contains("se pierde si borras los datos del sitio", texto);
        Assert.Contains("Cualquier persona que tenga el enlace de un análisis puede verlo", texto);
        Assert.Contains("90 días", texto);
    }

    [Fact]
    public void Con_el_almacenamiento_bloqueado_lo_explica_sin_llamar_a_la_api_y_ofrece_seguir_analizando()
    {
        Navegador.Disponible = false;
        Servidor(s => Devolver(s, _ => true));

        var cut = Abrir();

        Assert.Contains("El historial no está disponible en este navegador", cut.Find("h2").TextContent);
        Assert.Contains("guarda el enlace de cada resultado", cut.Markup);
        Assert.Equal("alert", cut.Find(".empty").GetAttribute("role"));
        Assert.DoesNotContain(Http.Solicitudes, s => s.Url.StartsWith("/api/analisis"));
        Assert.Contains(cut.FindAll("a.btn"), a => a.TextContent.Contains("Nuevo análisis"));
    }

    [Fact]
    public void Sin_analisis_guardados_muestra_el_estado_vacio_sin_consultar_la_api()
    {
        Servidor(s => Devolver(s, _ => true));

        var cut = Abrir();

        Assert.Contains("Todavía no hay análisis guardados en este navegador", cut.Find("h2").TextContent);
        Assert.Contains("Analizar mi primer proyecto", cut.Markup);
        Assert.DoesNotContain(Http.Solicitudes, s => s.Url.StartsWith("/api/analisis"));
    }

    [Fact]
    public void Lista_los_analisis_con_los_datos_del_servidor_del_mas_reciente_al_mas_antiguo_y_permite_abrirlos()
    {
        Guardar(Local(2, "B.csproj"), Local(1, "A.csproj"));
        Servidor(s => Devolver(s, _ => true));

        var cut = Abrir();

        var filas = cut.FindAll("tbody tr");
        Assert.Equal(2, filas.Count);
        Assert.Contains("Servidor-00000001", filas[0].TextContent);   // viene del servidor, no del almacenamiento local
        Assert.Contains("5", filas[0].QuerySelector("td.num")!.TextContent);
        Assert.Equal($"resultado/{Id(1)}", filas[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("2 análisis guardados en este navegador", cut.Find(".foot").TextContent);
    }

    [Fact]
    public void Un_identificador_que_el_servidor_no_devuelve_se_marca_ya_no_disponible_sin_llamarlo_ajeno_ni_borrarlo()
    {
        Guardar(Local(1, "Existe.csproj"), Local(2, "Caducado.csproj"));
        Servidor(s => Devolver(s, i => i == Id(1)));

        var cut = Abrir();

        var texto = cut.Markup;
        Assert.Contains("Ya no disponible", texto);
        Assert.Contains("Caducado.csproj", texto);                    // se muestra con lo guardado localmente
        Assert.Contains("no existe, venció su plazo o no está disponible", texto);
        Assert.DoesNotContain("tuyo", texto, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ajeno", texto, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Id(2).ToString(), Navegador.Datos[AlmacenHistorial.Clave]);   // no se descarta en silencio
        Assert.Equal(2, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public void Quitar_de_la_lista_solo_borra_la_entrada_local_y_no_llama_a_la_api()
    {
        Guardar(Local(1, "Uno.csproj"), Local(2, "Dos.csproj"));
        Servidor(s => Devolver(s, _ => true));
        var cut = Abrir();
        var solicitudes = Http.Solicitudes.Count;

        cut.FindAll("button.quitar")[0].Click();

        Assert.Single(cut.FindAll("tbody tr"));
        Assert.DoesNotContain(Id(1).ToString(), Navegador.Datos[AlmacenHistorial.Clave]);
        Assert.Contains(Id(2).ToString(), Navegador.Datos[AlmacenHistorial.Clave]);
        Assert.Equal(solicitudes, Http.Solicitudes.Count);
        Assert.All(Http.Solicitudes, s => Assert.Equal("GET", s.Metodo));
        Assert.Contains("el análisis sigue existiendo en el servidor", cut.Markup);
    }

    [Fact]
    public void Con_mas_de_50_identificadores_consulta_por_lotes_y_muestra_todas_las_entradas()
    {
        Guardar(Enumerable.Range(1, 120).Select(i => Local(i, $"P{i}.csproj")).ToArray());
        Servidor(s => Devolver(s, i => BitConverter.ToInt32(i.ToByteArray(), 0) % 2 == 0));

        var cut = Abrir();

        var lotes = Http.Solicitudes.Where(s => s.Url.StartsWith("/api/analisis?ids=")).ToList();
        Assert.Equal(3, lotes.Count);                                                       // 50 + 50 + 20
        Assert.All(lotes, l => Assert.True(l.Url.Split("ids=")[1].Split('&')[0].Split(',').Length <= 50));
        Assert.Equal(120, cut.FindAll("tbody tr").Count);                                   // ninguna entrada se descarta
        Assert.Contains("120 análisis guardados", cut.Find(".foot").TextContent);
    }

    [Fact]
    public void Si_un_lote_falla_las_entradas_se_conservan_marcadas_con_el_error_y_se_puede_reintentar()
    {
        Guardar(Enumerable.Range(1, 60).Select(i => Local(i, $"P{i}.csproj")).ToArray());
        var fallar = true;
        Servidor(s => s.RequestUri!.AbsolutePath != "/api/configuracion" && fallar && s.RequestUri.Query.Contains(Id(60).ToString())
            ? Respuestas.Problema("almacenamiento_no_disponible", HttpStatusCode.ServiceUnavailable, "No se pudo leer el historial", null, "REF-HIST")
            : Devolver(s, _ => true));

        var cut = Abrir();

        Assert.Equal(60, cut.FindAll("tbody tr").Count);                                    // las 60 siguen ahí
        Assert.Contains("No se pudieron consultar algunos análisis", cut.Find(".alert.bad").TextContent);
        Assert.Contains("REF-HIST", cut.Find(".alert.bad").TextContent);
        Assert.Contains("No se pudo consultar", cut.Markup);

        fallar = false;
        cut.Find("button.reintentar").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".alert.bad")));
        Assert.Empty(cut.FindAll(".badge.nd"));
    }

    [Fact]
    public void Un_almacenamiento_danado_se_avisa_y_no_rompe_la_pagina()
    {
        Navegador.Datos[AlmacenHistorial.Clave] = "{no es una lista";
        Servidor(s => Devolver(s, _ => true));

        var cut = Abrir();

        Assert.Contains("no se pudo leer", cut.Find(".alert.warn").TextContent);
        Assert.Contains("Todavía no hay análisis guardados", cut.Markup);
    }
}
