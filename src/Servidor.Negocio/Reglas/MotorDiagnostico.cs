using Servidor.Negocio.Dominio;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Negocio.Reglas;

/// <summary>
/// Motor de diagnóstico (E6). Aplica las reglas en un orden fijo, conserva TODOS los hallazgos y consolida el estado:
/// <list type="number">
/// <item>Por TFM: gana el hallazgo negativo más grave (incompatible &gt; sin soporte activo &gt; desactualizada). Sin negativos: «no determinada» si falta evidencia
/// decisiva (para ese TFM o para el paquete); si no falta nada, «actualizada».</item>
/// <item>Global: agregación PESIMISTA (el proyecto debe funcionar en todos sus TFM): gana el negativo más grave de cualquier TFM; sin negativos, «no determinada» si
/// algún TFM o el paquete carece de evidencia decisiva; si no, «actualizada».</item>
/// </list>
/// Los datos que faltan nunca borran un hallazgo negativo conocido: se listan como limitaciones. Licencia, descargas, antigüedad y AOT no cambian el estado.
/// Es puro y determinista: mismo contexto (incluida la hora que llega en él) ⇒ mismo diagnóstico. No usa red, base de datos ni reloj propio.
/// </summary>
public sealed class MotorDiagnostico : IMotorDiagnostico
{
    private static readonly EstadoDiagnostico[] Negativos = [EstadoDiagnostico.Incompatible, EstadoDiagnostico.SinSoporteActivo, EstadoDiagnostico.Desactualizada];

    private readonly ParametrosDiagnostico _parametros;
    private readonly IReadOnlyList<IReglaDiagnostico> _reglas;

    public MotorDiagnostico(ParametrosDiagnostico? parametros = null, IEnumerable<IReglaDiagnostico>? reglas = null)
    {
        _parametros = parametros ?? new ParametrosDiagnostico();
        _reglas = (reglas ?? ReglasPorDefecto()).ToList();
    }

    public static IReadOnlyList<IReglaDiagnostico> ReglasPorDefecto() =>
        [new ReglaFramework(), new ReglaSoporteActivo(), new ReglaActualizacion(), new ReglaAntiguedad(), new ReglaLicencia(), new ReglaAot()];

    public string VersionReglas => _parametros.VersionReglas;

    public Diagnostico Diagnosticar(ContextoDiagnostico contexto)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        var metadato = contexto.Metadato;
        var tfms = contexto.FrameworksEvaluados;

        if (metadato.Informacion is not { } info)
            return SinEvidencia(contexto);

        var entrada = new EntradaReglas(contexto, info, _parametros);
        var hallazgos = new List<HallazgoDeRegla>();
        var faltantes = new List<(string Regla, Faltante F)>();
        foreach (var regla in _reglas)
        {
            var r = regla.Evaluar(entrada);
            hallazgos.AddRange(r.Hallazgos);
            faltantes.AddRange(r.Faltantes.Select(f => (regla.Nombre, f)));
        }

        var porTfm = tfms.Select(t => new EvaluacionTfm(t, EstadoDe(hallazgos, faltantes.Select(x => x.F).ToList(), t))).ToList();
        var global = EstadoGlobal(hallazgos, faltantes.Select(x => x.F).ToList(), porTfm);

        var lista = hallazgos.Select(h => h.Hallazgo)
            .Concat(faltantes.Select(x => new Hallazgo(x.Regla, x.F.Codigo, GravedadHallazgo.Aviso, x.F.Descripcion,
                x.F.Tfm is null ? [] : [new Evidencia("tfm", x.F.Tfm)], EvidenciaDecisivaFaltante: true)))
            .Select((h, i) => (h, i))
            .OrderByDescending(x => x.h.Gravedad)   // estable: dentro de la misma gravedad se conserva el orden de las reglas
            .ThenBy(x => x.i)
            .Select(x => x.h)
            .ToList();

        return new Diagnostico(global, Explicar(global, hallazgos, faltantes.Select(x => x.F).ToList()), info.UltimaEstable?.Version, lista, porTfm,
            metadato.Origen, metadato.FechaDatos, ConstructorIndicadores.Construir(entrada));
    }

    private static EstadoDiagnostico EstadoDe(List<HallazgoDeRegla> hallazgos, List<Faltante> faltantes, string tfm)
    {
        var negativos = hallazgos.Where(h => h.Estado is { } s && Negativos.Contains(s) && (h.Tfm is null || Igual(h.Tfm, tfm))).Select(h => h.Estado!.Value).ToList();
        if (negativos.Count > 0) return MasGrave(negativos);
        return faltantes.Any(f => f.Tfm is null || Igual(f.Tfm, tfm)) ? EstadoDiagnostico.NoDeterminada : EstadoDiagnostico.Actualizada;
    }

    private static EstadoDiagnostico EstadoGlobal(List<HallazgoDeRegla> hallazgos, List<Faltante> faltantes, List<EvaluacionTfm> porTfm)
    {
        var negativos = hallazgos.Where(h => h.Estado is { } s && Negativos.Contains(s)).Select(h => h.Estado!.Value).ToList();
        if (negativos.Count > 0) return MasGrave(negativos);
        return faltantes.Count > 0 || porTfm.Any(t => t.Estado == EstadoDiagnostico.NoDeterminada) ? EstadoDiagnostico.NoDeterminada : EstadoDiagnostico.Actualizada;
    }

    private static EstadoDiagnostico MasGrave(IEnumerable<EstadoDiagnostico> estados) => estados.MinBy(s => Array.IndexOf(Negativos, s));

    private static bool Igual(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Explicar(EstadoDiagnostico estado, List<HallazgoDeRegla> hallazgos, List<Faltante> faltantes)
    {
        var titulo = estado switch
        {
            EstadoDiagnostico.Incompatible => "Incompatible: la evidencia es suficiente para afirmar que la versión declarada no es compatible con algún framework evaluado.",
            EstadoDiagnostico.SinSoporteActivo => "Sin soporte activo: NuGet marca el paquete como obsoleto.",
            EstadoDiagnostico.Desactualizada => "Desactualizada: en al menos un framework evaluado existe una versión estable más reciente compatible.",
            EstadoDiagnostico.NoDeterminada => "No determinada: falta evidencia decisiva y no se conoce ningún hallazgo negativo.",
            _ => "Actualizada: con la evidencia disponible no se halló ningún hallazgo negativo."
        };

        var motivos = hallazgos.Where(h => h.Hallazgo.Gravedad >= GravedadHallazgo.Aviso).Select(h => h.Hallazgo.Mensaje).ToList();
        var texto = titulo;
        if (motivos.Count > 0) texto += " Motivos: " + string.Join(" ", motivos);
        if (faltantes.Count > 0) texto += " Limitaciones: " + string.Join(" ", faltantes.Select(f => f.Descripcion));
        if (faltantes.Count > 0 && estado != EstadoDiagnostico.NoDeterminada)
            texto += " Evidencia incompleta: este estado se basa en hallazgos conocidos, pero falta evidencia decisiva; el análisis se marca como parcial.";
        return texto;
    }

    private Diagnostico SinEvidencia(ContextoDiagnostico c)
    {
        var motivo = c.Metadato.Mensaje ?? "No se obtuvieron datos de NuGet para este paquete.";
        var hallazgos = new List<Hallazgo>
        {
            new("evidencia", CodigoSinEvidencia(c.Metadato.Motivo), GravedadHallazgo.Aviso, motivo, [], EvidenciaDecisivaFaltante: true),
            new("aot", "aot_no_evaluado", GravedadHallazgo.Informativo, "La compatibilidad con AOT no se evalúa en esta versión de DiagNET: no se afirma ni se descarta.", []),
        };
        return new Diagnostico(EstadoDiagnostico.NoDeterminada,
            "No determinada: no hay datos de NuGet ni copia en caché para este paquete. " + motivo,
            null, hallazgos, c.FrameworksEvaluados.Select(t => new EvaluacionTfm(t, EstadoDiagnostico.NoDeterminada)).ToList(),
            c.Metadato.Origen, c.Metadato.FechaDatos, null);
    }

    private static string CodigoSinEvidencia(MotivoSinDatos? motivo) => motivo switch
    {
        MotivoSinDatos.NoExisteEnNuGet => "paquete_no_existe_en_nuget",
        MotivoSinDatos.NuGetNoDisponible => "nuget_no_disponible",
        MotivoSinDatos.PresupuestoAgotado => "presupuesto_agotado",
        MotivoSinDatos.CircuitoAbierto => "nuget_no_disponible_circuito_abierto",
        MotivoSinDatos.ErrorInterno => "error_interno_consulta",
        _ => "sin_evidencia"
    };
}
