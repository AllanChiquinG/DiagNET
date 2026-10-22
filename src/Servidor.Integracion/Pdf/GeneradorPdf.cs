using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using Servidor.Negocio.Reportes;

namespace Servidor.Integracion.Pdf;

/// <summary>
/// Dibuja el <see cref="ModeloReporte"/> con PDFsharp/MigraDoc 6.2.4 (MIT). SOLO dibuja y pagina: no redacta, no decide estados y no accede a red ni base de datos.
/// Diseño: resumen inicial + tabla compacta de TODAS las dependencias + detalle de cada una en bloques CONTINUOS (sin una página por dependencia: el salto de página
/// ocurre solo cuando se acaba el espacio). Las tablas repiten su cabecera y todas las páginas llevan «Página X de Y».
/// </summary>
public sealed class GeneradorPdf : IGeneradorPdf
{
    // Paleta del prototipo (azul sobrio); el estado SIEMPRE lleva texto, el color es solo refuerzo.
    private static readonly Color Azul = Color.FromRgb(38, 121, 207);
    private static readonly Color AzulOscuro = Color.FromRgb(20, 62, 112);
    private static readonly Color AzulClaro = Color.FromRgb(232, 241, 251);
    private static readonly Color AzulMuyClaro = Color.FromRgb(245, 249, 254);
    private static readonly Color Gris = Color.FromRgb(90, 100, 112);
    private static readonly Color Linea = Color.FromRgb(200, 208, 218);
    private static readonly Color Ambar = Color.FromRgb(255, 244, 214);
    private static readonly Color AmbarOscuro = Color.FromRgb(120, 80, 0);

    // Columnas de la tabla compacta: librería, versión declarada, última versión, estado, recomendación.
    private static readonly double[] AnchosTabla = [50, 27, 22, 27, 52];

    private const double AnchoUtilMm = 178;   // A4 (210) menos márgenes de 16 mm
    private const double Cuerpo = 8.5;
    private const double Chico = 7.5;

    /// <summary>Nota estándar del indicador AOT: se explica una sola vez en las limitaciones generales, no en cada librería.</summary>
    private const string NotaAotEstandar = "DiagNET no evalúa AOT en esta versión";

    private static readonly object Candado = new();

    public byte[] Generar(ModeloReporte modelo)
    {
        // La caché de fuentes de PDFsharp es global: se serializa la generación (un reporte tarda milisegundos).
        lock (Candado)
        {
            FuentesPdf.Registrar();

            // Pasada 1: detecta los caracteres que la fuente no puede dibujar. Si los hay, la pasada 2 añade el aviso (el reporte nunca los pierde ni los sustituye en silencio).
            AjusteTexto.Reiniciar();
            var documento = Construir(modelo, null);
            if (AjusteTexto.Ocurrencias > 0)
            {
                var aviso = (AjusteTexto.Ocurrencias, AjusteTexto.NoSoportados.ToList());
                AjusteTexto.Reiniciar();
                documento = Construir(modelo, aviso);
            }

            var renderizador = new PdfDocumentRenderer { Document = documento };
            renderizador.RenderDocument();
            using var flujo = new MemoryStream();
            renderizador.PdfDocument.Save(flujo, false);
            return flujo.ToArray();
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ documento

    private static Document Construir(ModeloReporte m, (int Ocurrencias, List<int> Puntos)? caracteres)
    {
        var doc = new Document();
        doc.Info.Title = Partir($"DiagNET · {m.Encabezado.NombreArchivo}", 400);
        doc.Info.Subject = "Reporte de diagnóstico de dependencias NuGet";
        doc.Info.Author = "DiagNET";
        doc.Info.Keywords = "DiagNET, NuGet, dependencias";

        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = FuentesPdf.Familia;
        normal.Font.Size = Cuerpo;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.1;

        var seccion = doc.AddSection();
        var pagina = seccion.PageSetup;
        pagina.PageFormat = PageFormat.A4;
        pagina.Orientation = Orientation.Portrait;
        pagina.LeftMargin = pagina.RightMargin = Unit.FromMillimeter(16);
        pagina.TopMargin = Unit.FromMillimeter(15);
        pagina.BottomMargin = Unit.FromMillimeter(19);
        pagina.FooterDistance = Unit.FromMillimeter(8);

        Pie(seccion, m);
        Portada(seccion, m, caracteres);
        TablaDiagnosticos(seccion, m);
        Detalles(seccion, m);
        Limitaciones(seccion, m);
        return doc;
    }

    private static void Pie(Section seccion, ModeloReporte m)
    {
        var p = seccion.Footers.Primary.AddParagraph();
        p.Format.Font.Size = Chico;
        p.Format.Font.Color = Gris;
        p.Format.Borders.Top.Color = Linea;
        p.Format.Borders.Top.Width = 0.5;
        p.Format.Borders.DistanceFromTop = Unit.FromPoint(3);
        p.Format.TabStops.AddTabStop(Unit.FromMillimeter(AnchoUtilMm), TabAlignment.Right);
        p.AddText(Partir($"DiagNET · {Acortar(m.Encabezado.NombreArchivo, 60)} · análisis del {m.Encabezado.FechaAnalisis}", AnchoUtilMm - 28, Chico));
        p.AddTab();
        p.AddText("Página ");
        p.AddPageField();
        p.AddText(" de ");
        p.AddNumPagesField();
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ portada

    private static void Portada(Section s, ModeloReporte m, (int Ocurrencias, List<int> Puntos)? caracteres)
    {
        var e = m.Encabezado;

        var marca = s.AddParagraph("DiagNET");
        marca.Format.Font.Size = 9;
        marca.Format.Font.Bold = true;
        marca.Format.Font.Color = Azul;
        marca.Format.SpaceAfter = 0;

        var titulo = s.AddParagraph("Reporte de diagnóstico de dependencias NuGet");
        titulo.Format.Font.Size = 16;
        titulo.Format.Font.Bold = true;
        titulo.Format.Font.Color = AzulOscuro;
        titulo.Format.SpaceAfter = Unit.FromPoint(1);

        var archivo = s.AddParagraph(Partir(e.NombreArchivo, AnchoUtilMm, 11, true));
        archivo.Format.Font.Size = 11;
        archivo.Format.Font.Bold = true;
        archivo.Format.SpaceAfter = Unit.FromPoint(6);

        var id = NuevaTabla(s, 44, AnchoUtilMm - 44);
        Fila(id, "Archivo analizado", Partir(e.NombreArchivo, AnchoUtilMm - 44));
        Fila(id, "Identificador del análisis", e.IdAnalisis.ToString("D"));
        Fila(id, "Frameworks declarados", Partir(e.FrameworksDeclarados.Count == 0 ? "Framework no declarado" : string.Join(" · ", e.FrameworksDeclarados), AnchoUtilMm - 44));
        Fila(id, "Frameworks evaluados", Partir(e.FrameworksEvaluados.Count == 0 ? "Ningún framework evaluable" : string.Join(" · ", e.FrameworksEvaluados), AnchoUtilMm - 44));
        Fila(id, "Fecha del análisis", e.FechaAnalisis);
        Fila(id, "Reporte generado", e.FechaGeneracion);
        Fila(id, "Zona horaria de las fechas", "UTC (hora universal). La aplicación web muestra las fechas en la hora local de tu navegador, por eso pueden diferir en horas del mismo instante.");
        Fila(id, "Estado del análisis", e.EstadoAnalisis);
        Fila(id, "Versión de las reglas", Partir(e.VersionReglas, AnchoUtilMm - 44));
        Fila(id, "Conservación", e.PurgaAutomaticaActiva
            ? $"El análisis se conserva {e.RetencionDias} días y luego se elimina automáticamente; cualquier persona con su enlace puede abrirlo. Este PDF no se guarda: se genera de nuevo desde el resultado guardado."
            : $"Plazo de conservación previsto: {e.RetencionDias} días. La eliminación automática todavía no está activada, por lo que el análisis puede seguir disponible después de ese plazo; cualquier persona con su enlace puede abrirlo. Este PDF no se guarda: se genera de nuevo desde el resultado guardado.");

        Titulo2(s, "Resumen");
        var r = m.Resumen;
        var t = s.AddTable();
        t.Borders.Color = Linea;
        t.Borders.Width = 0.5;
        for (var i = 0; i < 6; i++) t.AddColumn(Unit.FromMillimeter(AnchoUtilMm / 6));
        var cab = t.AddRow();
        cab.HeadingFormat = true;
        cab.Shading.Color = AzulClaro;
        var etiquetas = new[] { "Total", "Actualizadas", "Desactualizadas", "Incompatibles", "Sin soporte activo", "No determinadas" };
        var valores = new[] { r.Total, r.Actualizadas, r.Desactualizadas, r.Incompatibles, r.SinSoporteActivo, r.NoDeterminadas };
        var datos = t.AddRow();
        for (var i = 0; i < 6; i++)
        {
            var p = cab.Cells[i].AddParagraph(etiquetas[i]);
            p.Format.Font.Size = 7.5;
            p.Format.Font.Bold = true;
            p.Format.Alignment = ParagraphAlignment.Center;
            var v = datos.Cells[i].AddParagraph(valores[i].ToString(CultureInfo.InvariantCulture));
            v.Format.Font.Size = 14;
            v.Format.Font.Bold = true;
            v.Format.Alignment = ParagraphAlignment.Center;
        }

        AvisosDelAnalisis(s, m, caracteres);
    }

    /// <summary>
    /// Separa lo que la persona debe distinguir: (1) DIAGNÓSTICO PARCIAL — algún resultado decisivo no pudo determinarse; (2) AVISOS del análisis (frameworks, presupuesto…);
    /// (3) RECOMENDACIONES INCOMPLETAS — alguna búsqueda de recomendaciones no pudo verificarse; (4) CARACTERES que la fuente del PDF no puede dibujar. No se mezclan.
    /// </summary>
    private static void AvisosDelAnalisis(Section s, ModeloReporte m, (int Ocurrencias, List<int> Puntos)? caracteres)
    {
        if (m.DiagnosticoParcial)
            Caja(s, "Diagnóstico parcial",
                ["Algunas librerías no pudieron determinarse por completo o quedaron con evidencia incompleta. Los demás resultados son válidos y se conservan tal cual."], Ambar);

        if (m.AvisosDelAnalisis.Count > 0)
            Caja(s, "Avisos del análisis", m.AvisosDelAnalisis, AzulClaro);

        if (m.AvisosDeRecomendaciones.Count > 0)
            Caja(s, "Recomendaciones incompletas", m.AvisosDeRecomendaciones, Ambar);

        if (caracteres is { } c)
            Caja(s, "Caracteres que la fuente del PDF no puede dibujar", [TextoCaracteres(c)], Ambar);
    }

    private static string TextoCaracteres((int Ocurrencias, List<int> Puntos) c)
    {
        var lista = string.Join(", ", c.Puntos.Take(12).Select(p => string.Create(CultureInfo.InvariantCulture, $"U+{p:X4}")));
        return $"El resultado guardado contiene {c.Puntos.Count} carácter(es) distinto(s) ({lista}{(c.Puntos.Count > 12 ? ", …" : "")}) que la fuente incrustada no puede dibujar. "
               + "No se omiten ni se reemplazan en silencio: aquí aparecen escritos como «‹U+XXXX›» con su punto de código Unicode. Los datos guardados y la pantalla no cambian.";
    }

    private static void Caja(Section s, string titulo, IReadOnlyList<string> lineas, Color fondo)
    {
        var t = s.AddTable();
        t.Format.SpaceBefore = Unit.FromPoint(5);
        t.Borders.Color = Linea;
        t.Borders.Width = 0.5;
        t.AddColumn(Unit.FromMillimeter(AnchoUtilMm));
        var fila = t.AddRow();
        fila.Shading.Color = fondo;
        var c = fila.Cells[0];
        c.Format.KeepTogether = true;
        var cab = c.AddParagraph(titulo);
        cab.Format.Font.Bold = true;
        cab.Format.Font.Color = fondo == Ambar ? AmbarOscuro : AzulOscuro;
        cab.Format.KeepWithNext = true;
        foreach (var linea in lineas) Vineta(c, linea);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ tabla compacta

    private static void TablaDiagnosticos(Section s, ModeloReporte m)
    {
        Titulo2(s, $"Diagnóstico de las librerías ({m.Filas.Count})");
        var nota = s.AddParagraph("Ordenadas por gravedad. Todas las fechas están en UTC. El origen de los datos es NuGet (consulta actual) salvo que la fila indique otra cosa; el detalle de cada librería (más abajo) lo registra siempre.");
        nota.Format.Font.Color = Gris;
        nota.Format.Font.Size = Chico;

        if (m.Filas.Count == 0)
        {
            var vacio = s.AddParagraph("Este proyecto no declara paquetes NuGet: no hay librerías que diagnosticar.");
            vacio.Format.Font.Bold = true;
            vacio.Format.SpaceBefore = Unit.FromPoint(4);
            return;
        }

        var t = s.AddTable();
        t.Borders.Color = Linea;
        t.Borders.Width = 0.5;
        t.Format.SpaceBefore = Unit.FromPoint(3);
        t.Format.Font.Size = 8;
        t.Format.SpaceAfter = 0;
        foreach (var a in AnchosTabla) t.AddColumn(Unit.FromMillimeter(a));

        var cab = t.AddRow();
        cab.HeadingFormat = true;                         // la cabecera se repite en cada página
        cab.Shading.Color = AzulOscuro;
        string[] titulos = ["Librería", "Versión declarada", "Última versión", "Estado", "Recomendación"];
        for (var i = 0; i < titulos.Length; i++)
        {
            var p = cab.Cells[i].AddParagraph(titulos[i]);
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 7.5;
            p.Format.Font.Color = Colors.White;
        }

        foreach (var f in m.Filas)
        {
            var fila = t.AddRow();
            foreach (var c in fila.Cells.OfType<Cell>()) c.Format.KeepTogether = true;   // una fila no se parte entre páginas por la mitad de un párrafo

            var nombre = fila.Cells[0].AddParagraph(Partir(f.Nombre, AnchosTabla[0], 8, true));
            nombre.Format.Font.Bold = true;
            nombre.Format.SpaceAfter = 0;
            var origenActual = f.Origen.StartsWith("NuGet (consulta actual)", StringComparison.Ordinal);
            if (!origenActual || f.Etiquetas.Count > 0)
            {
                if (!origenActual)
                {
                    var origen = fila.Cells[0].AddParagraph(Partir(f.Origen, AnchosTabla[0], Chico));
                    origen.Format.Font.Size = Chico;
                    origen.Format.Font.Color = Gris;
                    origen.Format.SpaceAfter = 0;
                }

                foreach (var etiqueta in f.Etiquetas)
                {
                    var et = fila.Cells[0].AddParagraph(etiqueta);
                    et.Format.Font.Size = Chico;
                    et.Format.Font.Bold = true;
                    et.Format.Font.Color = AmbarOscuro;
                    et.Format.SpaceAfter = 0;
                }
            }

            fila.Cells[1].AddParagraph(Partir(f.VersionDeclarada, AnchosTabla[1], 8));
            fila.Cells[2].AddParagraph(Partir(f.UltimaVersion, AnchosTabla[2], 8));
            var estado = fila.Cells[3].AddParagraph(f.Estado);
            estado.Format.Font.Bold = true;
            fila.Cells[3].Shading.Color = ColorEstado(f.Estado);
            fila.Cells[4].AddParagraph(Partir(f.Recomendacion, AnchosTabla[4], 8));
        }
    }

    private static Color ColorEstado(string estado) => estado switch
    {
        "Actualizada" => Color.FromRgb(223, 244, 228),
        "Desactualizada" => Color.FromRgb(255, 244, 214),
        "Incompatible" => Color.FromRgb(252, 226, 226),
        "Sin soporte activo" => Color.FromRgb(240, 226, 247),
        _ => Color.FromRgb(236, 238, 241),
    };

    // ------------------------------------------------------------------------------------------------------------------------------------------ detalle (bloques continuos)

    private static void Detalles(Section s, ModeloReporte m)
    {
        if (m.Detalles.Count == 0) return;
        Titulo2(s, "Detalle de cada librería");
        var intro = s.AddParagraph("Por qué recibió su estado, qué indicadores se consultaron, de dónde salen los datos y qué se recomienda. Todo proviene del resultado guardado; las fechas están en UTC.");
        intro.Format.Font.Color = Gris;
        intro.Format.Font.Size = Chico;

        foreach (var d in m.Detalles) Detalle(s, d);
    }

    private static void Detalle(Section s, DetalleReporte d)
    {
        // Cabecera: nombre y estado en una barra sombreada; se encadena (KeepWithNext) con las dos líneas siguientes para no quedar sola al final de una página.
        var cab = s.AddParagraph();
        cab.Format.SpaceBefore = Unit.FromPoint(9);
        cab.Format.SpaceAfter = 0;
        cab.Format.Shading.Color = ColorEstado(d.Estado);
        cab.Format.KeepWithNext = true;
        cab.Format.TabStops.AddTabStop(Unit.FromMillimeter(AnchoUtilMm - 0.5), TabAlignment.Right);
        var nombre = cab.AddFormattedText(Partir(d.Nombre, AnchoUtilMm - 45, 10, true), TextFormat.Bold);
        nombre.Font.Size = 10;
        nombre.Font.Color = AzulOscuro;
        cab.AddTab();
        cab.AddFormattedText(d.Estado, TextFormat.Bold);

        var meta = s.AddParagraph();
        meta.Format.Font.Size = Chico;
        meta.Format.Font.Color = Gris;
        meta.Format.SpaceAfter = Unit.FromPoint(1);
        meta.Format.KeepWithNext = true;
        meta.AddText(Partir($"Versión declarada: {d.VersionDeclarada}   ·   Origen de los datos: {d.Origen}", AnchoUtilMm, Chico));

        if (!string.IsNullOrWhiteSpace(d.Titulo))
        {
            var titulo = s.AddParagraph(Partir(d.Titulo, AnchoUtilMm));
            titulo.Format.KeepWithNext = d.Motivos.Count > 0;
        }

        if (d.Motivos.Count > 0)
        {
            Etiqueta(s, "Por qué este estado");
            foreach (var motivo in d.Motivos) Motivo(s, motivo, true);
        }
        else
        {
            Etiqueta(s, "Por qué este estado", "No hay motivos de aviso para esta librería.");
        }

        if (d.EvidenciaFaltante.Count > 0)
        {
            Etiqueta(s, "Falta evidencia para concluir por completo", "El estado se basa en lo que sí se pudo comprobar; por esto el análisis puede figurar como parcial.");
            foreach (var motivo in d.EvidenciaFaltante) Motivo(s, motivo, true);
        }

        if (d.EstadoPorFramework.Count > 0)
            Etiqueta(s, "Estado por framework", string.Join("   ·   ", d.EstadoPorFramework.Select(p => $"{p.Titulo}: {p.Valor}")));

        if (d.Informativos.Count > 0)
        {
            Etiqueta(s, $"Otros datos informativos ({d.Informativos.Count})");
            foreach (var motivo in d.Informativos) Motivo(s, motivo, false);
        }

        Etiqueta(s, "Indicadores consultados", d.TieneIndicadores ? null : "No hay indicadores: no se obtuvieron datos de NuGet para esta librería, y no se inventan.");
        Indicadores(s, d.Indicadores);

        if (d.Deprecacion.Count > 0)
        {
            Etiqueta(s, "Deprecación publicada por NuGet");
            foreach (var l in d.Deprecacion) Vineta(s, l, Cuerpo);
        }

        if (d.TieneIndicadores)
        {
            Etiqueta(s, "Compatibilidad por framework (inferida)");
            foreach (var l in d.Compatibilidad) Vineta(s, l, Cuerpo);
        }

        if (d.Avisos.Count > 0)
        {
            Etiqueta(s, "Avisos de esta librería");
            foreach (var l in d.Avisos) Vineta(s, l, Cuerpo);
        }

        Etiqueta(s, "Recomendación", d.Recomendaciones.Count == 0 && d.NotasRecomendacion.Count == 0 ? "No hay recomendaciones para esta librería." : null);
        foreach (var r in d.Recomendaciones) Recomendacion(s, r);
        foreach (var n in d.NotasRecomendacion) Texto(s, n, gris: true);
    }

    /// <summary>Rótulo de sección dentro del bloque de una librería; con <paramref name="texto"/> el contenido va en la misma línea (ahorra una línea por sección).</summary>
    private static void Etiqueta(Section s, string rotulo, string? texto = null)
    {
        var p = s.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(4);
        p.Format.SpaceAfter = Unit.FromPoint(1);
        p.Format.KeepWithNext = texto is null;
        p.AddFormattedText(rotulo, TextFormat.Bold).Font.Color = Azul;
        if (texto is not null) p.AddText("  " + Partir(texto, AnchoUtilMm - 4));
    }

    /// <summary>Motivo con su evidencia COMPLETA a continuación, en la misma viñeta (clave: valor · clave: valor).</summary>
    private static void Motivo(Section s, MotivoReporte motivo, bool conGravedad)
    {
        var p = s.AddParagraph();
        FormatoVineta(p);
        p.AddText("• ");
        if (conGravedad) p.AddFormattedText(motivo.Gravedad + ": ", TextFormat.Bold);
        p.AddText(Partir(motivo.Mensaje, AnchoUtilMm - 8));
        if (motivo.Evidencia.Count == 0) return;
        EvidenciaEnLinea(s, motivo.Evidencia, 8);
    }

    private static void EvidenciaEnLinea(Section s, IReadOnlyList<ParReporte> evidencia, double sangriaMm)
    {
        var linea = s.AddParagraph();
        linea.Format.LeftIndent = Unit.FromMillimeter(sangriaMm);
        linea.Format.Font.Size = Chico;
        linea.Format.Font.Color = Gris;
        linea.Format.SpaceAfter = Unit.FromPoint(1);
        for (var i = 0; i < evidencia.Count; i++)
        {
            if (i > 0) linea.AddText("   ·   ");
            linea.AddFormattedText(evidencia[i].Titulo + ": ", TextFormat.Bold);
            linea.AddText(Partir(evidencia[i].Valor, AnchoUtilMm - sangriaMm - 4, Chico));
        }
    }

    /// <summary>Indicadores en cuadrícula compacta de 4 columnas (rótulo, valor, rótulo, valor); «No determinado/No evaluado» en cursiva, distinto de un valor real.</summary>
    private static void Indicadores(Section s, IReadOnlyList<ParReporte> pares)
    {
        var t = s.AddTable();
        t.Borders.Color = Linea;
        t.Borders.Width = 0.5;
        t.Format.Font.Size = 8;
        t.Format.SpaceAfter = 0;
        double[] anchos = [38, 51, 38, 51];
        foreach (var a in anchos) t.AddColumn(Unit.FromMillimeter(a));

        for (var i = 0; i < pares.Count; i += 2)
        {
            var fila = t.AddRow();
            foreach (var c in fila.Cells.OfType<Cell>()) c.Format.KeepTogether = true;
            for (var k = 0; k < 2; k++)
            {
                if (i + k >= pares.Count) break;
                var par = pares[i + k];
                var rotulo = fila.Cells[k * 2];
                rotulo.Shading.Color = AzulClaro;
                var pr = rotulo.AddParagraph(Partir(par.Titulo, anchos[k * 2], 8, true));
                pr.Format.Font.Bold = true;
                pr.Format.SpaceAfter = 0;
                var celda = fila.Cells[k * 2 + 1];
                var valor = celda.AddParagraph(Partir(par.Valor, anchos[k * 2 + 1], 8));
                valor.Format.SpaceAfter = 0;
                if (!par.Conocido) valor.Format.Font.Italic = true;
                // La nota estándar de AOT se explica una vez en las limitaciones generales; cualquier otra nota se conserva junto a su indicador.
                if (!string.IsNullOrWhiteSpace(par.Nota) && !par.Nota.StartsWith(NotaAotEstandar, StringComparison.Ordinal))
                {
                    var nota = celda.AddParagraph(Partir(par.Nota, anchos[k * 2 + 1], 7));
                    nota.Format.Font.Size = 7;
                    nota.Format.Font.Color = Gris;
                    nota.Format.SpaceAfter = 0;
                }
            }
        }
    }

    /// <summary>Recomendación en un bloque continuo (barra lateral): se parte entre páginas si hace falta, sin reservar espacio.</summary>
    private static void Recomendacion(Section s, RecomendacionReporte r)
    {
        var candidato = r.Etiqueta.StartsWith("Candidato", StringComparison.Ordinal);
        var fondo = candidato ? Ambar : AzulMuyClaro;
        var barra = candidato ? Color.FromRgb(200, 140, 20) : Azul;

        Paragraph Nuevo()
        {
            var p = s.AddParagraph();
            p.Format.Shading.Color = fondo;
            p.Format.Borders.Left.Width = 2.5;
            p.Format.Borders.Left.Color = barra;
            p.Format.Borders.DistanceFromLeft = Unit.FromPoint(4);
            p.Format.LeftIndent = Unit.FromMillimeter(3);
            p.Format.SpaceAfter = 0;
            return p;
        }

        var etiqueta = Nuevo();
        etiqueta.Format.SpaceBefore = Unit.FromPoint(2);
        etiqueta.Format.KeepWithNext = true;
        etiqueta.AddFormattedText(r.Etiqueta, TextFormat.Bold).Font.Color = AzulOscuro;

        var paquete = Nuevo();
        paquete.Format.KeepWithNext = true;
        paquete.AddFormattedText(Partir(r.Paquete, AnchoUtilMm - 24, Cuerpo, true), TextFormat.Bold);
        paquete.AddText($" — {r.VersionComprobada}");

        Nuevo().AddText(Partir(r.Justificacion, AnchoUtilMm - 10));
        Nuevo().AddFormattedText(r.CambiosDeCodigo, TextFormat.Bold);

        if (r.Evidencia.Count > 0)
        {
            var ev = Nuevo();
            ev.Format.Font.Size = Chico;
            ev.Format.Font.Color = Gris;
            for (var i = 0; i < r.Evidencia.Count; i++)
            {
                if (i > 0) ev.AddText("   ·   ");
                ev.AddFormattedText(r.Evidencia[i].Titulo + ": ", TextFormat.Bold);
                ev.AddText(Partir(r.Evidencia[i].Valor, AnchoUtilMm - 14, Chico));
            }
        }

        foreach (var l in r.Limitaciones)
        {
            var p = Nuevo();
            p.Format.FirstLineIndent = Unit.FromMillimeter(-3);
            p.Format.LeftIndent = Unit.FromMillimeter(6);
            p.Format.Font.Size = 8;
            p.AddText("Limitación: " + Partir(l, AnchoUtilMm - 16, 8));
        }

        s.AddParagraph().Format.SpaceAfter = Unit.FromPoint(1);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ limitaciones

    private static void Limitaciones(Section s, ModeloReporte m)
    {
        Titulo2(s, "Limitaciones de este reporte");
        foreach (var l in m.Limitaciones) Vineta(s, l, Cuerpo);
        Vineta(s, "Los caracteres que la fuente incrustada (Liberation Sans, alfabeto latino) no puede dibujar no se omiten ni se reemplazan en silencio: se escriben como «‹U+XXXX›» con su punto de código y el reporte lo avisa al inicio.", Cuerpo);
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------ utilidades

    private static void Titulo2(Section s, string texto)
    {
        var p = s.AddParagraph(texto);
        p.Format.Font.Size = 12;
        p.Format.Font.Bold = true;
        p.Format.Font.Color = AzulOscuro;
        p.Format.SpaceBefore = Unit.FromPoint(12);
        p.Format.SpaceAfter = Unit.FromPoint(3);
        p.Format.KeepWithNext = true;
        p.Format.Borders.Bottom.Color = Azul;
        p.Format.Borders.Bottom.Width = 0.75;
    }

    private static void Texto(Section s, string texto, bool gris = false)
    {
        var p = s.AddParagraph(Partir(texto, AnchoUtilMm));
        if (gris) p.Format.Font.Color = Gris;
    }

    private static void Vineta(Section s, string texto, double puntos)
    {
        var p = s.AddParagraph();
        FormatoVineta(p);
        p.Format.Font.Size = puntos;
        p.AddText("• " + Partir(texto, AnchoUtilMm - 8, puntos));
    }

    private static void Vineta(Cell c, string texto)
    {
        var p = c.AddParagraph();
        FormatoVineta(p);
        p.AddText("• " + Partir(texto, AnchoUtilMm - 12));
    }

    private static void FormatoVineta(Paragraph p)
    {
        p.Format.LeftIndent = Unit.FromMillimeter(4);
        p.Format.FirstLineIndent = Unit.FromMillimeter(-4);
        p.Format.SpaceAfter = Unit.FromPoint(1);
    }

    private static Table NuevaTabla(Section s, double ancho1, double ancho2)
    {
        var t = s.AddTable();
        t.Borders.Color = Linea;
        t.Borders.Width = 0.5;
        t.Format.SpaceAfter = 0;
        t.AddColumn(Unit.FromMillimeter(ancho1));
        t.AddColumn(Unit.FromMillimeter(ancho2));
        return t;
    }

    private static void Fila(Table t, string titulo, string valor)
    {
        var fila = t.AddRow();
        fila.Cells[0].Shading.Color = AzulClaro;
        var rotulo = fila.Cells[0].AddParagraph(titulo);
        rotulo.Format.Font.Bold = true;
        rotulo.Format.SpaceAfter = 0;
        fila.Cells[1].AddParagraph(valor).Format.SpaceAfter = 0;
        foreach (var c in fila.Cells.OfType<Cell>()) c.Format.KeepTogether = true;
    }

    private static string Acortar(string texto, int maximo) => texto.Length <= maximo ? texto : texto[..(maximo - 1)] + "…";

    /// <summary>Prepara un texto de los datos para dibujarlo (ver <see cref="AjusteTexto"/>): caracteres sin glifo visibles y tramos largos partidos dentro de <paramref name="anchoMm"/>.</summary>
    private static string Partir(string texto, double anchoMm, double puntos = Cuerpo, bool negrita = false) => AjusteTexto.Partir(texto, anchoMm, puntos, negrita);
}
