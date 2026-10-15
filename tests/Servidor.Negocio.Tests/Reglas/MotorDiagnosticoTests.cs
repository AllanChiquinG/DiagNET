using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Reglas;
using static Servidor.Negocio.Tests.Reglas.Casos;

namespace Servidor.Negocio.Tests.Reglas;

/// <summary>Tabla de decisiones de <c>criterios_diagnostico.md</c> §7 (entrada → estado → motivo) y casos límite de E6.</summary>
public class MotorDiagnosticoTests
{
    private static readonly Deprecacion Obsoleto = new(["Legacy"], "Usa otro paquete.", "Otro.Paquete", "[2.0.0, )");

    // ---- 1 a 4: estados básicos ----

    [Fact]
    public void T01_declarada_igual_a_la_ultima_estable_compatible_sin_deprecacion_y_seleccion_completa_es_actualizada()
    {
        var d = Diagnosticar(Simple("1.0.0", "1.0.0"));

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        Assert.DoesNotContain(d.Hallazgos, h => h.Gravedad >= GravedadHallazgo.Aviso);
        Assert.Equal([new EvaluacionTfm(Net8, EstadoDiagnostico.Actualizada)], d.EvaluacionPorTfm);
        Assert.Equal("1.0.0", d.UltimaVersion);
    }

    [Fact]
    public void T02_existe_una_estable_posterior_compatible_es_desactualizada_con_evidencia()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.1.0"));

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        var h = d.Hallazgo("version_posterior_compatible")!;
        Assert.Equal(GravedadHallazgo.Aviso, h.Gravedad);
        Assert.Contains(h.Evidencia, e => e is { Clave: "version_posterior_compatible", Valor: "2.1.0" });
        Assert.Contains(h.Evidencia, e => e is { Clave: "version_declarada", Valor: "1.0.0" });
        Assert.Contains("2.1.0", d.Motivos);
    }

    [Fact]
    public void T03_la_posterior_solo_sirve_para_un_framework_mayor_no_cuenta_como_actualizacion()
    {
        var info = Info(V("1.0.0"), V("3.0.0", [Net10]), PorTfm((Net8, V("1.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        var h = d.Hallazgo("version_posterior_no_compatible")!;
        Assert.Equal(GravedadHallazgo.Informativo, h.Gravedad);
        Assert.Contains("3.0.0", h.Mensaje);
    }

    [Fact]
    public void T04_sin_grupo_de_dependencias_compatible_NO_basta_para_afirmar_incompatibilidad_es_no_determinada_y_parcial()
    {
        var info = Info(V("1.0.0", ["net6.0"]), V("1.0.0", ["net6.0"]), PorTfm((Net8, null)), true,
            [Compat(Net8, ResultadoCompatibilidad.Incompatible, "Solo net6.0")]);

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Null(d.Hallazgo("sin_grupo_para_tfm"));
        var h = d.Hallazgo("sin_grupo_compatible_inferido")!;
        Assert.True(h.EvidenciaDecisivaFaltante);
        Assert.Contains("no demuestra incompatibilidad", h.Mensaje);
        Assert.True(d.TieneEvidenciaIncompleta());
        var c = Assert.Single(d.Indicadores!.CompatibilidadPorTfm);
        Assert.Equal(ResultadoCompatibilidad.NoDeterminada, c.Resultado);
        Assert.Equal("inferida_dependency_groups", c.Base);
    }

    [Fact]
    public void T04b_con_evidencia_suficiente_configurada_la_incompatibilidad_si_se_afirma()
    {
        var info = Info(V("1.0.0", ["net6.0"]), V("1.0.0", ["net6.0"]), PorTfm((Net8, null)), true,
            [Compat(Net8, ResultadoCompatibilidad.Incompatible, "Solo net6.0")]);

        var d = Diagnosticar(info, parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.Incompatible, d.Estado);
        var h = d.Hallazgo("incompatible_con_evidencia_suficiente")!;
        Assert.Equal(GravedadHallazgo.Grave, h.Gravedad);
        Assert.Contains("inferida", h.Mensaje);
        Assert.Contains(h.Evidencia, e => e is { Clave: "base", Valor: "inferida_dependency_groups" });
        Assert.False(d.TieneEvidenciaIncompleta());
    }

    // ---- 5 y 6: estados concurrentes ----

    [Fact]
    public void T05_deprecada_y_con_version_posterior_es_sin_soporte_activo_y_conserva_ambos_motivos()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", Obsoleto));

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.NotNull(d.Hallazgo("paquete_obsoleto"));
        Assert.NotNull(d.Hallazgo("version_posterior_compatible"));
        var h = d.Hallazgo("paquete_obsoleto")!;
        Assert.Contains(h.Evidencia, e => e is { Clave: "paquete_alterno", Valor: "Otro.Paquete [2.0.0, )" });
        Assert.Contains(h.Evidencia, e => e is { Clave: "mensaje_del_autor", Valor: "Usa otro paquete." });
        Assert.Contains("no garantiza soporte", h.Mensaje);
    }

    [Fact]
    public void T06_deprecada_e_incompatible_gana_incompatible_y_la_deprecacion_se_lista()
    {
        var info = Info(V("1.0.0", ["net6.0"], Obsoleto), V("1.0.0", ["net6.0"], Obsoleto), PorTfm((Net8, null)), true, [Compat(Net8, ResultadoCompatibilidad.Incompatible)]);

        var d = Diagnosticar(info, parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.Incompatible, d.Estado);
        Assert.NotNull(d.Hallazgo("paquete_obsoleto"));
        Assert.NotNull(d.Hallazgo("incompatible_con_evidencia_suficiente"));
    }

    [Fact]
    public void Incompatible_deprecada_y_desactualizada_a_la_vez_conservan_los_tres_hallazgos_y_gana_incompatible()
    {
        var info = Info(V("1.0.0", ["net6.0"], Obsoleto), V("2.0.0"), PorTfm((Net8, V("2.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Incompatible)]);

        var d = Diagnosticar(info, parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.Incompatible, d.Estado);
        Assert.Equal(["incompatible_con_evidencia_suficiente", "paquete_obsoleto", "version_posterior_compatible"], d.Hallazgos.Where(h => h.Gravedad >= GravedadHallazgo.Aviso).Select(h => h.Codigo).Order());
        Assert.Equal(EstadoDiagnostico.Incompatible, d.EvaluacionPorTfm.Single().Estado);
    }

    // ---- 7: antigüedad separada ----

    [Fact]
    public void T07_un_paquete_estable_y_antiguo_al_dia_sigue_actualizado_con_aviso_informativo_de_antiguedad()
    {
        var d = Diagnosticar(Simple("1.0.0", "1.0.0", ultimaPublicacion: Ahora.AddMonths(-60)));

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        var h = d.Hallazgo("sin_publicaciones_recientes")!;
        Assert.Equal(GravedadHallazgo.Informativo, h.Gravedad);
        Assert.Contains(h.Evidencia, e => e is { Clave: "meses", Valor: "60" });
        Assert.Equal(new Indicador<int?>(Disponibilidad.Conocido, 60), d.Indicadores!.MesesDesdeUltimaPublicacion);
    }

    [Fact]
    public void La_antiguedad_justo_bajo_el_umbral_no_genera_aviso_y_en_el_umbral_si()
    {
        Assert.Null(Diagnosticar(Simple(ultimaPublicacion: Ahora.AddMonths(-23))).Hallazgo("sin_publicaciones_recientes"));
        Assert.NotNull(Diagnosticar(Simple(ultimaPublicacion: Ahora.AddMonths(-24))).Hallazgo("sin_publicaciones_recientes"));
    }

    [Fact]
    public void Una_version_posterior_reciente_es_desactualizada_aunque_nada_sea_antiguo_y_la_antiguedad_no_la_provoca()
    {
        var reciente = Diagnosticar(Simple("1.0.0", "2.0.0", ultimaPublicacion: Ahora.AddMonths(-1)));
        var antigua = Diagnosticar(Simple("1.0.0", "1.0.0", ultimaPublicacion: Ahora.AddMonths(-120)));

        Assert.Equal(EstadoDiagnostico.Desactualizada, reciente.Estado);
        Assert.Equal(EstadoDiagnostico.Actualizada, antigua.Estado);
    }

    [Fact]
    public void Sin_fecha_de_ultima_publicacion_la_antiguedad_es_no_determinada_y_no_cambia_el_estado()
    {
        var info = Info(V("1.0.0", publicada: null), V("1.0.0"), PorTfm((Net8, V("1.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Compatible)]) with { UltimaPublicacion = null };

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.MesesDesdeUltimaPublicacion.Disponibilidad);
        Assert.Null(d.Indicadores.MesesDesdeUltimaPublicacion.Valor);
        Assert.NotNull(d.Hallazgo("antiguedad_no_determinada"));
    }

    // ---- 8 y 9: sin datos y caché ----

    [Theory]
    [InlineData(MotivoSinDatos.NuGetNoDisponible, "nuget_no_disponible")]
    [InlineData(MotivoSinDatos.NoExisteEnNuGet, "paquete_no_existe_en_nuget")]
    [InlineData(MotivoSinDatos.PresupuestoAgotado, "presupuesto_agotado")]
    [InlineData(MotivoSinDatos.CircuitoAbierto, "nuget_no_disponible_circuito_abierto")]
    [InlineData(MotivoSinDatos.ErrorInterno, "error_interno_consulta")]
    public void T08_sin_datos_ni_copia_es_no_determinada_sin_indicadores_ni_fecha_inventada(MotivoSinDatos motivo, string codigo)
    {
        var d = Diagnosticar(null, [Net8, Net10], origen: OrigenDato.NoDeterminado, motivo: motivo, mensaje: "Explicación.");

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Null(d.Indicadores);
        Assert.Null(d.FechaConsulta);
        Assert.Null(d.UltimaVersion);
        Assert.Equal(OrigenDato.NoDeterminado, d.Origen);
        Assert.Equal(codigo, d.Hallazgos.First().Codigo);
        Assert.Equal([EstadoDiagnostico.NoDeterminada, EstadoDiagnostico.NoDeterminada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.Contains("Explicación.", d.Motivos);
        Assert.NotNull(d.Hallazgo("aot_no_evaluado"));
    }

    [Fact]
    public void T09_la_copia_vencida_conserva_origen_y_fecha_original_y_se_diagnostica_con_sus_datos()
    {
        var original = Ahora.AddHours(-30);

        var d = Diagnosticar(Simple("1.0.0", "2.0.0", consulta: original), origen: OrigenDato.CacheExpirada, fecha: original);

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.Equal(OrigenDato.CacheExpirada, d.Origen);
        Assert.Equal(original, d.FechaConsulta);
    }

    // ---- 10 a 13: evidencia incompleta ----

    [Fact]
    public void T10_un_hallazgo_negativo_conocido_gana_aunque_falten_fecha_y_seleccion_completa()
    {
        var info = Info(V("1.0.0", deprecacion: Obsoleto, publicada: null), null, PorTfm((Net8, null)), completa: false, [Compat(Net8, ResultadoCompatibilidad.Compatible)],
            ultimaPublicacion: null) with { UltimaPublicacion = null };

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.NotNull(d.Hallazgo("seleccion_incompleta"));
        Assert.Contains("Limitaciones:", d.Motivos);
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.UltimaVersionEstable.Disponibilidad);
    }

    [Fact]
    public void T11_seleccion_incompleta_con_una_posterior_compatible_conocida_es_desactualizada()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", completa: false));

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.False(d.Indicadores!.SeleccionVersionesCompleta);
    }

    [Fact]
    public void T12_seleccion_incompleta_sin_posterior_conocida_es_no_determinada_nunca_actualizada()
    {
        var d = Diagnosticar(Simple("1.0.0", "1.0.0", completa: false));

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.NotNull(d.Hallazgo("seleccion_incompleta"));
    }

    [Theory]
    [InlineData("[1.0.0,2.0.0)", TipoVersion.Rango)]
    [InlineData("$(Version)", TipoVersion.Propiedad)]
    [InlineData("1.*", TipoVersion.Flotante)]
    [InlineData(null, TipoVersion.Ausente)]
    public void T13_una_version_no_exacta_no_se_resuelve_y_el_estado_es_no_determinada(string? version, TipoVersion tipo)
    {
        var info = Info(null, V("2.0.0"), PorTfm((Net8, V("2.0.0"))), true, []);

        var d = Diagnosticar(info, dep: Dep(version!, tipo));

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.NotNull(d.Hallazgo("version_no_resuelta"));
        Assert.Null(d.Hallazgo("version_posterior_compatible"));
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.FechaPublicacionDeclarada.Disponibilidad);
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores.Licencia.Disponibilidad);
    }

    [Theory]
    [InlineData("[1.0.0,)", TipoVersion.Rango)]
    [InlineData("$(V)", TipoVersion.Propiedad)]
    [InlineData("1.*", TipoVersion.Flotante)]
    [InlineData(null, TipoVersion.Ausente)]
    public void P2_la_deprecacion_de_la_ultima_estable_no_se_atribuye_a_una_version_no_exacta_solo_es_informacion_del_paquete(string? version, TipoVersion tipo)
    {
        var info = Info(null, V("2.0.0", deprecacion: Obsoleto), PorTfm((Net8, V("2.0.0"))), true, []);

        var d = Diagnosticar(info, dep: Dep(version!, tipo));

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Null(d.Hallazgo("paquete_obsoleto"));
        var h = d.Hallazgo("ultima_estable_deprecada")!;
        Assert.Equal(GravedadHallazgo.Informativo, h.Gravedad);
        Assert.Contains("no de tu dependencia", h.Mensaje);
        Assert.Contains("No se atribuye", h.Mensaje);
        Assert.Contains(h.Evidencia, e => e is { Clave: "paquete_alterno", Valor: "Otro.Paquete [2.0.0, )" });
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.Deprecado.Disponibilidad);
        Assert.Null(d.Indicadores.Deprecacion);
        Assert.NotNull(d.Hallazgo("deprecacion_no_determinada"));
    }

    [Fact]
    public void P2_una_version_exacta_inexistente_en_el_registro_tampoco_hereda_la_deprecacion_de_la_ultima_estable()
    {
        var info = Info(null, V("2.0.0", deprecacion: Obsoleto), PorTfm((Net8, V("2.0.0"))), true, [], estado: EstadoVersionDeclarada.NoPublicada);

        var d = Diagnosticar(info, dep: Dep("9.9.9"));

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Null(d.Hallazgo("paquete_obsoleto"));
        Assert.NotNull(d.Hallazgo("ultima_estable_deprecada"));
    }

    [Fact]
    public void P2_la_deprecacion_de_la_version_declarada_exacta_si_se_atribuye_aunque_la_ultima_no_este_deprecada()
    {
        var d = Diagnosticar(Info(V("1.0.0", deprecacion: Obsoleto), V("2.0.0"), PorTfm((Net8, V("2.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Compatible)]));

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.Equal(new Indicador<bool?>(Disponibilidad.Conocido, true), d.Indicadores!.Deprecado);
    }

    [Fact]
    public void Una_version_declarada_inexistente_en_nuget_es_no_determinada_con_su_explicacion()
    {
        var info = Info(null, V("2.0.0"), PorTfm((Net8, V("2.0.0"))), true, [], estado: EstadoVersionDeclarada.NoPublicada);

        var d = Diagnosticar(info, dep: Dep("9.9.9"));

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Contains("9.9.9", d.Hallazgo("version_declarada_no_publicada")!.Mensaje);
    }

    [Fact]
    public void Una_deprecacion_no_conocida_no_se_presenta_como_ausencia_de_deprecacion()
    {
        var info = Info(V("1.0.0", deprecacionConocida: false), V("1.0.0"), PorTfm((Net8, V("1.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.NotNull(d.Hallazgo("deprecacion_no_determinada"));
        Assert.Null(d.Hallazgo("sin_deprecacion_publicada"));
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.Deprecado.Disponibilidad);
        Assert.Null(d.Indicadores.Deprecado.Valor);
    }

    // ---- 14 a 17: licencia, AOT, descargas ----

    [Fact]
    public void T14_licencia_ausente_no_cambia_el_estado_ni_es_cero_solo_aviso_informativo()
    {
        var d = Diagnosticar(Simple(licencia: null));

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        Assert.Equal(GravedadHallazgo.Informativo, d.Hallazgo("licencia_por_revisar")!.Gravedad);
        Assert.Equal(Disponibilidad.NoDeterminado, d.Indicadores!.Licencia.Disponibilidad);
        Assert.Null(d.Indicadores.Licencia.Valor);
    }

    [Fact]
    public void Una_licencia_que_es_solo_una_url_se_revisa_y_una_expresion_compuesta_se_muestra_tal_cual()
    {
        var url = Diagnosticar(Simple(licencia: "https://ejemplo.org/licencia", tipo: TipoLicencia.Url));
        var compuesta = Diagnosticar(Simple(licencia: "MIT OR Apache-2.0", tipo: TipoLicencia.Expresion));

        Assert.NotNull(url.Hallazgo("licencia_por_revisar"));
        Assert.Equal(TipoLicencia.Url, url.Indicadores!.Licencia.Valor!.Tipo);
        Assert.NotNull(compuesta.Hallazgo("licencia_informada"));
        Assert.Equal("MIT OR Apache-2.0", compuesta.Indicadores!.Licencia.Valor!.Valor);
        Assert.Equal(EstadoDiagnostico.Actualizada, url.Estado);
        Assert.Equal(EstadoDiagnostico.Actualizada, compuesta.Estado);
    }

    [Fact]
    public void El_tipo_de_licencia_no_informado_queda_desconocido_aunque_el_texto_parezca_una_url()
    {
        var d = Diagnosticar(Simple(licencia: "https://ejemplo.org/l", tipo: null));

        Assert.Null(d.Indicadores!.Licencia.Valor!.Tipo);
        Assert.Equal("https://ejemplo.org/l", d.Indicadores.Licencia.Valor.Valor);
        Assert.NotNull(d.Hallazgo("licencia_por_revisar"));
    }

    [Fact]
    public void T15_aot_siempre_es_no_evaluado_informativo_y_no_penaliza_ni_el_estado_ni_la_parcialidad()
    {
        var d = Diagnosticar(Simple());

        Assert.Equal(Disponibilidad.NoEvaluado, d.Indicadores!.Aot.Disponibilidad);
        Assert.Null(d.Indicadores.Aot.Valor);
        Assert.Equal(GravedadHallazgo.Informativo, d.Hallazgo("aot_no_evaluado")!.Gravedad);
        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
    }

    [Fact]
    public void T17_cero_descargas_conocido_es_distinto_de_descargas_ausentes()
    {
        var cero = Diagnosticar(Simple(descargas: 0)).Indicadores!.Descargas;
        var ausente = Diagnosticar(Simple(descargas: null)).Indicadores!.Descargas;

        Assert.Equal(new Indicador<long?>(Disponibilidad.Conocido, 0), cero);
        Assert.Equal(Disponibilidad.NoDeterminado, ausente.Disponibilidad);
        Assert.Null(ausente.Valor);
        Assert.NotNull(ausente.Nota);
    }

    // ---- 16: varios TFM ----

    [Fact]
    public void T16_incompatible_en_un_tfm_y_actualizada_en_el_otro_la_agregacion_pesimista_da_incompatible_con_detalle_por_tfm()
    {
        var info = Info(V("1.0.0", [Net10]), V("1.0.0", [Net10]), PorTfm((Net8, null), (Net10, V("1.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Incompatible), Compat(Net10, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info, [Net8, Net10], parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.Incompatible, d.Estado);
        Assert.Equal([new EvaluacionTfm(Net8, EstadoDiagnostico.Incompatible), new EvaluacionTfm(Net10, EstadoDiagnostico.Actualizada)], d.EvaluacionPorTfm);
        Assert.Equal(2, d.Indicadores!.CompatibilidadPorTfm.Count);
    }

    [Fact]
    public void Desactualizada_solo_en_net10_hace_desactualizada_el_global_y_actualizada_net8()
    {
        var info = Info(V("1.0.0"), V("2.0.0"), PorTfm((Net8, V("1.0.0")), (Net10, V("2.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info, [Net8, Net10]);

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.Contains(d.Hallazgo("version_posterior_compatible")!.Evidencia, e => e is { Clave: "tfm", Valor: Net10 });
    }

    [Fact]
    public void Un_tfm_no_determinado_junto_a_un_hallazgo_negativo_de_otro_deja_gobernar_al_negativo_y_lista_la_limitacion()
    {
        var info = Info(V("1.0.0"), V("2.0.0"), PorTfm((Net8, V("2.0.0")), (Net10, null)), completa: false,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info, [Net8, Net10]);

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.Equal([EstadoDiagnostico.Desactualizada, EstadoDiagnostico.NoDeterminada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.Contains(d.Hallazgos, h => h.Codigo == "seleccion_incompleta" && h.Evidencia.Any(e => e.Valor == Net10));
    }

    [Fact]
    public void Una_compatibilidad_no_determinada_en_un_tfm_sin_hallazgos_negativos_es_no_determinada()
    {
        var info = Info(V("1.0.0"), V("1.0.0"), PorTfm((Net8, V("1.0.0")), (Net10, V("1.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.NoDeterminada, "Grupo no interpretable")]);

        var d = Diagnosticar(info, [Net8, Net10]);

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.NoDeterminada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.Contains("Grupo no interpretable", d.Hallazgo("compatibilidad_no_determinada")!.Mensaje);
    }

    [Fact]
    public void Con_versiones_compatibles_distintas_por_tfm_NO_se_presenta_la_menor_como_comprobada_para_todos()
    {
        var info = Info(V("1.0.0"), V("3.0.0"), PorTfm((Net8, V("2.0.0")), (Net10, V("3.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info, [Net8, Net10]);

        var ind = d.Indicadores!.UltimaVersionEstableCompatible;
        Assert.Equal(Disponibilidad.NoDeterminado, ind.Disponibilidad);
        Assert.Null(ind.Valor);
        Assert.Contains("net8.0=2.0.0", ind.Nota);
        Assert.Contains("net10.0=3.0.0", ind.Nota);
        Assert.Contains("no se ha verificado una versión común", ind.Nota);
        Assert.Equal(GravedadHallazgo.Aviso, d.Hallazgo("version_comun_no_verificada")!.Gravedad);
        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.Equal([EstadoDiagnostico.Desactualizada, EstadoDiagnostico.Desactualizada], d.EvaluacionPorTfm.Select(t => t.Estado));
    }

    [Fact]
    public void Si_todos_los_tfm_comparten_la_misma_mejor_version_esa_si_esta_comprobada_para_todos()
    {
        var info = Info(V("1.0.0"), V("2.0.0"), PorTfm((Net8, V("2.0.0")), (Net10, V("2.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info, [Net8, Net10]);

        Assert.Equal(new Indicador<string?>(Disponibilidad.Conocido, "2.0.0"), d.Indicadores!.UltimaVersionEstableCompatible);
        Assert.Null(d.Hallazgo("version_comun_no_verificada"));
    }

    [Fact]
    public void Con_un_solo_tfm_la_ultima_compatible_esta_comprobada()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0"));

        Assert.Equal(new Indicador<string?>(Disponibilidad.Conocido, "2.0.0"), d.Indicadores!.UltimaVersionEstableCompatible);
        Assert.Null(d.Hallazgo("version_comun_no_verificada"));
    }

    [Fact]
    public void Sin_framework_evaluable_la_compatibilidad_no_se_afirma_pero_un_hallazgo_conocido_se_conserva()
    {
        var sinNegativo = Diagnosticar(Simple(), []);
        var conDeprecacion = Diagnosticar(Simple(deprecacion: Obsoleto), []);

        Assert.Equal(EstadoDiagnostico.NoDeterminada, sinNegativo.Estado);
        Assert.NotNull(sinNegativo.Hallazgo("sin_framework_evaluable"));
        Assert.Empty(sinNegativo.EvaluacionPorTfm);
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, conDeprecacion.Estado);
    }

    // ---- P3 / P4 confirmadas: incertidumbre visible y agregación solo con evidencia suficiente ----

    [Fact]
    public void P3_un_estado_negativo_conocido_con_evidencia_decisiva_faltante_conserva_el_estado_pero_declara_la_incertidumbre()
    {
        var info = Info(V("1.0.0", deprecacion: Obsoleto), null, PorTfm((Net8, null)), completa: false, [Compat(Net8, ResultadoCompatibilidad.Compatible)]);

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.True(d.TieneEvidenciaIncompleta());
        Assert.Contains("Evidencia incompleta", d.Motivos);
        Assert.Contains("el análisis se marca como parcial", d.Motivos);
        Assert.Contains(d.Hallazgos, h => h.Codigo == "seleccion_incompleta" && h.EvidenciaDecisivaFaltante);
    }

    [Fact]
    public void P3_un_estado_negativo_con_toda_la_evidencia_decisiva_no_declara_incertidumbre()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", Obsoleto));

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.False(d.TieneEvidenciaIncompleta());
        Assert.DoesNotContain("Evidencia incompleta", d.Motivos);
    }

    [Fact]
    public void P3_licencia_descargas_antiguedad_y_aot_ausentes_o_desconocidos_no_declaran_evidencia_incompleta()
    {
        var info = Info(V("1.0.0", licencia: null), V("1.0.0"), PorTfm((Net8, V("1.0.0"))), true, [Compat(Net8, ResultadoCompatibilidad.Compatible)],
            descargas: null) with { UltimaPublicacion = null };

        var d = Diagnosticar(info);

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        Assert.False(d.TieneEvidenciaIncompleta());
    }

    [Fact]
    public void P3_desactualizada_con_seleccion_incompleta_se_confirma_por_evidencia_positiva_pero_avisa_que_puede_haber_otra_posterior()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", completa: false));

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        var h = d.Hallazgo("seleccion_incompleta_version_sugerida")!;
        Assert.Equal(GravedadHallazgo.Aviso, h.Gravedad);
        Assert.False(h.EvidenciaDecisivaFaltante);   // para afirmar «desactualizada» la evidencia es suficiente
    }

    [Fact]
    public void P4_compatibilidad_desconocida_en_un_tfm_no_se_convierte_en_incompatibilidad()
    {
        var info = Info(V("1.0.0"), V("1.0.0"), PorTfm((Net8, V("1.0.0")), (Net10, null)), true,
            [Compat(Net8, ResultadoCompatibilidad.Compatible), Compat(Net10, ResultadoCompatibilidad.NoDeterminada, "Sin grupos")]);

        var d = Diagnosticar(info, [Net8, Net10], parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.NoDeterminada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.True(d.TieneEvidenciaIncompleta());
    }

    [Fact]
    public void P4_un_negativo_con_evidencia_suficiente_en_un_tfm_gobierna_aunque_otro_tfm_no_se_pueda_concluir_y_se_declara_la_incertidumbre()
    {
        var info = Info(V("1.0.0"), V("1.0.0"), PorTfm((Net8, null), (Net10, V("1.0.0"))), true,
            [Compat(Net8, ResultadoCompatibilidad.Incompatible), Compat(Net10, ResultadoCompatibilidad.NoDeterminada, "Grupo no interpretable")]);

        var d = Diagnosticar(info, [Net8, Net10], parametros: ConEvidenciaSuficiente);

        Assert.Equal(EstadoDiagnostico.Incompatible, d.Estado);
        Assert.Equal([EstadoDiagnostico.Incompatible, EstadoDiagnostico.NoDeterminada], d.EvaluacionPorTfm.Select(t => t.Estado));
        Assert.True(d.TieneEvidenciaIncompleta());
    }

    [Fact]
    public void P4_la_etiqueta_inferida_se_conserva_en_cada_resultado_de_compatibilidad()
    {
        var compatible = Diagnosticar(Simple());
        var sinGrupo = Diagnosticar(Info(V("1.0.0", ["net6.0"]), V("1.0.0", ["net6.0"]), PorTfm((Net8, null)), true, [Compat(Net8, ResultadoCompatibilidad.Incompatible)]));

        Assert.All(compatible.Indicadores!.CompatibilidadPorTfm.Concat(sinGrupo.Indicadores!.CompatibilidadPorTfm), c => Assert.Equal("inferida_dependency_groups", c.Base));
        Assert.Contains("inferencia", compatible.Hallazgo("compatible_inferida")!.Mensaje);
        Assert.Contains("inferida", sinGrupo.Hallazgo("sin_grupo_compatible_inferido")!.Mensaje);
    }

    [Fact]
    public void Por_defecto_ninguna_base_de_compatibilidad_basta_para_afirmar_incompatibilidad()
    {
        Assert.Empty(new ParametrosDiagnostico().BasesSuficientesParaIncompatible);
    }

    // ---- propiedades generales ----

    [Fact]
    public void La_compatibilidad_se_presenta_siempre_como_inferida()
    {
        var d = Diagnosticar(Simple());

        var h = d.Hallazgo("compatible_inferida")!;
        Assert.Contains("inferencia", h.Mensaje);
        Assert.Contains("no certifica", h.Mensaje);
        Assert.All(d.Indicadores!.CompatibilidadPorTfm, c => Assert.Equal("inferida_dependency_groups", c.Base));
    }

    [Fact]
    public void Los_prerelease_no_cuentan_como_version_posterior_y_se_compara_semver_no_texto()
    {
        // 1.10.0 > 1.9.0 aunque como texto sea menor; la posterior que el selector entrega es estable.
        Assert.Equal(EstadoDiagnostico.Desactualizada, Diagnosticar(Simple("1.9.0", "1.10.0")).Estado);
        // Declarada prerelease más nueva que la última estable: no está desactualizada.
        Assert.Equal(EstadoDiagnostico.Actualizada, Diagnosticar(Simple("2.0.0-beta.1", "1.5.0")).Estado);
    }

    [Fact]
    public void El_resultado_es_reproducible_mismo_contexto_mismo_diagnostico()
    {
        var info = Simple("1.0.0", "2.0.0", Obsoleto);

        Assert.Equal(Serializar(Diagnosticar(info)), Serializar(Diagnosticar(info)));
    }

    [Fact]
    public void La_version_de_reglas_incluye_los_parametros_y_cambia_si_cambian_y_cabe_en_la_columna()
    {
        var predeterminada = new MotorDiagnostico().VersionReglas;
        var otra = new MotorDiagnostico(new ParametrosDiagnostico { UmbralAntiguedadMeses = 12 }).VersionReglas;

        Assert.Equal("e6-1.1|ant=24m|agr=pes", predeterminada);
        Assert.NotEqual(predeterminada, otra);
        Assert.True(predeterminada.Length <= LimitesDatos.VersionReglas && otra.Length <= LimitesDatos.VersionReglas);
    }

    [Fact]
    public void El_umbral_de_antiguedad_es_un_parametro_y_solo_cambia_el_aviso_informativo()
    {
        var info = Simple(ultimaPublicacion: Ahora.AddMonths(-13));

        var a24 = Diagnosticar(info);
        var a12 = Diagnosticar(info, parametros: new ParametrosDiagnostico { UmbralAntiguedadMeses = 12 });

        Assert.Null(a24.Hallazgo("sin_publicaciones_recientes"));
        Assert.NotNull(a12.Hallazgo("sin_publicaciones_recientes"));
        Assert.Equal(a24.Estado, a12.Estado);
    }

    [Fact]
    public void Los_hallazgos_se_ordenan_por_gravedad_y_los_motivos_citan_los_de_aviso_o_mayor()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", Obsoleto));

        Assert.Equal(d.Hallazgos.OrderByDescending(h => h.Gravedad).Select(h => h.Gravedad), d.Hallazgos.Select(h => h.Gravedad));
        Assert.StartsWith("Sin soporte activo:", d.Motivos);
        Assert.Contains("obsoleto", d.Motivos);
        Assert.DoesNotContain("AOT", d.Motivos);   // los informativos no ensucian la explicación
    }

    [Fact]
    public void Los_indicadores_guardan_la_ultima_estable_la_fecha_declarada_y_la_deprecacion_con_su_alterno()
    {
        var d = Diagnosticar(Simple("1.0.0", "2.0.0", Obsoleto));

        var i = d.Indicadores!;
        Assert.Equal(new Indicador<string?>(Disponibilidad.Conocido, "2.0.0"), i.UltimaVersionEstable);
        Assert.Equal(Disponibilidad.Conocido, i.FechaPublicacionDeclarada.Disponibilidad);
        Assert.Equal(new Indicador<bool?>(Disponibilidad.Conocido, true), i.Deprecado);
        Assert.Equal("Otro.Paquete", i.Deprecacion!.PaqueteAlterno);
        Assert.Equal("[2.0.0, )", i.Deprecacion.RangoVersionAlterno);
    }

    [Fact]
    public void El_motor_no_modifica_el_estado_por_licencia_aot_descargas_ni_antiguedad_juntos()
    {
        var d = Diagnosticar(Simple(licencia: null, ultimaPublicacion: Ahora.AddMonths(-100), descargas: null));

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
    }
}
