using Cliente.Web.Servicios;
using Contratos = Compartido.Contratos;
using Dominio = Servidor.Negocio.Dominio;
using Lectura = Servidor.Negocio.Lectura;
using Servidor.Negocio.Reportes;

namespace Servidor.Api.Tests;

/// <summary>
/// El PDF debe decir lo mismo que la pantalla: los textos de estados, orígenes, gravedades, tipos de versión y compatibilidad del reporte (Negocio) se comparan con los
/// de la interfaz (Cliente.Web.Presentacion) para TODOS los valores. Si alguien cambia un texto en un lado, esta prueba obliga a cambiarlo también en el otro.
/// </summary>
public class ParidadTextosTests
{
    private static TC Convertir<TC>(Enum valor) where TC : struct, Enum => Enum.Parse<TC>(valor.ToString());

    [Fact]
    public void Los_estados_de_diagnostico_y_su_orden_son_los_mismos_en_pantalla_y_PDF()
    {
        foreach (var e in Enum.GetValues<Dominio.EstadoDiagnostico>())
        {
            var c = Convertir<Contratos.EstadoDiagnostico>(e);
            Assert.Equal(Presentacion.Texto(c), TextosReporte.Texto(e));
            Assert.Equal(Presentacion.Orden(c), TextosReporte.Orden(e));
        }
    }

    [Fact]
    public void El_resto_de_textos_enumerados_son_los_mismos_en_pantalla_y_PDF()
    {
        foreach (var v in Enum.GetValues<Dominio.EstadoAnalisis>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.EstadoAnalisis>(v)), TextosReporte.Texto(v));
        foreach (var v in Enum.GetValues<Lectura.TipoVersion>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.TipoVersionDeclarada>(v)), TextosReporte.Texto(v));
        foreach (var v in Enum.GetValues<Dominio.GravedadHallazgo>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.GravedadHallazgo>(v)), TextosReporte.Texto(v));
        foreach (var v in Enum.GetValues<Dominio.OrigenDato>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.OrigenDato>(v)), TextosReporte.Texto(v));
        foreach (var v in Enum.GetValues<Dominio.ResultadoCompatibilidad>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.ResultadoCompatibilidad>(v)), TextosReporte.Texto(v));
        foreach (var v in Enum.GetValues<Dominio.TipoLicencia>()) Assert.Equal(Presentacion.Texto(Convertir<Contratos.TipoLicencia>(v)), TextosReporte.Texto(v));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(999L)]
    [InlineData(1000L)]
    [InlineData(123456789L)]
    public void Los_enteros_se_formatean_igual(long valor) => Assert.Equal(Presentacion.Entero(valor), TextosReporte.Entero(valor));

    [Fact]
    public void Las_etiquetas_de_recomendacion_son_las_que_publica_la_API_sin_reescribirlas()
    {
        foreach (var t in Enum.GetValues<Dominio.TipoRecomendacion>())
            Assert.Equal(Servidor.Negocio.Recomendaciones.TextosRecomendacion.Etiqueta(t),
                Api.Mapeo.MapeadorAnalisis.ADto(AnalisisConRecomendacion(t), 90).Dependencias.Single().Recomendaciones.Single().Etiqueta);
    }

    private static Dominio.Analisis AnalisisConRecomendacion(Dominio.TipoRecomendacion tipo) =>
        new(Guid.NewGuid(), "A.csproj", ["net8.0"], ["net8.0"], DateTimeOffset.UnixEpoch, Dominio.EstadoAnalisis.Completo,
            [new Dominio.DependenciaAnalizada("Ejemplo.X", "1.0.0", Lectura.TipoVersion.Exacta, null, [],
                new Dominio.Diagnostico(Dominio.EstadoDiagnostico.Actualizada, "x", null, [], [], Dominio.OrigenDato.NoDeterminado, null, null),
                [new Dominio.Recomendacion(tipo, "Ejemplo.Y", "1.0.0", "j", [], [], false)])], [], "r");
}
