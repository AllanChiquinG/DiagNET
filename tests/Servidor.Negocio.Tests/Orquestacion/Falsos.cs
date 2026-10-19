using System.Collections.Concurrent;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Negocio.Tests.Orquestacion;

// ============================================================================================================================
// DOBLES DE PRUEBA. NADA de este archivo se usa en producción. En particular, MotorDiagnosticoDePrueba y MotorRecomendacionDePrueba
// son SUSTITUTOS EXCLUSIVOS DE PRUEBAS de los motores definitivos de E6 y E7, que NO están implementados todavía: sirven solo para ejercitar
// el orquestador (E5) con los puertos IMotorDiagnostico e IMotorRecomendacion. No representan las reglas ni la precedencia de E6.
// ============================================================================================================================

internal sealed class NuGetFalso : IClienteNuGet
{
    private int _llamadas;
    public int Llamadas => _llamadas;
    public ConcurrentQueue<ConsultaPaquete> Consultas { get; } = new();

    public Func<ConsultaPaquete, int, CancellationToken, Task<ResultadoNuGet<InformacionPaquete>>> Comportamiento { get; set; } =
        (c, _, _) => Task.FromResult(ResultadoNuGet<InformacionPaquete>.Correcto(Paquetes.Info(c.Paquete, c.VersionDeclarada), [], 2));

    public async Task<ResultadoNuGet<InformacionPaquete>> ObtenerPaqueteAsync(ConsultaPaquete consulta, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default)
    {
        var n = Interlocked.Increment(ref _llamadas);
        Consultas.Enqueue(consulta);
        return await Comportamiento(consulta, n, ct);
    }

    public Task<ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>> BuscarCandidatosAsync(string texto, string? excluirPaquete, int maximo, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<ResultadoNuGet<long?>> ObtenerDescargasAsync(string paquete, PresupuestoTiempo? presupuesto = null, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class CacheFalsa : IMetadatoRepositorio
{
    private readonly object _candado = new();
    private readonly Dictionary<(string, string), MetadatoLibreria> _filas = new();
    private readonly Dictionary<string, SeleccionPaquete> _selecciones = new();

    public bool FallaLectura { get; set; }
    public bool FallaEscritura { get; set; }
    public int Lecturas { get; set; }
    public int Escrituras { get; set; }

    public MetadatoLibreria? Fila(string nombre, string version)
    {
        lock (_candado) return _filas.GetValueOrDefault((NormalizadorPaquetes.Identificador(nombre), NormalizadorPaquetes.VersionNormalizada(version)));
    }

    public SeleccionPaquete? Seleccion(string nombre)
    {
        lock (_candado) return _selecciones.GetValueOrDefault(NormalizadorPaquetes.Identificador(nombre));
    }

    public Task<MetadatoLibreria?> BuscarAsync(string nombrePaquete, string version, CancellationToken ct = default)
    {
        lock (_candado)
        {
            Lecturas++;
            if (FallaLectura) throw new InvalidOperationException("base de datos caída (lectura)");
            return Task.FromResult(Fila(nombrePaquete, version));
        }
    }

    public Task GuardarAsync(MetadatoLibreria metadato, CancellationToken ct = default)
    {
        lock (_candado)
        {
            Escrituras++;
            if (FallaEscritura) throw new InvalidOperationException("base de datos caída (escritura)");
            var clave = (NormalizadorPaquetes.Identificador(metadato.NombrePaquete), NormalizadorPaquetes.VersionNormalizada(metadato.Version));
            if (!_filas.TryGetValue(clave, out var actual) || actual.FechaConsulta <= metadato.FechaConsulta)
                _filas[clave] = metadato with { Version = clave.Item2 };
        }

        return Task.CompletedTask;
    }

    public Task<SeleccionPaquete?> BuscarSeleccionAsync(string nombrePaquete, CancellationToken ct = default)
    {
        lock (_candado)
        {
            Lecturas++;
            if (FallaLectura) throw new InvalidOperationException("base de datos caída (lectura)");
            return Task.FromResult(Seleccion(nombrePaquete));
        }
    }

    public Task GuardarSeleccionAsync(SeleccionPaquete seleccion, CancellationToken ct = default)
    {
        lock (_candado)
        {
            Escrituras++;
            if (FallaEscritura) throw new InvalidOperationException("base de datos caída (escritura)");
            var clave = NormalizadorPaquetes.Identificador(seleccion.NombrePaquete);
            if (!_selecciones.TryGetValue(clave, out var actual) || actual.FechaConsulta <= seleccion.FechaConsulta) _selecciones[clave] = seleccion;
        }

        return Task.CompletedTask;
    }

    public Task<int> PurgarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<int> PurgarSeleccionesAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class AnalisisRepositorioFalso : IAnalisisRepositorio
{
    private readonly List<Analisis> _guardados = [];
    public IReadOnlyList<Analisis> Guardados { get { lock (_guardados) return _guardados.ToList(); } }
    public bool FallaGuardado { get; set; }
    public int IntentosDeGuardado { get; private set; }

    public Task GuardarAsync(Analisis analisis, CancellationToken ct = default)
    {
        lock (_guardados)
        {
            IntentosDeGuardado++;
            if (FallaGuardado) throw new InvalidOperationException("base de datos caída (guardado)");
            _guardados.Add(analisis);
        }

        return Task.CompletedTask;
    }

    public Task<Analisis?> ObtenerAsync(Guid id, CancellationToken ct = default) => Task.FromResult(Guardados.FirstOrDefault(a => a.Id == id));

    public Task<Pagina<ResumenAnalisis>> ListarAsync(int numeroPagina, int tamanoPagina, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<int> EliminarAnterioresAsync(DateTimeOffset limite, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class InferidorFalso : IInferidorCompatibilidad
{
    public CompatibilidadTfm Inferir(string tfm, IReadOnlyList<string> grupos) => new(tfm,
        grupos.Count == 0 ? ResultadoCompatibilidad.NoDeterminada
        : grupos.Any(g => g.Length == 0 || g == tfm) ? ResultadoCompatibilidad.Compatible
        : ResultadoCompatibilidad.Incompatible, "inferida_dependency_groups");
}

/// <summary>SUSTITUTO EXCLUSIVO DE PRUEBAS del motor de E6. Reglas mínimas e inventadas solo para ejercitar el orquestador.</summary>
internal sealed class MotorDiagnosticoDePrueba : IMotorDiagnostico
{
    public string VersionReglas => "prueba-e6";
    public Func<ContextoDiagnostico, Diagnostico?>? Anular { get; set; }
    public ConcurrentQueue<string> Evaluadas { get; } = new();
    private int _simultaneas;
    public int MaximoSimultaneo { get; private set; }
    public TimeSpan Retardo { get; set; }

    public Diagnostico Diagnosticar(ContextoDiagnostico c)
    {
        var n = Interlocked.Increment(ref _simultaneas);
        lock (Evaluadas) MaximoSimultaneo = Math.Max(MaximoSimultaneo, n);
        try
        {
            if (Retardo > TimeSpan.Zero) Thread.Sleep(Retardo);
            Evaluadas.Enqueue(c.Dependencia.Nombre);
            if (Anular?.Invoke(c) is { } forzado) return forzado;

            var info = c.Metadato.Informacion;
            if (info is null)
                return Resultado(EstadoDiagnostico.NoDeterminada, $"Sin datos: {c.Metadato.Mensaje}", null, c);

            if (info.Declarada?.Deprecacion is not null)
                return Resultado(EstadoDiagnostico.SinSoporteActivo, "Marcado como obsoleto en NuGet.", info.UltimaEstable?.Version, c);
            if (info.CompatibilidadDeclarada.Any(x => x.Resultado == ResultadoCompatibilidad.Incompatible))
                return Resultado(EstadoDiagnostico.Incompatible, "La versión declarada no declara compatibilidad.", info.UltimaEstable?.Version, c);
            if (c.Dependencia.TipoVersion != TipoVersion.Exacta || info.EstadoDeclarada != EstadoVersionDeclarada.Publicada)
                return Resultado(EstadoDiagnostico.NoDeterminada, "La versión declarada no es exacta o no se halló.", info.UltimaEstable?.Version, c);
            if (info.UltimaEstable is not null && info.UltimaEstable.Version != info.Declarada!.Version)
                return Resultado(EstadoDiagnostico.Desactualizada, "Hay una versión más reciente.", info.UltimaEstable.Version, c);
            return Resultado(EstadoDiagnostico.Actualizada, "Al día.", info.UltimaEstable?.Version, c);
        }
        finally
        {
            Interlocked.Decrement(ref _simultaneas);
        }
    }

    private static Diagnostico Resultado(EstadoDiagnostico estado, string motivo, string? ultima, ContextoDiagnostico c) => new(
        estado, motivo, ultima, [], c.FrameworksEvaluados.Select(t => new EvaluacionTfm(t, estado)).ToList(),
        OrigenDato.Nuget /* el orquestador lo sobrescribe */, DateTimeOffset.UnixEpoch /* el orquestador lo sobrescribe */, null);
}

/// <summary>SUSTITUTO EXCLUSIVO DE PRUEBAS del motor de E7.</summary>
internal sealed class MotorRecomendacionDePrueba : IMotorRecomendacion
{
    public string VersionReglas => "prueba-e7";
    private int _llamadas;
    public int Llamadas => _llamadas;
    public ConcurrentQueue<string> Paquetes { get; } = new();
    public bool Falla { get; set; }

    public Task<ResultadoRecomendacion> RecomendarAsync(ContextoDiagnostico c, Diagnostico d, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        Interlocked.Increment(ref _llamadas);
        Paquetes.Enqueue(c.Dependencia.Nombre);
        if (Falla) throw new InvalidOperationException("fallo del motor de recomendación");
        IReadOnlyList<Recomendacion> r = [new Recomendacion(TipoRecomendacion.ActualizarVersion, c.Dependencia.Nombre, d.UltimaVersion, "Recomendación de prueba.", [], [], false)];
        return Task.FromResult(new ResultadoRecomendacion(r, []));
    }
}

internal static class Paquetes
{
    public static readonly DateTimeOffset Ahora = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    public static VersionPublicada Version(string v, string[]? grupos = null, Deprecacion? dep = null, string? licencia = "MIT", DateTimeOffset? publicada = null) => new(
        v, v, v.Contains('-'), true, publicada ?? Ahora.AddMonths(-3), licencia, licencia is null ? null : TipoLicencia.Expresion, dep, grupos ?? ["net8.0"], []);

    /// <summary>Información de un paquete con las versiones 1.0.0 (declarada por defecto) y 2.0.0 (última estable), ambas compatibles con net8.0.</summary>
    public static InformacionPaquete Info(string paquete, string? declarada, DateTimeOffset? fecha = null, string ultima = "2.0.0", bool completa = true,
        Deprecacion? depDeclarada = null, long? descargas = 1000)
    {
        var decl = declarada is null ? null : Version(declarada, dep: depDeclarada);
        var estable = Version(ultima);
        return new InformacionPaquete(
            paquete, declarada is null ? EstadoVersionDeclarada.NoSolicitada : EstadoVersionDeclarada.Publicada, decl,
            decl is null ? [] : [new CompatibilidadTfm("net8.0", ResultadoCompatibilidad.Compatible, "inferida_dependency_groups")],
            estable, null, new Dictionary<string, VersionPublicada?> { ["net8.0"] = estable }, estable.Publicada, completa, 1, 1, 5,
            fecha ?? Ahora, descargas, null);
    }

    public static DependenciaDeclarada Dep(string nombre, string? version = "1.0.0", TipoVersion tipo = TipoVersion.Exacta, string? condicion = null) =>
        new(nombre, version, tipo, condicion, []);

    public static ProyectoLeido Proyecto(string[] frameworks, params DependenciaDeclarada[] deps) => new(
        "Prueba.csproj", frameworks.Select(f => new FrameworkDeclarado(f, true)).ToList(), deps, []);

    public static ProyectoLeido Proyecto(params DependenciaDeclarada[] deps) => Proyecto(["net8.0"], deps);
}
