using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Recomendaciones;
using Servidor.Negocio.Reglas;
using Servidor.Negocio.Tests.Orquestacion;

namespace Servidor.Negocio.Tests.Recomendaciones;

/// <summary>Motor de recomendaciones de E7 sobre un registro simulado y el motor de diagnóstico REAL de E6.</summary>
public class MotorRecomendacionTests
{
    private const string N8 = "net8.0";
    private const string N10 = "net10.0";
    private static readonly string[] Ambos = [N8, N10];

    private readonly RegistroFalso _reg = new();
    private readonly CacheFalsa _cache = new();
    private readonly FakeTimeProvider _tiempo = new(Paquetes.Ahora);

    private static VersionPublicada V(string version, string[]? grupos = null, Deprecacion? dep = null, string? licencia = "MIT", bool listada = true) =>
        Paquetes.Version(version, grupos ?? [N8], dep, licencia) with { Listada = listada };

    private static Deprecacion Obsoleto(string? alterno = null, string? rango = null) => new(["Legacy"], "Paquete en desuso.", alterno, rango);

    private MotorRecomendacion Motor(OpcionesRecomendacion? opciones = null) =>
        new(new ServicioMetadatos(_reg, _cache, new InferidorFalso(), _tiempo), _reg, opciones);

    /// <summary>Diagnostica con el motor real (como hace el orquestador) y luego recomienda.</summary>
    private async Task<(Diagnostico D, ResultadoRecomendacion R)> Recomendar(
        string paquete, string? declarada, string[]? tfms = null, TipoVersion tipo = TipoVersion.Exacta, OpcionesRecomendacion? opciones = null)
    {
        tfms ??= [N8];
        var dep = new DependenciaDeclarada(paquete, declarada, tipo, null, []);
        var servicio = new ServicioMetadatos(_reg, _cache, new InferidorFalso(), _tiempo);
        var meta = await servicio.ObtenerAsync(new ConsultaPaquete(paquete, tipo == TipoVersion.Exacta ? declarada : null, tfms));
        var contexto = new ContextoDiagnostico(dep, meta, tfms, Paquetes.Ahora);
        var diag = new MotorDiagnostico().Diagnosticar(contexto);
        var rec = await Motor(opciones).RecomendarAsync(contexto, diag, null, CancellationToken.None);
        return (diag, rec);
    }

    private static string Valor(Recomendacion r, string clave) => r.Evidencia.Single(e => e.Clave == clave).Valor;

    // ---------------- actualización del mismo paquete ----------------

    [Fact]
    public async Task Una_actualizacion_valida_se_recomienda_con_version_evidencia_y_limitaciones_y_sin_prometer_migracion_sin_cambios()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("1.5.0"));

        var (d, r) = await Recomendar("Lib.A", "1.0.0");

        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.ActualizarVersion, rec.Tipo);
        Assert.Equal(("Lib.A", "1.5.0"), (rec.Paquete, rec.VersionSugerida));
        Assert.False(rec.PosiblesCambiosDeCodigo);
        Assert.Contains("1.5.0", rec.Justificacion);
        Assert.Equal("1.0.0", Valor(rec, "version_declarada"));
        Assert.Equal("1.5.0", Valor(rec, "version_comprobada"));
        Assert.Contains("net8.0=compatible", Valor(rec, "compatibilidad_inferida"));
        Assert.Equal("cache_vigente", Valor(rec, "origen_de_los_datos"));   // la comprobación reutiliza la caché que dejó el diagnóstico
        Assert.Contains(rec.Limitaciones, l => l.Contains("se infiere") && l.Contains("no se compiló ni se ejecutó"));
        Assert.Contains(rec.Limitaciones, l => l.Contains("Cambios de código no determinados.") && l.Contains("no prueba que no sean necesarios"));
        Assert.DoesNotContain(rec.Limitaciones, l => l.Contains("sin cambios", StringComparison.OrdinalIgnoreCase) && !l.Contains("sin cambios de código"));
        Assert.DoesNotContain(rec.Limitaciones, l => l.Contains("no previstos", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(rec.Limitaciones, l => l.Contains("AOT no se evalúa"));
        Assert.Empty(r.Notas);
    }

    [Fact]
    public async Task Una_actualizacion_de_version_mayor_marca_posibles_cambios_de_codigo()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0"));

        var rec = Assert.Single((await Recomendar("Lib.A", "1.0.0")).R.Recomendaciones);

        Assert.True(rec.PosiblesCambiosDeCodigo);
        Assert.Contains(rec.Limitaciones, l => l.Contains("Puede requerir cambios de código"));
    }

    [Fact]
    public async Task No_se_recomienda_la_mayor_version_si_exige_un_framework_posterior()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0", [N10]));

        var (d, r) = await Recomendar("Lib.A", "1.0.0");

        Assert.Equal(EstadoDiagnostico.Actualizada, d.Estado);
        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "sin_recomendaciones");
    }

    [Fact]
    public async Task Con_varios_tfm_se_verifica_una_version_comun_antes_de_recomendar_la_actualizacion()
    {
        _reg.Paquete("Lib.A", V("1.0.0", Ambos), V("2.0.0", Ambos));

        var rec = Assert.Single((await Recomendar("Lib.A", "1.0.0", Ambos)).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.ActualizarVersion, rec.Tipo);
        Assert.Equal("2.0.0", rec.VersionSugerida);
        Assert.Equal("versión 2.0.0: net8.0=compatible, net10.0=compatible (inferida de los grupos de dependencias)", Valor(rec, "compatibilidad_inferida"));
        Assert.Equal("net8.0 y net10.0", Valor(rec, "frameworks_evaluados"));
        Assert.Equal("cache_vigente", Valor(rec, "origen_de_los_datos"));   // la fila de esa versión ya estaba en la caché de este análisis: no hizo falta otra consulta
    }

    [Fact]
    public async Task Con_varios_tfm_sin_version_comun_no_se_recomienda_una_actualizacion_global_solo_un_candidato_para_revision()
    {
        // 2.0.0 solo sirve para net8.0 y 3.0.0 solo para net10.0: ninguna es común.
        _reg.Paquete("Lib.A", V("1.0.0", Ambos), V("2.0.0", [N8]), V("3.0.0", [N10]));

        var (_, r) = await Recomendar("Lib.A", "1.0.0", Ambos);

        Assert.DoesNotContain(r.Recomendaciones, x => x.Tipo == TipoRecomendacion.ActualizarVersion);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Contains("NO está comprobado", rec.Justificacion);
        Assert.Contains(rec.Limitaciones, l => l.Contains("No se pudo comprobar la compatibilidad con todos los frameworks"));
        Assert.Contains(r.Notas, n => n.Codigo == "actualizacion_no_verificada");
    }

    [Fact]
    public async Task Si_la_version_candidata_esta_deprecada_no_se_recomienda_ni_se_ofrece()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0", dep: Obsoleto()));

        var (_, r) = await Recomendar("Lib.A", "1.0.0");

        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "actualizacion_no_verificada" && n.Mensaje.Contains("obsoleta"));
    }

    [Fact]
    public async Task Una_deprecada_con_una_version_posterior_no_deprecada_prioriza_actualizar_el_mismo_paquete_sobre_el_reemplazo()
    {
        _reg.Paquete("Lib.A", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[1.0.0, )")), V("1.2.0"))
            .Paquete("Lib.Nueva", V("1.0.0"));

        var (d, r) = await Recomendar("Lib.A", "1.0.0");

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal((TipoRecomendacion.ActualizarVersion, "Lib.A", "1.2.0"), (rec.Tipo, rec.Paquete, rec.VersionSugerida));
        Assert.Equal(0, _reg.Busquedas);
    }

    [Fact]
    public async Task Sin_framework_evaluable_la_actualizacion_es_un_candidato_para_revision_no_una_recomendacion_firme()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0"));

        var (_, r) = await Recomendar("Lib.A", "1.0.0", tfms: []);

        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Contains("framework evaluable", Valor(rec, "resultado_de_la_comprobacion"));
    }

    [Fact]
    public async Task Datos_incompletos_si_no_se_pueden_consultar_los_datos_de_la_version_candidata_se_presenta_para_revision()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0"));
        var servicio = new ServicioMetadatos(_reg, _cache, new InferidorFalso(), _tiempo);
        var meta = await servicio.ObtenerAsync(new ConsultaPaquete("Lib.A", "1.0.0", [N8]));
        var dep = new DependenciaDeclarada("Lib.A", "1.0.0", TipoVersion.Exacta, null, []);
        var contexto = new ContextoDiagnostico(dep, meta, [N8], Paquetes.Ahora);
        var diag = new MotorDiagnostico().Diagnosticar(contexto);
        _cache.FallaLectura = true;      // la caché falla
        _reg.Caidos.Add("Lib.A");        // y NuGet cae antes de verificar la versión candidata

        var r = await Motor().RecomendarAsync(contexto, diag, null, CancellationToken.None);

        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Contains(r.Notas, n => n.Codigo == "actualizacion_no_verificada");
    }

    // ---------------- reemplazo declarado por NuGet ----------------

    [Fact]
    public async Task El_reemplazo_declarado_con_datos_comprobados_es_una_alternativa_que_cita_al_autor_y_al_rango()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[2.0.0, )")))
            .Paquete("Lib.Nueva", V("1.0.0"), V("2.1.0"));

        var (d, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal((TipoRecomendacion.Alternativa, "Lib.Nueva", "2.1.0"), (rec.Tipo, rec.Paquete, rec.VersionSugerida));
        Assert.True(rec.PosiblesCambiosDeCodigo);
        Assert.Contains("el autor de Lib.Vieja", Valor(rec, "declarado_por"));
        Assert.Equal("[2.0.0, )", Valor(rec, "rango_declarado"));
        Assert.Contains(rec.Limitaciones, l => l.Contains("DiagNET no comprueba que su API sea equivalente"));
        Assert.Equal(0, _reg.Busquedas);   // con reemplazo declarado no se busca
    }

    [Fact]
    public async Task Se_respeta_el_rango_la_ultima_version_fuera_del_rango_no_se_recomienda_se_usa_la_minima_inclusiva_comprobada()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[1.0.0,2.0.0)")))
            .Paquete("Lib.Nueva", V("1.0.0"), V("3.0.0"));

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal((TipoRecomendacion.Alternativa, "1.0.0"), (rec.Tipo, rec.VersionSugerida));   // 3.0.0 queda fuera del rango declarado
    }

    [Fact]
    public async Task Si_ninguna_version_del_rango_se_puede_comprobar_se_presenta_para_revision_sin_version()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "(1.0.0,2.0.0)")))
            .Paquete("Lib.Nueva", V("1.0.0"), V("3.0.0"));

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Null(rec.VersionSugerida);
        Assert.Contains("no incluye ninguna", rec.Justificacion);
    }

    [Fact]
    public async Task Un_rango_alterno_que_no_se_puede_interpretar_no_se_afirma_como_alternativa()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "1.*-algo raro")))
            .Paquete("Lib.Nueva", V("1.0.0"));

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Contains(rec.Limitaciones, l => l.Contains("No se pudo interpretar el rango"));
    }

    [Fact]
    public async Task Un_reemplazo_declarado_inexistente_no_se_recomienda_y_se_explica()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.NoExiste", null)));

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "alternativa_declarada_inexistente");
        Assert.Contains(r.Notas, n => n.Codigo == "sin_recomendaciones");
    }

    [Fact]
    public async Task Un_reemplazo_declarado_que_tambien_esta_deprecado_no_es_admisible()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", null)))
            .Paquete("Lib.Nueva", V("1.0.0", dep: Obsoleto()));

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "alternativa_declarada_no_admisible" && n.Mensaje.Contains("obsoleta"));
    }

    [Theory]
    [InlineData("sin_grupos")]
    [InlineData("otro_framework")]
    public async Task Si_la_compatibilidad_del_reemplazo_no_puede_determinarse_es_candidato_para_revision_y_no_se_dice_que_sea_compatible(string caso)
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", null)))
            .Paquete("Lib.Nueva", caso == "sin_grupos" ? V("1.0.0", []) : V("1.0.0", ["net6.0"]));

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Contains("no se afirma que sea un reemplazo compatible", rec.Justificacion);
        Assert.Contains(rec.Limitaciones, l => l.Contains("No se pudo comprobar la compatibilidad"));
        Assert.DoesNotContain(r.Recomendaciones, x => x.Tipo == TipoRecomendacion.Alternativa);
    }

    [Fact]
    public async Task Si_no_se_pueden_consultar_los_datos_del_reemplazo_declarado_se_presenta_para_revision_y_se_conserva_el_diagnostico()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[1.0.0, )")));
        _reg.Caidos.Add("Lib.Nueva");

        var (d, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal((TipoRecomendacion.CandidatoParaRevision, "Lib.Nueva"), (rec.Tipo, rec.Paquete));
        Assert.Null(rec.VersionSugerida);
        Assert.Contains(r.Notas, n => n.Codigo == "alternativa_declarada_no_comprobada");
    }

    [Fact]
    public async Task El_reemplazo_declarado_en_una_version_no_exacta_no_se_atribuye_a_la_dependencia()
    {
        // P2: la deprecación de la última estable no se atribuye a una versión no exacta, así que no se usa su alternatePackage.
        _reg.Paquete("Lib.Vieja", V("2.0.0", dep: Obsoleto("Lib.Nueva", null))).Paquete("Lib.Nueva", V("1.0.0"));

        var (_, r) = await Recomendar("Lib.Vieja", "[1.0.0,)", tipo: TipoVersion.Rango);

        Assert.Empty(r.Recomendaciones);
    }

    // ---------------- búsqueda ----------------

    private static CandidatoBusqueda Cand(string id, string? version, long? descargas = 1000, bool? verificado = false) => new(id, version, descargas, verificado, "descripción");

    [Fact]
    public async Task Los_candidatos_de_busqueda_solo_se_ofrecen_para_revision_y_la_popularidad_no_los_justifica()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()))
            .Paquete("Lib.Otra", V("1.0.0"))
            .Paquete("Lib.Famosa", V("9.0.0", dep: Obsoleto()))        // muy popular pero deprecada
            .Paquete("Lib.Antigua", V("1.0.0", ["net472"]));           // sin compatibilidad comprobada
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto(
        [
            Cand("Lib.Famosa", "9.0.0", 900_000_000, true),
            Cand("Lib.Antigua", "1.0.0", 5_000_000, true),
            Cand("Lib.Otra", "1.0.0", 50, false),
        ]);

        var (d, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.Equal("Lib.Vieja", _reg.UltimoTextoBuscado);
        var rec = Assert.Single(r.Recomendaciones);
        Assert.Equal((TipoRecomendacion.CandidatoParaRevision, "Lib.Otra"), (rec.Tipo, rec.Paquete));
        Assert.Contains("NO está demostrada", rec.Justificacion);
        Assert.DoesNotContain("popular", rec.Justificacion, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("50", Valor(rec, "dato_informativo_descargas"));
        Assert.Contains(rec.Limitaciones, l => l.Contains("no prueban que resuelva lo mismo"));
        Assert.Contains(r.Notas, n => n.Codigo == "candidatos_descartados");
    }

    [Fact]
    public async Task Ninguna_busqueda_produce_una_alternativa_firme_ni_con_el_sello_de_verificado_ni_con_millones_de_descargas()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()))
            .Paquete("Lib.A", V("1.0.0")).Paquete("Lib.B", V("1.0.0")).Paquete("Lib.C", V("1.0.0")).Paquete("Lib.D", V("1.0.0"));
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto(
            new[] { "Lib.A", "Lib.B", "Lib.C", "Lib.D" }.Select(id => Cand(id, "1.0.0", 100_000_000, true)).ToList());

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(3, r.Recomendaciones.Count);   // se validan como máximo 3
        Assert.All(r.Recomendaciones, x => Assert.Equal(TipoRecomendacion.CandidatoParaRevision, x.Tipo));
    }

    [Fact]
    public async Task Una_busqueda_que_falla_no_pierde_el_diagnostico_solo_deja_una_nota()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.NoDisponible(CausaFallo.ErrorServidor, "503");

        var (d, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Estado);
        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "busqueda_alternativas_no_disponible");
        var nota = Assert.Single(r.Notas, n => n.Codigo == "recomendaciones_no_verificadas");
        Assert.Contains("No se pudieron obtener o verificar recomendaciones", nota.Mensaje);
        Assert.DoesNotContain(r.Notas, n => n.Codigo == "sin_recomendaciones");   // no se afirma «no hay»: no se pudo comprobar
    }

    [Fact]
    public async Task Una_busqueda_que_lanza_una_excepcion_tampoco_rompe_el_motor()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));
        _reg.Busqueda = _ => throw new InvalidOperationException("fallo inesperado");

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Contains(r.Notas, n => n.Codigo == "busqueda_alternativas_no_disponible");
    }

    [Fact]
    public async Task Cero_recomendaciones_es_un_resultado_valido_y_se_explica()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Empty(r.Recomendaciones);
        var nota = Assert.Single(r.Notas, n => n.Codigo == "sin_recomendaciones");
        Assert.Contains("No se encontraron recomendaciones en la búsqueda realizada", nota.Mensaje);
        Assert.Contains("resultado válido", nota.Mensaje);
        Assert.DoesNotContain(r.Notas, n => n.Codigo == "recomendaciones_no_verificadas");
        Assert.Contains("no inventa reemplazos", nota.Mensaje);
    }

    [Fact]
    public async Task La_busqueda_solo_se_hace_para_paquetes_sin_soporte_activo()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("1.1.0", [N10]));    // al día para net8.0; no hay nada que sugerir
        _reg.Busqueda = _ => throw new InvalidOperationException("no debe buscarse");

        var (_, r) = await Recomendar("Lib.A", "1.0.0");

        Assert.Equal(0, _reg.Busquedas);
        Assert.Empty(r.Recomendaciones);
    }

    // ---------------- ajustes confirmados (R1/R2 límite, R3 autor, R4 candidatos, R5 cambios, R6/R7 evaluación incompleta) ----------------

    [Fact]
    public async Task R1_R2_al_agotar_el_limite_de_verificaciones_se_indica_que_la_busqueda_fue_limitada_sin_afirmar_que_no_existe_una_version_comun()
    {
        _reg.Paquete("Lib.A", V("1.0.0", Ambos), V("2.0.0", [N8]), V("3.0.0", [N10]));

        var (_, r) = await Recomendar("Lib.A", "1.0.0", Ambos, opciones: new OpcionesRecomendacion { MaxVerificacionesPorPaquete = 1 });

        var nota = Assert.Single(r.Notas, n => n.Codigo == "actualizacion_no_verificada");
        Assert.Contains("Se alcanzó el límite de 1 verificaciones: la búsqueda fue limitada", nota.Mensaje);
        Assert.Contains("entre las versiones comprobadas (3.0.0)", nota.Mensaje);
        Assert.DoesNotContain("no existe", nota.Mensaje, StringComparison.OrdinalIgnoreCase);
        var evaluacion = Assert.Single(r.Notas, n => n.Codigo == "recomendaciones_evaluacion_incompleta");
        Assert.Contains("la búsqueda fue limitada", evaluacion.Mensaje);
        Assert.Contains(Assert.Single(r.Recomendaciones).Limitaciones, l => l.Contains("la búsqueda fue limitada"));
    }

    [Fact]
    public async Task R1_R2_sin_alcanzar_el_limite_la_nota_dice_entre_que_versiones_se_busco_y_no_afirma_que_no_exista_otra()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0", dep: Obsoleto()));

        var (_, r) = await Recomendar("Lib.A", "1.0.0");

        var nota = Assert.Single(r.Notas, n => n.Codigo == "actualizacion_no_verificada");
        Assert.Contains("entre las versiones comprobadas (2.0.0", nota.Mensaje);
        Assert.Contains("Eso no implica que no exista otra", nota.Mensaje);
        Assert.Contains("no todo el historial", nota.Mensaje);
        Assert.DoesNotContain(r.Notas, n => n.Codigo == "recomendaciones_evaluacion_incompleta");
    }

    [Fact]
    public async Task R3_la_alternativa_del_autor_dice_que_su_declaracion_no_demuestra_equivalencia_compatibilidad_ni_migracion_sencilla()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[2.0.0, )")))
            .Paquete("Lib.Nueva", V("1.0.0"), V("2.1.0"));

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.Alternativa, rec.Tipo);
        Assert.Equal("Alternativa sugerida por el autor", TextosRecomendacion.Etiqueta(rec.Tipo));
        Assert.StartsWith("Alternativa sugerida por el autor", rec.Justificacion);
        Assert.Contains("no demuestra equivalencia funcional, compatibilidad con todos los frameworks ni una migración sencilla", rec.Justificacion);
        Assert.Contains(rec.Limitaciones, l => l.Contains("La declaración del autor no demuestra equivalencia funcional"));
        Assert.Equal("equivalencia funcional, compatibilidad con todos los frameworks ni una migración sencilla", Valor(rec, "lo_que_no_demuestra_la_declaracion"));
        // la compatibilidad inferida de la versión concreta se conserva aparte de lo que declara el autor
        Assert.Equal("versión 2.1.0: net8.0=compatible (inferida de los grupos de dependencias)", Valor(rec, "compatibilidad_inferida"));
        Assert.Equal("2.1.0", Valor(rec, "version_comprobada"));
        Assert.Contains("el autor de Lib.Vieja", Valor(rec, "declarado_por"));
    }

    [Fact]
    public async Task R3_la_alternativa_del_autor_no_comprobada_tambien_se_presenta_como_sugerida_por_el_autor_sin_afirmar_compatibilidad()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", null))).Paquete("Lib.Nueva", V("1.0.0", ["net6.0"]));

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.StartsWith("Alternativa sugerida por el autor de Lib.Vieja", rec.Justificacion);
        Assert.Contains("no se afirma que sea un reemplazo compatible", rec.Justificacion);
        Assert.Contains("no demuestra equivalencia funcional", rec.Justificacion);
    }

    [Fact]
    public async Task R4_los_candidatos_de_busqueda_se_muestran_como_candidatos_para_revision_con_relacion_funcional_no_demostrada_y_nunca_como_reemplazos_recomendados()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto())).Paquete("Lib.Otra", V("1.0.0"));
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto([Cand("Lib.Otra", "1.0.0")]);

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.CandidatoParaRevision, rec.Tipo);
        Assert.Equal("Candidato para revisión (relación funcional no demostrada)", TextosRecomendacion.Etiqueta(rec.Tipo));
        Assert.StartsWith("Candidato para revisión:", rec.Justificacion);
        Assert.Contains("NO está demostrada", rec.Justificacion);
        Assert.Contains("no es un reemplazo recomendado", rec.Justificacion);
        Assert.DoesNotContain("recomendamos", rec.Justificacion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R5_cuando_el_indicador_es_false_se_muestra_cambios_de_codigo_no_determinados_nunca_sin_cambios_ni_cambios_no_previstos()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("1.5.0"));

        var rec = Assert.Single((await Recomendar("Lib.A", "1.0.0")).R.Recomendaciones);

        Assert.False(rec.PosiblesCambiosDeCodigo);
        Assert.Equal("Cambios de código no determinados.", TextosRecomendacion.CambiosDeCodigo(false));
        Assert.Contains(rec.Limitaciones, l => l.StartsWith("Cambios de código no determinados."));
        Assert.All(rec.Limitaciones.Append(rec.Justificacion), t =>
        {
            Assert.DoesNotContain("sin cambios", t.Replace("sin cambios de código", ""), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no previstos", t, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no requiere", t, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task R5_cuando_el_indicador_es_true_se_muestra_puede_requerir_cambios_de_codigo()
    {
        _reg.Paquete("Lib.A", V("1.0.0"), V("2.0.0"));

        var rec = Assert.Single((await Recomendar("Lib.A", "1.0.0")).R.Recomendaciones);

        Assert.True(rec.PosiblesCambiosDeCodigo);
        Assert.Equal("Puede requerir cambios de código.", TextosRecomendacion.CambiosDeCodigo(true));
        Assert.Contains(rec.Limitaciones, l => l.StartsWith("Puede requerir cambios de código."));
    }

    [Fact]
    public async Task R6_R7_se_distingue_no_se_encontraron_recomendaciones_de_no_se_pudieron_obtener_o_verificar()
    {
        // (a) búsqueda realizada sin resultados
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));
        var (_, encontrado) = await Recomendar("Lib.Vieja", "1.0.0");
        // (b) búsqueda caída
        var otro = new MotorRecomendacionTests();
        otro._reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));
        otro._reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.NoDisponible(CausaFallo.Red, "sin red");
        var (_, caido) = await otro.Recomendar("Lib.Vieja", "1.0.0");

        Assert.Equal(["sin_recomendaciones"], encontrado.Notas.Select(n => n.Codigo));
        Assert.StartsWith("No se encontraron recomendaciones en la búsqueda realizada", encontrado.Notas.Single().Mensaje);
        Assert.Contains(caido.Notas, n => n.Codigo == "recomendaciones_no_verificadas" && n.Mensaje.StartsWith("No se pudieron obtener o verificar recomendaciones"));
        Assert.DoesNotContain(caido.Notas, n => n.Codigo == "sin_recomendaciones");
    }

    [Fact]
    public async Task R6_R7_con_alguna_recomendacion_pero_datos_no_consultables_se_indica_que_no_se_evaluaron_completamente_y_por_que()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", "[1.0.0, )")));
        _reg.Caidos.Add("Lib.Nueva");

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Single(r.Recomendaciones);
        var nota = Assert.Single(r.Notas, n => n.Codigo == "recomendaciones_evaluacion_incompleta");
        Assert.Contains("no se evaluaron completamente", nota.Mensaje);
        Assert.Contains("no se pudieron consultar los datos de Lib.Nueva", nota.Mensaje);
    }

    [Fact]
    public async Task Los_candidatos_de_busqueda_que_no_se_pudieron_verificar_por_falta_de_datos_hacen_la_evaluacion_incompleta_no_vacia()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto())).Paquete("Lib.Caida", V("1.0.0"));
        _reg.Caidos.Add("Lib.Caida");
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.Correcto([Cand("Lib.Caida", "1.0.0")]);

        var (_, r) = await Recomendar("Lib.Vieja", "1.0.0");

        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "recomendaciones_no_verificadas" && n.Mensaje.Contains("no se pudieron verificar por falta de datos"));
        Assert.DoesNotContain(r.Notas, n => n.Codigo == "sin_recomendaciones");
    }

    [Fact]
    public void La_version_de_reglas_de_recomendacion_es_una_revision_fija_que_cabe_con_la_del_diagnostico_en_la_columna()
    {
        var motor = Motor();
        var compuesta = $"{new MotorDiagnostico().VersionReglas}+{motor.VersionReglas}";

        Assert.Equal("e7-1.0", motor.VersionReglas);
        Assert.Equal("e6-1.1|ant=24m|agr=pes+e7-1.0", compuesta);
        Assert.True(compuesta.Length <= LimitesDatos.VersionReglas);
    }

    // ---------------- licencia, AOT y datos ----------------

    [Fact]
    public async Task La_licencia_es_solo_informativa_un_reemplazo_con_licencia_restrictiva_no_se_filtra()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto("Lib.Nueva", null)))
            .Paquete("Lib.Nueva", V("1.0.0", licencia: "GPL-3.0-only"));

        var rec = Assert.Single((await Recomendar("Lib.Vieja", "1.0.0")).R.Recomendaciones);

        Assert.Equal(TipoRecomendacion.Alternativa, rec.Tipo);
        Assert.Equal("GPL-3.0-only", Valor(rec, "licencia_informativa"));
        Assert.Contains(rec.Limitaciones, l => l.Contains("solo como información"));
    }

    [Fact]
    public async Task Sin_datos_de_nuget_no_se_recomienda_nada_y_se_dice()
    {
        var dep = new DependenciaDeclarada("Lib.A", "1.0.0", TipoVersion.Exacta, null, []);
        var meta = new ResultadoMetadato(OrigenDato.NoDeterminado, null, null, MotivoSinDatos.NuGetNoDisponible, "caído", [], 0);
        var contexto = new ContextoDiagnostico(dep, meta, [N8], Paquetes.Ahora);
        var diag = new MotorDiagnostico().Diagnosticar(contexto);

        var r = await Motor().RecomendarAsync(contexto, diag, null, CancellationToken.None);

        Assert.Empty(r.Recomendaciones);
        Assert.Contains(r.Notas, n => n.Codigo == "recomendaciones_no_verificadas");
        Assert.DoesNotContain(r.Notas, n => n.Codigo == "sin_recomendaciones");
    }

    [Fact]
    public async Task Los_limites_de_la_busqueda_se_respetan_pide_5_y_valida_3()
    {
        _reg.Paquete("Lib.Vieja", V("1.0.0", dep: Obsoleto()));
        var pedidos = 0;
        var servicio = new ServicioMetadatos(_reg, _cache, new InferidorFalso(), _tiempo);
        var nuget = new BusquedaConLimite(_reg, m => pedidos = m);
        var dep = new DependenciaDeclarada("Lib.Vieja", "1.0.0", TipoVersion.Exacta, null, []);
        var meta = await servicio.ObtenerAsync(new ConsultaPaquete("Lib.Vieja", "1.0.0", [N8]));
        var contexto = new ContextoDiagnostico(dep, meta, [N8], Paquetes.Ahora);

        await new MotorRecomendacion(servicio, nuget).RecomendarAsync(contexto, new MotorDiagnostico().Diagnosticar(contexto), null, CancellationToken.None);

        Assert.Equal(5, pedidos);
    }

    private sealed class BusquedaConLimite(RegistroFalso registro, Action<int> maximo) : IClienteNuGet
    {
        public Task<ResultadoNuGet<InformacionPaquete>> ObtenerPaqueteAsync(ConsultaPaquete consulta, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default) =>
            registro.ObtenerPaqueteAsync(consulta, presupuesto, ct);

        public Task<ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> BuscarCandidatosAsync(
            string texto, string? excluirPaquete, int max, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
        {
            maximo(max);
            return registro.BuscarCandidatosAsync(texto, excluirPaquete, max, presupuesto, ct);
        }

        public Task<ResultadoNuGet<long?>> ObtenerDescargasAsync(string paquete, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
