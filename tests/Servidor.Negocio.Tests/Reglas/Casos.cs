using System.Text.Json;
using System.Text.Json.Serialization;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Nuget;
using Servidor.Negocio.Orquestacion;
using Servidor.Negocio.Reglas;

namespace Servidor.Negocio.Tests.Reglas;

/// <summary>Constructores de datos sintéticos para el motor de E6. Son datos de prueba, no un sustituto del motor.</summary>
internal static class Casos
{
    public static readonly DateTimeOffset Ahora = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    public const string Net8 = "net8.0";
    public const string Net10 = "net10.0";

    public static VersionPublicada V(string version, string[]? grupos = null, Deprecacion? deprecacion = null, string? licencia = "MIT",
        TipoLicencia? tipo = TipoLicencia.Expresion, DateTimeOffset? publicada = null, bool deprecacionConocida = true) =>
        new(version, version, version.Contains('-'), true, publicada ?? Ahora.AddMonths(-3), licencia, licencia is null ? null : tipo, deprecacion,
            grupos ?? [Net8, Net10], [], deprecacionConocida);

    public static CompatibilidadTfm Compat(string tfm, ResultadoCompatibilidad r, string? detalle = null) => new(tfm, r, "inferida_dependency_groups", detalle);

    public static Dictionary<string, VersionPublicada?> PorTfm(params (string Tfm, VersionPublicada? V)[] pares) => pares.ToDictionary(p => p.Tfm, p => p.V);

    public static InformacionPaquete Info(
        VersionPublicada? declarada, VersionPublicada? estable, Dictionary<string, VersionPublicada?> compatiblesPorTfm, bool completa = true,
        IReadOnlyList<CompatibilidadTfm>? compatibilidad = null, DateTimeOffset? ultimaPublicacion = null, long? descargas = null,
        EstadoVersionDeclarada? estado = null, DateTimeOffset? consulta = null) =>
        new("Ejemplo.Paquete", estado ?? (declarada is null ? EstadoVersionDeclarada.NoSolicitada : EstadoVersionDeclarada.Publicada), declarada,
            compatibilidad ?? [], estable, null, compatiblesPorTfm, ultimaPublicacion ?? estable?.Publicada, completa, 1, 1, 5, consulta ?? Ahora, descargas,
            descargas is null ? "NuGet no informó descargas." : null);

    /// <summary>Un único TFM (net8.0): declarada 1.0.0 compatible; última estable y última compatible = <paramref name="ultima"/>.</summary>
    public static InformacionPaquete Simple(string declarada = "1.0.0", string ultima = "1.0.0", Deprecacion? deprecacion = null, bool completa = true,
        string? licencia = "MIT", TipoLicencia? tipo = TipoLicencia.Expresion, DateTimeOffset? ultimaPublicacion = null, long? descargas = 1000, DateTimeOffset? consulta = null) =>
        Info(V(declarada, deprecacion: deprecacion, licencia: licencia, tipo: tipo), V(ultima, publicada: ultimaPublicacion), PorTfm((Net8, V(ultima))), completa,
            [Compat(Net8, ResultadoCompatibilidad.Compatible)], ultimaPublicacion, descargas, consulta: consulta);

    public static DependenciaDeclarada Dep(string version = "1.0.0", TipoVersion tipo = TipoVersion.Exacta) => new("Ejemplo.Paquete", version, tipo, null, []);

    public static ResultadoMetadato Meta(InformacionPaquete? info, OrigenDato origen = OrigenDato.Nuget, MotivoSinDatos? motivo = null, string? mensaje = null,
        DateTimeOffset? fecha = null) =>
        new(origen, info, info is null ? null : fecha ?? info.FechaConsulta, motivo, mensaje, [], 0);

    public static Diagnostico Diagnosticar(InformacionPaquete? info, string[]? tfms = null, DependenciaDeclarada? dep = null, ParametrosDiagnostico? parametros = null,
        OrigenDato origen = OrigenDato.Nuget, MotivoSinDatos? motivo = null, string? mensaje = null, DateTimeOffset? fecha = null) =>
        new MotorDiagnostico(parametros).Diagnosticar(new ContextoDiagnostico(dep ?? Dep(), Meta(info, origen, motivo, mensaje, fecha), tfms ?? [Net8], Ahora));

    /// <summary>Parámetros que consideran suficiente la evidencia «inferida» para afirmar incompatibilidad (solo para probar la agregación; en producción la lista está vacía).</summary>
    public static readonly ParametrosDiagnostico ConEvidenciaSuficiente = new() { BasesSuficientesParaIncompatible = ["inferida_dependency_groups"] };

    public static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() }, WriteIndented = false };

    public static string Serializar(object o) => JsonSerializer.Serialize(o, Json);

    public static Hallazgo? Hallazgo(this Diagnostico d, string codigo) => d.Hallazgos.FirstOrDefault(h => h.Codigo == codigo);
}
