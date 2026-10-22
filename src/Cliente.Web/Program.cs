using Cliente.Web;
using Cliente.Web.Servicios;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// El análisis puede tardar hasta el presupuesto del servidor (2 minutos): el tiempo de espera del navegador es mayor, y el usuario puede cancelar.
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress), Timeout = TimeSpan.FromMinutes(5) });
builder.Services.AddScoped<ClienteApi>();
builder.Services.AddScoped<IServicioNavegador, ServicioNavegador>();
builder.Services.AddScoped<AlmacenHistorial>();
builder.Services.AddScoped<AvisosSesion>();
builder.Services.AddScoped<ConfiguracionCliente>();

await builder.Build().RunAsync();
