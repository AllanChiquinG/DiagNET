using Compartido.Contratos;
using Servidor.Negocio.Lectura;
using Servidor.Negocio.Orquestacion;

namespace Servidor.Api.Endpoints;

public static class ConfiguracionEndpoints
{
    /// <summary>Valores públicos que la interfaz muestra (retención real, límites). No expone cadenas de conexión ni ninguna otra configuración.</summary>
    public static RouteGroupBuilder MapConfiguracion(this RouteGroupBuilder api)
    {
        api.MapGet("/configuracion", (OpcionesConsultas consultas, OpcionesLectura lectura, OpcionesOrquestacion orquestacion, Servidor.Negocio.Operacion.OpcionesPurga purga) =>
            Results.Ok(new ConfiguracionPublicaDto(
                consultas.RetencionDias, consultas.MaximoIds, lectura.TamanoMaximoArchivoBytes, lectura.MaximoDependencias,
                (int)orquestacion.PresupuestoAnalisis.TotalSeconds, purga.Habilitada))).WithName("Configuracion");
        return api;
    }
}
