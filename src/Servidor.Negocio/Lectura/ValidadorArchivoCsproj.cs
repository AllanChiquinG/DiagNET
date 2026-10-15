namespace Servidor.Negocio.Lectura;

/// <summary>Validaciones de forma previas a leer el contenido: presencia, extensión y tamaño.</summary>
public sealed class ValidadorArchivoCsproj(OpcionesLectura opciones)
{
    /// <summary>Quita cualquier ruta del nombre (separadores de Windows o Unix). Nunca se conserva una ruta del equipo del usuario.</summary>
    public static string NombreSinRuta(string nombre)
    {
        var indice = nombre.LastIndexOfAny(['/', '\\']);
        return (indice >= 0 ? nombre[(indice + 1)..] : nombre).Trim();
    }

    /// <param name="nombre">Nombre recibido, o <c>null</c> si no llegó archivo.</param>
    /// <param name="longitud">Tamaño declarado en bytes, si se conoce.</param>
    /// <returns>El nombre sin ruta.</returns>
    public string Validar(string? nombre, long? longitud)
    {
        if (string.IsNullOrWhiteSpace(nombre) || longitud is null)
            throw new ArchivoInvalidoException(CodigosLectura.ArchivoAusente, "No se recibió ningún archivo. Selecciona un archivo .csproj.");

        var limpio = NombreSinRuta(nombre);
        if (!limpio.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || limpio.Length <= ".csproj".Length)
            throw new ArchivoInvalidoException(CodigosLectura.ExtensionInvalida, "El archivo debe tener la extensión .csproj.");

        if (longitud.Value <= 0)
            throw new ArchivoInvalidoException(CodigosLectura.ArchivoVacio, "El archivo está vacío.");

        if (longitud.Value > opciones.TamanoMaximoArchivoBytes)
            throw new ArchivoInvalidoException(CodigosLectura.ArchivoExcedeTamano, MensajeTamano());

        return limpio;
    }

    internal string MensajeTamano() =>
        $"El archivo supera el tamaño máximo permitido ({opciones.TamanoMaximoArchivoBytes / (1024 * 1024)} MB).";
}
