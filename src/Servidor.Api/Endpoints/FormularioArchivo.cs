using Compartido.Contratos;
using Servidor.Api.Errores;

namespace Servidor.Api.Endpoints;

/// <summary>
/// Lectura común del archivo subido (la usan <c>POST /api/proyectos/lectura</c> y <c>POST /api/analisis</c>). Se lee el formulario a mano (en lugar de IFormFile)
/// porque no hay cookies ni sesión: no existe un token antifalsificación que validar. El archivo permanece en memoria y nunca se escribe en disco.
/// </summary>
public static class FormularioArchivo
{
    public const string CampoArchivo = "archivo";

    public sealed record Resultado(IFormFile? Archivo, IResult? Error);

    public static async Task<Resultado> LeerAsync(HttpRequest solicitud, OpcionesApi opciones, CancellationToken ct)
    {
        if (solicitud.ContentLength > opciones.TamanoMaximoCuerpoBytes)
            return new(null, CuerpoDemasiadoGrande(opciones));

        if (!solicitud.HasFormContentType)
            return new(null, ManejoErrores.Problema(CodigosError.ArchivoAusente,
                $"Envía el archivo como multipart/form-data en el campo «{CampoArchivo}»."));

        IFormCollection formulario;
        try
        {
            formulario = await solicitud.ReadFormAsync(ct);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new(null, CuerpoDemasiadoGrande(opciones));
        }
        catch (InvalidDataException)
        {
            return new(null, ManejoErrores.Problema(CodigosError.ArchivoAusente, "No se pudo leer el formulario enviado."));
        }

        var archivo = formulario.Files.GetFile(CampoArchivo);
        return archivo is null
            ? new(null, ManejoErrores.Problema(CodigosError.ArchivoAusente, "No se recibió ningún archivo. Selecciona un archivo .csproj."))
            : new(archivo, null);
    }

    private static IResult CuerpoDemasiadoGrande(OpcionesApi opciones) =>
        ManejoErrores.Problema(CodigosError.CuerpoExcedeTamano,
            $"La solicitud supera el tamaño máximo permitido ({opciones.TamanoMaximoCuerpoBytes / (1024 * 1024)} MB).");
}
