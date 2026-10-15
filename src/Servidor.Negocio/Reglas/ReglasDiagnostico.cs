using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;

namespace Servidor.Negocio.Reglas;

internal static class Ayuda
{
    public static Evidencia E(string clave, string? valor) => new(clave, valor ?? "(sin dato)");

    public static string Lista(IEnumerable<string> textos) => string.Join(", ", textos);
}

/// <summary>Versión declarada frente a versiones estables posteriores COMPATIBLES con cada TFM evaluado (DEC-01, propuesta). La antigüedad es otra regla.</summary>
public sealed class ReglaActualizacion : IReglaDiagnostico
{
    public string Nombre => "actualizacion";

    public ResultadoRegla Evaluar(EntradaReglas e)
    {
        var hallazgos = new List<HallazgoDeRegla>();
        var faltantes = new List<Faltante>();
        var dep = e.Contexto.Dependencia;

        if (dep.TipoVersion != TipoVersion.Exacta)
        {
            faltantes.Add(new("version_no_resuelta",
                $"La versión declarada ({dep.Version ?? "ausente"}) es de tipo «{dep.TipoVersion}», no una versión exacta: DiagNET no la resuelve, así que no se compara con versiones posteriores."));
            return new(hallazgos, faltantes);
        }

        if (!e.VersionResuelta)
        {
            faltantes.Add(e.Info.EstadoDeclarada == EstadoVersionDeclarada.NoPublicada
                ? new("version_declarada_no_publicada", $"La versión {dep.Version} no figura en el registro de nuget.org para este paquete.")
                : new("version_declarada_no_determinada", $"No se pudo determinar la versión {dep.Version} en el registro de nuget.org."));
            return new(hallazgos, faltantes);
        }

        var declarada = e.Info.Declarada!.Version;
        foreach (var tfm in e.Frameworks)
        {
            var candidata = e.PorTfm(tfm);
            var cmp = candidata is null ? null : ComparadorVersiones.Comparar(candidata.Version, declarada);

            if (candidata is not null && cmp is null)
            {
                faltantes.Add(new("version_no_comparable", $"No se pudo comparar {candidata.Version} con {declarada} (formato no interpretable).", tfm));
            }
            else if (cmp > 0)
            {
                hallazgos.Add(new(new Hallazgo(Nombre, "version_posterior_compatible", GravedadHallazgo.Aviso,
                    $"Hay una versión estable más reciente compatible con {tfm}: {candidata!.Version} (declarada: {declarada}).",
                    [Ayuda.E("tfm", tfm), Ayuda.E("version_declarada", declarada), Ayuda.E("version_posterior_compatible", candidata.Version),
                     Ayuda.E("seleccion_completa", e.Info.SeleccionCompleta ? "si" : "no"), Ayuda.E("compatibilidad", "inferida de los grupos de dependencias")]),
                    EstadoDiagnostico.Desactualizada, tfm));
                if (!e.Info.SeleccionCompleta)
                    hallazgos.Add(new(new Hallazgo(Nombre, "seleccion_incompleta_version_sugerida", GravedadHallazgo.Aviso,
                        $"No se recorrió todo el registro: {candidata.Version} es la más reciente compatible con {tfm} entre las leídas, pero puede existir otra posterior.",
                        [Ayuda.E("tfm", tfm), Ayuda.E("version_posterior_compatible", candidata.Version)]), null, tfm));
            }
            else if (!e.Info.SeleccionCompleta)
            {
                faltantes.Add(new("seleccion_incompleta",
                    $"No se recorrió todo el registro del paquete: no se puede afirmar que {declarada} sea la última versión estable compatible con {tfm}.", tfm));
            }
            else if (e.Info.UltimaEstable is { } ultima && ComparadorVersiones.Comparar(ultima.Version, declarada) > 0)
            {
                hallazgos.Add(new(new Hallazgo(Nombre, "version_posterior_no_compatible", GravedadHallazgo.Informativo,
                    $"Existe {ultima.Version}, pero no declara compatibilidad con {tfm}: no se considera una actualización posible.",
                    [Ayuda.E("tfm", tfm), Ayuda.E("ultima_estable", ultima.Version), Ayuda.E("version_declarada", declarada)]), null, tfm));
            }
        }


        // P1: la actualización se confirma POR TFM. Una versión común a todos los TFM solo se da por verificada si el mejor candidato de cada TFM es el mismo.
        var porTfm = e.Frameworks.Select(t => (Tfm: t, V: e.PorTfm(t))).ToList();
        var algunaDesactualizada = hallazgos.Any(h => h.Estado == EstadoDiagnostico.Desactualizada);
        var comunVerificada = porTfm.All(x => x.V is not null) && porTfm.Select(x => x.V!.Version).Distinct().Count() == 1;
        if (e.Frameworks.Count > 1 && algunaDesactualizada && !comunVerificada)
            hallazgos.Add(new(new Hallazgo(Nombre, "version_comun_no_verificada", GravedadHallazgo.Aviso,
                "Las versiones más recientes compatibles son: " + Ayuda.Lista(porTfm.Select(x => $"{x.Tfm}={x.V?.Version ?? "ninguna"}"))
                + ". No se ha verificado una versión común a todos los frameworks: una actualización global necesita comprobarla.",
                porTfm.Select(x => Ayuda.E("ultima_compatible_" + x.Tfm, x.V?.Version)).ToList())));
        return new(hallazgos, faltantes);
    }
}

/// <summary>
/// Deprecación publicada en NuGet (DEC-02, P2 confirmada). Solo se atribuye a la dependencia la deprecación de la versión DECLARADA cuando es exacta y está
/// disponible. Si la versión es desconocida, variable o un rango, la deprecación de la última estable es información del paquete, no de la dependencia instalada.
/// </summary>
public sealed class ReglaSoporteActivo : IReglaDiagnostico
{
    public string Nombre => "soporte_activo";

    public ResultadoRegla Evaluar(EntradaReglas e)
    {
        if (!e.VersionResuelta)
        {
            var hallazgos = new List<HallazgoDeRegla>();
            if (e.Info.UltimaEstable is { DeprecacionConocida: true, Deprecacion: { } d } ultima)
                hallazgos.Add(new(new Hallazgo(Nombre, "ultima_estable_deprecada", GravedadHallazgo.Informativo,
                    $"Información del paquete (no de tu dependencia): la última versión estable, {ultima.Version}, está marcada como obsoleta en NuGet ({Razones(d)}). "
                    + "No se atribuye a la versión instalada porque no se pudo resolver.", Evidencia(ultima, d))));
            return new(hallazgos, [new("deprecacion_no_determinada",
                "No se puede afirmar si la versión instalada está marcada como obsoleta porque su versión no es una versión exacta presente en el registro.")]);
        }

        var version = e.Info.Declarada!;
        if (!version.DeprecacionConocida)
            return new([], [new("deprecacion_no_determinada", "No se pudo determinar si NuGet marca la versión declarada como obsoleta.")]);

        if (version.Deprecacion is not { } depr)
            return new([new(new Hallazgo(Nombre, "sin_deprecacion_publicada", GravedadHallazgo.Informativo,
                $"NuGet no publica ninguna deprecación para {version.Version}. Esto no garantiza soporte activo ni correcciones de seguridad.",
                [Ayuda.E("version_evaluada", version.Version)]))], []);

        return new([new(new Hallazgo(Nombre, "paquete_obsoleto", GravedadHallazgo.Grave,
            $"NuGet marca {version.Version} como obsoleto ({Razones(depr)}). Es evidencia publicada por el autor; su ausencia no garantiza soporte y su presencia no prueba por sí sola falta de correcciones.",
            Evidencia(version, depr)), EstadoDiagnostico.SinSoporteActivo)], []);
    }

    private static string Razones(Deprecacion d) => d.Razones.Count == 0 ? "sin razón indicada" : Ayuda.Lista(d.Razones);

    private static List<Evidencia> Evidencia(VersionPublicada v, Deprecacion d)
    {
        var evidencia = new List<Evidencia> { Ayuda.E("version_evaluada", v.Version), Ayuda.E("razones", Razones(d)) };
        if (d.Mensaje is not null) evidencia.Add(Ayuda.E("mensaje_del_autor", d.Mensaje));
        if (d.PaqueteAlterno is not null)
            evidencia.Add(Ayuda.E("paquete_alterno", d.RangoVersionAlterno is null ? d.PaqueteAlterno : $"{d.PaqueteAlterno} {d.RangoVersionAlterno}"));
        return evidencia;
    }
}

/// <summary>Compatibilidad de la versión declarada con cada TFM evaluado. Siempre INFERIDA de los grupos de dependencias (DEC-10, propuesta).</summary>
public sealed class ReglaFramework : IReglaDiagnostico
{
    public string Nombre => "framework";

    public ResultadoRegla Evaluar(EntradaReglas e)
    {
        var hallazgos = new List<HallazgoDeRegla>();
        var faltantes = new List<Faltante>();

        if (e.Frameworks.Count == 0)
        {
            faltantes.Add(new("sin_framework_evaluable", "El proyecto no tiene un framework evaluable (ausente, con variable o fuera de alcance): la compatibilidad no se determina."));
            return new(hallazgos, faltantes);
        }

        foreach (var tfm in e.Frameworks)
        {
            var c = e.Info.CompatibilidadDeclarada.FirstOrDefault(x => string.Equals(x.Tfm, tfm, StringComparison.OrdinalIgnoreCase));
            var ev = new List<Evidencia> { Ayuda.E("tfm", tfm) };
            switch (c?.Resultado)
            {
                case ResultadoCompatibilidad.Incompatible when e.Parametros.BasesSuficientesParaIncompatible.Contains(c.Base):
                    ev.Add(Ayuda.E("base", c.Base));
                    ev.Add(Ayuda.E("detalle", c.Detalle));
                    hallazgos.Add(new(new Hallazgo(Nombre, "incompatible_con_evidencia_suficiente", GravedadHallazgo.Grave,
                        $"La versión declarada es incompatible con {tfm} según la evidencia «{c.Base}» (inferida; no se ejecutó ni se compiló nada).", ev),
                        EstadoDiagnostico.Incompatible, tfm));
                    break;
                case ResultadoCompatibilidad.Incompatible:
                    // Un paquete sin grupo de dependencias compatible NO demuestra incompatibilidad (el registro no expone sus ensamblados): no se concluye.
                    faltantes.Add(new("sin_grupo_compatible_inferido",
                        $"El paquete no declara un grupo de dependencias compatible con {tfm}. Eso no demuestra incompatibilidad (el registro de NuGet no expone los ensamblados del paquete), así que no se afirma; compatibilidad inferida, no determinada.", tfm));
                    break;
                case ResultadoCompatibilidad.Compatible:
                    ev.Add(Ayuda.E("base", c.Base));
                    ev.Add(Ayuda.E("detalle", c.Detalle));
                    hallazgos.Add(new(new Hallazgo(Nombre, "compatible_inferida", GravedadHallazgo.Informativo,
                        $"La versión declarada declara compatibilidad con {tfm}. Es una inferencia a partir de los grupos de dependencias: no certifica ensamblados ni ejecución.", ev), null, tfm));
                    break;
                default:
                    faltantes.Add(new("compatibilidad_no_determinada",
                        c?.Detalle is { } d
                            ? $"No se pudo determinar la compatibilidad con {tfm}: {d}"
                            : $"No se pudo determinar la compatibilidad de la versión declarada con {tfm}.", tfm));
                    break;
            }
        }

        return new(hallazgos, faltantes);
    }
}

/// <summary>Antigüedad: indicador informativo, SEPARADO de «desactualizada». Una biblioteca estable y antigua no está abandonada por su edad.</summary>
public sealed class ReglaAntiguedad : IReglaDiagnostico
{
    public string Nombre => "antiguedad";

    public ResultadoRegla Evaluar(EntradaReglas e)
    {
        var meses = ConstructorIndicadores.Meses(e.Info.UltimaPublicacion, e.Contexto.Ahora);
        if (meses is null)
            return new([new(new Hallazgo(Nombre, "antiguedad_no_determinada", GravedadHallazgo.Informativo,
                "NuGet no informa la fecha de la última publicación: no se calcula la antigüedad.", []))], []);
        if (meses < e.Parametros.UmbralAntiguedadMeses) return ResultadoRegla.Vacio;

        return new([new(new Hallazgo(Nombre, "sin_publicaciones_recientes", GravedadHallazgo.Informativo,
            $"La última versión estable se publicó hace {meses} meses. Es solo informativo: no implica que el paquete esté abandonado ni cambia el estado.",
            [Ayuda.E("meses", meses.ToString()), Ayuda.E("umbral_meses", e.Parametros.UmbralAntiguedadMeses.ToString()),
             Ayuda.E("ultima_publicacion", e.Info.UltimaPublicacion?.ToString("yyyy-MM-dd"))]))], []);
    }
}

/// <summary>Licencia: solo informativa (DEC-13, propuesta; no hay política aprobada). No cambia estado ni filtra nada.</summary>
public sealed class ReglaLicencia : IReglaDiagnostico
{
    public string Nombre => "licencia";

    public ResultadoRegla Evaluar(EntradaReglas e)
    {
        if (!e.VersionResuelta)
            return new([new(new Hallazgo(Nombre, "licencia_no_determinada", GravedadHallazgo.Informativo,
                "No se informa la licencia porque la versión declarada no se resolvió.", []))], []);

        var v = e.Info.Declarada!;
        if (string.IsNullOrWhiteSpace(v.Licencia))
            return new([new(new Hallazgo(Nombre, "licencia_por_revisar", GravedadHallazgo.Informativo,
                "NuGet no informa licencia para esta versión: revísala antes de usarla. No hay política de licencias aprobada.", []))], []);

        if (v.TipoLicencia is TipoLicencia.Expresion or TipoLicencia.Archivo)
            return new([new(new Hallazgo(Nombre, "licencia_informada", GravedadHallazgo.Informativo,
                $"Licencia informada por el paquete: {v.Licencia}. DiagNET no la valida contra ninguna política.",
                [Ayuda.E("licencia", v.Licencia), Ayuda.E("tipo", v.TipoLicencia.ToString())]))], []);

        var motivo = v.TipoLicencia == TipoLicencia.Url ? "solo una dirección web" : "de un tipo que NuGet no informó";
        return new([new(new Hallazgo(Nombre, "licencia_por_revisar", GravedadHallazgo.Informativo,
            $"La licencia es {motivo} ({v.Licencia}): revísala. No hay política de licencias aprobada.", [Ayuda.E("licencia", v.Licencia)]))], []);
    }
}

/// <summary>AOT: no se evalúa en esta versión (DEC-04, propuesta). Informativo fijo; nunca penaliza el estado ni la parcialidad.</summary>
public sealed class ReglaAot : IReglaDiagnostico
{
    public string Nombre => "aot";

    public ResultadoRegla Evaluar(EntradaReglas e) =>
        new([new(new Hallazgo(Nombre, "aot_no_evaluado", GravedadHallazgo.Informativo,
            "La compatibilidad con AOT no se evalúa en esta versión de DiagNET: no se afirma ni se descarta.", []))], []);
}
