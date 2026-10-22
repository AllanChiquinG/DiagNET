using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Cliente.Web.Servicios;

/// <summary>
/// Capacidades del navegador que Blazor no ofrece directamente. Es una interfaz para que los componentes se puedan probar sin navegador:
/// las pruebas usan una implementación en memoria; la real es <see cref="ServicioNavegador"/> (script js/diagnet.js).
/// </summary>
public interface IServicioNavegador
{
    /// <summary>Si el almacenamiento local funciona (puede estar bloqueado por el modo privado, políticas o cuota).</summary>
    Task<bool> AlmacenamientoDisponibleAsync();

    /// <summary>Lanza si el almacenamiento no está disponible.</summary>
    Task<string?> LeerAsync(string clave);

    /// <summary>Lanza si el almacenamiento no está disponible o está lleno.</summary>
    Task EscribirAsync(string clave, string valor);

    Task<bool> CopiarAsync(string texto);

    Task<string> DireccionActualAsync();

    /// <summary>Entrega un archivo al usuario (descarga del navegador). <c>false</c> si el navegador no pudo iniciar la descarga.</summary>
    Task<bool> DescargarArchivoAsync(string nombre, byte[] contenido, string tipo);

    Task EnfocarAsync(ElementReference elemento);

    /// <summary>
    /// Abre el panel como modal: mueve el foco dentro, lo mantiene (Tab/Mayús+Tab) y recuerda el elemento que lo abrió (<paramref name="selectorDisparador"/>, un selector CSS).
    /// <paramref name="alCerrar"/> se invoca cuando el usuario pulsa Escape, aunque el foco no esté dentro del panel.
    /// </summary>
    Task AbrirPanelAsync(ElementReference panel, string? selectorDisparador, Func<Task> alCerrar);

    /// <summary>Cierra el panel y devuelve el foco a quien lo abrió.</summary>
    Task CerrarPanelAsync();
}

public sealed class ServicioNavegador(IJSRuntime js) : IServicioNavegador, IAsyncDisposable
{
    private DotNetObjectReference<PuenteCierre>? _puente;

    public async Task<bool> AlmacenamientoDisponibleAsync()
    {
        try
        {
            return await js.InvokeAsync<bool>("DiagNET.almacenamientoDisponible");
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task<string?> LeerAsync(string clave) => await js.InvokeAsync<string?>("DiagNET.leer", clave);

    public async Task EscribirAsync(string clave, string valor) => await js.InvokeVoidAsync("DiagNET.escribir", clave, valor);

    public async Task<bool> CopiarAsync(string texto) => await js.InvokeAsync<bool>("DiagNET.copiar", texto);

    public async Task<string> DireccionActualAsync() => await js.InvokeAsync<string>("DiagNET.direccionActual");

    public async Task<bool> DescargarArchivoAsync(string nombre, byte[] contenido, string tipo)
    {
        try
        {
            return await js.InvokeAsync<bool>("DiagNET.descargar", nombre, contenido, tipo);
        }
        catch (JSException)
        {
            return false;
        }
    }

    public async Task EnfocarAsync(ElementReference elemento) => await js.InvokeAsync<bool>("DiagNET.enfocar", elemento);

    public async Task AbrirPanelAsync(ElementReference panel, string? selectorDisparador, Func<Task> alCerrar)
    {
        _puente?.Dispose();
        _puente = DotNetObjectReference.Create(new PuenteCierre(alCerrar));
        await js.InvokeVoidAsync("DiagNET.abrirPanel", panel, selectorDisparador, _puente);
    }

    public async Task CerrarPanelAsync()
    {
        try
        {
            await js.InvokeVoidAsync("DiagNET.cerrarPanel");
        }
        finally
        {
            _puente?.Dispose();
            _puente = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _puente?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Recibe desde JavaScript el aviso de cierre por Escape.</summary>
    public sealed class PuenteCierre(Func<Task> alCerrar)
    {
        [JSInvokable]
        public Task Cerrar() => alCerrar();
    }
}
