using System.Text;
using System.Text.RegularExpressions;
using Servidor.Integracion.Pdf;
using Servidor.Negocio.Dominio;
using Servidor.Negocio.Reportes;
using UglyToad.PdfPig;

namespace Servidor.Integracion.Tests.Pdf;

public class GeneradorPdfTests
{
    private static byte[] Pdf(Analisis a, int retencion = 90) =>
        new GeneradorPdf().Generar(ConsolidadorReporte.Consolidar(a, retencion, new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero)));

    /// <summary>Si la variable está definida, además de probar guarda los PDF para revisarlos a ojo (fuera del repositorio).</summary>
    private static void Guardar(string nombre, byte[] pdf)
    {
        var carpeta = Environment.GetEnvironmentVariable("DIAGNET_PDF_MUESTRAS");
        if (string.IsNullOrWhiteSpace(carpeta)) return;
        Directory.CreateDirectory(carpeta);
        File.WriteAllBytes(Path.Combine(carpeta, nombre), pdf);
    }

    private static (int Paginas, IReadOnlyList<string> TextoPorPagina) Leer(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        var paginas = doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)).Replace("​", "")).ToList();
        return (doc.NumberOfPages, paginas);
    }

    [Fact]
    public void Genera_un_PDF_valido_de_una_pagina_para_un_analisis_sin_dependencias()
    {
        var pdf = Pdf(MuestrasReporte.SinDependencias());
        Guardar("01_sin_dependencias.pdf", pdf);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        var (paginas, texto) = Leer(pdf);
        Assert.Equal(1, paginas);
        Assert.Contains("no declara paquetes NuGet", texto[0]);
        Assert.Contains("Página 1 de 1", texto[0]);
    }

    [Fact]
    public void El_numero_de_paginas_depende_del_contenido_y_todas_estan_numeradas()
    {
        var pequeno = Pdf(MuestrasReporte.Pequeno());
        var extenso = Pdf(MuestrasReporte.Extenso(40));
        var enorme = Pdf(MuestrasReporte.Extenso(100));
        Guardar("02_pequeno.pdf", pequeno);
        Guardar("04_extenso_40.pdf", extenso);
        Guardar("05_extenso_100.pdf", enorme);

        var (pp, _) = Leer(pequeno);
        var (pe, te) = Leer(extenso);
        var (pn, tn) = Leer(enorme);
        Assert.True(pe > pp && pn > pe, $"páginas: pequeño {pp}, 40 deps {pe}, 100 deps {pn}");
        for (var i = 0; i < pe; i++) Assert.Contains($"Página {i + 1} de {pe}", te[i]);
        for (var i = 0; i < pn; i++) Assert.Contains($"Página {i + 1} de {pn}", tn[i]);
    }

    [Fact]
    public void La_cabecera_de_la_tabla_se_repite_en_cada_pagina_que_continua_la_tabla()
    {
        var (_, texto) = Leer(Pdf(MuestrasReporte.Extenso(100)));

        var conCabecera = texto.Where(t => t.Contains("Versión declarada Última versión")).ToList();
        Assert.True(conCabecera.Count >= 3, "la tabla de 100 filas ocupa varias páginas y cada una repite su cabecera");
        Assert.All(conCabecera, t => Assert.Contains("Recomendación", t));
    }

    [Fact]
    public void Conserva_tildes_enes_y_nombres_largos_sin_perder_caracteres()
    {
        var pdf = Pdf(MuestrasReporte.Parcial());
        Guardar("03_parcial.pdf", pdf);
        var (_, texto) = Leer(pdf);
        var todo = string.Concat(texto).Replace("\n", "").Replace(" ", "");

        Assert.Contains("Aplicación", todo);
        Assert.Contains("Ñandú", todo);
        Assert.Contains("Gestión", todo);
        Assert.Contains("producción)conacentosáéíóú.csproj", todo);
        Assert.Contains(MuestrasReporte.NombreLargo, todo);
        Assert.Contains(new string('v', 140), todo);
    }

    [Fact]
    public void Muestra_varios_frameworks_y_distingue_diagnostico_parcial_de_recomendaciones_incompletas()
    {
        var todo = string.Join("\n", Leer(Pdf(MuestrasReporte.Parcial())).TextoPorPagina);

        Assert.Contains("net8.0 · net10.0", todo);
        Assert.Contains("Frameworks evaluados", todo);
        Assert.Contains("Diagnóstico parcial", todo);
        Assert.Contains("Avisos del análisis", todo);
        Assert.Contains("Recomendaciones incompletas", todo);
        Assert.Contains("No se pudieron buscar recomendaciones", todo);
        Assert.Contains("Se omitió el framework net6.0", todo);
    }

    [Fact]
    public void Un_analisis_completo_no_afirma_diagnostico_parcial_ni_recomendaciones_incompletas()
    {
        var todo = string.Join("\n", Leer(Pdf(MuestrasReporte.Pequeno())).TextoPorPagina);

        Assert.DoesNotContain("Diagnóstico parcial", todo);
        Assert.DoesNotContain("Recomendaciones incompletas", todo);
        Assert.Contains("Completo", todo);
    }

    [Fact]
    public void Conserva_los_textos_aprobados_de_E6_y_E7_y_las_limitaciones()
    {
        var parcial = string.Join(" ", Leer(Pdf(MuestrasReporte.Parcial())).TextoPorPagina).Replace("\n", " ");

        Assert.Contains("Candidato para revisión (relación funcional no demostrada)", parcial);
        Assert.Contains("Cambios de código no determinados.", parcial);
        Assert.Contains("Puede requerir cambios de código.", parcial);
        Assert.Contains("INFERIDA", parcial);
        Assert.Contains("compatibilidad con AOT no se evalúa", parcial);
        Assert.Contains("No evaluado", parcial);
        Assert.Contains("Copia guardada vencida", parcial);
        Assert.Contains("Evidencia incompleta", parcial);
        Assert.Contains("Sin datos de NuGet", parcial);   // en el detalle de la librería sin datos
        Assert.Contains("Origen de los datos: Copia guardada vencida", parcial);
        Assert.DoesNotContain("reemplazo recomendado", parcial.Replace("no es un reemplazo recomendado", ""));

        var pequeno = string.Join(" ", Leer(Pdf(MuestrasReporte.Pequeno())).TextoPorPagina).Replace("\n", " ");
        Assert.Contains("Alternativa sugerida por el autor", pequeno);
    }

    [Fact]
    public void Es_determinista_en_contenido_aunque_se_genere_varias_veces_y_en_paralelo()
    {
        var a = MuestrasReporte.Extenso(30);
        var referencia = string.Join("|", Leer(Pdf(a)).TextoPorPagina);

        var resultados = Enumerable.Range(0, 6).AsParallel().Select(_ => string.Join("|", Leer(Pdf(a)).TextoPorPagina)).ToList();
        Assert.All(resultados, r => Assert.Equal(referencia, r));
    }

    [Fact]
    public void Incrusta_la_fuente_y_no_deja_caracteres_de_sustitucion()
    {
        var pdf = Pdf(MuestrasReporte.Parcial());
        using var doc = PdfDocument.Open(pdf);
        var fuentes = doc.GetPages().SelectMany(p => p.Letters).Select(l => l.FontName).Distinct().ToList();
        Assert.NotEmpty(fuentes);
        Assert.All(fuentes, f => Assert.Contains("Liberation Sans", f ?? ""));
        Assert.DoesNotContain("�", string.Concat(Leer(pdf).TextoPorPagina));
    }

    public static TheoryData<string> Escenarios => new() { "sin_dependencias", "pequeno", "parcial", "extenso_40", "extenso_100" };

    private static Analisis Escenario(string nombre) => nombre switch
    {
        "sin_dependencias" => MuestrasReporte.SinDependencias(),
        "pequeno" => MuestrasReporte.Pequeno(),
        "parcial" => MuestrasReporte.Parcial(),
        "extenso_40" => MuestrasReporte.Extenso(40),
        _ => MuestrasReporte.Extenso(100),
    };

    [Theory]
    [MemberData(nameof(Escenarios))]
    public void Ninguna_palabra_se_sale_de_la_pagina_ni_se_solapa_con_otra(string escenario)
    {
        using var doc = PdfDocument.Open(Pdf(Escenario(escenario)));

        foreach (var pagina in doc.GetPages())
        {
            var palabras = pagina.GetWords().ToList();
            Assert.NotEmpty(palabras);
            foreach (var w in palabras)
            {
                var r = w.BoundingBox;
                Assert.True(r.Left >= 30 && r.Right <= pagina.Width - 30, $"Página {pagina.Number}: «{w.Text}» queda fuera de los márgenes ({r.Left:0.#}–{r.Right:0.#} de {pagina.Width:0.#})");
                Assert.True(r.Bottom >= 20 && r.Top <= pagina.Height - 20, $"Página {pagina.Number}: «{w.Text}» queda fuera de la página verticalmente");
            }

            for (var i = 0; i < palabras.Count; i++)
                for (var j = i + 1; j < palabras.Count; j++)
                {
                    var a = palabras[i].BoundingBox;
                    var b = palabras[j].BoundingBox;
                    var ancho = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                    var alto = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
                    if (ancho <= 0 || alto <= 0) continue;
                    var area = ancho * alto;
                    var menor = Math.Min(a.Width * a.Height, b.Width * b.Height);
                    Assert.True(area < 0.2 * menor, $"Página {pagina.Number}: «{palabras[i].Text}» se solapa con «{palabras[j].Text}»");
                }
        }
    }

    [Fact]
    public void Un_identificador_sin_separadores_se_parte_dentro_de_su_celda_y_no_pierde_caracteres()
    {
        var pdf = Pdf(MuestrasReporte.Parcial());
        var (_, texto) = Leer(pdf);
        var todo = string.Concat(texto).Replace(" ", "");

        Assert.Contains(MuestrasReporte.NombreSinCortes, todo);
        Assert.Contains(new string('9', 70), todo);
    }

    [Theory]
    [InlineData(40)]
    [InlineData(100)]
    public void El_detalle_fluye_en_bloques_continuos_y_no_reserva_una_pagina_por_dependencia(int cantidad)
    {
        var (paginas, _) = Leer(Pdf(MuestrasReporte.Extenso(cantidad)));

        // Sin imponer un número fijo: con contenido habitual el reporte ocupa claramente menos páginas que dependencias.
        Assert.True(paginas < cantidad * 0.75, $"{cantidad} dependencias ocupan {paginas} páginas");
    }

    [Fact]
    public void El_caso_de_40_dependencias_parcial_con_nombres_largos_es_compacto_completo_y_sin_solapamientos()
    {
        var a = MuestrasReporte.Extenso40Parcial();
        var pdf = Pdf(a);
        Guardar("06_extenso_40_parcial_nombres_largos.pdf", pdf);
        var (paginas, texto) = Leer(pdf);
        var todo = string.Concat(texto).Replace(" ", "");

        Assert.Equal(40, a.Dependencias.Count);
        Assert.True(paginas < 30, $"{paginas} páginas");
        Assert.Contains("Diagnóstico parcial", string.Join(" ", texto));
        foreach (var d in a.Dependencias) Assert.Contains(d.Nombre.Replace(" ", ""), todo);          // toda dependencia, completa, en tabla y detalle
        foreach (var d in a.Dependencias.Where(x => x.Diagnostico.Hallazgos.Count > 0))
            foreach (var h in d.Diagnostico.Hallazgos) Assert.Contains(h.Mensaje.Replace(" ", ""), todo);   // motivos completos
        foreach (var r in a.Dependencias.SelectMany(x => x.Recomendaciones))
        {
            Assert.Contains(r.Justificacion.Replace(" ", ""), todo);
            foreach (var l in r.Limitaciones) Assert.Contains(l.Replace(" ", ""), todo);             // limitaciones completas
            foreach (var e in r.Evidencia) Assert.Contains(e.Valor.Replace(" ", ""), todo);
        }
    }

    [Fact]
    public void Las_fechas_indican_UTC_y_se_explica_la_diferencia_con_la_hora_local_de_la_web()
    {
        var todo = string.Join(" ", Leer(Pdf(MuestrasReporte.Pequeno())).TextoPorPagina);

        Assert.Contains("Zona horaria de las fechas UTC (hora universal)", todo);
        Assert.Contains("hora local de tu navegador", todo);
        Assert.Contains("5 oct 2026, 15:42 UTC", todo);
        Assert.Contains("21 oct 2026, 09:30 UTC", todo);
        Assert.DoesNotMatch(@"\d{1,2} (ene|feb|mar|abr|may|jun|jul|ago|sep|oct|nov|dic) \d{4}, \d{2}:\d{2}(?! UTC)", todo);   // ninguna fecha sin zona
    }

    [Fact]
    public void Un_caracter_que_la_fuente_no_dibuja_no_se_pierde_ni_se_sustituye_en_silencio()
    {
        var pdf = Pdf(MuestrasReporte.ConCaracteresNoSoportados());
        Guardar("07_caracteres_no_soportados.pdf", pdf);
        var texto = string.Join(" ", Leer(pdf).TextoPorPagina);
        var plano = texto.Replace(" ", "");

        // Cada carácter sin glifo aparece escrito con su punto de código (nada desaparece ni se cambia por un cuadro o un «?»).
        foreach (var punto in new[] { "U+65E5", "U+672C", "U+8A9E", "U+1F680", "U+0007", "U+66F4", "U+65B0", "U+1F600" })
            Assert.Contains($"‹{punto}›", plano);
        Assert.DoesNotContain("?", plano.Replace("¿", ""));
        // Y el reporte lo avisa explícitamente, al inicio y en las limitaciones.
        Assert.Contains("Caracteres que la fuente del PDF no puede dibujar", texto);
        Assert.Contains("No se omiten ni se reemplazan en silencio", texto);
        Assert.True(Regex.Matches(texto, "no se omiten ni se reemplazan en silencio", RegexOptions.IgnoreCase).Count >= 2);
        // Lo que sí se puede dibujar sigue intacto.
        Assert.Contains("Ejemplo.Latino", texto);
    }

    [Fact]
    public void Sin_caracteres_especiales_no_aparece_el_aviso_de_caracteres()
    {
        var texto = string.Join(" ", Leer(Pdf(MuestrasReporte.Parcial())).TextoPorPagina);

        Assert.DoesNotContain("Caracteres que la fuente del PDF no puede dibujar", texto);
        Assert.DoesNotMatch(@"‹U\+[0-9A-F]{4,6}›", texto);   // (la limitación general cita el formato como «‹U+XXXX›», con X, no con un código real)
        Assert.Contains("Ñandú", texto);               // tildes, eñes y símbolos del español sí se dibujan
    }

    [Theory]
    [InlineData('A', true)]
    [InlineData('ñ', true)]
    [InlineData('Ñ', true)]
    [InlineData('é', true)]
    [InlineData('¿', true)]
    [InlineData('«', true)]
    [InlineData('•', true)]
    [InlineData('日', false)]
    [InlineData('\u0007', false)]
    public void La_cobertura_de_la_fuente_se_lee_del_archivo_TrueType(char c, bool esperado)
    {
        var cobertura = new CoberturaFuente(new FuentesPdf().GetFont("LiberationSans-Regular")!);

        Assert.Equal(esperado, cobertura.Tiene(c));
        Assert.False(cobertura.Tiene(0x1F600));       // emoji (fuera del plano básico)
        Assert.False(cobertura.Tiene(0x110000));
    }
}
