using System.Text;
using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Reglas;
using static Servidor.Negocio.Tests.Orquestacion.Paquetes;

namespace Servidor.Negocio.Tests.Orquestacion;

/// <summary>
/// Orquestador (E5) + MOTOR REAL de diagnóstico (E6). Lo único que sigue siendo sustituto de pruebas es el motor de recomendaciones (E7),
/// la caché en memoria y NuGet simulado.
/// </summary>
public class OrquestadorConMotorRealTests
{
    private readonly NuGetFalso _nuget = new();
    private readonly CacheFalsa _cache = new();
    private readonly AnalisisRepositorioFalso _repo = new();
    private readonly MotorRecomendacionDePrueba _recomendador = new();   // SUSTITUTO EXCLUSIVO DE PRUEBAS (E7 pendiente)
    private readonly MotorDiagnostico _motor = new();
    private readonly FakeTimeProvider _tiempo = new(Ahora);

    private OrquestadorAnalisis Crear() => new(
        new LectorCsproj(new OpcionesLectura()), new ServicioMetadatos(_nuget, _cache, new InferidorFalso(), _tiempo), _motor, _recomendador, _repo, _tiempo);

    private static string Csproj(string tfms, params (string Nombre, string Version)[] paquetes) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>{tfms}</TargetFrameworks></PropertyGroup><ItemGroup>"
        + string.Concat(paquetes.Select(p => $"<PackageReference Include=\"{p.Nombre}\" Version=\"{p.Version}\" />")) + "</ItemGroup></Project>";

    private static MemoryStream Flujo(string xml) => new(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public async Task El_analisis_guarda_la_version_de_reglas_del_motor_real_y_estados_reproducibles()
    {
        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "2.0.0"), ("Ejemplo.Dos", "1.0.0"))));

        Assert.Equal("e6-1.1|ant=24m|agr=pes", r.Analisis.VersionReglas);
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada], r.Analisis.Dependencias.Select(d => d.Diagnostico.Estado));
        Assert.All(r.Analisis.Dependencias, d => Assert.NotNull(d.Diagnostico.Indicadores));
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
    }

    [Fact]
    public async Task Solo_las_dependencias_deficientes_reciben_recomendaciones_y_llevan_su_hallazgo_con_evidencia()
    {
        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "2.0.0"), ("Ejemplo.Dos", "1.0.0"))));

        Assert.Equal(["Ejemplo.Dos"], _recomendador.Paquetes);
        var dos = r.Analisis.Dependencias[1];
        Assert.Single(dos.Recomendaciones);
        var h = dos.Diagnostico.Hallazgos.First(x => x.Codigo == "version_posterior_compatible");
        Assert.Contains(h.Evidencia, e => e is { Clave: "version_posterior_compatible", Valor: "2.0.0" });
    }

    [Fact]
    public async Task Con_NuGet_caido_y_copia_vencida_el_motor_diagnostica_con_los_datos_viejos_y_conserva_origen_y_fecha()
    {
        var vieja = Ahora.AddDays(-3);
        var paraCache = ConversorCache.Convertir(Info("Ejemplo.Uno", "1.0.0", vieja));
        foreach (var fila in paraCache.Metadatos) await _cache.GuardarAsync(fila);
        await _cache.GuardarSeleccionAsync(paraCache.Seleccion!);
        _nuget.Comportamiento = (_, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.NoDisponible(CausaFallo.ErrorServidor, "503"));

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "1.0.0"))));

        var d = Assert.Single(r.Analisis.Dependencias);
        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Diagnostico.Estado);
        Assert.Equal(OrigenDato.CacheExpirada, d.Diagnostico.Origen);
        Assert.Equal(vieja, d.Diagnostico.FechaConsulta);
        Assert.Contains(d.Advertencias, a => a.Codigo == "cache_expirada");
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);   // la copia vencida por sí sola no vuelve parcial el análisis
    }

    [Fact]
    public async Task Un_paquete_inexistente_es_no_determinado_con_su_explicacion_y_vuelve_parcial_el_analisis_sin_perder_los_demas()
    {
        _nuget.Comportamiento = (c, _, _) => Task.FromResult(c.Paquete == "Ejemplo.NoExiste"
            ? ResultadoNuGet<InformacionPaquete>.NoEncontrado("No existe en nuget.org.")
            : ResultadoNuGet<InformacionPaquete>.Correcto(Info(c.Paquete, c.VersionDeclarada), [], 2));

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "2.0.0"), ("Ejemplo.NoExiste", "1.0.0"))));

        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        var malo = r.Analisis.Dependencias[1].Diagnostico;
        Assert.Equal(EstadoDiagnostico.NoDeterminada, malo.Estado);
        Assert.Null(malo.Indicadores);
        Assert.Null(malo.FechaConsulta);
        Assert.Contains("No existe en nuget.org.", malo.Motivos);
        Assert.Equal(EstadoDiagnostico.Actualizada, r.Analisis.Dependencias[0].Diagnostico.Estado);
    }

    [Fact]
    public async Task Una_version_en_rango_no_se_resuelve_y_el_analisis_es_parcial_con_la_explicacion_del_motor()
    {
        var xml = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>"
                  + "<PackageReference Include=\"Ejemplo.Uno\" Version=\"[1.0.0,2.0.0)\" /></ItemGroup></Project>";

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(xml));

        var d = Assert.Single(r.Analisis.Dependencias).Diagnostico;
        Assert.Equal(EstadoDiagnostico.NoDeterminada, d.Estado);
        Assert.Contains(d.Hallazgos, h => h.Codigo == "version_no_resuelta");
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
    }

    [Fact]
    public async Task Con_dos_frameworks_el_diagnostico_guarda_el_estado_por_tfm_y_el_global_pesimista()
    {
        // La declarada 1.0.0 solo tiene grupo net8.0; la 2.0.0 es la última para net8.0 y no existe versión compatible con net10.0.
        _nuget.Comportamiento = (c, _, _) =>
        {
            var info = Info(c.Paquete, c.VersionDeclarada, ultima: "2.0.0") with
            {
                CompatibilidadDeclarada =
                [
                    new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups"),
                    new CompatibilidadTfm("net10.0", ResultadoCompatibilidad.Incompatible, "inferida_dependency_groups"),
                ],
                UltimaEstableCompatiblePorTfm = new Dictionary<string, VersionPublicada?> { ["net8.0"] = Version("2.0.0"), ["net10.0"] = null },
            };
            return Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(info, [], 2));
        };

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0;net10.0", ("Ejemplo.Uno", "1.0.0"))));

        var d = Assert.Single(r.Analisis.Dependencias).Diagnostico;
        // net10.0: la falta de un grupo compatible NO demuestra incompatibilidad → no determinada; net8.0 sí tiene evidencia de «desactualizada».
        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Estado);
        Assert.Equal([new EvaluacionTfm("net8.0", EstadoDiagnostico.Desactualizada), new EvaluacionTfm("net10.0", EstadoDiagnostico.NoDeterminada)], d.EvaluacionPorTfm);
        Assert.True(d.TieneEvidenciaIncompleta());
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);   // estado negativo conocido + evidencia faltante ⇒ parcial
        Assert.Equal(["net8.0", "net10.0"], r.Analisis.FrameworksEvaluados);
    }

    [Fact]
    public async Task El_resultado_del_motor_real_no_cambia_si_se_repite_el_analisis_con_los_mismos_datos()
    {
        var xml = Csproj("net8.0", ("Ejemplo.Uno", "2.0.0"), ("Ejemplo.Dos", "1.0.0"));

        var a = await Crear().AnalizarAsync("Mi.csproj", Flujo(xml));
        var b = await Crear().AnalizarAsync("Mi.csproj", Flujo(xml));

        Assert.Equal(Reglas.Casos.Serializar(a.Analisis.Dependencias.Select(d => d.Diagnostico with { Origen = OrigenDato.Nuget, FechaConsulta = null }).ToList()),
                     Reglas.Casos.Serializar(b.Analisis.Dependencias.Select(d => d.Diagnostico with { Origen = OrigenDato.Nuget, FechaConsulta = null }).ToList()));
    }

    [Fact]
    public async Task Un_estado_negativo_con_evidencia_decisiva_faltante_vuelve_parcial_el_analisis_y_lo_explica()
    {
        // Deprecada (negativo conocido) pero con el registro leído solo en parte y sin última estable conocida.
        _nuget.Comportamiento = (c, _, _) =>
        {
            var info = Info(c.Paquete, c.VersionDeclarada, completa: false, depDeclarada: new Deprecacion(["Legacy"], null, null, null)) with
            {
                UltimaEstable = null,
                UltimaEstableCompatiblePorTfm = new Dictionary<string, VersionPublicada?> { ["net8.0"] = null },
            };
            return Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(info, [], 2));
        };

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "1.0.0"))));

        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, Assert.Single(r.Analisis.Dependencias).Diagnostico.Estado);
        Assert.Equal(EstadoAnalisis.Parcial, r.Analisis.Estado);
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "analisis_parcial" && a.Mensaje.Contains("evidencia decisiva incompleta"));
    }

    [Fact]
    public async Task Un_estado_negativo_con_toda_la_evidencia_no_vuelve_parcial_el_analisis()
    {
        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Dos", "1.0.0"))));

        Assert.Equal(EstadoDiagnostico.Desactualizada, Assert.Single(r.Analisis.Dependencias).Diagnostico.Estado);
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
    }

    [Fact]
    public async Task Licencia_descargas_antiguedad_y_aot_desconocidos_por_si_solos_no_vuelven_parcial_el_analisis()
    {
        _nuget.Comportamiento = (c, _, _) =>
        {
            var info = Info(c.Paquete, c.VersionDeclarada, ultima: "1.0.0", descargas: null) with { UltimaPublicacion = null };
            info = info with { Declarada = info.Declarada! with { Licencia = null, TipoLicencia = null } };
            return Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(info, [], 2));
        };

        var r = await Crear().AnalizarAsync("Mi.csproj", Flujo(Csproj("net8.0", ("Ejemplo.Uno", "1.0.0"))));

        Assert.Equal(EstadoDiagnostico.Actualizada, Assert.Single(r.Analisis.Dependencias).Diagnostico.Estado);
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
    }
}
