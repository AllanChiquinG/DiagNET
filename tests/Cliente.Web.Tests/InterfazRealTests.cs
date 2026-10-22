using System.Text.RegularExpressions;
using Bunit;
using Cliente.Web.Layout;

namespace Cliente.Web.Tests;

/// <summary>Estructura general de la aplicación: navegación, accesibilidad básica y ausencia de restos del prototipo (datos fijos, simuladores, recursos externos).</summary>
public class InterfazRealTests : BunitContext
{
    private static string RaizRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DiagNET.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (DiagNET.slnx).");
    }

    private static string Cliente(string relativa) => Path.Combine(RaizRepositorio(), "src", "Cliente.Web", relativa);

    private static IEnumerable<string> FuentesDelCliente() =>
        Directory.EnumerateFiles(Path.Combine(RaizRepositorio(), "src", "Cliente.Web"), "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs") || f.EndsWith(".js") || f.EndsWith(".css") || f.EndsWith(".html"));

    [Fact]
    public void El_diseno_tiene_enlace_de_salto_navegacion_principal_con_dos_destinos_y_regiones_de_la_pagina()
    {
        var cut = Render<MainLayout>(p => p.Add(c => c.Body, builder => builder.AddContent(0, "contenido")));

        Assert.Equal("#contenido", cut.Find("a.skip").GetAttribute("href"));
        Assert.Equal("contenido", cut.Find("main").Id);
        var enlaces = cut.FindAll("nav[aria-label='Principal'] a");
        Assert.Equal(["Nuevo análisis", "Historial"], enlaces.Select(a => a.TextContent.Trim()));
        Assert.Equal(["", "historial"], enlaces.Select(a => a.GetAttribute("href")));
        Assert.NotNull(cut.Find("header.top"));
        Assert.NotNull(cut.Find("footer.pie"));
        Assert.Equal("DiagNET, ir al inicio", cut.Find("a.brand").GetAttribute("aria-label"));
        Assert.DoesNotContain("<img", cut.Markup);   // sin logo universitario ni imágenes: el logotipo es propio y vectorial
    }

    [Fact]
    public void La_interfaz_no_conserva_restos_del_prototipo_ni_simuladores_ni_datos_fijos()
    {
        var prohibidos = new[] { "setInterval", "Math.random", "Prototipo ·", "Prototipo:", "simular archivo", "datos de ejemplo", "class=\"proto\"", "data-n=", "Acme.Reportes", "MiTienda", "DX-8F3A21", "Newtonsoft.Json", "Serilog" };

        foreach (var archivo in FuentesDelCliente())
        {
            var texto = File.ReadAllText(archivo);
            foreach (var p in prohibidos)
                Assert.False(texto.Contains(p), $"{Path.GetFileName(archivo)} contiene «{p}»: la interfaz real no puede traer datos ni controles del prototipo.");
        }
    }

    [Fact]
    public void La_pagina_de_arranque_no_pide_recursos_a_terceros_y_carga_el_puente_antes_de_blazor()
    {
        var html = File.ReadAllText(Cliente("wwwroot/index.html"));
        var css = File.ReadAllText(Cliente("wwwroot/css/app.css"));

        Assert.Contains("<html lang=\"es\">", html);
        Assert.DoesNotContain("fonts.googleapis", html + css);
        Assert.DoesNotMatch(@"(href|src)=""https?://", html);
        Assert.DoesNotMatch(@"url\(\s*['""]?https?://", css);
        Assert.True(html.IndexOf("js/diagnet.js", StringComparison.Ordinal) < html.IndexOf("blazor.webassembly", StringComparison.Ordinal));
    }

    [Fact]
    public void Cada_llamada_a_JavaScript_del_servicio_existe_en_el_puente()
    {
        var servicio = File.ReadAllText(Cliente("Servicios/ServicioNavegador.cs"));
        var puente = File.ReadAllText(Cliente("wwwroot/js/diagnet.js"));

        var usadas = Regex.Matches(servicio, "\"DiagNET\\.(\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        var exportadas = Regex.Match(puente, @"window\.DiagNET\s*=\s*\{(?<n>[^}]*)\}").Groups["n"].Value.Split(',').Select(x => x.Trim()).ToList();

        Assert.NotEmpty(usadas);
        Assert.All(usadas, nombre => Assert.Contains(nombre, exportadas));
        Assert.All(exportadas, nombre => Assert.Contains($"function {nombre}(", puente));
    }

    [Fact]
    public void La_hoja_de_estilos_respeta_el_movimiento_reducido_conserva_la_paleta_azul_y_oculta_solo_visualmente()
    {
        var css = File.ReadAllText(Cliente("wwwroot/css/app.css"));

        Assert.Contains("prefers-reduced-motion:reduce", css);
        Assert.Contains("color-scheme:light", css);
        Assert.Contains("--sky-600:#2679CF", css);
        Assert.Contains("max-width:1180px", css);
        Assert.Contains(".vh{position:absolute", css);
        Assert.Contains(":focus-visible", css);
    }
}
