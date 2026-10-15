using Servidor.Negocio.Dominio;

namespace Servidor.Negocio.Reglas;

/// <summary>Construye la copia de indicadores que se guarda con el diagnóstico. «Sin dato» es siempre <see cref="Disponibilidad.NoDeterminado"/>, nunca falso ni cero.</summary>
public static class ConstructorIndicadores
{
    public static int? Meses(DateTimeOffset? fecha, DateTimeOffset ahora)
    {
        if (fecha is not { } f) return null;
        var meses = (ahora.Year - f.Year) * 12 + ahora.Month - f.Month;
        if (ahora.Day < f.Day) meses--;
        return Math.Max(0, meses);
    }

    public static IndicadoresLibreria Construir(EntradaReglas e)
    {
        var info = e.Info;
        var resuelta = e.VersionResuelta;
        var declarada = resuelta ? info.Declarada : null;
        const string NoResuelta = "La versión declarada no se resolvió.";

        var fecha = declarada?.Publicada is { } p
            ? new Indicador<DateTimeOffset?>(Disponibilidad.Conocido, p)
            : new Indicador<DateTimeOffset?>(Disponibilidad.NoDeterminado, null, resuelta ? "NuGet no informa la fecha de publicación." : NoResuelta);

        var estable = info.UltimaEstable is { } u
            ? new Indicador<string?>(Disponibilidad.Conocido, u.Version)
            : info.SeleccionCompleta
                ? new Indicador<string?>(Disponibilidad.Conocido, null, "NuGet no publica ninguna versión estable listada.")
                : new Indicador<string?>(Disponibilidad.NoDeterminado, null, "No se recorrió todo el registro del paquete.");

        var meses = Meses(info.UltimaPublicacion, e.Contexto.Ahora);
        var antiguedad = meses is { } m
            ? new Indicador<int?>(Disponibilidad.Conocido, m)
            : new Indicador<int?>(Disponibilidad.NoDeterminado, null, "NuGet no informa la fecha de la última publicación.");

        var descargas = info.Descargas is { } d
            ? new Indicador<long?>(Disponibilidad.Conocido, d)
            : new Indicador<long?>(Disponibilidad.NoDeterminado, null, info.DescargasNota ?? "NuGet no informó las descargas.");

        var licencia = declarada is null
            ? new Indicador<Licencia?>(Disponibilidad.NoDeterminado, null, NoResuelta)
            : string.IsNullOrWhiteSpace(declarada.Licencia)
                ? new Indicador<Licencia?>(Disponibilidad.NoDeterminado, null, "NuGet no informa licencia para esta versión.")
                : new Indicador<Licencia?>(Disponibilidad.Conocido, new Licencia(declarada.TipoLicencia, declarada.Licencia));

        // P2: solo se atribuye deprecación a la versión DECLARADA cuando es exacta y está disponible; la de la última estable es información del paquete (hallazgo).
        var conocida = declarada is { DeprecacionConocida: true };
        var deprecado = conocida
            ? new Indicador<bool?>(Disponibilidad.Conocido, declarada!.Deprecacion is not null)
            : new Indicador<bool?>(Disponibilidad.NoDeterminado, null,
                resuelta ? "No se pudo determinar la deprecación de la versión declarada." : "La versión declarada no se resolvió: no se atribuye deprecación a la dependencia instalada.");

        return new IndicadoresLibreria(
            fecha, estable, Compatible(e), info.SeleccionCompleta, antiguedad, descargas, licencia, deprecado,
            conocida ? declarada!.Deprecacion : null, CompatibilidadPorTfm(e),
            new Indicador<bool?>(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));
    }

    /// <summary>Compatibilidad inferida por TFM. Un «incompatible» sin evidencia suficiente se guarda como no determinada (la ausencia de grupo no demuestra incompatibilidad); la base sigue indicando que es inferida.</summary>
    private static IReadOnlyList<CompatibilidadTfm> CompatibilidadPorTfm(EntradaReglas e) => e.Info.CompatibilidadDeclarada
        .Select(c => c.Resultado == ResultadoCompatibilidad.Incompatible && !e.Parametros.BasesSuficientesParaIncompatible.Contains(c.Base)
            ? c with { Resultado = ResultadoCompatibilidad.NoDeterminada, Detalle = "Sin grupo de dependencias compatible: no demuestra incompatibilidad. " + c.Detalle }
            : c)
        .ToList();

    /// <summary>
    /// Última estable compatible. Con un solo TFM, o varios con la MISMA versión, es una versión comprobada para todos. Con versiones distintas por TFM NO se
    /// presenta la menor como si estuviera comprobada para todos: queda no determinada y se detalla cada TFM.
    /// </summary>
    private static Indicador<string?> Compatible(EntradaReglas e)
    {
        if (e.Frameworks.Count == 0) return new(Disponibilidad.NoDeterminado, null, "No hay framework evaluable.");

        var porTfm = e.Frameworks.Select(t => (Tfm: t, V: e.PorTfm(t))).ToList();
        if (porTfm.Any(x => x.V is null))
            return e.Info.SeleccionCompleta && porTfm.All(x => x.V is null)
                ? new(Disponibilidad.Conocido, null, $"Ninguna versión estable listada declara compatibilidad inferida con {Ayuda.Lista(porTfm.Select(x => x.Tfm))}.")
                : new(Disponibilidad.NoDeterminado, null, "Sin versión compatible comprobada para todos los frameworks evaluados: " + Detalle(porTfm) + ".");

        var distintas = porTfm.Select(x => x.V!.Version).Distinct().ToList();
        return distintas.Count == 1
            ? new(Disponibilidad.Conocido, distintas[0])
            : new(Disponibilidad.NoDeterminado, null, "Las más recientes compatibles difieren por framework (" + Detalle(porTfm) + "); no se ha verificado una versión común.");
    }

    private static string Detalle(List<(string Tfm, Nuget.VersionPublicada? V)> porTfm) => Ayuda.Lista(porTfm.Select(x => $"{x.Tfm}={x.V?.Version ?? "ninguna"}"));
}
