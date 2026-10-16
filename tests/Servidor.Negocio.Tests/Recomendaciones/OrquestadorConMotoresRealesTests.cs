using System.Text;
using Microsoft.Extensions.Time.Testing;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Recomendaciones;
using Servidor.Negocio.Reglas;
using Servidor.Negocio.Tests.Orquestacion;

namespace Servidor.Negocio.Tests.Recomendaciones;

/// <summary>Orquestador (E5) + diagnóstico real (E6) + recomendaciones reales (E7) sobre un registro simulado.</summary>
public class OrquestadorConMotoresRealesTests
{
    private readonly RegistroFalso _reg = new();
    private readonly CacheFalsa _cache = new();
    private readonly AnalisisRepositorioFalso _repo = new();
    private readonly FakeTimeProvider _tiempo = new(Paquetes.Ahora);

    private static VersionPublicada V(string version, string[]? grupos = null, Deprecacion? dep = null) => Paquetes.Version(version, grupos ?? ["net8.0"], dep);

    private OrquestadorAnalisis Crear()
    {
        var servicio = new ServicioMetadatos(_reg, _cache, new InferidorFalso(), _tiempo);
        return new OrquestadorAnalisis(new LectorCsproj(new OpcionesLectura()), servicio, new MotorDiagnostico(), new MotorRecomendacion(servicio, _reg), _repo, _tiempo);
    }

    private static MemoryStream Csproj(string tfms, params (string Nombre, string Version)[] paquetes) => new(Encoding.UTF8.GetBytes(
        $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>{tfms}</TargetFrameworks></PropertyGroup><ItemGroup>"
        + string.Concat(paquetes.Select(p => $"<PackageReference Include=\"{p.Nombre}\" Version=\"{p.Version}\" />")) + "</ItemGroup></Project>"));

    [Fact]
    public async Task Un_proyecto_mixto_produce_diagnosticos_y_recomendaciones_justificadas_por_dependencia()
    {
        _reg.Paquete("Lib.AlDia", V("1.0.0"))
            .Paquete("Lib.Vieja", V("1.0.0"), V("1.4.0"))
            .Paquete("Lib.Obsoleta", V("1.0.0", dep: new Deprecacion(["Legacy"], null, "Lib.Sucesora", "[1.0.0, )")))
            .Paquete("Lib.Sucesora", V("3.0.0"));

        var r = await Crear().AnalizarAsync("Mi.csproj", Csproj("net8.0", ("Lib.AlDia", "1.0.0"), ("Lib.Vieja", "1.0.0"), ("Lib.Obsoleta", "1.0.0")));

        var deps = r.Analisis.Dependencias;
        Assert.Equal([EstadoDiagnostico.Actualizada, EstadoDiagnostico.Desactualizada, EstadoDiagnostico.SinSoporteActivo], deps.Select(d => d.Diagnostico.Estado));
        Assert.Empty(deps[0].Recomendaciones);                                         // al día: sin recomendaciones y sin ruido
        Assert.Equal((TipoRecomendacion.ActualizarVersion, "1.4.0"), (deps[1].Recomendaciones.Single().Tipo, deps[1].Recomendaciones.Single().VersionSugerida));
        Assert.Equal((TipoRecomendacion.Alternativa, "Lib.Sucesora"), (deps[2].Recomendaciones.Single().Tipo, deps[2].Recomendaciones.Single().Paquete));
        Assert.All(deps.SelectMany(d => d.Recomendaciones), x =>
        {
            Assert.NotEmpty(x.Justificacion);
            Assert.NotEmpty(x.Evidencia);
            Assert.NotEmpty(x.Limitaciones);
        });
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);
    }

    [Fact]
    public async Task Si_la_busqueda_falla_el_diagnostico_se_conserva_con_su_nota_y_el_analisis_no_se_vuelve_parcial()
    {
        _reg.Paquete("Lib.Obsoleta", V("1.0.0", dep: new Deprecacion(["Legacy"], null, null, null)));
        _reg.Busqueda = _ => ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.NoDisponible(CausaFallo.ErrorServidor, "503");

        var r = await Crear().AnalizarAsync("Mi.csproj", Csproj("net8.0", ("Lib.Obsoleta", "1.0.0")));

        var d = Assert.Single(r.Analisis.Dependencias);
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, d.Diagnostico.Estado);
        Assert.Empty(d.Recomendaciones);
        Assert.Contains(d.Advertencias, a => a.Codigo == "busqueda_alternativas_no_disponible");
        Assert.Contains(d.Advertencias, a => a.Codigo == "recomendaciones_no_verificadas");
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);                      // el diagnóstico sigue completo...
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "recomendaciones_incompletas");   // ...pero se avisa de que las recomendaciones no se evaluaron por completo
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "recomendaciones_incompletas" && a.Mensaje.Contains("El diagnóstico no se ve afectado"));
        Assert.Single(_repo.Guardados);
    }

    [Fact]
    public async Task Un_error_inesperado_al_recomendar_conserva_el_diagnostico_y_las_demas_dependencias()
    {
        _reg.Paquete("Lib.Obsoleta", V("1.0.0", dep: new Deprecacion(["Legacy"], null, "Lib.Rara", null)))
            .Paquete("Lib.Vieja", V("1.0.0"), V("1.4.0"))
            .Paquete("Lib.Rara", V("1.0.0"));
        _reg.Lanzar.Add("Lib.Rara");

        var r = await Crear().AnalizarAsync("Mi.csproj", Csproj("net8.0", ("Lib.Obsoleta", "1.0.0"), ("Lib.Vieja", "1.0.0")));

        var rota = r.Analisis.Dependencias[0];
        Assert.Equal(EstadoDiagnostico.SinSoporteActivo, rota.Diagnostico.Estado);
        Assert.Empty(rota.Recomendaciones);
        Assert.Contains(rota.Advertencias, a => a.Codigo == "recomendacion_no_disponible");
        Assert.Contains(r.Analisis.Advertencias, a => a.Codigo == "recomendaciones_incompletas");
        Assert.Single(r.Analisis.Dependencias[1].Recomendaciones);                // la otra dependencia no pierde su recomendación
    }

    [Fact]
    public async Task Con_dos_frameworks_sin_version_comun_el_analisis_conserva_el_diagnostico_por_tfm_y_no_afirma_una_actualizacion_global()
    {
        _reg.Paquete("Lib.A", V("1.0.0", ["net8.0", "net10.0"]), V("2.0.0", ["net8.0"]), V("3.0.0", ["net10.0"]));

        var r = await Crear().AnalizarAsync("Mi.csproj", Csproj("net8.0;net10.0", ("Lib.A", "1.0.0")));

        var d = Assert.Single(r.Analisis.Dependencias);
        Assert.Equal(EstadoDiagnostico.Desactualizada, d.Diagnostico.Estado);
        Assert.Equal(["net8.0", "net10.0"], d.Diagnostico.EvaluacionPorTfm.Select(t => t.Tfm));
        Assert.DoesNotContain(d.Recomendaciones, x => x.Tipo == TipoRecomendacion.ActualizarVersion);
        Assert.Contains(d.Advertencias, a => a.Codigo == "actualizacion_no_verificada");
    }

    [Fact]
    public async Task Las_notas_del_motor_de_recomendaciones_no_cambian_la_parcialidad_del_analisis()
    {
        _reg.Paquete("Lib.Obsoleta", V("1.0.0", dep: new Deprecacion(["Legacy"], null, null, null)));

        var r = await Crear().AnalizarAsync("Mi.csproj", Csproj("net8.0", ("Lib.Obsoleta", "1.0.0")));

        Assert.Contains(Assert.Single(r.Analisis.Dependencias).Advertencias, a => a.Codigo == "sin_recomendaciones");
        Assert.Equal(EstadoAnalisis.Completo, r.Analisis.Estado);        // cero recomendaciones es un resultado válido, no una parcialidad
        Assert.DoesNotContain(r.Analisis.Advertencias, a => a.Codigo == "recomendaciones_incompletas");   // y la búsqueda se hizo por completo: no hay aviso de evaluación incompleta
        Assert.Equal("e6-1.1|ant=24m|agr=pes+e7-1.0", r.Analisis.VersionReglas);
    }
}
