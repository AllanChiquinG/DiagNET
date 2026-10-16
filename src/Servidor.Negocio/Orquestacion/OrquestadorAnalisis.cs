using System.Collections.Concurrent;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Orquestacion;

public sealed class OpcionesOrquestacion
{
    /// <summary>Presupuesto global de un análisis, incluyendo esperas y reintentos (plan: 120 s).</summary>
    public TimeSpan PresupuestoAnalisis { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Dependencias procesadas a la vez. El gateway limita además las solicitudes HTTP simultáneas (4).</summary>
    public int MaxParalelismo { get; set; } = 8;
}

/// <summary>Estadísticas útiles para medir consultas y tiempos (E12); no forman parte del resultado guardado.</summary>
public sealed record ResultadoOrquestacion(
    Analisis Analisis,
    int SolicitudesHttp,
    TimeSpan Duracion,
    bool PresupuestoAgotado,
    IReadOnlyDictionary<OrigenDato, int> DependenciasPorOrigen);

/// <summary>No se pudo guardar el resultado: fallo GLOBAL (el análisis no queda a medias en la base).</summary>
public sealed class AlmacenamientoNoDisponibleException(Exception interna)
    : Exception("No se pudo guardar el resultado del análisis.", interna);

/// <summary>
/// Coordina un análisis completo: lectura (E2) → metadatos por dependencia con caché y deduplicación (E4/E5) → diagnóstico y recomendaciones
/// (puertos de E6/E7) → guardado ATÓMICO del resultado final, una sola vez. Garantías:
/// <list type="bullet">
/// <item>Presupuesto global (120 s) con esperas y reintentos incluidos; al agotarse, lo pendiente pasa a «no determinada», el análisis es parcial y se guarda.</item>
/// <item>Un fallo por paquete es local: ni un 404, ni una caída de NuGet, ni un error de un motor eliminan los diagnósticos de las demás dependencias.</item>
/// <item>Fallo global solo si no se puede guardar (<see cref="AlmacenamientoNoDisponibleException"/>) o si la entrada no es válida (nada se consulta ni guarda).</item>
/// <item>Cancelación pedida por el llamador: se propaga y no se guarda nada. No existen estados persistidos «en proceso».</item>
/// <item>El orquestador no contiene reglas de diagnóstico ni de recomendación.</item>
/// </list>
/// </summary>
public sealed class OrquestadorAnalisis(
    ILectorProyecto lector, ServicioMetadatos metadatos, IMotorDiagnostico motorDiagnostico, IMotorRecomendacion motorRecomendacion,
    IAnalisisRepositorio repositorio, TimeProvider tiempo, OpcionesOrquestacion? opciones = null)
{
    private static readonly EstadoDiagnostico[] Deficientes =
        [EstadoDiagnostico.Desactualizada, EstadoDiagnostico.Incompatible, EstadoDiagnostico.SinSoporteActivo];

    private static readonly HashSet<string> CodigosRecomendacionIncompleta =
        ["recomendaciones_no_verificadas", "recomendaciones_evaluacion_incompleta", "recomendacion_no_disponible"];

    private readonly OpcionesOrquestacion _opciones = opciones ?? new OpcionesOrquestacion();

    /// <summary>Lee el archivo (si no es válido lanza <see cref="ArchivoInvalidoException"/>: no se consulta NuGet ni se guarda nada) y lo analiza.</summary>
    public Task<ResultadoOrquestacion> AnalizarAsync(string? nombreArchivo, Stream contenido, CancellationToken ct = default) =>
        AnalizarAsync(lector.Leer(nombreArchivo, contenido), ct);

    public async Task<ResultadoOrquestacion> AnalizarAsync(ProyectoLeido proyecto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proyecto);
        ct.ThrowIfCancellationRequested();

        var inicio = tiempo.GetUtcNow();
        var evaluados = proyecto.Frameworks.Where(f => f.EnAlcance == true).Select(f => f.Valor).ToList();
        using var presupuesto = new PresupuestoTiempo(_opciones.PresupuestoAnalisis, tiempo);

        // Una consulta por (paquete, versión, TFM) dentro del análisis; las simultáneas equivalentes de otros análisis las comparte ServicioMetadatos.
        var memoria = new ConcurrentDictionary<string, Lazy<Task<ResultadoMetadato>>>();
        var solicitudesContadas = new ConcurrentDictionary<string, byte>();
        var solicitudes = 0;
        using var cupo = new SemaphoreSlim(Math.Max(1, _opciones.MaxParalelismo));

        async Task<DependenciaAnalizada> ProcesarAsync(DependenciaDeclarada dep)
        {
            await cupo.WaitAsync(ct);
            try
            {
                var consulta = new ConsultaPaquete(dep.Nombre, dep.TipoVersion == TipoVersion.Exacta ? dep.Version : null, evaluados);
                var clave = ServicioMetadatos.Clave(consulta);
                var lazy = memoria.GetOrAdd(clave, _ => new Lazy<Task<ResultadoMetadato>>(() => ConsultarSeguroAsync(consulta, presupuesto, ct)));
                var metadato = await lazy.Value;
                if (solicitudesContadas.TryAdd(clave, 0)) Interlocked.Add(ref solicitudes, metadato.SolicitudesHttp);

                return await DiagnosticarAsync(dep, metadato, evaluados, presupuesto, ct);
            }
            finally
            {
                cupo.Release();
            }
        }

        var resultados = await Task.WhenAll(proyecto.Dependencias.Select(ProcesarAsync));
        ct.ThrowIfCancellationRequested();

        var porOrigen = resultados.GroupBy(d => d.Diagnostico.Origen).ToDictionary(g => g.Key, g => g.Count());
        var degradadas = memoria.Values.Select(l => l.Value.Result).Count(r => r.DegradadoPorPresupuesto);
        var advertencias = proyecto.Advertencias.Select(ADominio).ToList();
        var noDeterminadas = resultados.Count(d => d.Diagnostico.Estado == EstadoDiagnostico.NoDeterminada);

        if (degradadas > 0)
            advertencias.Add(new("presupuesto_agotado",
                $"Se alcanzó el tiempo máximo del análisis ({_opciones.PresupuestoAnalisis.TotalSeconds:0} s): {degradadas} consulta(s) se resolvieron solo con la caché o quedaron sin determinar."));
        // Las recomendaciones pueden no haberse evaluado por completo SIN que el diagnóstico deje de estar completo: se avisa, pero no se vuelve parcial el análisis.
        var recomendacionesIncompletas = resultados.Count(d => d.Advertencias.Any(a => CodigosRecomendacionIncompleta.Contains(a.Codigo)));
        if (recomendacionesIncompletas > 0)
            advertencias.Add(new("recomendaciones_incompletas",
                $"En {recomendacionesIncompletas} dependencia(s) las recomendaciones no se pudieron obtener o evaluar por completo (ver el detalle de cada una). El diagnóstico no se ve afectado."));
        if (evaluados.Count == 0)
            advertencias.Add(new("sin_framework_evaluable",
                "Ningún framework del proyecto se pudo evaluar (ausente, con variable o fuera de alcance): la compatibilidad no se determina."));

        // Parcial: alguna dependencia sin estado determinado, o con un estado (aunque sea negativo) al que le falta evidencia decisiva, o ningún TFM resuelto.
        // La incertidumbre no se esconde detrás de un estado negativo. Caché vencida, licencia, descargas, antigüedad o AOT no lo vuelven parcial.
        var incompletas = resultados.Count(d => d.Diagnostico.Estado != EstadoDiagnostico.NoDeterminada && d.Diagnostico.TieneEvidenciaIncompleta());
        var parcial = noDeterminadas > 0 || incompletas > 0 || evaluados.Count == 0;
        if (noDeterminadas > 0 || incompletas > 0)
        {
            var partes = new List<string>();
            if (noDeterminadas > 0) partes.Add($"{noDeterminadas} de {resultados.Length} dependencia(s) no pudieron determinarse");
            if (incompletas > 0) partes.Add($"{incompletas} tiene(n) un estado conocido pero con evidencia decisiva incompleta");
            advertencias.Add(new("analisis_parcial", string.Join("; ", partes) + ". Puedes volver a analizar el archivo más tarde."));
        }

        var analisis = new Analisis(
            Guid.NewGuid(),
            proyecto.NombreArchivo,
            proyecto.Frameworks.Select(f => f.Valor).ToList(),
            evaluados,
            inicio,
            parcial ? EstadoAnalisis.Parcial : EstadoAnalisis.Completo,
            resultados,
            advertencias,
            $"{motorDiagnostico.VersionReglas}+{motorRecomendacion.VersionReglas}");

        // Guardado único y atómico del resultado final. No usa el presupuesto (aunque se haya agotado el resultado parcial se guarda).
        try
        {
            await repositorio.GuardarAsync(analisis, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AlmacenamientoNoDisponibleException(ex);
        }

        return new ResultadoOrquestacion(analisis, solicitudes, tiempo.GetUtcNow() - inicio, degradadas > 0, porOrigen);
    }

    private async Task<ResultadoMetadato> ConsultarSeguroAsync(ConsultaPaquete consulta, PresupuestoTiempo presupuesto, CancellationToken ct)
    {
        try
        {
            return await metadatos.ObtenerAsync(consulta, presupuesto, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un error inesperado en una consulta afecta solo a esa dependencia.
            var referencia = Guid.NewGuid().ToString("N")[..8];
            return new ResultadoMetadato(OrigenDato.NoDeterminado, null, null, MotivoSinDatos.ErrorInterno,
                $"Error interno al consultar los datos del paquete (referencia {referencia}).",
                [new Advertencia("error_interno", $"No se pudieron obtener los datos de {consulta.Paquete} por un error interno (referencia {referencia}).", consulta.Paquete)], 0);
        }
    }

    private async Task<DependenciaAnalizada> DiagnosticarAsync(
        DependenciaDeclarada dep, ResultadoMetadato metadato, IReadOnlyList<string> evaluados, PresupuestoTiempo presupuesto, CancellationToken ct)
    {
        var contexto = new ContextoDiagnostico(dep, metadato, evaluados, tiempo.GetUtcNow());
        var avisos = dep.Advertencias.Select(ADominio).Concat(metadato.Advertencias).ToList();

        Diagnostico diagnostico;
        try
        {
            diagnostico = motorDiagnostico.Diagnosticar(contexto);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var referencia = Guid.NewGuid().ToString("N")[..8];
            avisos.Add(new("error_interno", $"No se pudo diagnosticar {dep.Nombre} por un error interno (referencia {referencia}).", dep.Nombre));
            diagnostico = new Diagnostico(EstadoDiagnostico.NoDeterminada,
                $"No se pudo diagnosticar esta librería por un error interno (referencia {referencia}). No es un error de tu proyecto.", null,
                [new Hallazgo("orquestador", "error_interno", GravedadHallazgo.Aviso, "Falló la evaluación de esta dependencia.", [])],
                evaluados.Select(t => new EvaluacionTfm(t, EstadoDiagnostico.NoDeterminada)).ToList(),
                metadato.Origen, metadato.FechaDatos, null);
        }

        // Origen y fecha son hechos del dato (RN-06): se conservan siempre, sea cual sea el motor. Una copia vencida conserva su fecha ORIGINAL.
        diagnostico = diagnostico with { Origen = metadato.Origen, FechaConsulta = metadato.FechaDatos };

        IReadOnlyList<Recomendacion> recomendaciones = [];
        if (Deficientes.Contains(diagnostico.Estado))
        {
            try
            {
                var resultado = await motorRecomendacion.RecomendarAsync(contexto, diagnostico, presupuesto, ct);
                recomendaciones = resultado.Recomendaciones;
                avisos.AddRange(resultado.Notas);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                avisos.Add(new("recomendacion_no_disponible",
                    $"No se pudo generar una recomendación para {dep.Nombre}; el diagnóstico se conserva.", dep.Nombre));
            }
        }

        return new DependenciaAnalizada(dep.Nombre, dep.Version, dep.TipoVersion, dep.Condicion, avisos, diagnostico, recomendaciones);
    }

    private static Advertencia ADominio(AdvertenciaLectura a) => new(a.Codigo, a.Mensaje, a.Dependencia);
}
