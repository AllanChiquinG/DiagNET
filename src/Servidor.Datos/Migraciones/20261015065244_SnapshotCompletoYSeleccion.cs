using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Servidor.Datos.Migraciones
{
    /// <inheritdoc />
    public partial class SnapshotCompletoYSeleccion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_recomendacion_tipo",
                table: "recomendacion");

            migrationBuilder.AlterColumn<string>(
                name: "tipo",
                table: "recomendacion",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "evidencia",
                table: "recomendacion",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Datos que respaldan la recomendación (lista JSON).");

            migrationBuilder.AddColumn<string>(
                name: "limitaciones",
                table: "recomendacion",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Limitaciones que el usuario debe conocer (lista JSON).");

            migrationBuilder.AddColumn<bool>(
                name: "posibles_cambios_codigo",
                table: "recomendacion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "fecha_consulta",
                table: "diagnostico",
                type: "timestamptz",
                nullable: true,
                comment: "Fecha de los datos usados; NULL si no se obtuvo ningún dato (nunca se inventa).",
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz");

            migrationBuilder.AddColumn<string>(
                name: "evaluacion_por_tfm",
                table: "diagnostico",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Estado evaluado para cada TFM (lista JSON).");

            migrationBuilder.AddColumn<string>(
                name: "hallazgos",
                table: "diagnostico",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Hallazgos de las reglas con su motivo y evidencia (lista JSON).");

            migrationBuilder.AddColumn<string>(
                name: "origen",
                table: "diagnostico",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "no_determinado",
                comment: "De dónde salieron los datos: nuget, cache_vigente, cache_expirada o no_determinado.");

            migrationBuilder.AddColumn<string>(
                name: "advertencias",
                table: "dependencia",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "condicion",
                table: "dependencia",
                type: "text",
                nullable: true,
                comment: "Condición de MSBuild de la referencia, tal cual (no se evalúa).");

            migrationBuilder.AddColumn<string>(
                name: "tipo_version",
                table: "dependencia",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "ausente",
                comment: "Forma de la versión declarada: exacta, rango, flotante, propiedad, ausente o invalida.");

            migrationBuilder.AddColumn<string>(
                name: "advertencias",
                table: "analisis",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Advertencias del análisis (lista JSON).");

            migrationBuilder.AddColumn<string>(
                name: "frameworks_declarados",
                table: "analisis",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Frameworks tal como se declararon (lista JSON).");

            migrationBuilder.AddColumn<string>(
                name: "frameworks_evaluados",
                table: "analisis",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb",
                comment: "Frameworks efectivamente evaluados (los admitidos).");

            migrationBuilder.AddColumn<string>(
                name: "version_reglas",
                table: "analisis",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "",
                comment: "Versión de las reglas de diagnóstico usadas; evita reinterpretar resultados antiguos.");

            migrationBuilder.CreateTable(
                name: "seleccion_paquete",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    nombre_paquete = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ultima_version_estable = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true, comment: "NULL si no se determinó."),
                    ultima_compatible_por_tfm = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb", comment: "Objeto JSON: TFM → última versión estable compatible."),
                    seleccion_completa = table.Column<bool>(type: "boolean", nullable: false, comment: "false si no se recorrieron todas las páginas del registro: nunca se presenta como selección global."),
                    fecha_consulta = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_seleccion_paquete", x => x.id);
                },
                comment: "Caché de la selección de versiones por paquete (DEC-11): última estable y última compatible por TFM, con vigencia.");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recomendacion_tipo",
                table: "recomendacion",
                sql: "tipo IN ('actualizar_version','alternativa','candidato_para_revision')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_diagnostico_origen",
                table: "diagnostico",
                sql: "origen IN ('nuget','cache_vigente','cache_expirada','no_determinado')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_dependencia_tipo_version",
                table: "dependencia",
                sql: "tipo_version IN ('exacta','rango','flotante','propiedad','ausente','invalida')");

            migrationBuilder.CreateIndex(
                name: "ix_seleccion_fecha_consulta",
                table: "seleccion_paquete",
                column: "fecha_consulta");

            // Una fila por paquete sin distinguir mayúsculas (índice de expresión: EF Core no lo modela).
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX ux_seleccion_paquete_nombre ON seleccion_paquete (lower(nombre_paquete));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "seleccion_paquete");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recomendacion_tipo",
                table: "recomendacion");

            migrationBuilder.DropCheckConstraint(
                name: "ck_diagnostico_origen",
                table: "diagnostico");

            migrationBuilder.DropCheckConstraint(
                name: "ck_dependencia_tipo_version",
                table: "dependencia");

            migrationBuilder.DropColumn(
                name: "evidencia",
                table: "recomendacion");

            migrationBuilder.DropColumn(
                name: "limitaciones",
                table: "recomendacion");

            migrationBuilder.DropColumn(
                name: "posibles_cambios_codigo",
                table: "recomendacion");

            migrationBuilder.DropColumn(
                name: "evaluacion_por_tfm",
                table: "diagnostico");

            migrationBuilder.DropColumn(
                name: "hallazgos",
                table: "diagnostico");

            migrationBuilder.DropColumn(
                name: "origen",
                table: "diagnostico");

            migrationBuilder.DropColumn(
                name: "advertencias",
                table: "dependencia");

            migrationBuilder.DropColumn(
                name: "condicion",
                table: "dependencia");

            migrationBuilder.DropColumn(
                name: "tipo_version",
                table: "dependencia");

            migrationBuilder.DropColumn(
                name: "advertencias",
                table: "analisis");

            migrationBuilder.DropColumn(
                name: "frameworks_declarados",
                table: "analisis");

            migrationBuilder.DropColumn(
                name: "frameworks_evaluados",
                table: "analisis");

            migrationBuilder.DropColumn(
                name: "version_reglas",
                table: "analisis");

            migrationBuilder.AlterColumn<string>(
                name: "tipo",
                table: "recomendacion",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(30)",
                oldMaxLength: 30);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "fecha_consulta",
                table: "diagnostico",
                type: "timestamptz",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true,
                oldComment: "Fecha de los datos usados; NULL si no se obtuvo ningún dato (nunca se inventa).");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recomendacion_tipo",
                table: "recomendacion",
                sql: "tipo IN ('actualizar_version','alternativa')");
        }
    }
}
