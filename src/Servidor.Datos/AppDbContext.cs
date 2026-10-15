using Microsoft.EntityFrameworkCore;
using Servidor.Datos.Entidades;

namespace Servidor.Datos;

/// <summary>Contexto de EF Core con las tablas del modelo de datos. Se crea uno por operación (nunca se comparte entre tareas).</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> opciones) : DbContext(opciones)
{
    internal DbSet<AnalisisFila> Analisis => Set<AnalisisFila>();
    internal DbSet<DependenciaFila> Dependencias => Set<DependenciaFila>();
    internal DbSet<MetadatoLibreriaFila> Metadatos => Set<MetadatoLibreriaFila>();
    internal DbSet<DiagnosticoFila> Diagnosticos => Set<DiagnosticoFila>();
    internal DbSet<RecomendacionFila> Recomendaciones => Set<RecomendacionFila>();
    internal DbSet<SeleccionPaqueteFila> Selecciones => Set<SeleccionPaqueteFila>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
