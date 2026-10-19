using Compartido.Contratos;
using Servidor.Api.Errores;
using Servidor.Api.Mapeo;
using Servidor.Negocio.Lectura;

namespace Servidor.Api.Endpoints;

public static class ProyectosEndpoints
{
    public const string CampoArchivo = FormularioArchivo.CampoArchivo;

    public static RouteGroupBuilder MapProyectos(this RouteGroupBuilder api)
    {
        // Endpoint de LECTURA (E2), que se conserva mientras la interfaz lo necesite (E9 decidirá si se retira): lee el .csproj y devuelve lo que se declaró
        // —dependencias, frameworks y advertencias de lectura— SIN consultar NuGet, SIN diagnosticar y SIN guardar nada. Sirve para mostrar una vista previa
        // del archivo antes de analizarlo. Relación con el análisis: POST /api/analisis ejecuta esta misma lectura (mismo lector y mismas validaciones, por lo
        // que rechaza las mismas entradas con los mismos códigos) y a continuación consulta NuGet, diagnostica, recomienda y guarda; este endpoint solo hace la primera parte.
        api.MapPost("/proyectos/lectura", async (HttpRequest solicitud, ILectorProyecto lector, OpcionesApi opciones, CancellationToken ct) =>
        {
            var formulario = await FormularioArchivo.LeerAsync(solicitud, opciones, ct);
            if (formulario.Error is not null) return formulario.Error;

            try
            {
                await using var contenido = formulario.Archivo!.OpenReadStream();
                var proyecto = lector.Leer(formulario.Archivo.FileName, contenido);
                return Results.Ok(MapeadorProyecto.ADto(proyecto));
            }
            catch (ArchivoInvalidoException ex)
            {
                return ManejoErrores.Problema(ex.Codigo, ex.Message);
            }
        }).WithName("LecturaProyecto");

        return api;
    }
}

/// <summary>Límites HTTP de la API. El del cuerpo debe ser mayor que el del archivo para dejar margen al formulario.</summary>
public sealed class OpcionesApi
{
    public long TamanoMaximoCuerpoBytes { get; set; } = LimitesEntrada.TamanoMaximoCuerpoBytes;
}
