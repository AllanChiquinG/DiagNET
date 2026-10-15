using System.Text.Json;

namespace Compartido.Contratos.Tests;

/// <summary>Los fixtures de Ejemplos deben ser válidos contra los tipos reales del contrato.</summary>
public class EjemplosDeContratoTests
{
    public static TheoryData<string> Analisis => new()
    {
        "analisis_completo.json", "analisis_parcial.json", "analisis_sin_paquetes.json", "analisis_multi_tfm.json",
    };

    [Theory]
    [MemberData(nameof(Analisis))]
    public void El_resumen_coincide_con_las_dependencias(string archivo)
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>(archivo);

        Assert.Equal(ResumenEstadosDto.Calcular(analisis.Dependencias), analisis.Resumen);
        var r = analisis.Resumen;
        Assert.Equal(r.Total, r.Actualizadas + r.Desactualizadas + r.Incompatibles + r.SinSoporteActivo + r.NoDeterminadas);
    }

    [Theory]
    [MemberData(nameof(Analisis))]
    public void El_estado_del_analisis_es_coherente_con_sus_dependencias(string archivo)
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>(archivo);
        var hayNoDeterminadas = analisis.Dependencias.Any(d => d.Diagnostico.Estado == EstadoDiagnostico.NoDeterminada);

        Assert.Equal(hayNoDeterminadas, analisis.Estado == EstadoAnalisis.Parcial);
    }

    [Theory]
    [MemberData(nameof(Analisis))]
    public void Aot_siempre_es_no_evaluado_y_no_vuelve_parcial_un_analisis(string archivo)
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>(archivo);

        foreach (var indicadores in analisis.Dependencias.Select(d => d.Diagnostico.Indicadores).OfType<IndicadoresDto>())
        {
            Assert.Equal(Disponibilidad.NoEvaluado, indicadores.Aot.Disponibilidad);
            Assert.Null(indicadores.Aot.Valor);
        }
    }

    [Theory]
    [MemberData(nameof(Analisis))]
    public void Un_indicador_solo_tiene_valor_si_es_conocido(string archivo)
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>(archivo);

        foreach (var i in analisis.Dependencias.Select(d => d.Diagnostico.Indicadores).OfType<IndicadoresDto>())
        {
            Coherente(i.FechaPublicacionDeclarada);
            Coherente(i.UltimaVersionEstable);
            Coherente(i.UltimaVersionEstableCompatible);
            Coherente(i.MesesDesdeUltimaPublicacion);
            Coherente(i.Descargas);
            Coherente(i.Licencia);
            Coherente(i.Deprecado);
            Coherente(i.Aot);
        }

        static void Coherente<T>(IndicadorDto<T> indicador)
        {
            if (indicador.Disponibilidad == Disponibilidad.Conocido)
                Assert.NotNull(indicador.Valor);
            else
                Assert.Null(indicador.Valor);
        }
    }

    [Theory]
    [MemberData(nameof(Analisis))]
    public void Todo_diagnostico_tiene_motivo_y_los_fallos_no_inventan_datos(string archivo)
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>(archivo);

        foreach (var d in analisis.Dependencias)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Diagnostico.Resumen));
            if (d.Diagnostico.Origen == OrigenDato.NoDeterminado)
            {
                Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Diagnostico.Estado);
                Assert.Null(d.Diagnostico.Indicadores);
                Assert.Null(d.Diagnostico.FechaConsulta);
            }
        }
    }

    [Fact]
    public void Cero_conocido_y_dato_ausente_son_distintos()
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>("analisis_multi_tfm.json");
        var i = analisis.Dependencias.Single().Diagnostico.Indicadores!;

        Assert.Equal(Disponibilidad.Conocido, i.Descargas.Disponibilidad);
        Assert.Equal(0L, i.Descargas.Valor);
        Assert.Equal(Disponibilidad.NoDeterminado, i.Licencia.Disponibilidad);
    }

    [Fact]
    public void Multi_tfm_conserva_evaluacion_por_tfm_y_omite_los_fuera_de_alcance()
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>("analisis_multi_tfm.json");

        Assert.Equal(["net8.0", "net10.0", "net48"], analisis.FrameworksDeclarados);
        Assert.Equal(["net8.0", "net10.0"], analisis.FrameworksEvaluados);
        var diag = analisis.Dependencias.Single().Diagnostico;
        Assert.Equal(analisis.FrameworksEvaluados, diag.EvaluacionPorTfm.Select(e => e.Tfm));
        Assert.Equal(EstadoDiagnostico.Incompatible, diag.Estado);
    }

    [Fact]
    public void Una_dependencia_deprecada_con_alterno_lo_expone_como_evidencia()
    {
        var analisis = Ayudas.Leer<ResultadoAnalisisDto>("analisis_completo.json");
        var dep = analisis.Dependencias.Single(d => d.Diagnostico.Estado == EstadoDiagnostico.SinSoporteActivo);

        Assert.Equal("Ejemplo.Moderno", dep.Diagnostico.Indicadores!.Deprecacion!.PaqueteAlterno);
        Assert.All(dep.Recomendaciones, r => Assert.NotEmpty(r.Justificacion));
    }

    [Fact]
    public void Proyecto_leido_conserva_versiones_literales_sin_resolverlas()
    {
        var proyecto = Ayudas.Leer<ProyectoLeidoDto>("proyecto_leido.json");

        Assert.Equal("[1.0.0,2.0.0)", proyecto.Dependencias.Single(d => d.Nombre == "Ejemplo.Cliente").VersionDeclarada);
        Assert.Equal(TipoVersionDeclarada.Propiedad, proyecto.Dependencias.Single(d => d.Nombre == "Ejemplo.Variable").TipoVersion);
        Assert.All(
            proyecto.Dependencias.Where(d => d.TipoVersion != TipoVersionDeclarada.Exacta),
            d => Assert.Contains(d.Advertencias, a => a.Codigo == "version_no_resuelta"));
    }

    [Fact]
    public void Historial_es_una_pagina_de_resumenes()
    {
        var pagina = Ayudas.Leer<PaginaDto<ResumenAnalisisDto>>("historial_pagina.json");

        Assert.Equal(pagina.Total, pagina.Elementos.Count);
        Assert.Contains(pagina.Elementos, e => e.Estado == EstadoAnalisis.Parcial);
    }

    [Theory]
    [InlineData("problema_xml_invalido.json")]
    [InlineData("problema_framework_fuera_de_alcance.json")]
    [InlineData("problema_analisis_no_encontrado.json")]
    public void Los_errores_de_ejemplo_siguen_el_catalogo(string archivo)
    {
        var problema = Ayudas.Leer<ProblemaDto>(archivo);

        var definicion = CodigosError.Definicion(problema.Codigo!);
        Assert.Equal(definicion.Status, problema.Status);
        Assert.Equal(definicion.Titulo, problema.Title);
        Assert.False(string.IsNullOrWhiteSpace(problema.Referencia));
    }

    [Theory]
    [MemberData(nameof(Analisis))]
    public void Reserializar_produce_el_mismo_contrato(string archivo)
    {
        var original = Ayudas.Leer<ResultadoAnalisisDto>(archivo);

        var texto = JsonSerializer.Serialize(original, Ayudas.Json);
        var otra = JsonSerializer.Deserialize<ResultadoAnalisisDto>(texto, Ayudas.Json);

        Assert.Equal(texto, JsonSerializer.Serialize(otra, Ayudas.Json));
    }
}
