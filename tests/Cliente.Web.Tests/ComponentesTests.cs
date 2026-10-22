using Bunit;
using Cliente.Web.Componentes;
using Cliente.Web.Servicios;
using Compartido.Contratos;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Cliente.Web.Tests;

public class ContadoresEstadoTests : BunitContext
{
    private static ResumenEstadosDto Resumen => new(13, 5, 4, 1, 2, 1);

    [Fact]
    public void Muestra_seis_contadores_total_mas_los_cinco_estados_con_icono_y_texto()
    {
        var cut = Render<ContadoresEstado>(p => p.Add(c => c.Resumen, Resumen));

        var tiles = cut.FindAll("button.tile");
        Assert.Equal(6, tiles.Count);
        Assert.Equal(["13", "5", "4", "1", "2", "1"], tiles.Select(t => t.QuerySelector(".v")!.TextContent));
        Assert.Equal(["Total", "Actualizada", "Desactualizada", "Incompatible", "Sin soporte activo", "No determinada"], tiles.Select(t => t.QuerySelector(".l")!.TextContent.Trim()));
        Assert.Equal(5, cut.FindAll("button.tile svg.ico").Count);   // el color nunca es la única señal: cada estado lleva icono
    }

    [Fact]
    public void Elegir_un_estado_lo_filtra_y_elegirlo_otra_vez_quita_el_filtro()
    {
        EstadoDiagnostico? filtro = null;
        var cut = Render<ContadoresEstado>(p => p
            .Add(c => c.Resumen, Resumen)
            .Add(c => c.Filtro, filtro)
            .Add(c => c.FiltroChanged, EventCallback.Factory.Create<EstadoDiagnostico?>(this, v => filtro = v)));

        cut.Find("button.tile.old").Click();
        Assert.Equal(EstadoDiagnostico.Desactualizada, filtro);

        cut.Render(p => p.Add(c => c.Resumen, Resumen).Add(c => c.Filtro, filtro)
            .Add(c => c.FiltroChanged, EventCallback.Factory.Create<EstadoDiagnostico?>(this, v => filtro = v)));
        Assert.Equal("true", cut.Find("button.tile.old").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("button.tile.ok").GetAttribute("aria-pressed"));

        cut.Find("button.tile.old").Click();
        Assert.Null(filtro);
    }
}

public class TablaDiagnosticosTests : BunitContext
{
    private static List<DependenciaAnalizadaDto> Mixtas() =>
    [
        Muestras.Dependencia("Ejemplo.Ok1"), Muestras.Dependencia("Ejemplo.Ok2"), Muestras.Dependencia("Ejemplo.Ok3"), Muestras.Dependencia("Ejemplo.Ok4"), Muestras.Dependencia("Ejemplo.Ok5"),
        Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada),
        Muestras.Dependencia("Ejemplo.NoSe", EstadoDiagnostico.NoDeterminada),
        Muestras.Dependencia("Ejemplo.Obsoleta", EstadoDiagnostico.SinSoporteActivo),
        Muestras.Dependencia("Ejemplo.Rota", EstadoDiagnostico.Incompatible),
    ];

    private IRenderedComponent<TablaDiagnosticos> Tabla(IReadOnlyList<DependenciaAnalizadaDto> deps, EstadoDiagnostico? filtro = null, bool todas = false,
        Action<DependenciaAnalizadaDto>? alSeleccionar = null) =>
        Render<TablaDiagnosticos>(p => p
            .Add(c => c.Dependencias, deps).Add(c => c.Filtro, filtro).Add(c => c.MostrarTodas, todas)
            .Add(c => c.MostrarTodasChanged, EventCallback.Factory.Create<bool>(this, _ => { }))
            .Add(c => c.AlSeleccionar, EventCallback.Factory.Create<(DependenciaAnalizadaDto, string)>(this, x => alSeleccionar?.Invoke(x.Item1))));

    private static string[] Nombres(IRenderedComponent<TablaDiagnosticos> cut) => cut.FindAll("tbody tr.fila .nombre").Select(e => e.TextContent).ToArray();

    [Fact]
    public void Ordena_por_gravedad_y_pliega_las_actualizadas_despues_de_las_tres_primeras()
    {
        var cut = Tabla(Mixtas());

        Assert.Equal(["Ejemplo.Rota", "Ejemplo.Obsoleta", "Ejemplo.Vieja", "Ejemplo.NoSe", "Ejemplo.Ok1", "Ejemplo.Ok2", "Ejemplo.Ok3"], Nombres(cut));
        Assert.Contains("Mostrando 7 de 9 librerías", cut.Find(".foot [role=status]").TextContent);
        Assert.Contains("Mostrar las 2 restantes (actualizadas)", cut.Find(".foot button").TextContent);
    }

    [Fact]
    public void Con_mostrar_todas_aparecen_las_nueve_filas_sin_perder_ninguna()
    {
        var cut = Tabla(Mixtas(), todas: true);

        Assert.Equal(9, Nombres(cut).Length);
        Assert.Contains("Ocultar actualizadas", cut.Find(".foot button").TextContent);
    }

    [Fact]
    public void El_filtro_muestra_solo_ese_estado_y_no_oculta_actualizadas_dentro_del_filtro()
    {
        var cut = Tabla(Mixtas(), EstadoDiagnostico.Actualizada);

        Assert.Equal(5, Nombres(cut).Length);
        Assert.Empty(cut.FindAll(".foot button"));
    }

    [Fact]
    public void Un_filtro_sin_resultados_lo_dice_y_una_lista_vacia_explica_que_no_hay_paquetes()
    {
        var sinInc = Tabla([Muestras.Dependencia("Ejemplo.Ok1")], EstadoDiagnostico.Incompatible);
        var vacia = Tabla([]);

        Assert.Contains("No hay librerías con este estado.", sinInc.Markup);
        Assert.Contains("Este proyecto no declara paquetes NuGet.", vacia.Markup);
    }

    [Fact]
    public void Cada_fila_muestra_estado_con_texto_version_declarada_y_ultima_version()
    {
        var dep = Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada, "1.0.0", diagnostico: Muestras.Diagnostico(EstadoDiagnostico.Desactualizada, indicadores: Muestras.Indicadores("2.5.0")));

        var fila = Tabla([dep]).Find("tbody tr.fila");

        Assert.Contains("Desactualizada", fila.TextContent);
        Assert.Equal(["1.0.0", "2.5.0"], fila.QuerySelectorAll("td.vers").Select(e => e.TextContent.Trim()).ToArray());
    }

    [Fact]
    public void Una_version_no_exacta_se_muestra_literal_con_su_tipo_y_sin_ultima_version_si_no_hay_indicadores()
    {
        var dep = Muestras.Dependencia("Ejemplo.Variable", EstadoDiagnostico.NoDeterminada, "$(MiVersion)", TipoVersionDeclarada.Propiedad,
            Muestras.Diagnostico(EstadoDiagnostico.NoDeterminada, origen: OrigenDato.NoDeterminado, sinIndicadores: true));

        var fila = Tabla([dep]).Find("tbody tr.fila");

        Assert.Contains("$(MiVersion)", fila.TextContent);
        Assert.Contains("definida por una variable", fila.TextContent);
        Assert.Contains("—", fila.QuerySelectorAll("td.vers")[1].TextContent);
        Assert.Contains("Sin recomendación", fila.TextContent);
    }

    [Fact]
    public void Muestra_origen_de_copia_con_la_fecha_original_y_la_etiqueta_de_evidencia_incompleta()
    {
        var copia = Muestras.Dependencia("Ejemplo.Copia", EstadoDiagnostico.Desactualizada,
            diagnostico: Muestras.Diagnostico(EstadoDiagnostico.Desactualizada, origen: OrigenDato.CacheExpirada, fecha: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));
        var incompleta = Muestras.Dependencia("Ejemplo.Incompleta", EstadoDiagnostico.SinSoporteActivo,
            diagnostico: Muestras.Diagnostico(EstadoDiagnostico.SinSoporteActivo, hallazgos: [Muestras.Hallazgo("seleccion_incompleta", "No se recorrió todo el registro.", faltante: true)]));

        var cut = Tabla([copia, incompleta]);

        var etiquetas = cut.FindAll(".cachetag").Select(e => e.TextContent).ToList();
        Assert.Contains(etiquetas, t => t.StartsWith("Copia vencida · ") && t.Contains("2026"));
        Assert.Contains("Evidencia incompleta", etiquetas);   // la incertidumbre no se esconde detrás del estado negativo
    }

    [Fact]
    public void La_recomendacion_usa_la_etiqueta_aprobada_y_cuenta_las_demas()
    {
        var dep = Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.SinSoporteActivo, recomendaciones:
            [Muestras.Recomendacion(TipoRecomendacion.CandidatoParaRevision, "Ejemplo.A", "1.0.0"), Muestras.Recomendacion(TipoRecomendacion.CandidatoParaRevision, "Ejemplo.B", "2.0.0")]);

        var texto = Tabla([dep]).Find("td.rec").TextContent;

        Assert.Contains("Candidato para revisión (relación funcional no demostrada): Ejemplo.A 1.0.0", texto);
        Assert.Contains("(+1)", texto);
        Assert.DoesNotContain("reemplazo recomendado", texto, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Abrir_el_detalle_funciona_con_el_boton_del_nombre_y_con_clic_en_la_fila_sin_abrirlo_dos_veces()
    {
        var abiertas = new List<string>();
        var cut = Tabla([Muestras.Dependencia("Ejemplo.Uno"), Muestras.Dependencia("Ejemplo.Dos")], alSeleccionar: d => abiertas.Add(d.Nombre));

        cut.Find("tr.fila[data-nombre='Ejemplo.Uno'] button.nombre").Click();   // teclado/lector de pantalla: el botón (Enter lo activa)
        cut.Find("tr.fila[data-nombre='Ejemplo.Dos']").Click();                 // ratón: toda la fila

        Assert.Equal(["Ejemplo.Uno", "Ejemplo.Dos"], abiertas);
    }

    [Fact]
    public void El_disparador_del_panel_se_identifica_por_un_selector_que_apunta_al_boton_de_esa_fila()
    {
        var selectores = new List<string>();
        var cut = Render<TablaDiagnosticos>(p => p
            .Add(c => c.Dependencias, [Muestras.Dependencia("Ejemplo.Uno"), Muestras.Dependencia("Ejemplo.Dos")])
            .Add(c => c.MostrarTodasChanged, EventCallback.Factory.Create<bool>(this, _ => { }))
            .Add(c => c.AlSeleccionar, EventCallback.Factory.Create<(DependenciaAnalizadaDto, string)>(this, x => selectores.Add(x.Item2))));

        cut.Find("tr.fila[data-nombre='Ejemplo.Dos']").Click();

        var selector = Assert.Single(selectores);
        Assert.Equal("Ejemplo.Dos", cut.Find(selector).TextContent);   // el selector encuentra exactamente el botón de la fila clicada
    }

    [Fact]
    public void Los_nombres_largos_no_se_cortan_y_la_tabla_tiene_encabezados_con_alcance()
    {
        var largo = "Paquete." + string.Concat(Enumerable.Repeat("Segmento", 20));

        var cut = Tabla([Muestras.Dependencia(largo)]);

        Assert.Equal(largo, cut.Find("button.nombre").TextContent);
        Assert.All(cut.FindAll("thead th"), th => Assert.Equal("col", th.GetAttribute("scope")));
        Assert.NotNull(cut.Find("caption"));
    }

    [Fact]
    public void Con_cien_filas_se_muestran_todas_sin_perder_ninguna_al_pedir_todas()
    {
        var cien = Enumerable.Range(0, 100).Select(i => Muestras.Dependencia($"Ejemplo.P{i:000}", i % 4 == 0 ? EstadoDiagnostico.Desactualizada : EstadoDiagnostico.Actualizada)).ToList();

        var cut = Tabla(cien, todas: true);

        Assert.Equal(100, Nombres(cut).Length);
    }
}

public class TarjetaRecomendacionTests : BunitContext
{
    [Fact]
    public void Una_actualizacion_muestra_su_etiqueta_justificacion_evidencia_limitaciones_y_los_cambios_de_codigo()
    {
        var cut = Render<TarjetaRecomendacion>(p => p.Add(c => c.Recomendacion, Muestras.Recomendacion(cambios: false)));

        Assert.Equal("Actualización del mismo paquete", cut.Find(".t").TextContent);
        Assert.Contains("versión comprobada 2.0.0", cut.Find(".chip").TextContent);
        Assert.Contains("Justificación redactada por el servidor.", cut.Markup);
        Assert.Equal("Cambios de código no determinados.", cut.Find(".cambios").TextContent);
        Assert.Contains("Evidencia (1)", cut.Find("summary").TextContent);
        Assert.Contains("no se compiló ni se ejecutó nada", cut.Find(".why").TextContent);
    }

    [Fact]
    public void Un_candidato_para_revision_se_distingue_visualmente_y_no_se_llama_reemplazo_recomendado()
    {
        var cut = Render<TarjetaRecomendacion>(p => p.Add(c => c.Recomendacion, Muestras.Recomendacion(TipoRecomendacion.CandidatoParaRevision, version: null)));

        Assert.Contains("revision", cut.Find("section").ClassList);
        Assert.Equal("Candidato para revisión (relación funcional no demostrada)", cut.Find(".t").TextContent);
        Assert.Contains("sin versión comprobada", cut.Find(".chip").TextContent);
        Assert.DoesNotContain("reemplazo recomendado", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void La_alternativa_se_muestra_como_sugerida_por_el_autor_y_los_cambios_posibles_se_dicen_sin_endulzar()
    {
        var cut = Render<TarjetaRecomendacion>(p => p.Add(c => c.Recomendacion, Muestras.Recomendacion(TipoRecomendacion.Alternativa, cambios: true)));

        Assert.Equal("Alternativa sugerida por el autor", cut.Find(".t").TextContent);
        Assert.Equal("Puede requerir cambios de código.", cut.Find(".cambios").TextContent);
        Assert.DoesNotContain("sin cambios", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}

public class PanelDetalleTests : BunitContext
{
    private readonly FakeNavegador _navegador = new();

    public PanelDetalleTests() => Services.AddSingleton<IServicioNavegador>(_navegador);

    private IRenderedComponent<PanelDetalle> Panel(DependenciaAnalizadaDto dep, Action? alCerrar = null) =>
        Render<PanelDetalle>(p => p.Add(c => c.Dependencia, dep).Add(c => c.AlCerrar, EventCallback.Factory.Create(this, () => alCerrar?.Invoke())));

    [Fact]
    public void Es_un_dialogo_modal_con_titulo_y_abre_el_panel_en_el_navegador_una_sola_vez()
    {
        var cut = Panel(Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada));

        var dialogo = cut.Find("[role=dialog]");
        Assert.Equal("true", dialogo.GetAttribute("aria-modal"));
        Assert.Equal("Ejemplo.Vieja", cut.Find("#" + dialogo.GetAttribute("aria-labelledby")).TextContent);
        Assert.Equal(1, _navegador.PanelesAbiertos);
        Assert.Equal("Cerrar el detalle de Ejemplo.Vieja", cut.Find("button.x").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Cerrar_con_el_boton_o_con_Escape_avisa_al_padre_y_al_quitar_el_panel_se_devuelve_el_foco()
    {
        var cierres = 0;
        var cut = Panel(Muestras.Dependencia("Ejemplo.Uno"), () => cierres++);

        cut.Find("button.x").Click();
        await cut.InvokeAsync(() => _navegador.AlCerrarRegistrado!());   // lo que hace el JavaScript al pulsar Escape, incluso con el foco fuera del panel
        cut.FindAll(".pie-panel button")[1].Click();               // «Cerrar» del pie
        Assert.Equal(3, cierres);

        await DisposeAsync();   // el padre quita el panel
        Assert.Equal(1, _navegador.PanelesCerrados);              // al desmontarse, el foco vuelve a la librería que lo abrió
    }

    [Fact]
    public void Muestra_los_motivos_por_gravedad_con_su_evidencia_y_el_estado_por_framework()
    {
        var dep = Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada, diagnostico: Muestras.Diagnostico(EstadoDiagnostico.Desactualizada,
            "Desactualizada: existe una versión más reciente. Motivos: ...", [
                Muestras.Hallazgo("version_posterior_compatible", "Hay una versión más reciente: 2.0.0.", GravedadHallazgo.Aviso, false, ("version_posterior_compatible", "2.0.0")),
                Muestras.Hallazgo("aot_no_evaluado", "AOT no se evalúa.", GravedadHallazgo.Informativo),
            ]));

        var cut = Panel(dep);

        Assert.Equal("Desactualizada: existe una versión más reciente.", cut.Find(".lead-estado").TextContent);
        Assert.Contains("Hay una versión más reciente: 2.0.0.", cut.Find("#sec-motivos + .lead-estado + .why").TextContent);
        Assert.Contains("version posterior compatible", cut.Markup);                   // la evidencia está disponible
        Assert.Contains("2.0.0", cut.Find("details.ev dd").TextContent);
        Assert.Contains("Otros datos informativos (1)", cut.Markup);
        Assert.Contains("net8.0", cut.Find(".tfms").TextContent);
    }

    [Fact]
    public void Si_falta_evidencia_decisiva_lo_dice_aunque_el_estado_sea_negativo()
    {
        var dep = Muestras.Dependencia("Ejemplo.Obsoleta", EstadoDiagnostico.SinSoporteActivo, diagnostico: Muestras.Diagnostico(EstadoDiagnostico.SinSoporteActivo, "Sin soporte activo: ...", [
            Muestras.Hallazgo("paquete_obsoleto", "NuGet marca la versión como obsoleta.", GravedadHallazgo.Grave),
            Muestras.Hallazgo("seleccion_incompleta", "No se recorrió todo el registro.", faltante: true),
        ]));

        var cut = Panel(dep);

        Assert.Contains("Falta evidencia para concluir por completo", cut.Find(".alert.warn").TextContent);
        Assert.Contains("el análisis puede figurar como parcial", cut.Find(".alert.warn").TextContent);
        Assert.Contains("No se recorrió todo el registro.", cut.Find(".alert.warn").TextContent);
        Assert.Contains("NuGet marca la versión como obsoleta.", cut.Find(".why").TextContent);
    }

    [Fact]
    public void Los_indicadores_desconocidos_se_ven_como_no_determinado_o_no_evaluado_y_nunca_como_cero_o_falso()
    {
        var indicadores = Muestras.Indicadores(ultima: null, descargas: null, seleccionCompleta: false, licencia: null, licenciaNota: "NuGet no informa licencia.");
        var dep = Muestras.Dependencia("Ejemplo.Poco", EstadoDiagnostico.NoDeterminada, diagnostico: Muestras.Diagnostico(EstadoDiagnostico.NoDeterminada, indicadores: indicadores));

        var cut = Panel(dep);

        var filas = cut.FindAll(".dl dt").Zip(cut.FindAll(".dl dd")).ToDictionary(x => x.First.TextContent.Trim(), x => x.Second.TextContent.Trim());
        Assert.StartsWith("No determinado", filas["Última versión estable"]);
        Assert.StartsWith("No determinado", filas["Descargas"]);
        Assert.StartsWith("No determinado", filas["Licencia (informativa)"]);
        Assert.StartsWith("Incompleta", filas["Selección de versiones"]);
        Assert.StartsWith("No evaluado", filas["Compatibilidad con AOT"]);
        Assert.Contains("DiagNET no evalúa AOT", filas["Compatibilidad con AOT"]);
        Assert.DoesNotContain("Descargas0", string.Concat(filas.Select(f => f.Key + f.Value)));
        Assert.NotEmpty(cut.FindAll("dd.desconocido"));
    }

    [Fact]
    public void Los_valores_conocidos_se_muestran_incluido_un_cero_real_y_una_licencia_de_tipo_desconocido_lo_dice()
    {
        var indicadores = Muestras.Indicadores(descargas: 0, licencia: new LicenciaDto(null, "https://ejemplo.org/licencia"));
        var dep = Muestras.Dependencia("Ejemplo.Cero", diagnostico: Muestras.Diagnostico(EstadoDiagnostico.Actualizada, indicadores: indicadores));

        var cut = Panel(dep);

        var filas = cut.FindAll(".dl dt").Zip(cut.FindAll(".dl dd")).ToDictionary(x => x.First.TextContent.Trim(), x => x.Second.TextContent.Trim());
        Assert.Equal("0", filas["Descargas"]);
        Assert.Contains("tipo no informado por NuGet", filas["Licencia (informativa)"]);
        Assert.Equal("No", filas["Deprecada en NuGet"]);
    }

    [Fact]
    public void La_compatibilidad_se_presenta_como_inferida_y_con_su_aviso()
    {
        var cut = Panel(Muestras.Dependencia("Ejemplo.Uno"));

        Assert.Contains("compatible (inferida)", cut.Markup);
        Assert.Contains("La compatibilidad es INFERIDA", cut.Markup);
        Assert.Contains("no certifica ensamblados, compilación ni ejecución", cut.Markup);
        Assert.Contains("no demuestra incompatibilidad", cut.Markup);
    }

    [Fact]
    public void Una_deprecacion_muestra_razones_mensaje_y_paquete_alterno_con_su_rango()
    {
        var indicadores = Muestras.Indicadores(deprecado: true, deprecacion: new DeprecacionDto(["Legacy"], "Usa otro paquete.", "Ejemplo.Nueva", "[2.0.0, )"));
        var cut = Panel(Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.SinSoporteActivo, diagnostico: Muestras.Diagnostico(EstadoDiagnostico.SinSoporteActivo, indicadores: indicadores)));

        var texto = cut.Markup;

        Assert.Contains("Razones: Legacy", texto);
        Assert.Contains("Mensaje del autor: Usa otro paquete.", texto);
        Assert.Contains("Ejemplo.Nueva", texto);
        Assert.Contains("rango [2.0.0, )", texto);
    }

    [Fact]
    public void Sin_indicadores_dice_que_no_se_obtuvieron_datos_y_no_inventa_ninguno()
    {
        var dep = Muestras.Dependencia("Ejemplo.Privado", EstadoDiagnostico.NoDeterminada,
            diagnostico: Muestras.Diagnostico(EstadoDiagnostico.NoDeterminada, origen: OrigenDato.NoDeterminado, sinIndicadores: true));

        var cut = Panel(dep);

        Assert.Contains("No hay indicadores: no se obtuvieron datos de NuGet", cut.Markup);
        Assert.Contains("Sin datos de NuGet", cut.Markup);
        Assert.Equal(2, cut.FindAll(".dl dt").Count);   // solo origen y AOT; nada más
    }

    [Fact]
    public void Una_copia_vencida_conserva_la_fecha_original_y_lo_explica()
    {
        var dep = Muestras.Dependencia("Ejemplo.Copia", diagnostico: Muestras.Diagnostico(EstadoDiagnostico.Desactualizada, origen: OrigenDato.CacheExpirada, fecha: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero)));

        var cut = Panel(dep);

        Assert.Contains("Copia guardada vencida", cut.Markup);
        Assert.Contains("La fecha es la de la copia original", cut.Markup);
    }

    [Fact]
    public void Con_recomendaciones_las_muestra_con_sus_tarjetas_y_sin_recomendaciones_distingue_los_dos_casos()
    {
        var con = Panel(Muestras.Dependencia("Ejemplo.Vieja", EstadoDiagnostico.SinSoporteActivo, recomendaciones: [Muestras.Recomendacion(TipoRecomendacion.Alternativa)]));
        Assert.Single(con.FindComponents<TarjetaRecomendacion>());

        var buscada = Panel(Muestras.Dependencia("Ejemplo.Sola", EstadoDiagnostico.SinSoporteActivo,
            advertencias: [new AdvertenciaDto("sin_recomendaciones", "No se encontraron recomendaciones en la búsqueda realizada para Ejemplo.Sola. Es un resultado válido.")]));
        var fallo = Panel(Muestras.Dependencia("Ejemplo.Caida", EstadoDiagnostico.SinSoporteActivo,
            advertencias: [new AdvertenciaDto("recomendaciones_no_verificadas", "No se pudieron obtener o verificar recomendaciones para Ejemplo.Caida. Esto no significa que no existan.")]));

        Assert.Contains("No se encontraron recomendaciones en la búsqueda realizada", buscada.Find("#sec-reco").ParentElement!.TextContent);
        Assert.Contains("No se pudieron obtener o verificar recomendaciones", fallo.Find("#sec-reco").ParentElement!.TextContent);
        Assert.DoesNotContain("No se encontraron", fallo.Markup);
    }

    [Fact]
    public void Copiar_el_nombre_usa_el_portapapeles_y_avisa_si_no_se_pudo()
    {
        var cut = Panel(Muestras.Dependencia("Ejemplo.Uno"));
        cut.Find(".pie-panel button.sec").Click();
        Assert.Equal(["Ejemplo.Uno"], _navegador.Copiados);
        Assert.Contains("Nombre copiado", cut.Find(".copiado").TextContent);

        _navegador.CopiaFunciona = false;
        cut.Find(".pie-panel button.sec").Click();
        Assert.Contains("No se pudo copiar", cut.Find(".copiado").TextContent);
    }

    [Fact]
    public void Las_advertencias_de_la_libreria_se_listan_aparte_de_las_notas_de_recomendacion()
    {
        var dep = Muestras.Dependencia("Ejemplo.Uno", advertencias: [new AdvertenciaDto("version_no_resuelta", "La versión no se resolvió."), new AdvertenciaDto("sin_recomendaciones", "Nota de recomendación.")]);

        var cut = Panel(dep);

        Assert.Contains("La versión no se resolvió.", cut.Find("#sec-avisos").ParentElement!.TextContent);
        Assert.DoesNotContain("Nota de recomendación.", cut.Find("#sec-avisos").ParentElement!.TextContent);
    }
}

public class AvisosAnalisisTests : BunitContext
{
    [Fact]
    public void Un_resultado_parcial_se_anuncia_con_su_explicacion_y_los_avisos()
    {
        var cut = Render<AvisosAnalisis>(p => p.Add(c => c.Estado, EstadoAnalisis.Parcial)
            .Add(c => c.Advertencias, [new AdvertenciaDto("analisis_parcial", "3 de 13 dependencia(s) no pudieron determinarse.")]));

        Assert.Contains("Resultado parcial", cut.Find(".alert.warn").TextContent);
        Assert.Contains("3 de 13 dependencia(s) no pudieron determinarse.", cut.Markup);
        Assert.Equal("status", cut.Find(".alert").GetAttribute("role"));
    }

    [Fact]
    public void Un_resultado_completo_con_avisos_los_muestra_como_informacion_y_sin_avisos_no_muestra_nada()
    {
        var con = Render<AvisosAnalisis>(p => p.Add(c => c.Estado, EstadoAnalisis.Completo)
            .Add(c => c.Advertencias, [new AdvertenciaDto("recomendaciones_incompletas", "En 1 dependencia(s) las recomendaciones no se pudieron obtener o evaluar por completo.")]));
        var sin = Render<AvisosAnalisis>(p => p.Add(c => c.Estado, EstadoAnalisis.Completo));

        Assert.Contains("Avisos del análisis", con.Find(".alert.info").TextContent);
        Assert.DoesNotContain("Resultado parcial", con.Markup);
        Assert.Empty(sin.Markup.Trim());
    }
}
