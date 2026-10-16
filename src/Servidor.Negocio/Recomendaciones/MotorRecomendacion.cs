using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Reglas;

namespace Servidor.Negocio.Recomendaciones;

public sealed class OpcionesRecomendacion
{
    /// <summary>Candidatos que se piden a la búsqueda de NuGet (plan: hasta 5).</summary>
    public int MaxCandidatosBusqueda { get; set; } = 5;

    /// <summary>Candidatos de búsqueda que se validan (plan: hasta 3).</summary>
    public int MaxValidacionesBusqueda { get; set; } = 3;

    /// <summary>Versiones distintas que se verifican por paquete (actualización o reemplazo declarado).</summary>
    public int MaxVerificacionesPorPaquete { get; set; } = 3;

    /// <summary>Máximo de candidatos de búsqueda que se muestran.</summary>
    public int MaxCandidatosMostrados { get; set; } = 3;
}

/// <summary>
/// Motor de recomendaciones por reglas (E7). No usa IA ni fuentes externas: solo el diagnóstico, los datos de nuget.org (vía <see cref="ServicioMetadatos"/>, con caché) y la búsqueda pública.
/// <list type="number">
/// <item><b>Actualizar el mismo paquete</b> (prioridad): solo a una versión ADMISIBLE = estable, listada, no deprecada, posterior a la declarada y con compatibilidad inferida
/// COMPROBADA para TODOS los frameworks evaluados. Con varios TFM se verifica una versión común (consultando esa versión) antes de recomendarla.</item>
/// <item><b>Alternativa sugerida por el autor</b> (<c>alternatePackage</c> de la deprecación de la versión declarada): se respeta su rango y se comprueban sus datos. Esa declaración NO demuestra
/// equivalencia funcional, compatibilidad ni una migración sencilla. Si no se puede determinar la compatibilidad, es «candidato para revisión», nunca «reemplazo compatible».</item>
/// <item><b>Candidatos de búsqueda</b> (solo si la dependencia está sin soporte activo y no hubo nada mejor): SIEMPRE «candidato para revisión» con la relación funcional NO demostrada.
/// La coincidencia textual, las descargas o el sello de verificado son solo datos informativos.</item>
/// </list>
/// Se distingue «no se encontraron recomendaciones en la búsqueda realizada» (<c>sin_recomendaciones</c>) de «no se pudieron obtener o verificar recomendaciones»
/// (<c>recomendaciones_no_verificadas</c>) y de «se evaluaron de forma incompleta» (<c>recomendaciones_evaluacion_incompleta</c>). La licencia es informativa y AOT no se evalúa.
/// Un fallo de la búsqueda o de la verificación deja una nota y NUNCA afecta al diagnóstico.
/// </summary>
public sealed class MotorRecomendacion(ServicioMetadatos metadatos, IClienteNuGet nuget, OpcionesRecomendacion? opciones = null) : IMotorRecomendacion
{
    /// <summary>Revisión de las reglas de recomendación; se guarda con el análisis junto a la del diagnóstico.</summary>
    public const string Revision = "e7-1.0";

    private readonly OpcionesRecomendacion _op = opciones ?? new OpcionesRecomendacion();

    public string VersionReglas => Revision;

    private enum Veredicto { Verificada, Indeterminada, Rechazada, SinDatos }

    private sealed record Verificacion(string Paquete, string Version, Veredicto Veredicto, string Detalle, ResultadoMetadato? Datos, VersionPublicada? V);

    public async Task<ResultadoRecomendacion> RecomendarAsync(ContextoDiagnostico c, Diagnostico d, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        var recs = new List<Recomendacion>();
        var notas = new List<Advertencia>();
        var incompleto = new List<string>();   // motivos por los que la evaluación no fue completa (datos no disponibles, límites alcanzados, búsqueda caída)
        var nombre = c.Dependencia.Nombre;

        if (c.Metadato.Informacion is not { } info)
            return new([], [new("recomendaciones_no_verificadas",
                $"No se pudieron obtener o verificar recomendaciones para {nombre}: no hay datos de NuGet para este paquete. Esto no significa que no existan.", nombre)]);

        var resuelta = c.Dependencia.TipoVersion == TipoVersion.Exacta && info.EstadoDeclarada == EstadoVersionDeclarada.Publicada && info.Declarada is not null;

        // 1) Actualizar el mismo paquete.
        var actualizacion = resuelta ? await ActualizarAsync(c, info, presupuesto, notas, incompleto, ct) : null;
        if (actualizacion is not null) recs.Add(actualizacion);

        if (actualizacion?.Tipo != TipoRecomendacion.ActualizarVersion)
        {
            // 2) Alternativa sugerida por el autor del paquete deprecado (solo de la versión declarada exacta: P2).
            if (resuelta && info.Declarada!.Deprecacion is { PaqueteAlterno: { } alterno } deprecacion)
                recs.AddRange(await ReemplazoDeclaradoAsync(c, info, deprecacion, alterno, presupuesto, notas, incompleto, ct));

            // 3) Búsqueda: solo para paquetes sin soporte activo y cuando no hay nada mejor.
            if (recs.Count == 0 && d.Estado == EstadoDiagnostico.SinSoporteActivo)
                recs.AddRange(await BuscarAsync(c, presupuesto, notas, incompleto, ct));
        }

        if (recs.Count == 0 && incompleto.Count > 0)
            notas.Add(new("recomendaciones_no_verificadas",
                $"No se pudieron obtener o verificar recomendaciones para {nombre}: {string.Join("; ", incompleto)}. Esto no significa que no existan.", nombre));
        else if (recs.Count == 0)
            notas.Add(new("sin_recomendaciones",
                $"No se encontraron recomendaciones en la búsqueda realizada para {nombre}. Es un resultado válido: DiagNET no inventa reemplazos.", nombre));
        else if (incompleto.Count > 0)
            notas.Add(new("recomendaciones_evaluacion_incompleta",
                $"Las recomendaciones de {nombre} no se evaluaron completamente: {string.Join("; ", incompleto)}.", nombre));

        return new(recs, notas);
    }

    // ------------------------------------------------------------------ 1. actualización

    private async Task<Recomendacion?> ActualizarAsync(
        ContextoDiagnostico c, InformacionPaquete info, PresupuestoTiempo? presupuesto, List<Advertencia> notas, List<string> incompleto, CancellationToken ct)
    {
        var nombre = c.Dependencia.Nombre;
        var declarada = info.Declarada!.Version;
        var tfms = c.FrameworksEvaluados;

        // Candidatos: la mejor versión compatible de cada TFM (o la última estable si no hay TFM), posteriores a la declarada, de mayor a menor.
        var todas = (tfms.Count == 0 ? new[] { info.UltimaEstable?.Version } : tfms.Select(t => info.UltimaEstableCompatiblePorTfm
                .FirstOrDefault(p => string.Equals(p.Key, t, StringComparison.OrdinalIgnoreCase)).Value?.Version))
            .OfType<string>()
            .Distinct()
            .Where(v => ComparadorVersiones.Comparar(v, declarada) > 0)
            .OrderByDescending(v => v, Comparer<string>.Create((a, b) => ComparadorVersiones.Comparar(a, b) ?? 0))
            .ToList();
        if (todas.Count == 0) return null;

        var candidatas = todas.Take(_op.MaxVerificacionesPorPaquete).ToList();
        var limitada = todas.Count > candidatas.Count;

        Verificacion? mejorIndeterminada = null;
        var rechazos = new List<string>();
        foreach (var version in candidatas)
        {
            var v = await VerificarAsync(nombre, version, tfms, presupuesto, ct);
            if (v.Veredicto == Veredicto.Verificada)
                return Crear(TipoRecomendacion.ActualizarVersion, nombre, version,
                    $"Actualizar {nombre} de {declarada} a {version}: es la versión estable más reciente que declara compatibilidad (inferida) con {Lista(tfms)}, "
                    + "no está marcada como obsoleta y es posterior a la declarada.",
                    Evidencia(v, tfms, [("version_declarada", declarada)]), cambioDeCodigo: MayorCambio(declarada, version));

            if (v.Veredicto is Veredicto.Indeterminada or Veredicto.SinDatos)
            {
                mejorIndeterminada ??= v;
                if (v.Veredicto == Veredicto.SinDatos) incompleto.Add($"no se pudieron consultar los datos de {nombre} {version}");
            }
            else
            {
                rechazos.Add($"{version}: {v.Detalle}");
            }
        }

        var comprobadas = string.Join(", ", candidatas);
        var avisoLimite = limitada
            ? $" Se alcanzó el límite de {_op.MaxVerificacionesPorPaquete} verificaciones: la búsqueda fue limitada."
            : " Solo se comprueban las mejores versiones de cada framework, no todo el historial.";
        if (limitada) incompleto.Add($"se alcanzó el límite de {_op.MaxVerificacionesPorPaquete} verificaciones de versiones de {nombre} y la búsqueda fue limitada");

        if (mejorIndeterminada is not null)
        {
            var m = mejorIndeterminada;
            notas.Add(new("actualizacion_no_verificada",
                $"Existe {m.Version} de {nombre}, pero no se encontró una versión común comprobada para todos los frameworks evaluados entre las versiones comprobadas ({comprobadas}): {m.Detalle}. "
                + "No se recomienda actualizar sin revisarla." + avisoLimite, nombre));
            return Crear(TipoRecomendacion.CandidatoParaRevision, nombre, m.Version,
                $"Hay una versión más reciente de {nombre} ({m.Version}) para revisar, pero NO está comprobado que sirva para todos tus frameworks.",
                Evidencia(m, tfms, [("version_declarada", declarada), ("motivo_de_revision", m.Detalle), ("versiones_comprobadas", comprobadas)]),
                cambioDeCodigo: MayorCambio(declarada, m.Version),
                limitacionExtra: $"No se pudo comprobar la compatibilidad con todos los frameworks evaluados: {m.Detalle}." + avisoLimite);
        }

        notas.Add(new("actualizacion_no_verificada",
            $"No se encontró una versión común y admisible de {nombre} entre las versiones comprobadas ({comprobadas}; {string.Join("; ", rechazos)}). "
            + "Eso no implica que no exista otra." + avisoLimite + " No se recomienda actualizar con estos datos.", nombre));
        return null;
    }

    // ------------------------------------------------------------------ 2. alternativa sugerida por el autor

    private const string NoDemuestra = "equivalencia funcional, compatibilidad con todos los frameworks ni una migración sencilla";

    private async Task<IEnumerable<Recomendacion>> ReemplazoDeclaradoAsync(
        ContextoDiagnostico c, InformacionPaquete info, Deprecacion dep, string alterno, PresupuestoTiempo? presupuesto, List<Advertencia> notas, List<string> incompleto, CancellationToken ct)
    {
        var nombre = c.Dependencia.Nombre;
        var tfms = c.FrameworksEvaluados;
        var rangoTexto = dep.RangoVersionAlterno;
        var rango = RangoVersiones.Analizar(rangoTexto);
        var declaradoPor = new (string, string)[]
        {
            ("declarado_por", $"el autor de {nombre} (deprecación de la versión {info.Declarada!.Version})"),
            ("rango_declarado", rangoTexto ?? "(sin rango)"),
            ("lo_que_no_demuestra_la_declaracion", NoDemuestra),
        };

        if (alterno.Length > 100 || string.Equals(alterno, nombre, StringComparison.OrdinalIgnoreCase))
        {
            notas.Add(new("alternativa_declarada_no_valida", $"El paquete sugerido por el autor de {nombre} no es un identificador consultable en nuget.org; no se recomienda.", nombre));
            return [];
        }

        var ultima = await metadatos.ObtenerAsync(new ConsultaPaquete(alterno, null, tfms), presupuesto, ct);
        if (ultima.Informacion is not { } ai)
        {
            if (ultima.Motivo == MotivoSinDatos.NoExisteEnNuGet)
            {
                notas.Add(new("alternativa_declarada_inexistente", $"El paquete «{alterno}» sugerido por el autor de {nombre} no existe en nuget.org; no se recomienda.", nombre));
                return [];
            }

            notas.Add(new("alternativa_declarada_no_comprobada", $"No se pudieron consultar los datos de «{alterno}», la alternativa sugerida por el autor de {nombre}.", nombre));
            incompleto.Add($"no se pudieron consultar los datos de {alterno}, la alternativa sugerida por el autor");
            return [Crear(TipoRecomendacion.CandidatoParaRevision, alterno, null,
                $"Alternativa sugerida por el autor de {nombre}: {alterno}. No se pudieron comprobar sus datos, así que se presenta para revisión. {Declaracion()}",
                declaradoPor.Select(e => new Evidencia(e.Item1, e.Item2)).Append(new Evidencia("comprobacion", ultima.Mensaje ?? "sin datos de NuGet")).ToList(),
                cambioDeCodigo: true, limitacionExtra: "No se pudieron comprobar los datos del paquete (existencia, versión, compatibilidad, deprecación).")];
        }

        // Versiones a verificar dentro del rango declarado.
        var posibles = new List<string>();
        void Agregar(string? v)
        {
            if (v is not null && !posibles.Contains(v) && (rango is null || rango.Contiene(v))) posibles.Add(v);
        }

        if (tfms.Count == 0) Agregar(ai.UltimaEstable?.Version);
        else
            foreach (var v in tfms.Select(t => ai.UltimaEstableCompatiblePorTfm.FirstOrDefault(p => string.Equals(p.Key, t, StringComparison.OrdinalIgnoreCase)).Value?.Version)
                         .OfType<string>().Distinct().OrderByDescending(v => v, Comparer<string>.Create((a, b) => ComparadorVersiones.Comparar(a, b) ?? 0)))
                Agregar(v);
        if (rango is { Minima: { } min, MinimaInclusiva: true }) Agregar(min);   // respeta el rango: la versión mínima que el autor declara admisible

        var limiteRango = rangoTexto is not null && rango is null
            ? $"No se pudo interpretar el rango declarado «{rangoTexto}»: no se garantiza que la versión respete lo que declaró el autor."
            : null;
        var limitada = posibles.Count > _op.MaxVerificacionesPorPaquete;

        Verificacion? mejor = null;
        var rechazos = new List<string>();
        foreach (var version in posibles.Take(_op.MaxVerificacionesPorPaquete))
        {
            var v = await VerificarAsync(alterno, version, tfms, presupuesto, ct);
            if (v.Veredicto == Veredicto.Verificada && limiteRango is null)
                return [Crear(TipoRecomendacion.Alternativa, alterno, version,
                    $"Alternativa sugerida por el autor: el autor de {nombre} lo marcó como obsoleto y sugiere {alterno}{(rangoTexto is null ? "" : $" (rango {rangoTexto})")}. {Declaracion()} "
                    + $"Aparte, la comprobación de DiagNET para {alterno} {version}: declara compatibilidad (inferida) con {Lista(tfms)} y no está marcado como obsoleto.",
                    Evidencia(v, tfms, declaradoPor), cambioDeCodigo: true,
                    limitacionExtra: $"La declaración del autor no demuestra {NoDemuestra}; DiagNET no comprueba que su API sea equivalente a la que usas.")];

            if (v.Veredicto is Veredicto.Verificada or Veredicto.Indeterminada or Veredicto.SinDatos)
            {
                mejor ??= v;
                if (v.Veredicto == Veredicto.SinDatos) incompleto.Add($"no se pudieron consultar los datos de {alterno} {version}");
            }
            else
            {
                rechazos.Add($"{version}: {v.Detalle}");
            }
        }

        if (limitada && mejor is null)
            incompleto.Add($"se alcanzó el límite de {_op.MaxVerificacionesPorPaquete} verificaciones de versiones de {alterno} y la búsqueda fue limitada");

        if (mejor is not null)
        {
            var extra = limiteRango ?? $"No se pudo comprobar la compatibilidad con todos los frameworks evaluados: {mejor.Detalle}.";
            notas.Add(new("alternativa_declarada_para_revision", $"{alterno}, sugerido por el autor de {nombre}, se presenta solo para revisión: {extra}", nombre));
            return [Crear(TipoRecomendacion.CandidatoParaRevision, alterno, mejor.Version,
                $"Alternativa sugerida por el autor de {nombre}: {alterno}. Se presenta para revisión (no se afirma que sea un reemplazo compatible). {Declaracion()}",
                Evidencia(mejor, tfms, declaradoPor.Append(("motivo_de_revision", extra)).ToArray()), cambioDeCodigo: true, limitacionExtra: extra)];
        }

        if (posibles.Count == 0)
        {
            var motivo = rango is null
                ? $"no se pudo comprobar la compatibilidad de {alterno} con {Lista(tfms)}: ninguna versión estable suya declara compatibilidad inferida"
                : $"el rango {rangoTexto} no incluye ninguna de las versiones más recientes compatibles de {alterno}, así que no se pudo comprobar una versión dentro del rango";
            notas.Add(new("alternativa_declarada_para_revision", $"{alterno}, sugerido por el autor de {nombre}: {motivo}.", nombre));
            return [Crear(TipoRecomendacion.CandidatoParaRevision, alterno, null,
                $"Alternativa sugerida por el autor de {nombre}: {alterno}. Se presenta para revisión (no se afirma que sea un reemplazo compatible): {motivo}. {Declaracion()}",
                declaradoPor.Select(e => new Evidencia(e.Item1, e.Item2)).ToList(), cambioDeCodigo: true, limitacionExtra: char.ToUpper(motivo[0]) + motivo[1..] + ".")];
        }

        notas.Add(new("alternativa_declarada_no_admisible", $"La alternativa sugerida por el autor de {nombre} ({alterno}) no es admisible entre las versiones comprobadas: {string.Join("; ", rechazos)}.", nombre));
        return [];
    }

    private static string Declaracion() => $"Esa declaración no demuestra {NoDemuestra}.";

    // ------------------------------------------------------------------ 3. búsqueda

    private async Task<IEnumerable<Recomendacion>> BuscarAsync(
        ContextoDiagnostico c, PresupuestoTiempo? presupuesto, List<Advertencia> notas, List<string> incompleto, CancellationToken ct)
    {
        var nombre = c.Dependencia.Nombre;
        var tfms = c.FrameworksEvaluados;

        ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>> busqueda;
        try
        {
            busqueda = await nuget.BuscarCandidatosAsync(nombre, nombre, _op.MaxCandidatosBusqueda, presupuesto, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            busqueda = ResultadoNuGet<IReadOnlyList<CandidatoBusqueda>>.NoDisponible(CausaFallo.Red, ex.Message);
        }

        if (!busqueda.EsCorrecto)
        {
            notas.Add(new("busqueda_alternativas_no_disponible", $"No se pudo buscar alternativas a {nombre} en nuget.org; el diagnóstico no se ve afectado.", nombre));
            incompleto.Add("la búsqueda de alternativas en nuget.org no estuvo disponible");
            return [];
        }

        var resultado = new List<Recomendacion>();
        var descartados = 0;
        var sinDatos = 0;
        var validados = 0;
        foreach (var candidato in busqueda.Valor!.Take(_op.MaxValidacionesBusqueda))
        {
            if (resultado.Count >= _op.MaxCandidatosMostrados) break;
            validados++;
            if (candidato.Id.Length > 100 || string.Equals(candidato.Id, nombre, StringComparison.OrdinalIgnoreCase)) { descartados++; continue; }

            var version = candidato.UltimaVersion;
            if (version is null)
            {
                var ultima = await metadatos.ObtenerAsync(new ConsultaPaquete(candidato.Id, null, tfms), presupuesto, ct);
                version = ultima.Informacion?.UltimaEstable?.Version;
                if (ultima.Informacion is null && ultima.Motivo != MotivoSinDatos.NoExisteEnNuGet) sinDatos++;
            }

            if (version is null) { descartados++; continue; }
            var v = await VerificarAsync(candidato.Id, version, tfms, presupuesto, ct);
            if (v.Veredicto != Veredicto.Verificada)   // incompatible, deprecado, inexistente o sin comprobar: no se ofrece
            {
                descartados++;
                if (v.Veredicto == Veredicto.SinDatos) sinDatos++;
                continue;
            }

            resultado.Add(Crear(TipoRecomendacion.CandidatoParaRevision, candidato.Id, version,
                $"Candidato para revisión: {candidato.Id} apareció al buscar «{nombre}» en nuget.org y {version} declara compatibilidad (inferida) con {Lista(tfms)}. "
                + "La relación funcional con tu paquete NO está demostrada; no es un reemplazo recomendado.",
                Evidencia(v, tfms, [("texto_buscado", nombre),
                    ("dato_informativo_descargas", candidato.Descargas?.ToString() ?? "(sin dato)"),
                    ("dato_informativo_verificado_nuget", candidato.Verificado is null ? "(sin dato)" : candidato.Verificado.Value ? "si" : "no")]),
                cambioDeCodigo: true,
                limitacionExtra: "La coincidencia del nombre, las descargas y el sello de verificado no prueban que resuelva lo mismo que tu paquete."));
        }

        if (descartados > 0)
            notas.Add(new("candidatos_descartados", $"{descartados} candidato(s) de la búsqueda se descartaron por no poder comprobarse (inexistentes, obsoletos, sin compatibilidad comprobada o sin versión).", nombre));
        if (sinDatos > 0) incompleto.Add($"{sinDatos} candidato(s) de la búsqueda no se pudieron verificar por falta de datos");
        if (resultado.Count == 0 && busqueda.Valor!.Count > validados)
            incompleto.Add($"solo se validaron {validados} de {busqueda.Valor.Count} candidatos de la búsqueda (límite de {_op.MaxValidacionesBusqueda}); la búsqueda fue limitada");
        return resultado;
    }

    // ------------------------------------------------------------------ verificación

    private async Task<Verificacion> VerificarAsync(string paquete, string version, IReadOnlyList<string> tfms, PresupuestoTiempo? presupuesto, CancellationToken ct)
    {
        var datos = await metadatos.ObtenerAsync(new ConsultaPaquete(paquete, version, tfms), presupuesto, ct);
        if (datos.Informacion is not { } info)
            return new(paquete, version, datos.Motivo == MotivoSinDatos.NoExisteEnNuGet ? Veredicto.Rechazada : Veredicto.SinDatos,
                datos.Motivo == MotivoSinDatos.NoExisteEnNuGet ? "el paquete no existe en nuget.org" : "no se pudieron consultar sus datos", datos, null);

        if (info.EstadoDeclarada == EstadoVersionDeclarada.NoPublicada)
            return new(paquete, version, Veredicto.Rechazada, "la versión no figura en nuget.org", datos, null);
        if (info.EstadoDeclarada != EstadoVersionDeclarada.Publicada || info.Declarada is not { } v)
            return new(paquete, version, Veredicto.Indeterminada, "no se pudo determinar la versión en el registro", datos, null);
        if (!v.Listada || v.EsPrerelease || ComparadorVersiones.EsPreliminar(v.Version))
            return new(paquete, version, Veredicto.Rechazada, "no es una versión estable listada", datos, v);
        if (v.Deprecacion is not null)
            return new(paquete, version, Veredicto.Rechazada, "está marcada como obsoleta", datos, v);
        if (!v.DeprecacionConocida)
            return new(paquete, version, Veredicto.Indeterminada, "no se pudo determinar si está obsoleta", datos, v);
        if (tfms.Count == 0)
            return new(paquete, version, Veredicto.Indeterminada, "el proyecto no tiene un framework evaluable", datos, v);

        var problemas = tfms.Select(t => (Tfm: t, C: info.CompatibilidadDeclarada.FirstOrDefault(x => string.Equals(x.Tfm, t, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.C?.Resultado != ResultadoCompatibilidad.Compatible)
            .Select(x => x.Tfm)
            .ToList();
        return problemas.Count == 0
            ? new(paquete, version, Veredicto.Verificada, "compatibilidad inferida comprobada con todos los frameworks evaluados", datos, v)
            : new(paquete, version, Veredicto.Indeterminada, $"compatibilidad no comprobada con {Lista(problemas)}", datos, v);
    }

    // ------------------------------------------------------------------ construcción

    private static string Lista(IReadOnlyList<string> tfms) => tfms.Count == 0 ? "ningún framework evaluable" : string.Join(" y ", tfms);

    private static bool MayorCambio(string desde, string hasta)
    {
        static string Mayor(string v) => v.Split('.', '-', '+')[0].TrimStart('0') is { Length: > 0 } m ? m : "0";
        var a = Mayor(desde);
        var b = Mayor(hasta);
        return a.Length != b.Length ? a.Length < b.Length : string.CompareOrdinal(a, b) < 0;
    }

    /// <summary>La evidencia de compatibilidad inferida se guarda APARTE, referida a la versión concreta comprobada, separada de lo que declara el autor.</summary>
    private static List<Evidencia> Evidencia(Verificacion v, IReadOnlyList<string> tfms, (string, string)[] extra)
    {
        var lista = extra.Select(e => new Evidencia(e.Item1, e.Item2)).ToList();
        lista.Add(new("paquete", v.Paquete));
        lista.Add(new("version_comprobada", v.Version));
        lista.Add(new("resultado_de_la_comprobacion", v.Detalle));
        lista.Add(new("frameworks_evaluados", Lista(tfms)));
        if (v.Datos?.Informacion is { } info)
            lista.Add(new("compatibilidad_inferida",
                info.CompatibilidadDeclarada.Count == 0
                    ? $"versión {v.Version}: (sin datos)"
                    : $"versión {v.Version}: " + string.Join(", ", info.CompatibilidadDeclarada.Select(x => $"{x.Tfm}={x.Resultado.ToString().ToLowerInvariant()}")) + " (inferida de los grupos de dependencias)"));
        if (v.Datos is { } d)
        {
            lista.Add(new("origen_de_los_datos", d.Origen switch
            {
                OrigenDato.Nuget => "nuget", OrigenDato.CacheVigente => "cache_vigente", OrigenDato.CacheExpirada => "cache_expirada", _ => "no_determinado"
            }));
            lista.Add(new("fecha_de_los_datos", d.FechaDatos?.ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture) ?? "(sin dato)"));
        }

        if (v.V is { } ver)
        {
            lista.Add(new("deprecacion", "no publicada por NuGet"));
            lista.Add(new("licencia_informativa", string.IsNullOrWhiteSpace(ver.Licencia) ? "no informada" : ver.Licencia));
        }

        return lista;
    }

    private static Recomendacion Crear(
        TipoRecomendacion tipo, string paquete, string? version, string justificacion, List<Evidencia> evidencia, bool cambioDeCodigo, string? limitacionExtra = null)
    {
        var limitaciones = new List<string>();
        if (limitacionExtra is not null) limitaciones.Add(limitacionExtra);
        limitaciones.Add("La compatibilidad se infiere de los grupos de dependencias que publica NuGet; no se compiló ni se ejecutó nada.");
        limitaciones.Add(TextosRecomendacion.CambiosDeCodigo(cambioDeCodigo) + (cambioDeCodigo
            ? " DiagNET no garantiza una migración sin cambios de código."
            : " La comparación de versiones no prueba que no sean necesarios; revisa las notas de la versión."));
        limitaciones.Add("La licencia se muestra solo como información (no hay política de licencias aprobada) y la compatibilidad con AOT no se evalúa.");
        return new Recomendacion(tipo, paquete, version, justificacion, evidencia, limitaciones, cambioDeCodigo);
    }
}

/// <summary>Textos que la interfaz y el PDF deben usar para mostrar una recomendación (un solo lugar, para que no se contradigan).</summary>
public static class TextosRecomendacion
{
    /// <summary>Etiqueta visible del tipo. «Alternativa» se muestra SIEMPRE como sugerida por el autor; un candidato, con la relación funcional no demostrada.</summary>
    public static string Etiqueta(TipoRecomendacion tipo) => tipo switch
    {
        TipoRecomendacion.ActualizarVersion => "Actualización del mismo paquete",
        TipoRecomendacion.Alternativa => "Alternativa sugerida por el autor",
        _ => "Candidato para revisión (relación funcional no demostrada)",
    };

    /// <summary>
    /// <c>true</c> ⇒ «Puede requerir cambios de código». <c>false</c> ⇒ «Cambios de código no determinados»: nunca «sin cambios» ni «cambios no previstos»,
    /// porque comparar versiones no prueba la ausencia de cambios.
    /// </summary>
    public static string CambiosDeCodigo(bool posiblesCambios) => posiblesCambios ? "Puede requerir cambios de código." : "Cambios de código no determinados.";
}
