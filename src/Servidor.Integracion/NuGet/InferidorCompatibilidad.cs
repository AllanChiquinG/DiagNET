using NuGet.Frameworks;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Nuget;

namespace Servidor.Integracion.NuGet;

/// <summary>
/// Infiere si un paquete declara compatibilidad con un TFM del proyecto a partir de los TFM de sus grupos de dependencias, usando las reglas
/// oficiales de NuGet.Frameworks (no comparaciones de texto). Es EVIDENCIA LIMITADA: no certifica assets (<c>lib/</c>), ejecución ni AOT, y un paquete
/// puede funcionar aunque no lo declare. «Sin grupos» o un TFM no interpretable devuelven <c>NoDeterminada</c>: se reconoce antes que suponer.
/// </summary>
public sealed class InferidorCompatibilidad : IInferidorCompatibilidad
{
    public const string BaseInferencia = "inferida_dependency_groups";

    public CompatibilidadTfm Inferir(string tfmProyecto, IReadOnlyList<string> gruposPaquete)
    {
        var proyecto = NuGetFramework.Parse(tfmProyecto.Trim());
        if (proyecto.IsUnsupported || proyecto.IsAny)
            return Resultado(tfmProyecto, ResultadoCompatibilidad.NoDeterminada, "El TFM del proyecto no se pudo interpretar.");

        if (gruposPaquete.Count == 0)
            return Resultado(tfmProyecto, ResultadoCompatibilidad.NoDeterminada,
                "El paquete no declara grupos de dependencias: no hay evidencia para inferir compatibilidad.");

        var interpretados = new List<NuGetFramework>();
        var noInterpretados = 0;
        foreach (var grupo in gruposPaquete)
        {
            if (string.IsNullOrWhiteSpace(grupo))
                return Resultado(tfmProyecto, ResultadoCompatibilidad.Compatible, "Hay un grupo sin framework, que aplica a todos.");

            var f = NuGetFramework.Parse(grupo.Trim());
            if (f.IsUnsupported) noInterpretados++;
            else if (f.IsAny) return Resultado(tfmProyecto, ResultadoCompatibilidad.Compatible, "Hay un grupo sin framework, que aplica a todos.");
            else interpretados.Add(f);
        }

        if (interpretados.Count == 0)
            return Resultado(tfmProyecto, ResultadoCompatibilidad.NoDeterminada, "Ningún grupo de dependencias tiene un framework interpretable.");

        var cercano = NuGetFrameworkUtility.GetNearest(interpretados, proyecto, f => f);
        if (cercano is not null)
            return Resultado(tfmProyecto, ResultadoCompatibilidad.Compatible, $"El grupo más cercano declarado es {cercano.GetShortFolderName()}.");

        var lista = string.Join(", ", interpretados.Select(f => f.GetShortFolderName()).Distinct().Take(8));
        var nota = noInterpretados > 0 ? $" ({noInterpretados} grupo(s) no interpretable(s) no se consideraron)" : "";
        return Resultado(tfmProyecto, ResultadoCompatibilidad.Incompatible,
            $"Ningún grupo declarado ({lista}) es compatible con {tfmProyecto}{nota}. Es lo que el paquete declara, no una prueba de que no funcione.");
    }

    private static CompatibilidadTfm Resultado(string tfm, ResultadoCompatibilidad resultado, string detalle) =>
        new(tfm, resultado, BaseInferencia, detalle);
}
