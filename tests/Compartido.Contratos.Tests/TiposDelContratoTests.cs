using System.Text.Json;

namespace Compartido.Contratos.Tests;

public class TiposDelContratoTests
{
    [Theory]
    [InlineData(EstadoDiagnostico.Actualizada, "actualizada")]
    [InlineData(EstadoDiagnostico.Desactualizada, "desactualizada")]
    [InlineData(EstadoDiagnostico.Incompatible, "incompatible")]
    [InlineData(EstadoDiagnostico.SinSoporteActivo, "sin_soporte_activo")]
    [InlineData(EstadoDiagnostico.NoDeterminada, "no_determinada")]
    public void Los_estados_viajan_como_texto_alineado_con_los_CHECK_de_la_base(EstadoDiagnostico estado, string texto)
    {
        Assert.Equal($"\"{texto}\"", JsonSerializer.Serialize(estado, Ayudas.Json));
        Assert.Equal(estado, JsonSerializer.Deserialize<EstadoDiagnostico>($"\"{texto}\"", Ayudas.Json));
    }

    [Fact]
    public void Un_estado_desconocido_no_se_acepta()
    {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<EstadoDiagnostico>("\"quizas\"", Ayudas.Json));
    }

    [Fact]
    public void Falso_y_cero_conocidos_no_se_confunden_con_no_determinado()
    {
        var falso = IndicadorDto<bool?>.Conocido(false);
        var cero = IndicadorDto<long?>.Conocido(0);
        var ausente = IndicadorDto<long?>.NoDeterminado("NuGet no devolvió el dato.");

        Assert.Equal(Disponibilidad.Conocido, falso.Disponibilidad);
        Assert.False(falso.Valor);
        Assert.Equal(0L, cero.Valor);
        Assert.Equal(Disponibilidad.Conocido, cero.Disponibilidad);
        Assert.Null(ausente.Valor);
        Assert.NotEqual(cero.Disponibilidad, ausente.Disponibilidad);
    }

    [Fact]
    public void Un_indicador_conocido_no_admite_valor_nulo()
    {
        Assert.Throws<ArgumentNullException>(() => IndicadorDto<string?>.Conocido(null!));
    }

    [Fact]
    public void No_evaluado_es_un_estado_distinto_de_no_determinado()
    {
        var aot = IndicadorDto<bool?>.NoEvaluado();

        Assert.Equal(Disponibilidad.NoEvaluado, aot.Disponibilidad);
        Assert.NotEqual(IndicadorDto<bool?>.NoDeterminado().Disponibilidad, aot.Disponibilidad);
    }

    [Fact]
    public void El_catalogo_de_errores_no_repite_codigos_y_usa_estados_HTTP_de_error()
    {
        var codigos = CodigosError.Todos.Select(d => d.Codigo).ToList();

        Assert.Equal(codigos.Count, codigos.Distinct().Count());
        Assert.All(CodigosError.Todos, d => Assert.InRange(d.Status, 400, 599));
        Assert.All(codigos, c => Assert.Matches("^[a-z_]+$", c));
    }

    [Fact]
    public void Todo_codigo_de_error_esta_documentado_en_el_contrato_de_la_api()
    {
        var documento = File.ReadAllText(Path.Combine(Ayudas.RaizRepositorio(), "docs", "contrato_api.md"));

        foreach (var definicion in CodigosError.Todos)
            Assert.Contains($"`{definicion.Codigo}`", documento);
    }

    [Fact]
    public void Los_errores_se_serializan_como_problem_details_con_extensiones()
    {
        var problema = new ProblemaDto(null, "t", 400, "d", "/api/analisis", CodigosError.XmlInvalido, "ref-1",
            [new ErrorCampoDto("archivo", "m")]);

        var json = JsonDocument.Parse(JsonSerializer.Serialize(problema, Ayudas.Json)).RootElement;

        Assert.Equal("xml_invalido", json.GetProperty("codigo").GetString());
        Assert.Equal("ref-1", json.GetProperty("referencia").GetString());
        Assert.Equal("archivo", json.GetProperty("errores")[0].GetProperty("campo").GetString());
    }

    [Fact]
    public void El_resumen_cuenta_total_mas_los_cinco_estados()
    {
        static DependenciaAnalizadaDto Dep(EstadoDiagnostico e) => new("P", "1.0.0", TipoVersionDeclarada.Exacta, null, [],
            new DiagnosticoDto(e, "motivo", [], [], OrigenDato.Nuget, DateTimeOffset.UnixEpoch, null), []);

        var resumen = ResumenEstadosDto.Calcular(
        [
            Dep(EstadoDiagnostico.Actualizada), Dep(EstadoDiagnostico.Actualizada), Dep(EstadoDiagnostico.Desactualizada),
            Dep(EstadoDiagnostico.Incompatible), Dep(EstadoDiagnostico.SinSoporteActivo), Dep(EstadoDiagnostico.NoDeterminada),
        ]);

        Assert.Equal(new ResumenEstadosDto(6, 2, 1, 1, 1, 1), resumen);
    }
}
