using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Servidor.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class EsquemaInicial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analisis",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    nombre_archivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    framework_objetivo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, comment: "Frameworks declarados, por ejemplo net8.0 o net8.0;net10.0."),
                    fecha_analisis = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, comment: "completo: todo determinado; parcial: alguna librería quedó no determinada."),
                    total_dependencias = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_analisis", x => x.id);
                    table.CheckConstraint("ck_analisis_estado", "estado IN ('completo','parcial')");
                    table.CheckConstraint("ck_analisis_total", "total_dependencias >= 0");
                },
                comment: "Un análisis por cada .csproj cargado. No se guarda el contenido del archivo.");

            migrationBuilder.CreateTable(
                name: "metadato_libreria",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nombre_paquete = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    fecha_publicacion = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    descargas = table.Column<long>(type: "bigint", nullable: true),
                    licencia = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    frameworks_objetivo = table.Column<string>(type: "jsonb", nullable: true),
                    compatible_aot = table.Column<bool>(type: "boolean", nullable: true, comment: "NULL = no determinado."),
                    deprecado = table.Column<bool>(type: "boolean", nullable: true, comment: "NULL = no determinado."),
                    fecha_consulta = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadato_libreria", x => x.id);
                    table.CheckConstraint("ck_metadato_descargas", "descargas IS NULL OR descargas >= 0");
                },
                comment: "Metadatos de NuGet por paquete y versión; funciona como caché compartida entre análisis.");

            migrationBuilder.CreateTable(
                name: "dependencia",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    analisis_id = table.Column<Guid>(type: "uuid", nullable: false),
                    nombre_paquete = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version_declarada = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, comment: "NULL si la versión no se puede resolver desde el .csproj.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_dependencia", x => x.id);
                    table.ForeignKey(
                        name: "fk_dependencia_analisis",
                        column: x => x.analisis_id,
                        principalTable: "analisis",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Librería declarada en el archivo de un análisis.");

            migrationBuilder.CreateTable(
                name: "diagnostico",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    dependencia_id = table.Column<long>(type: "bigint", nullable: false),
                    metadato_id = table.Column<long>(type: "bigint", nullable: true, comment: "Fila de caché usada, solo para trazabilidad."),
                    estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ultima_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    indicadores = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb", comment: "Copia de los indicadores usados: el resultado no cambia si la caché se renueva."),
                    motivos = table.Column<string>(type: "text", nullable: false),
                    fecha_consulta = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_diagnostico", x => x.id);
                    table.CheckConstraint("ck_diagnostico_estado", "estado IN ('actualizada','desactualizada','incompatible','sin_soporte_activo','no_determinada')");
                    table.ForeignKey(
                        name: "fk_diagnostico_dependencia",
                        column: x => x.dependencia_id,
                        principalTable: "dependencia",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_diagnostico_metadato",
                        column: x => x.metadato_id,
                        principalTable: "metadato_libreria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                },
                comment: "Resultado del diagnóstico de una dependencia, con copia de los indicadores usados.");

            migrationBuilder.CreateTable(
                name: "recomendacion",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    diagnostico_id = table.Column<long>(type: "bigint", nullable: false),
                    tipo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    paquete_alternativo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version_sugerida = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    justificacion = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_recomendacion", x => x.id);
                    table.CheckConstraint("ck_recomendacion_tipo", "tipo IN ('actualizar_version','alternativa')");
                    table.ForeignKey(
                        name: "fk_recomendacion_diagnostico",
                        column: x => x.diagnostico_id,
                        principalTable: "diagnostico",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                },
                comment: "Alternativa o actualización sugerida para una dependencia con deficiencia.");

            migrationBuilder.CreateIndex(
                name: "ix_analisis_fecha",
                table: "analisis",
                column: "fecha_analisis",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_dependencia_analisis",
                table: "dependencia",
                column: "analisis_id");

            migrationBuilder.CreateIndex(
                name: "ix_diagnostico_metadato",
                table: "diagnostico",
                column: "metadato_id");

            migrationBuilder.CreateIndex(
                name: "ux_diagnostico_dependencia",
                table: "diagnostico",
                column: "dependencia_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metadato_fecha_consulta",
                table: "metadato_libreria",
                column: "fecha_consulta");

            migrationBuilder.CreateIndex(
                name: "ix_recomendacion_diagnostico",
                table: "recomendacion",
                column: "diagnostico_id");

            migrationBuilder.CreateIndex(
                name: "ux_recomendacion",
                table: "recomendacion",
                columns: new[] { "diagnostico_id", "paquete_alternativo" },
                unique: true);

            // NuGet no distingue mayúsculas en el identificador del paquete: la unicidad se define sobre lower(nombre_paquete).
            // EF Core no modela índices de expresión, por eso se crea con SQL (y se elimina en Down).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_metadato_paquete_version ON metadato_libreria (lower(nombre_paquete), version);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ux_metadato_paquete_version;");

            migrationBuilder.DropTable(
                name: "recomendacion");

            migrationBuilder.DropTable(
                name: "diagnostico");

            migrationBuilder.DropTable(
                name: "dependencia");

            migrationBuilder.DropTable(
                name: "metadato_libreria");

            migrationBuilder.DropTable(
                name: "analisis");
        }
    }
}
