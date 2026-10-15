using Compartido.Contratos;
using Servidor.Api.Errores;
using Servidor.Api.Mapeo;
using Servidor.Negocio.Lectura;

namespace Servidor.Api.Endpoints;

public static class ProyectosEndpoints
{
    public const string CampoArchivo = "archivo";

    public static RouteGroupBuilder MapProyectos(this RouteGroupBuilder api)
    {
        // Endpoint temporal de E2: lee el .csproj sin consultar NuGet ni guardar nada.
        // Se lee el formulario a mano (en lugar de IFormFile) porque no hay cookies ni sesión: no existe un token antifalsificación que validar.
        api.MapPost("/proyectos/lectura", async (HttpRequest solicitud, ILectorProyecto lector, OpcionesApi opciones, CancellationToken ct) =>
        {
            if (solicitud.ContentLength > opciones.TamanoMaximoCuerpoBytes)
                return CuerpoDemasiadoGrande(opciones);

            if (!solicitud.HasFormContentType)
                return ManejoErrores.Problema(CodigosError.ArchivoAusente,
                    $"Envía el archivo como multipart/form-data en el campo «{CampoArchivo}».");

            IFormCollection formulario;
            try
            {
                formulario = await solicitud.ReadFormAsync(ct);
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return CuerpoDemasiadoGrande(opciones);
            }
            catch (InvalidDataException)
            {
                return ManejoErrores.Problema(CodigosError.ArchivoAusente, "No se pudo leer el formulario enviado.");
            }

            var archivo = formulario.Files.GetFile(CampoArchivo);
            if (archivo is null)
                return ManejoErrores.Problema(CodigosError.ArchivoAusente,
                    "No se recibió ningún archivo. Selecciona un archivo .csproj.");

            try
            {
                await using var contenido = archivo.OpenReadStream();
                var proyecto = lector.Leer(archivo.FileName, contenido);
                return Results.Ok(MapeadorProyecto.ADto(proyecto));
            }
            catch (ArchivoInvalidoException ex)
            {
                return ManejoErrores.Problema(ex.Codigo, ex.Message);
            }
        }).WithName("LecturaProyecto");

        return api;
    }

    private static IResult CuerpoDemasiadoGrande(OpcionesApi opciones) =>
        ManejoErrores.Problema(CodigosError.CuerpoExcedeTamano,
            $"La solicitud supera el tamaño máximo permitido ({opciones.TamanoMaximoCuerpoBytes / (1024 * 1024)} MB).");
}

/// <summary>Límites HTTP de la API. El del cuerpo debe ser mayor que el del archivo para dejar margen al formulario.</summary>
public sealed class OpcionesApi
{
    public long TamanoMaximoCuerpoBytes { get; set; } = LimitesEntrada.TamanoMaximoCuerpoBytes;
}
