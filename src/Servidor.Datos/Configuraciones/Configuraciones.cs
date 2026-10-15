using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Servidor.Datos.Entidades;
using Servidor.Negocio.Dominio;

namespace Servidor.Datos.Configuraciones;

// Una configuración por tabla. Los nombres, tipos, CHECK, índices y cascadas reproducen schema.sql (comparado, no ejecutado).

internal sealed class AnalisisConfiguracion : IEntityTypeConfiguration<AnalisisFila>
{
    public void Configure(EntityTypeBuilder<AnalisisFila> e)
    {
        e.ToTable("analisis", t =>
        {
            t.HasComment("Un análisis por cada .csproj cargado. No se guarda el contenido del archivo.");
            t.HasCheckConstraint("ck_analisis_estado", "estado IN ('completo','parcial')");
            t.HasCheckConstraint("ck_analisis_total", "total_dependencias >= 0");
        });
        e.HasKey(x => x.Id).HasName("pk_analisis");
        e.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").HasDefaultValueSql("gen_random_uuid()").ValueGeneratedNever();
        e.Property(x => x.NombreArchivo).HasColumnName("nombre_archivo").HasMaxLength(LimitesDatos.NombreArchivo).IsRequired();
        e.Property(x => x.FrameworkObjetivo).HasColumnName("framework_objetivo").HasMaxLength(LimitesDatos.FrameworkObjetivo).IsRequired()
            .HasComment("Frameworks declarados, por ejemplo net8.0 o net8.0;net10.0.");
        e.Property(x => x.FechaAnalisis).HasColumnName("fecha_analisis").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        e.Property(x => x.Estado).HasColumnName("estado").HasMaxLength(20).IsRequired()
            .HasComment("completo: todo determinado; parcial: alguna librería quedó no determinada.");
        e.Property(x => x.TotalDependencias).HasColumnName("total_dependencias").HasDefaultValue(0);
        // DEC-14 (confirmada por el propietario el 15/10/2026): datos necesarios para reconstruir el resultado y el PDF.
        e.Property(x => x.FrameworksDeclarados).HasColumnName("frameworks_declarados").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Frameworks tal como se declararon (lista JSON).");
        e.Property(x => x.FrameworksEvaluados).HasColumnName("frameworks_evaluados").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Frameworks efectivamente evaluados (los admitidos).");
        e.Property(x => x.Advertencias).HasColumnName("advertencias").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Advertencias del análisis (lista JSON).");
        e.Property(x => x.VersionReglas).HasColumnName("version_reglas").HasMaxLength(LimitesDatos.VersionReglas).HasDefaultValue("").IsRequired()
            .HasComment("Versión de las reglas de diagnóstico usadas; evita reinterpretar resultados antiguos.");
        e.HasIndex(x => x.FechaAnalisis).IsDescending().HasDatabaseName("ix_analisis_fecha");
    }
}

internal sealed class DependenciaConfiguracion : IEntityTypeConfiguration<DependenciaFila>
{
    public void Configure(EntityTypeBuilder<DependenciaFila> e)
    {
        e.ToTable("dependencia", t =>
        {
            t.HasComment("Librería declarada en el archivo de un análisis.");
            t.HasCheckConstraint("ck_dependencia_tipo_version", "tipo_version IN ('exacta','rango','flotante','propiedad','ausente','invalida')");
        });
        e.HasKey(x => x.Id).HasName("pk_dependencia");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        e.Property(x => x.AnalisisId).HasColumnName("analisis_id").HasColumnType("uuid");
        e.Property(x => x.NombrePaquete).HasColumnName("nombre_paquete").HasMaxLength(LimitesDatos.NombrePaquete).IsRequired();
        e.Property(x => x.VersionDeclarada).HasColumnName("version_declarada").HasMaxLength(LimitesDatos.Version)
            .HasComment("NULL si la versión no se puede resolver desde el .csproj.");
        e.Property(x => x.TipoVersion).HasColumnName("tipo_version").HasMaxLength(20).HasDefaultValue("ausente").IsRequired()
            .HasComment("Forma de la versión declarada: exacta, rango, flotante, propiedad, ausente o invalida.");
        e.Property(x => x.Condicion).HasColumnName("condicion").HasColumnType("text")
            .HasComment("Condición de MSBuild de la referencia, tal cual (no se evalúa).");
        e.Property(x => x.Advertencias).HasColumnName("advertencias").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        e.HasOne<AnalisisFila>().WithMany(a => a.Dependencias).HasForeignKey(x => x.AnalisisId).HasConstraintName("fk_dependencia_analisis").OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.AnalisisId).HasDatabaseName("ix_dependencia_analisis");
    }
}

internal sealed class MetadatoLibreriaConfiguracion : IEntityTypeConfiguration<MetadatoLibreriaFila>
{
    public void Configure(EntityTypeBuilder<MetadatoLibreriaFila> e)
    {
        e.ToTable("metadato_libreria", t =>
        {
            t.HasComment("Metadatos de NuGet por paquete y versión; funciona como caché compartida entre análisis.");
            t.HasCheckConstraint("ck_metadato_descargas", "descargas IS NULL OR descargas >= 0");
        });
        e.HasKey(x => x.Id).HasName("pk_metadato_libreria");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        e.Property(x => x.NombrePaquete).HasColumnName("nombre_paquete").HasMaxLength(LimitesDatos.NombrePaquete).IsRequired();
        e.Property(x => x.Version).HasColumnName("version").HasMaxLength(LimitesDatos.Version).IsRequired();
        e.Property(x => x.FechaPublicacion).HasColumnName("fecha_publicacion").HasColumnType("timestamptz");
        e.Property(x => x.Descargas).HasColumnName("descargas");
        e.Property(x => x.Licencia).HasColumnName("licencia").HasColumnType("text")
            .HasComment("Expresión de licencia o URL, tal como la publica NuGet (sin límite de longitud: una URL puede ser larga).");
        e.Property(x => x.FrameworksObjetivo).HasColumnName("frameworks_objetivo").HasColumnType("jsonb");
        e.Property(x => x.CompatibleAot).HasColumnName("compatible_aot").HasComment("NULL = no determinado.");
        e.Property(x => x.Deprecado).HasColumnName("deprecado").HasComment("NULL = no determinado.");
        e.Property(x => x.PaqueteAlterno).HasColumnName("paquete_alterno").HasMaxLength(LimitesDatos.NombrePaquete)
            .HasComment("Reemplazo declarado por el autor al deprecar el paquete (NULL si no hay o no se determinó).");
        e.Property(x => x.RangoAlterno).HasColumnName("rango_alterno").HasMaxLength(LimitesDatos.RangoAlterno)
            .HasComment("Rango de versiones del paquete alterno, tal como lo declara NuGet.");
        e.Property(x => x.FechaConsulta).HasColumnName("fecha_consulta").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        e.HasIndex(x => x.FechaConsulta).HasDatabaseName("ix_metadato_fecha_consulta");
        // El índice único sobre (lower(nombre_paquete), version) es una expresión: se crea con SQL en la migración.
    }
}

internal sealed class DiagnosticoConfiguracion : IEntityTypeConfiguration<DiagnosticoFila>
{
    public void Configure(EntityTypeBuilder<DiagnosticoFila> e)
    {
        e.ToTable("diagnostico", t =>
        {
            t.HasComment("Resultado del diagnóstico de una dependencia, con copia de los indicadores usados.");
            t.HasCheckConstraint("ck_diagnostico_estado",
                "estado IN ('actualizada','desactualizada','incompatible','sin_soporte_activo','no_determinada')");
            t.HasCheckConstraint("ck_diagnostico_origen", "origen IN ('nuget','cache_vigente','cache_expirada','no_determinado')");
        });
        e.HasKey(x => x.Id).HasName("pk_diagnostico");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        e.Property(x => x.DependenciaId).HasColumnName("dependencia_id");
        e.Property(x => x.MetadatoId).HasColumnName("metadato_id").HasComment("Fila de caché usada, solo para trazabilidad.");
        e.Property(x => x.Estado).HasColumnName("estado").HasMaxLength(30).IsRequired();
        e.Property(x => x.UltimaVersion).HasColumnName("ultima_version").HasMaxLength(LimitesDatos.Version);
        e.Property(x => x.Indicadores).HasColumnName("indicadores").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb")
            .HasComment("Copia de los indicadores usados: el resultado no cambia si la caché se renueva.");
        e.Property(x => x.Motivos).HasColumnName("motivos").HasColumnType("text").IsRequired();
        e.Property(x => x.FechaConsulta).HasColumnName("fecha_consulta").HasColumnType("timestamptz")
            .HasComment("Fecha de los datos usados; NULL si no se obtuvo ningún dato (nunca se inventa).");
        e.Property(x => x.Origen).HasColumnName("origen").HasMaxLength(20).HasDefaultValue("no_determinado").IsRequired()
            .HasComment("De dónde salieron los datos: nuget, cache_vigente, cache_expirada o no_determinado.");
        e.Property(x => x.Hallazgos).HasColumnName("hallazgos").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Hallazgos de las reglas con su motivo y evidencia (lista JSON).");
        e.Property(x => x.EvaluacionPorTfm).HasColumnName("evaluacion_por_tfm").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Estado evaluado para cada TFM (lista JSON).");
        e.HasOne<DependenciaFila>().WithOne(d => d.Diagnostico).HasForeignKey<DiagnosticoFila>(x => x.DependenciaId)
            .HasConstraintName("fk_diagnostico_dependencia").OnDelete(DeleteBehavior.Cascade);
        e.HasOne<MetadatoLibreriaFila>().WithMany().HasForeignKey(x => x.MetadatoId)
            .HasConstraintName("fk_diagnostico_metadato").OnDelete(DeleteBehavior.SetNull);
        e.HasIndex(x => x.DependenciaId).IsUnique().HasDatabaseName("ux_diagnostico_dependencia");
        e.HasIndex(x => x.MetadatoId).HasDatabaseName("ix_diagnostico_metadato");
    }
}

internal sealed class RecomendacionConfiguracion : IEntityTypeConfiguration<RecomendacionFila>
{
    public void Configure(EntityTypeBuilder<RecomendacionFila> e)
    {
        e.ToTable("recomendacion", t =>
        {
            t.HasComment("Alternativa o actualización sugerida para una dependencia con deficiencia.");
            t.HasCheckConstraint("ck_recomendacion_tipo", "tipo IN ('actualizar_version','alternativa','candidato_para_revision')");
        });
        e.HasKey(x => x.Id).HasName("pk_recomendacion");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        e.Property(x => x.DiagnosticoId).HasColumnName("diagnostico_id");
        e.Property(x => x.Tipo).HasColumnName("tipo").HasMaxLength(30).IsRequired();
        e.Property(x => x.PaqueteAlternativo).HasColumnName("paquete_alternativo").HasMaxLength(LimitesDatos.NombrePaquete).IsRequired();
        e.Property(x => x.VersionSugerida).HasColumnName("version_sugerida").HasMaxLength(LimitesDatos.Version);
        e.Property(x => x.Justificacion).HasColumnName("justificacion").HasColumnType("text").IsRequired();
        e.Property(x => x.Evidencia).HasColumnName("evidencia").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Datos que respaldan la recomendación (lista JSON).");
        e.Property(x => x.Limitaciones).HasColumnName("limitaciones").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb")
            .HasComment("Limitaciones que el usuario debe conocer (lista JSON).");
        e.Property(x => x.PosiblesCambiosDeCodigo).HasColumnName("posibles_cambios_codigo").HasDefaultValue(false);
        e.HasOne<DiagnosticoFila>().WithMany(d => d.Recomendaciones).HasForeignKey(x => x.DiagnosticoId).HasConstraintName("fk_recomendacion_diagnostico").OnDelete(DeleteBehavior.Cascade);
        e.HasIndex(x => x.DiagnosticoId).HasDatabaseName("ix_recomendacion_diagnostico");
        e.HasIndex(x => new { x.DiagnosticoId, x.PaqueteAlternativo }).IsUnique().HasDatabaseName("ux_recomendacion");
    }
}

internal sealed class SeleccionPaqueteConfiguracion : IEntityTypeConfiguration<SeleccionPaqueteFila>
{
    public void Configure(EntityTypeBuilder<SeleccionPaqueteFila> e)
    {
        e.ToTable("seleccion_paquete", t => t.HasComment(
            "Caché de la selección de versiones por paquete (DEC-11): última estable y última compatible por TFM, con vigencia."));
        e.HasKey(x => x.Id).HasName("pk_seleccion_paquete");
        e.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        e.Property(x => x.NombrePaquete).HasColumnName("nombre_paquete").HasMaxLength(LimitesDatos.NombrePaquete).IsRequired();
        e.Property(x => x.UltimaVersionEstable).HasColumnName("ultima_version_estable").HasMaxLength(LimitesDatos.Version)
            .HasComment("NULL si no se determinó.");
        e.Property(x => x.UltimaCompatiblePorTfm).HasColumnName("ultima_compatible_por_tfm").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb")
            .HasComment("Objeto JSON: TFM → última versión estable compatible.");
        e.Property(x => x.SeleccionCompleta).HasColumnName("seleccion_completa")
            .HasComment("false si no se recorrieron todas las páginas del registro: nunca se presenta como selección global.");
        e.Property(x => x.FechaConsulta).HasColumnName("fecha_consulta").HasColumnType("timestamptz").HasDefaultValueSql("now()");
        e.HasIndex(x => x.FechaConsulta).HasDatabaseName("ix_seleccion_fecha_consulta");
        // Unicidad por lower(nombre_paquete): índice de expresión creado con SQL en la migración.
    }
}
