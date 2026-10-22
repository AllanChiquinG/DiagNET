using Servidor.Negocio.Dominio;
using Servidor.Negocio.Lectura;

namespace Servidor.Integracion.Tests.Pdf;

/// <summary>
/// FIXTURES IDENTIFICADOS para probar el reporte (paquetes «Ejemplo.*» inventados): no son datos de NuGet ni se usan fuera de las pruebas.
/// Los análisis se construyen directamente como snapshot, exactamente como los devolvería el repositorio.
/// </summary>
public static class MuestrasReporte
{
    public static readonly DateTimeOffset Fecha = new(2026, 10, 5, 15, 42, 0, TimeSpan.Zero);
    public static readonly Guid Id = Guid.Parse("5d0f6a64-27f3-4d4c-9d6e-0f3a1c2b4e11");

    public const string NombreLargo = "Ejemplo.Nueva.Largo.Con.Muchisimos.Segmentos.Y.Un.Nombre.Que.No.Cabe.En.Una.Sola.Linea.De.La.Tabla";
    /// <summary>Un identificador sin ningún separador: la tabla no puede dejarlo salirse de su celda ni solaparse con la columna vecina.</summary>
    public static readonly string NombreSinCortes = "Ejemplo" + new string('N', 90);
    public const string ArchivoConAcentos = "Aplicación Ñandú – Gestión de Inversiones (versión en producción) con acentos áéíóú.csproj";

    public static IndicadoresLibreria Indicadores(string? ultima = "2.0.0", long? descargas = 123456, bool? deprecado = false, Deprecacion? dep = null, bool completa = true, bool compatible = true) =>
        new(new Indicador<DateTimeOffset?>(Disponibilidad.Conocido, Fecha.AddMonths(-30)),
            ultima is null ? new Indicador<string?>(Disponibilidad.NoDeterminado, null, "No se recorrió todo el registro.") : new Indicador<string?>(Disponibilidad.Conocido, ultima),
            new Indicador<string?>(Disponibilidad.Conocido, ultima),
            completa,
            new Indicador<int?>(Disponibilidad.Conocido, 30),
            descargas is null ? new Indicador<long?>(Disponibilidad.NoDeterminado, null, "NuGet no informó descargas.") : new Indicador<long?>(Disponibilidad.Conocido, descargas),
            new Indicador<Licencia?>(Disponibilidad.Conocido, new Licencia(TipoLicencia.Expresion, "MIT")),
            new Indicador<bool?>(Disponibilidad.Conocido, deprecado),
            dep,
            [new CompatibilidadTfm("net8.0", compatible ? ResultadoCompatibilidad.Compatible : ResultadoCompatibilidad.NoDeterminada, "inferida_dependency_groups", "Declara un grupo para net8.0.")],
            new Indicador<bool?>(Disponibilidad.NoEvaluado, null, "DiagNET no evalúa AOT en esta versión."));

    public static Hallazgo Hallazgo(string codigo, string mensaje, GravedadHallazgo g = GravedadHallazgo.Aviso, bool faltante = false, params (string, string)[] ev) =>
        new("regla", codigo, g, mensaje, ev.Select(e => new Evidencia(e.Item1, e.Item2)).ToList(), faltante);

    public static Diagnostico Diagnostico(EstadoDiagnostico estado, string motivos, IReadOnlyList<Hallazgo>? hallazgos = null, OrigenDato origen = OrigenDato.Nuget,
        IndicadoresLibreria? indicadores = null, bool sinIndicadores = false) =>
        new(estado, motivos, "2.0.0", hallazgos ?? [], [new EvaluacionTfm("net8.0", estado)], origen,
            origen == OrigenDato.NoDeterminado ? null : Fecha, sinIndicadores ? null : indicadores ?? Indicadores());

    public static Recomendacion Recomendacion(TipoRecomendacion tipo = TipoRecomendacion.ActualizarVersion, string paquete = "Ejemplo.Nueva", string? version = "2.0.0", bool cambios = true) =>
        new(tipo, paquete, version, "Justificación redactada por el motor de recomendaciones.", [new Evidencia("version_comprobada", version ?? "(sin dato)")],
            ["La compatibilidad se infiere de los grupos de dependencias que publica NuGet; no se compiló ni se ejecutó nada."], cambios);

    public static DependenciaAnalizada Dependencia(string nombre, EstadoDiagnostico estado = EstadoDiagnostico.Actualizada, string? version = "1.0.0", TipoVersion tipo = TipoVersion.Exacta,
        Diagnostico? diagnostico = null, IReadOnlyList<Recomendacion>? recomendaciones = null, IReadOnlyList<Advertencia>? advertencias = null) =>
        new(nombre, version, tipo, null, advertencias ?? [], diagnostico ?? Diagnostico(estado, $"Estado {estado}."), recomendaciones ?? []);

    public static Analisis Analisis(IReadOnlyList<DependenciaAnalizada> dependencias, EstadoAnalisis estado = EstadoAnalisis.Completo, string nombre = "Mi.csproj",
        IReadOnlyList<string>? declarados = null, IReadOnlyList<string>? evaluados = null, IReadOnlyList<Advertencia>? advertencias = null) =>
        new(Id, nombre, declarados ?? ["net8.0"], evaluados ?? declarados ?? ["net8.0"], Fecha, estado, dependencias, advertencias ?? [], "e6-1.1|ant=24m|agr=pes+e7-1.0");

    // ---- Escenarios del plan de E10 -------------------------------------------------------------------------------------------------------------------

    public static Analisis SinDependencias() =>
        Analisis([], advertencias: [new Advertencia("sin_paquetes", "El proyecto no declara paquetes NuGet.")]);

    public static Analisis Pequeno() => Analisis(
    [
        Dependencia("Ejemplo.Actual"),
        Dependencia("Ejemplo.Vieja", EstadoDiagnostico.Desactualizada, "1.0.0",
            diagnostico: Diagnostico(EstadoDiagnostico.Desactualizada, "Hay una versión estable más reciente. Motivos: 1.",
                [Hallazgo("version_nueva", "Existe la versión estable 2.0.0, publicada hace 30 meses.", GravedadHallazgo.Aviso, false, ("version_declarada", "1.0.0"), ("ultima_estable", "2.0.0"))]),
            recomendaciones: [Recomendacion(cambios: false)]),
        Dependencia("Ejemplo.SinSoporte", EstadoDiagnostico.SinSoporteActivo, "3.1.0",
            diagnostico: Diagnostico(EstadoDiagnostico.SinSoporteActivo, "NuGet la marca como deprecada. Motivos: 1.",
                [Hallazgo("deprecada", "El autor deprecó el paquete.", GravedadHallazgo.Grave)],
                indicadores: Indicadores(deprecado: true, dep: new Deprecacion(["Legacy"], "Usa Ejemplo.Sucesora.", "Ejemplo.Sucesora", "[2.0.0, )"))),
            recomendaciones: [Recomendacion(TipoRecomendacion.Alternativa, "Ejemplo.Sucesora", "2.1.0")]),
    ]);

    /// <summary>Resultado PARCIAL con los casos difíciles: copia vencida, sin datos, evidencia incompleta, candidato, rango, nombres largos y avisos distintos.</summary>
    public static Analisis Parcial() => Analisis(
    [
        Dependencia(NombreLargo, EstadoDiagnostico.Desactualizada, "10.20.30-beta.1+build.20261005.abcdef0123456789",
            diagnostico: Diagnostico(EstadoDiagnostico.Desactualizada, "Hay una versión más reciente. Motivos: 1.",
                [Hallazgo("version_nueva", "Existe una versión estable más reciente con un mensaje muy largo ".PadRight(180, 'x'), GravedadHallazgo.Aviso, false,
                    ("clave_con_nombre_largo", new string('v', 140)))]),
            recomendaciones: [Recomendacion(TipoRecomendacion.ActualizarVersion, NombreLargo, "11.0.0")]),
        Dependencia("Ejemplo.SinDatos", EstadoDiagnostico.NoDeterminada, null, TipoVersion.Propiedad,
            diagnostico: Diagnostico(EstadoDiagnostico.NoDeterminada, "No se pudo determinar. Limitaciones: NuGet no respondió.", origen: OrigenDato.NoDeterminado, sinIndicadores: true),
            advertencias: [new Advertencia("recomendaciones_no_verificadas", "No se pudo verificar si existen recomendaciones: NuGet no respondió.", "Ejemplo.SinDatos")]),
        Dependencia("Ejemplo.CopiaVencida", EstadoDiagnostico.Incompatible, "2.0.0",
            diagnostico: Diagnostico(EstadoDiagnostico.Incompatible, "Incompatible según evidencia suficiente. Evidencia incompleta: no se pudo revisar la última versión.",
                [Hallazgo("incompat", "No hay un grupo compatible con net10.0.", GravedadHallazgo.Grave),
                 Hallazgo("falta", "No se recorrió todo el registro del paquete.", GravedadHallazgo.Aviso, true, ("paginas_revisadas", "1 de 3"))],
                OrigenDato.CacheExpirada, Indicadores(completa: false, compatible: false)),
            recomendaciones: [Recomendacion(TipoRecomendacion.CandidatoParaRevision, "Ejemplo.Candidato", null, cambios: false)]),
        Dependencia("Ejemplo.Rango", EstadoDiagnostico.NoDeterminada, "[1.0,2.0)", TipoVersion.Rango),
        Dependencia(NombreSinCortes, EstadoDiagnostico.Actualizada, new string('9', 70)),
    ], EstadoAnalisis.Parcial, ArchivoConAcentos,
    ["net8.0", "net10.0"], ["net8.0"],
    [new Advertencia("framework_fuera_de_alcance_omitido", "Se omitió el framework net6.0 porque DiagNET solo evalúa .NET 8 y .NET 10."),
     new Advertencia("recomendaciones_incompletas", "No se pudieron buscar recomendaciones para 1 librería; esas librerías figuran sin recomendación verificada.")]);

    /// <summary>Análisis extenso: <paramref name="cantidad"/> dependencias con estados y contenido variados.</summary>
    public static Analisis Extenso(int cantidad = 40)
    {
        var lista = new List<DependenciaAnalizada>();
        for (var i = 1; i <= cantidad; i++)
        {
            var nombre = $"Ejemplo.Paquete{i:00}" + (i % 7 == 0 ? ".Con.Un.Nombre.Bastante.Mas.Largo.Que.El.Resto" : "");
            switch (i % 4)
            {
                case 0:
                    lista.Add(Dependencia(nombre, EstadoDiagnostico.Desactualizada, $"{i}.0.0",
                        diagnostico: Diagnostico(EstadoDiagnostico.Desactualizada, "Hay una versión más reciente. Motivos: 1.",
                            [Hallazgo("version_nueva", $"Existe la versión estable {i + 1}.0.0.", GravedadHallazgo.Aviso, false, ("ultima_estable", $"{i + 1}.0.0"))]),
                        recomendaciones: [Recomendacion(TipoRecomendacion.ActualizarVersion, nombre, $"{i + 1}.0.0")]));
                    break;
                case 1:
                    lista.Add(Dependencia(nombre, EstadoDiagnostico.NoDeterminada, $"{i}.0.0",
                        diagnostico: Diagnostico(EstadoDiagnostico.NoDeterminada, "No determinada. Limitaciones: sin grupo compatible.",
                            [Hallazgo("sin_grupo", "No hay un grupo de dependencias para net8.0.", GravedadHallazgo.Informativo)], indicadores: Indicadores(compatible: false))));
                    break;
                default:
                    lista.Add(Dependencia(nombre, EstadoDiagnostico.Actualizada, $"{i}.0.0"));
                    break;
            }
        }

        return Analisis(lista, nombre: "Extenso.csproj");
    }

    /// <summary>El caso de la revisión de E10: 40 dependencias, nombres largos, copia vencida, sin datos, evidencia incompleta, candidatos, varios frameworks y análisis PARCIAL.</summary>
    public static Analisis Extenso40Parcial()
    {
        var baseParcial = Parcial();
        var resto = Extenso(31).Dependencias;     // 5 del caso parcial + 4 de nombre muy largo + 31 = 40
        var largos = new List<DependenciaAnalizada>();
        for (var i = 0; i < 4; i++)
            largos.Add(Dependencia($"Ejemplo.Microsoft.Extensions.Configuration.Binder.Con.Nombre.Muy.Largo.Numero{i}.Y.Segmentos.Adicionales.Para.Forzar.Varias.Lineas", EstadoDiagnostico.Desactualizada,
                "1.2.3-rc.1+build.metadata.muy.larga.0123456789", diagnostico: Diagnostico(EstadoDiagnostico.Desactualizada, "Hay una versión más reciente. Motivos: 1.",
                    [Hallazgo("version_nueva", "Existe una versión estable más reciente: " + new string('z', 120), GravedadHallazgo.Aviso, false, ("ultima_estable", "9.9.9"), ("detalle", new string('d', 90)))]),
                recomendaciones: [Recomendacion(TipoRecomendacion.ActualizarVersion, $"Ejemplo.Largo{i}", "9.9.9")]));
        var todas = baseParcial.Dependencias.Concat(largos).Concat(resto).ToList();
        return baseParcial with { Dependencias = todas, NombreArchivo = ArchivoConAcentos };
    }

    /// <summary>Texto con caracteres que Liberation Sans no cubre (CJK, emoji, un control): el reporte no debe perderlos ni sustituirlos en silencio.</summary>
    public static Analisis ConCaracteresNoSoportados() => Analisis(
    [
        Dependencia("Ejemplo.日本語", EstadoDiagnostico.Desactualizada, "1.0.0",
            diagnostico: Diagnostico(EstadoDiagnostico.Desactualizada, "Hay una versión más reciente.",
                [Hallazgo("version_nueva", "Lanzamiento 🚀 disponible \u0007 con mensaje en japonés: 更新", GravedadHallazgo.Aviso, false, ("nota", "emoji 😀"))])),
        Dependencia("Ejemplo.Latino", EstadoDiagnostico.Actualizada, "1.0.0"),
    ], nombre: "Proyecto-日本.csproj");
}
