using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Servidor.Negocio.Lectura;

/// <summary>
/// Lee un .csproj como XML sin ejecutarlo: sin MSBuild, sin importaciones, sin DTD ni recursos externos, sin red y sin guardar nada.
/// Conserva lo declarado tal cual y avisa de lo que no puede resolver; nunca sustituye una versión desconocida por otra.
/// </summary>
public sealed partial class LectorCsproj(OpcionesLectura opciones) : ILectorProyecto
{
    private readonly ValidadorArchivoCsproj _validador = new(opciones);

    [GeneratedRegex(@"^net(8|10)\.0(-(?<sufijo>[a-z][a-z0-9.]*))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FrameworkAdmitido();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}(-[0-9A-Za-z.\-]+)?(\+[0-9A-Za-z.\-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionConcreta();

    public ProyectoLeido Leer(string? nombreArchivo, Stream contenido)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        var nombre = _validador.Validar(nombreArchivo, contenido.CanSeek ? contenido.Length : 1);
        var documento = Cargar(contenido);

        if (documento.Root is null || documento.Root.Name.LocalName != "Project")
            throw new ArchivoInvalidoException(CodigosLectura.ProyectoInvalido,
                "El archivo es un XML válido, pero no es un proyecto de .NET (falta el elemento raíz Project).");

        var advertencias = new List<AdvertenciaLectura>();
        var frameworks = LeerFrameworks(documento, advertencias);
        var dependencias = LeerDependencias(documento, advertencias);

        if (documento.Descendants().Any(e => e.Name.LocalName == "Import"))
            advertencias.Add(new(AdvertenciasLectura.ImportacionNoLeida,
                "El proyecto importa otros archivos (por ejemplo Directory.Packages.props) que DiagNET no lee; pueden declarar más paquetes o versiones."));

        if (dependencias.Count == 0)
            advertencias.Add(new(AdvertenciasLectura.SinPaquetes,
                "El proyecto no declara ningún PackageReference; no hay librerías que diagnosticar."));

        return new ProyectoLeido(nombre, frameworks, dependencias, advertencias);
    }

    private XDocument Cargar(Stream contenido)
    {
        var maximo = opciones.TamanoMaximoArchivoBytes;
        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int leidos;
        while ((leidos = contenido.Read(buffer, 0, buffer.Length)) > 0)
        {
            memoria.Write(buffer, 0, leidos);
            if (memoria.Length > maximo)
                throw new ArchivoInvalidoException(CodigosLectura.ArchivoExcedeTamano, _validador.MensajeTamano());
        }

        if (memoria.Length == 0)
            throw new ArchivoInvalidoException(CodigosLectura.ArchivoVacio, "El archivo está vacío.");

        memoria.Position = 0;
        var ajustes = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, // sin DOCTYPE: evita XXE y expansión de entidades
            XmlResolver = null,                     // nunca se resuelven recursos externos
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = maximo,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var lector = XmlReader.Create(memoria, ajustes);
            return XDocument.Load(lector, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new ArchivoInvalidoException(CodigosLectura.XmlInvalido,
                "El archivo no es un XML válido. Comprueba que sea un .csproj completo y que no incluya una declaración DOCTYPE.");
        }
    }

    // ---------- Frameworks (DEC-08) ----------

    private static List<FrameworkDeclarado> LeerFrameworks(XDocument documento, List<AdvertenciaLectura> advertencias)
    {
        var tokens = new List<string>();
        var condicionado = false;
        foreach (var e in documento.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks"))
        {
            if (e.Attribute("Condition") is not null || e.Parent?.Attribute("Condition") is not null) condicionado = true;
            foreach (var parte in e.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (!tokens.Contains(parte, StringComparer.OrdinalIgnoreCase)) tokens.Add(parte);
        }

        if (condicionado)
            advertencias.Add(new(AdvertenciasLectura.CondicionNoEvaluada,
                "La declaración del framework tiene una condición que DiagNET no evalúa; se muestran los valores tal cual."));

        var resultado = new List<FrameworkDeclarado>();
        var sufijos = new List<string>();
        var sinResolver = tokens.Count == 0;
        var fuera = new List<string>();
        var admitidos = 0;

        foreach (var token in tokens)
        {
            if (token.Contains("$(") || token.Contains("@("))
            {
                sinResolver = true;
                resultado.Add(new(token, null, "Depende de una variable que no se resuelve desde un solo .csproj."));
                continue;
            }

            var m = FrameworkAdmitido().Match(token);
            if (m.Success)
            {
                admitidos++;
                var sufijo = m.Groups["sufijo"];
                if (sufijo.Success)
                {
                    sufijos.Add(token);
                    resultado.Add(new(token, true, "Incluye un sufijo de plataforma; DiagNET lo conserva pero no determina la compatibilidad por plataforma."));
                }
                else
                {
                    resultado.Add(new(token, true));
                }
            }
            else
            {
                fuera.Add(token);
                resultado.Add(new(token, false, "Fuera del alcance de DiagNET (.NET 8 y .NET 10)."));
            }
        }

        if (admitidos == 0 && fuera.Count > 0 && !sinResolver)
            throw new ArchivoInvalidoException(CodigosLectura.FrameworkFueraDeAlcance,
                $"DiagNET analiza proyectos .NET 8 y .NET 10. Frameworks declarados: {string.Join(", ", fuera)}.");

        if (sinResolver)
            advertencias.Add(new(AdvertenciasLectura.FrameworkNoResuelto,
                tokens.Count == 0
                    ? "El proyecto no declara TargetFramework. Se lee el archivo, pero no se puede afirmar compatibilidad ni un diagnóstico completo."
                    : "Algún framework depende de una variable. Se lee el archivo, pero no se puede afirmar compatibilidad ni un diagnóstico completo."));

        if (fuera.Count > 0)
            advertencias.Add(new(AdvertenciasLectura.FrameworkFueraDeAlcanceOmitido,
                $"Se omiten por estar fuera del alcance (.NET 8 y .NET 10): {string.Join(", ", fuera)}. Solo se evaluarán los frameworks admitidos."));

        if (sufijos.Count > 0)
            advertencias.Add(new(AdvertenciasLectura.SufijoPlataforma,
                $"Se conservan los sufijos de plataforma ({string.Join(", ", sufijos)}); no se supone compatibilidad por plataforma."));

        return resultado;
    }

    // ---------- Dependencias (RF-04, DEC-09) ----------

    private sealed record Referencia(string Nombre, string? Version, string? Condicion);

    private List<DependenciaDeclarada> LeerDependencias(XDocument documento, List<AdvertenciaLectura> advertencias)
    {
        var referencias = new List<Referencia>();
        var noInterpretada = false;

        foreach (var e in documento.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var nombre = e.Attribute("Include")?.Value.Trim();
            if (string.IsNullOrEmpty(nombre))
            {
                noInterpretada = true; // Update, Remove o sin identificador
                continue;
            }

            var version = ValorDe(e, "Version") ?? ValorDe(e, "VersionOverride");
            referencias.Add(new Referencia(nombre, version, CondicionDe(e)));
        }

        if (noInterpretada)
            advertencias.Add(new(AdvertenciasLectura.ReferenciaNoInterpretada,
                "Hay PackageReference con Update, Remove o sin Include; DiagNET no los interpreta."));

        var resultado = new List<DependenciaDeclarada>();
        var distintas = 0;
        foreach (var grupo in referencias.GroupBy(r => r.Nombre, StringComparer.OrdinalIgnoreCase))
        {
            distintas++;
            // Duplicados equivalentes (misma versión y misma condición) se unifican; cualquier diferencia se conserva.
            var unicas = grupo
                .DistinctBy(r => (Normalizar(r.Version), Normalizar(r.Condicion)))
                .ToList();
            var conflicto = unicas.Count > 1;

            foreach (var r in unicas)
                resultado.Add(Construir(r, conflicto));
        }

        if (distintas > opciones.MaximoDependencias)
            throw new ArchivoInvalidoException(CodigosLectura.LimiteDependenciasExcedido,
                $"El proyecto declara {distintas} dependencias directas distintas; el máximo permitido es {opciones.MaximoDependencias}. " +
                "No se analizó ninguna.");

        return resultado;
    }

    private static string Normalizar(string? valor) => (valor ?? "").Trim().ToUpperInvariant();

    private static string? ValorDe(XElement referencia, string nombre)
    {
        var valor = referencia.Attribute(nombre)?.Value
                    ?? referencia.Elements().FirstOrDefault(h => h.Name.LocalName == nombre)?.Value;
        return valor?.Trim();
    }

    private static string? CondicionDe(XElement referencia)
    {
        var propia = referencia.Attribute("Condition")?.Value.Trim();
        var grupo = referencia.Parent?.Attribute("Condition")?.Value.Trim();
        return (string.IsNullOrEmpty(grupo), string.IsNullOrEmpty(propia)) switch
        {
            (true, true) => null,
            (false, true) => grupo,
            (true, false) => propia,
            _ => $"({grupo}) AND ({propia})",
        };
    }

    private static DependenciaDeclarada Construir(Referencia r, bool conflicto)
    {
        var advertencias = new List<AdvertenciaLectura>();
        var (tipo, version) = Clasificar(r.Version);

        switch (tipo)
        {
            case TipoVersion.Ausente:
                advertencias.Add(new(AdvertenciasLectura.VersionNoResuelta,
                    "No declara versión en este archivo (puede definirse en Directory.Packages.props). No se asume ninguna.", r.Nombre));
                break;
            case TipoVersion.Rango:
                advertencias.Add(new(AdvertenciasLectura.VersionNoResuelta,
                    "La versión es un rango; DiagNET no la resuelve a una versión concreta.", r.Nombre));
                break;
            case TipoVersion.Flotante:
                advertencias.Add(new(AdvertenciasLectura.VersionNoResuelta,
                    "La versión es flotante (usa «*»); DiagNET no la resuelve a una versión concreta.", r.Nombre));
                break;
            case TipoVersion.Propiedad:
                advertencias.Add(new(AdvertenciasLectura.VersionNoResuelta,
                    "La versión depende de una propiedad que no se resuelve desde un solo .csproj.", r.Nombre));
                break;
            case TipoVersion.Invalida:
                advertencias.Add(new(AdvertenciasLectura.VersionNoResuelta,
                    "La versión no tiene un formato reconocible; se conserva tal cual.", r.Nombre));
                break;
        }

        if (r.Nombre.Contains("$("))
            advertencias.Add(new(AdvertenciasLectura.NombreNoResuelto,
                "El identificador del paquete depende de una variable y no se puede consultar.", r.Nombre));

        if (r.Condicion is not null)
            advertencias.Add(new(AdvertenciasLectura.CondicionNoEvaluada,
                "La referencia tiene una condición que DiagNET no evalúa; se muestra tal cual.", r.Nombre));

        if (conflicto)
            advertencias.Add(new(AdvertenciasLectura.DependenciaDuplicadaConflictiva,
                "El paquete se declara varias veces con versiones o condiciones distintas; se conservan todas las declaraciones.", r.Nombre));

        return new DependenciaDeclarada(r.Nombre, version, tipo, r.Condicion, advertencias);
    }

    private static (TipoVersion Tipo, string? Version) Clasificar(string? bruto)
    {
        var v = bruto?.Trim();
        if (string.IsNullOrEmpty(v)) return (TipoVersion.Ausente, null);
        if (v.Contains("$(") || v.Contains("@(")) return (TipoVersion.Propiedad, v);
        if (v.Contains('*')) return (TipoVersion.Flotante, v);
        if (v[0] is '[' or '(' || v.Contains(',')) return (TipoVersion.Rango, v);
        return VersionConcreta().IsMatch(v) ? (TipoVersion.Exacta, v) : (TipoVersion.Invalida, v);
    }
}
