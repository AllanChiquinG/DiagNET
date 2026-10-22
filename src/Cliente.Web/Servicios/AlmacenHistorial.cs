using System.Text.Json;
using Compartido.Contratos;

namespace Cliente.Web.Servicios;

/// <summary>
/// Un análisis que ESTE navegador creó. Solo se guardan localmente el identificador y lo mínimo para mostrar la entrada aunque el análisis ya no exista en el servidor.
/// El historial pertenece al navegador: no es una cuenta, no se comparte entre dispositivos y se pierde si se borran los datos del sitio.
/// </summary>
public sealed record EntradaLocal(Guid Id, string NombreArchivo, DateTimeOffset FechaAnalisis);

public sealed record EstadoAlmacen(bool Disponible, IReadOnlyList<EntradaLocal> Entradas, bool Danado);

/// <summary>Lista de identificadores guardada en el almacenamiento local del navegador (DEC-06).</summary>
public sealed class AlmacenHistorial(IServicioNavegador navegador)
{
    public const string Clave = "diagnet.historial.v1";

    /// <summary>Límite de entradas guardadas, para no crecer sin fin; al superarlo se avisa (no se descarta en silencio).</summary>
    public const int MaximoEntradas = 500;

    private static readonly JsonSerializerOptions Opciones = new(JsonSerializerDefaults.Web);

    public async Task<EstadoAlmacen> CargarAsync()
    {
        if (!await navegador.AlmacenamientoDisponibleAsync()) return new EstadoAlmacen(false, [], false);

        try
        {
            var texto = await navegador.LeerAsync(Clave);
            if (string.IsNullOrWhiteSpace(texto)) return new EstadoAlmacen(true, [], false);

            var entradas = JsonSerializer.Deserialize<List<EntradaLocal>>(texto, Opciones);
            return entradas is null ? new EstadoAlmacen(true, [], true) : new EstadoAlmacen(true, entradas, false);
        }
        catch (JsonException)
        {
            return new EstadoAlmacen(true, [], true);
        }
        catch (Exception)
        {
            return new EstadoAlmacen(false, [], false);
        }
    }

    /// <summary>Agrega (o actualiza) una entrada. Devuelve <c>false</c> si no se pudo guardar (almacenamiento bloqueado, lleno o límite alcanzado).</summary>
    public async Task<bool> AgregarAsync(EntradaLocal entrada)
    {
        var estado = await CargarAsync();
        if (!estado.Disponible) return false;

        var lista = estado.Entradas.Where(e => e.Id != entrada.Id).ToList();
        if (lista.Count >= MaximoEntradas) return false;
        lista.Insert(0, entrada);
        return await GuardarAsync(lista);
    }

    public async Task<bool> QuitarAsync(Guid id)
    {
        var estado = await CargarAsync();
        if (!estado.Disponible) return false;
        return await GuardarAsync(estado.Entradas.Where(e => e.Id != id).ToList());
    }

    public async Task<bool> ContieneAsync(Guid id) => (await CargarAsync()).Entradas.Any(e => e.Id == id);

    private async Task<bool> GuardarAsync(List<EntradaLocal> lista)
    {
        try
        {
            await navegador.EscribirAsync(Clave, JsonSerializer.Serialize(lista, Opciones));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>Avisos que sobreviven a una navegación (por ejemplo, «este análisis no se pudo guardar en el historial del navegador»).</summary>
public sealed class AvisosSesion
{
    public bool HistorialNoGuardado { get; set; }

    public Guid? IdSinGuardar { get; set; }
}

/// <summary>Valores reales de configuración del servidor (retención, límites). Se piden una vez; si no responde, la interfaz no inventa números.</summary>
public sealed class ConfiguracionCliente(ClienteApi api)
{
    private ConfiguracionPublicaDto? _valor;

    public async Task<ConfiguracionPublicaDto?> ObtenerAsync()
    {
        if (_valor is not null) return _valor;
        var r = await api.ObtenerConfiguracionAsync();
        _valor = r.Valor;
        return _valor;
    }

    /// <summary>Límite de tamaño usado para validar en el navegador: el del servidor si respondió; si no, la constante compartida del contrato.</summary>
    public async Task<long> TamanoMaximoArchivoAsync() => (await ObtenerAsync())?.TamanoMaximoArchivoBytes ?? LimitesEntrada.TamanoMaximoArchivoBytes;
}
